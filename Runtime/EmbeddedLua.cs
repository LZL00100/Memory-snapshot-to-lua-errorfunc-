// -----------------------------------------------------------------------------
// 内嵌 Lua 引导脚本。
//
// 为什么是字符串常量而不是 .lua 文件：
//   UPM 包内以 ~ 结尾的目录会被 Unity 忽略、不为资产生成 .meta；包内 Resources
//   的加载行为在不同 Unity 版本上也不一致。把脚本编译进 DLL 是唯一能保证
//   Editor / Mono / IL2CPP 全平台「装了就能跑」的方式（Miku-LuaProfiler 同款做法）。
//
// 注入机制（零 C# 源码改动的关键）：
//   tolua_runtime/tolua.c 中 tolua_opentraceback() 把 C 函数 traceback 存进
//   lua registry 的 LUA_RIDX_CUSTOMTRACEBACK（= 28）槽；tolua_beginpcall /
//   tolua_pushtraceback / tolua_require / tolua_update 都通过 lua_getref(该槽)
//   取它作为 lua_pcall 的 errfunc。Lua 侧用 debug.getregistry() 换掉这个槽，
//   等价于给「所有」C# 发起的 lua_pcall 装上自定义错误处理函数。
//   errfunc 在栈展开之前被调用，因此 debug.getlocal/getupvalue 仍能读到现场。
//
// 约定：脚本内所有 Lua 字符串一律用单引号，避免 C# verbatim string 的引号冲突。
//       chunkName 含 "LuaErrorSnapshot"，用于让 dumper 跳过插件自身栈帧。
// -----------------------------------------------------------------------------

namespace YourStudio.LuaErrorSnapshot
{
    internal static class EmbeddedLua
    {
        // 由 SnapshotRuntime 拼在脚本最前面的是同一 chunk 内的
        // `local CFG = { capture = ..., maxFrames = ..., ... }`，
        // 因此这里直接沿用该局部变量即可，不产生任何全局变量。
        internal const string Bootstrap = @"
local S = {}

local type, pcall, tostring, tonumber = type, pcall, tostring, tonumber
local byte, char, sub, fmt, find, gsub, concat = string.byte, string.char,
    string.sub, string.format, string.find, string.gsub, table.concat
local getinfo, getlocal, getupvalue, traceback = debug.getinfo,
    debug.getlocal, debug.getupvalue, debug.traceback

-- ---------------------------------------------------------------------------
-- UTF-8 安全化
--
-- 宿主 LuaDLL.lua_ptrtostring 走 Marshal.PtrToStringAnsi。若错误消息里出现
-- 非法字节（被按字节截断的中文/emoji、二进制 Lua string、userdata 的
-- __tostring 输出），Mono 会抛 Illegal byte sequence 异常
-- 并覆盖掉真正的 Lua 错误。这里保证交给 C# 的一定是合法 UTF-8：
--   * 合法多字节序列原样保留（中文可正常显示）
--   * 孤立/非法字节转成 \xNN
--   * 控制字符转成 \xNN（换行/制表/回车保留）
--   * 截断按字符边界进行，绝不切在半个 UTF-8 字符中间
-- ---------------------------------------------------------------------------
local function isCont(b)
    return b >= 0x80 and b <= 0xBF
end

local function utf8Width(s, i, len)
    local b = byte(s, i)
    if b < 0x80 then return 1 end
    if b >= 0xC2 and b <= 0xDF then
        if i + 1 <= len and isCont(byte(s, i + 1)) then return 2 end
        return 0
    end
    if b >= 0xE0 and b <= 0xEF then
        if i + 2 > len or not isCont(byte(s, i + 2)) then return 0 end
        local b2 = byte(s, i + 1)
        if not isCont(b2) then return 0 end
        if b == 0xE0 and b2 < 0xA0 then return 0 end          -- overlong
        if b == 0xED and b2 > 0x9F then return 0 end          -- surrogate
        return 3
    end
    if b >= 0xF0 and b <= 0xF4 then
        if i + 3 > len or not isCont(byte(s, i + 2))
            or not isCont(byte(s, i + 3)) then return 0 end
        local b2 = byte(s, i + 1)
        if not isCont(b2) then return 0 end
        if b == 0xF0 and b2 < 0x90 then return 0 end          -- overlong
        if b == 0xF4 and b2 > 0x8F then return 0 end          -- > U+10FFFF
        return 4
    end
    return 0
end

local function sanitize(value, maxBytes)
    local s
    if type(value) == 'string' then
        s = value
    else
        local ok, r = pcall(tostring, value)
        s = ok and r or '<value: tostring failed>'
    end
    maxBytes = maxBytes or 4096
    if maxBytes < 1 then return '' end

