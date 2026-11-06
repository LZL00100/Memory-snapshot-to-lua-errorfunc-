# Lua Error Snapshot

ToLua 工程的零侵入错误快照插件。安装后，任何 `lua_pcall` 抛出的错误都会附带
「最近若干帧的 locals / upvalues 内存快照」，在栈展开之前采集。

宿主工程不需要修改任何一行 C# 或 Lua 源码。

## 工作机制

`tolua_runtime/tolua.c` 中，`tolua_opentraceback()` 把 C 函数 `traceback` 存进
lua registry 的 `LUA_RIDX_CUSTOMTRACEBACK`（= 28）槽；`tolua_beginpcall`、
`tolua_pushtraceback`、`tolua_require`、`tolua_update` 都通过 `lua_getref(28)`
取它作为 `lua_pcall` 的 errfunc。

本插件在 VM 就绪后用 `debug.getregistry()` 把这个槽替换成自己的 Lua 函数。
由于 errfunc 在栈展开之前被调用，`debug.getlocal` / `debug.getupvalue` 仍能读到
出错现场。替换一个 registry 槽等价于给所有 C# 发起的 pcall 装上错误处理函数，
覆盖范围比逐个改 `LuaState.BeginPCall` / `LuaTable.BeginCall` 更完整。

```
Lua 抛错
  -> lua_pcall 调用 errfunc（插件 handler，栈未展开）
  -> handler 返回「原始消息 + traceback + Memory Frames」
  -> C# LuaState.PCall 取回字符串，抛 LuaException
  -> 宿主原有链路 LuaManager.LuaError -> Call("error", err) -> ErrorView.Show
```

因此快照会随宿主原有的错误 UI / 日志一起显示，插件不需要自己接管展示。

## 安装方式一：embedded 包（推荐，零 manifest 改动）

把整个 `com.yourstudio.lua-error-snapshot/` 目录放进工程的 `Packages/` 下：

```
<Project>/Packages/com.yourstudio.lua-error-snapshot/
├── package.json
├── Runtime/
└── Editor/
```

Unity 会自动识别 embedded package，`Packages/manifest.json` **不需要**添加任何依赖。
卸载时删除该目录即可，宿主工程零残留。

## 安装方式二：UPM git URL（可版本化）

本目录本身就是一个合规的 UPM 包根（`package.json` 在根，含 `Runtime/` 与 `Editor/`），
可直接推成独立 git 仓库并在 `Packages/manifest.json` 加一行：

```json
{
  "dependencies": {
    "com.yourstudio.lua-error-snapshot": "https://github.com/LZL00100/Memory-snapshot-to-lua-errorfunc-.git#v1.0.0"
  }
}
```

可用 `#<tag>` / `#<branch>` / `#<commit>` 锁版本。详见 `Samples~/GitDistribution/README.md`。

## 配置

默认配置开箱可用（8 帧、64KB 上限、仅开发构建采集快照）。三种覆盖方式：

1. 改 `Runtime/SnapshotConfig.cs` 的字段默认值。
2. 编辑器菜单 `Tools/Lua Error Snapshot/`（偏好存 `EditorPrefs`，不写工程文件，
   删包零残留；仅影响 Editor）。
3. 代码里在安装前调用：

```csharp
YourStudio.LuaErrorSnapshot.SnapshotRuntime.Configure(
    new YourStudio.LuaErrorSnapshot.SnapshotConfig {
        maxFrames = 12,
        minInterval = 1.0f,
        developmentOnly = false,
    });
```

公共 API：`SnapshotRuntime.Configure / Install / Uninstall / Installed / Status / GetConfigCopy`。

## 关键设计约束

- 错误文本在交给 C# 之前统一做 UTF-8 合法化：合法多字节原样保留，非法字节转义成
  `\xNN`，截断按字符边界进行。这保证宿主 `LuaDLL.lua_ptrtostring` 走
  `Marshal.PtrToStringAnsi` 时不会再抛 `Illegal byte sequence encounted in the input`。
- errfunc 内全部逻辑由 `pcall` 包裹并有防重入标志；handler 自身报错会返回
  `LUA_ERRERR` 并丢掉原始错误，因此任何异常都兜底成「至少返回原始消息」。
- 序列化带节点数与字节数双预算，不遍历整张大表；字段名命中
  password/token/secret 等关键字时值替换为 `***`。
- 插件程序集 `autoReferenced = false` 且不引用 `Assembly-CSharp`，宿主类型一律按
  字符串反射查找；`[assembly: AlwaysLinkAssembly]` 防止 IL2CPP 裁剪掉自动初始化。

## 限制

- 注入时机在 `LuaState` 创建之后（约首帧）。`LuaState.Start()` 之前发生的极早期
  错误仍走 ToLua 原生 traceback，无快照。
- 纯 Lua 方案，无 IL 织入、无 native hook、无 Cecil 依赖，Mono / IL2CPP / LuaJIT 均适用。
- 注入刻意只用非泛型 `LuaState.DoString(string, string)`，不调用
  `DoString<bool>`：后者需 `MakeGenericMethod` 在 IL2CPP/AOT 下预先生成泛型实例，
  真机会抛 AOT 异常。安装结果改由引导脚本 `assert` 是否抛错来判定。
- `tolua_runtime` 若被替换成 `LUA_RIDX_CUSTOMTRACEBACK` 非 28 的定制版本，
  把 `SnapshotConfig.customTracebackSlot` 改成对应值即可（无需改代码）；
  引导脚本也会退化用 `tolua.traceback` 相等性定位（仅 Lua 5.2+ 的
  light C function 保证相等）；仍找不到则静默放弃安装，不影响宿主正常运行。
-  Lua 5.1 的 `debug.getlocal` 无法读取已被尾调用消除的帧；尾调用链可能少一帧。