    local out, n, i, len = {}, 0, 1, #s
    while i <= len do
        local b = byte(s, i)
        local seg, used
        if b < 0x80 then
            if b == 0x0A or b == 0x09 or b == 0x0D or (b >= 0x20 and b ~= 0x7F) then
                seg, used = char(b), 1
            else
                seg, used = fmt('\\x%02X', b), 1
            end
        else
            local w = utf8Width(s, i, len)
            if w == 0 then
                seg, used = fmt('\\x%02X', b), 1
            else
                seg, used = sub(s, i, i + w - 1), w
            end
        end
        if n + #seg > maxBytes then break end
        out[#out + 1] = seg
        n = n + #seg
        i = i + used
    end
    return concat(out)
end
S.sanitize = sanitize

-- ---------------------------------------------------------------------------
-- 值序列化（自包含，不依赖宿主的 sys/inspect）
-- 带节点数 / 字节数双预算，避免大表预遍历拖垮 errfunc。
-- ---------------------------------------------------------------------------
local SENSITIVE = { 'password', 'passwd', 'token', 'session', 'secret', 'signature' }

local function isSensitive(name)
    if type(name) ~= 'string' then return false end
    local lower = string.lower(name)
    for i = 1, #SENSITIVE do
        if find(lower, SENSITIVE[i], 1, true) then return true end
    end
    return false
end

local function isInternalLocal(name)
    return type(name) ~= 'string' or sub(name, 1, 1) == '('
end

local function quote(s)
    -- 单引号包裹，转义反斜杠与单引号；内容先经 sanitize 保证合法 UTF-8
    local body = gsub(sanitize(s, CFG.maxValueLen), '\\', '\\\\')
    body = gsub(body, '\'', '\\\'')
    return '\'' .. body .. '\''
end

local function serialize(v, depth, budget, seen)
    if budget.nodes <= 0 then return '<budget exhausted>' end
    budget.nodes = budget.nodes - 1

    local t = type(v)
    if t == 'nil' or t == 'boolean' or t == 'number' then
        return tostring(v)
    end
    if t == 'string' then
        return quote(v)
    end
    if t == 'function' or t == 'thread' or t == 'userdata' then
        -- userdata 的 __tostring 可能是 C# 代理，pcall 兜底
        local ok, s = pcall(tostring, v)
        return ok and sanitize(s, 256) or ('<' .. t .. '>')
    end
    if t ~= 'table' then
        return '<' .. t .. '>'
    end
    if depth <= 0 then return '{...}' end
    if seen[v] then return '<cycle>' end
    seen[v] = true

    local parts, count = {}, 0
    local keys = 0
    for k, val in pairs(v) do
        keys = keys + 1
        if keys > (CFG.maxTableKeys or 40) then
            parts[#parts + 1] = '...<more keys>'
            break
        end
        if budget.nodes <= 0 then
            parts[#parts + 1] = '...<budget exhausted>'
            break
        end
        local ks
        if type(k) == 'string' and find(k, '^[%a_][%w_]*$') then
            ks = sanitize(k, 64)
        else
            ks = '[' .. serialize(k, 0, budget, seen) .. ']'
        end
        local vs
        if isSensitive(type(k) == 'string' and k or '') then
            vs = '\'***\''
        else
            vs = serialize(val, depth - 1, budget, seen)
        end
        parts[#parts + 1] = ks .. ' = ' .. vs
        count = count + 1
    end
    seen[v] = nil
    if count == 0 then return '{}' end
    local text = '{ ' .. concat(parts, ', ') .. ' }'
    if #text > CFG.maxValueLen then
        text = sanitize(text, CFG.maxValueLen) .. '...<truncated>'
    end
    return text
end

local function safeInspect(v, budget)
    local ok, s = pcall(serialize, v, CFG.inspectDepth or 2, budget, {})
    if ok and type(s) == 'string' then return s end
    return '<serialize failed>'
end

-- ---------------------------------------------------------------------------
-- 栈帧采集
-- 保持与原有 MemoryFrameDumper 相同的层级偏移与输出格式，确保日志格式不变。
-- ---------------------------------------------------------------------------
local function collectLocals(level, opts, budget)
    local list, seenNames = {}, {}
    local i, missStreak = 1, 0
    while i <= 200 do
        local ok, name, value = pcall(getlocal, level + 1, i)
        if not ok then break end
        if name == nil then
            missStreak = missStreak + 1
            if missStreak >= 3 then break end
        else
            missStreak = 0
            if not isInternalLocal(name) and not seenNames[name] then
                seenNames[name] = true
                local strVal
                if isSensitive(name) then
                    strVal = '\'***\''
                else
                    strVal = safeInspect(value, budget)
                end
                list[#list + 1] = { name = sanitize(name, 64), value = strVal }
            end
        end
        i = i + 1
    end
    -- vararg（Lua 5.2+；5.1 下 getlocal 负索引会失败，pcall 自然退出）
    local vi = -1
    while vi >= -20 do
        local ok, name, value = pcall(getlocal, level + 1, vi)
        if not ok or name == nil then break end
        list[#list + 1] = {
            name = fmt('(vararg %d)', -vi),
            value = safeInspect(value, budget),
        }
        vi = vi - 1
    end
    return list
end

local function collectUpvalues(func, opts, budget)
    local list = {}
    if type(func) ~= 'function' then return list end
    local i = 1
    while i <= 200 do
        local ok, name, value = pcall(getupvalue, func, i)
        if not ok or name == nil or name == '' then break end
        local strVal
        if isSensitive(name) then
            strVal = '\'***\''
        else
            strVal = safeInspect(value, budget)
        end
        list[#list + 1] = { name = sanitize(name, 64), value = strVal }
        i = i + 1
    end
    return list
end

local function formatFrame(idx, info, locals, upvalues)
    local buf = {}
    local src = sanitize(info.short_src or info.source or '?', 256)
    local line = info.currentline or -1
    local fn = sanitize(info.name or ('<' .. (info.what or '?') .. '>'), 128)
    buf[#buf + 1] = fmt('[Frame #%d] %s:%d  in %s', idx, src, line, fn)

    if locals and #locals > 0 then
        buf[#buf + 1] = '  locals:'
        for _, kv in ipairs(locals) do
            local v = gsub(kv.value, '\n', '\n      ')
            buf[#buf + 1] = fmt('    %-16s = %s', kv.name, v)
        end
    end
    if upvalues and #upvalues > 0 then
        buf[#buf + 1] = '  upvalues:'
        for _, kv in ipairs(upvalues) do
            local v = gsub(kv.value, '\n', '\n      ')
            buf[#buf + 1] = fmt('    %-16s = %s', kv.name, v)
        end
    end
    return concat(buf, '\n')
end

local function isSelfFrame(info)
    if not info then return false end
    local src = info.source or info.short_src or ''
    return find(src, 'LuaErrorSnapshot', 1, true) ~= nil
end

-- 生成 fallback：任何异常都至少返回「原始消息 + traceback」，绝不丢错误
local function makeFallback(msg)
    local s = sanitize(msg, CFG.maxValueLen or 2048)
    if find(s, 'stack traceback:', 1, true) then return s end
    local ok, tb = pcall(traceback, '', 2)
    if ok and type(tb) == 'string' then
        return s .. '\n' .. sanitize(tb, CFG.maxTotalLen or 65536)
    end
    return s
end

-- errfunc 专用入口：跳过插件自身帧，取最近 maxFrames 个 Lua 帧
local function dumpFromErrorHandler(msg)
    local out = {}
    out[#out + 1] = makeFallback(msg)

    local budget = {
        nodes = CFG.maxNodes or 600,
        bytes = CFG.maxTotalLen or 65536,
    }

    local frames, frameIdx, scanned = {}, 1, 0
    local level = 1
    while frameIdx <= (CFG.maxFrames or 8) and scanned < 100 do
        scanned = scanned + 1
        local info = getinfo(level, 'nSlf')
        if not info then break end
        if info.what == 'Lua' and not isSelfFrame(info) then
            local locals = CFG.dumpLocals and collectLocals(level + 1, CFG, budget) or nil
            local ups = CFG.dumpUpvalues and collectUpvalues(info.func, CFG, budget) or nil
            local okFmt, text = pcall(formatFrame, frameIdx, info, locals, ups)
            frames[#frames + 1] = (okFmt and type(text) == 'string')
                and text
                or fmt('[Frame #%d] <format failed>', frameIdx)
            frameIdx = frameIdx + 1
        end
        level = level + 1
    end

    if #frames > 0 then
        out[#out + 1] = '--- Memory Frames (captured before stack unwind) ---'
        out[#out + 1] = concat(frames, '\n\n')
    end
    return concat(out, '\n')
end

-- ---------------------------------------------------------------------------
-- 防重入 / 限频 / 兜底
-- errfunc 内部再报错会返回 LUA_ERRERR 并把错误信息全部丢掉，因此主体必须用
-- pcall 包住，且 inHandler 一定要复位。
-- ---------------------------------------------------------------------------
local inHandler = false
local lastDump = -1e9

local function body(msg)
    if not CFG.capture then return makeFallback(msg) end
    local now = os.clock()
    if now - lastDump < (CFG.minInterval or 1.0) then
        return makeFallback(msg)
    end
    lastDump = now
    return dumpFromErrorHandler(msg)
end

local function handler(msg)
    local fallback = makeFallback(msg)
    if inHandler then return fallback end
    inHandler = true
    local ok, result = pcall(body, msg)
    inHandler = false
    if ok and type(result) == 'string' and #result > 0 then
        return sanitize(result, CFG.maxTotalLen or 65536)
    end
    return fallback
end

-- ---------------------------------------------------------------------------
-- 安装：定位并替换 registry 中的 CUSTOMTRACEBACK 槽
--
--   * 主锚点：官方 tolua_runtime/tolua.h 定义 LUA_RIDX_CUSTOMTRACEBACK = 28。
--     tolua_opentraceback 用 lua_rawseti(L, LUA_REGISTRYINDEX, 28) 写入，
--     tolua_beginpcall 用 lua_getref(L, 28)（等价于 lua_rawgeti）读出，
--     与 Lua 侧 debug.getregistry()[28] 是同一个值，直接替换即可。
--   * 不以 tolua.traceback 做「值相等」判断为主锚点：Lua 5.1 的
--     lua_pushcfunction 每次都会新建 C closure，两个相同 C 函数在 Lua 层
--     不一定 ==，只有 5.2+ 的 light C function 才保证相等。故仅作版本兜底。
--   * 任一条件不满足就静默放弃，绝不破坏宿主原有错误处理。
-- ---------------------------------------------------------------------------
local function findSlot(registry)
    -- 已安装过：直接复用记录的槽位（此时该槽里是 Lua 函数，不会命中 C 函数判断）
    local known = registry.__LuaErrorSnapshotSlot
    if type(known) == 'number' and type(registry[known]) == 'function' then
        return known
    end

    local slot = CFG.customTracebackSlot or 28
    local candidate = registry[slot]
    if type(candidate) == 'function' then
        local ok, info = pcall(getinfo, candidate, 'S')
        if ok and info and info.what == 'C' then return slot end
    end
    -- 版本兜底：Lua 5.2+ 的 light C function 才保证值相等，5.1 下通常不命中，
    -- 命中则用它；都不命中就静默放弃（C# 会记 install failed，绝不破坏宿主）。
    local anchor = (type(_G.tolua) == 'table') and _G.tolua.traceback or nil
    if type(anchor) == 'function' then
        for i = 1, 64 do
            if registry[i] == anchor then return i end
        end
    end
    return nil
end

local function install()
    local okReg, registry = pcall(debug.getregistry)
    if not okReg or type(registry) ~= 'table' then return false, 'no registry' end

    -- 已安装则直接成功（域重载后 LuaState 重建，registry 也是新的）
    if registry.__LuaErrorSnapshotHandler == handler then
        return true, 'already installed'
    end

    local slot = findSlot(registry)
    if not slot then return false, 'custom traceback slot not found' end

    -- 重复安装时不要把上一次的 handler 当成「原始 traceback」保存下来，
    -- 否则卸载会把插件自己的函数写回 registry，形成悬挂引用。
    local already = type(registry.__LuaErrorSnapshotHandler) == 'function'
    if not already then
        registry.__LuaErrorSnapshotOriginal = registry[slot]
        registry.__LuaErrorSnapshotSlot = slot
    end
    registry.__LuaErrorSnapshotHandler = handler
    registry[slot] = handler
    return true, already and 'replaced existing handler' or 'installed'
end

local function uninstall()
    local okReg, registry = pcall(debug.getregistry)
    if not okReg or type(registry) ~= 'table' then return false end
    local slot = registry.__LuaErrorSnapshotSlot
    local original = registry.__LuaErrorSnapshotOriginal
    if type(slot) == 'number' and type(original) == 'function' then
        registry[slot] = original
    end
    registry.__LuaErrorSnapshotOriginal = nil
    registry.__LuaErrorSnapshotSlot = nil
    registry.__LuaErrorSnapshotHandler = nil
    return true
end

local okInstall, installReason = install()

-- 元数据只写 lua registry，不往 _G 里加任何全局符号（零命名污染）。
-- C# 侧 LuaStateBridge.ExecuteUninstall 也从 registry 读取，两边保持一致。
local metaRegistry = debug.getregistry()
metaRegistry.__LuaErrorSnapshot = {
    installed = okInstall,
    reason = installReason,
    capture = CFG.capture and true or false,
    uninstall = uninstall,
    install = install,
}

-- C# 侧用「是否抛异常」判定安装结果（避免 DoString<bool> 的 AOT 泛型实例化）。
-- assert 是 base lib 的 C 函数，不走宿主覆盖过的 _G.error；
-- 且 install 失败时 registry 槽尚未被替换，抛出的错误会由 tolua 原生
-- C traceback 处理，不会自激调用插件 handler。
if not okInstall then
    assert(false, '[LuaErrorSnapshot] install failed: ' .. tostring(installReason))
end
";
    }
}
