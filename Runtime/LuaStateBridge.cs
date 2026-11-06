using System;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace YourStudio.LuaErrorSnapshot
{
    /// <summary>
    /// 仅通过反射与 ToLua 交互：插件程序集不引用 Assembly-CSharp，因此没有编译期依赖。
    /// 宿主类型名按字符串查找，找不到就静默放弃，绝不影响工程正常启动。
    /// </summary>
    internal static class LuaStateBridge
    {
        private const string LuaStateTypeName = "LuaInterface.LuaState";
        private const string ChunkName = "@LuaErrorSnapshot/bootstrap.lua";

        private static Type s_cachedType;

        internal static string Chunk => ChunkName;

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullName, false);
                if (t != null)
                {
                    return t;
                }
            }
            return null;
        }

        private static Type LuaStateType
        {
            get
            {
                if (s_cachedType == null)
                {
                    s_cachedType = FindType(LuaStateTypeName);
                }
                return s_cachedType;
            }
        }

        /// <summary>
        /// 取当前主 LuaState 实例。优先 LuaState.mainState 静态字段；
        /// 退化路径再按常见命名从 LuaManager / 任意静态 Instance 上找 Lua 字段。
        /// </summary>
        internal static object GetMainState()
        {
            Type t = LuaStateType;
            if (t == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance |
                                       BindingFlags.Public | BindingFlags.NonPublic;

            FieldInfo mainState = t.GetField("mainState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mainState != null)
            {
                object state = mainState.GetValue(null);
                if (state != null && t.IsInstanceOfType(state))
                {
                    // Editor 下 LuaState.DoString 要求先 Start()；该字段仅 UNITY_EDITOR 存在。
                    // 未就绪时返回 null，让 Bootstrap 下一帧重试，避免每帧抛异常。
                    FieldInfo beStart = t.GetField("beStart", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (beStart != null && beStart.FieldType == typeof(bool) && !(bool)beStart.GetValue(state))
                    {
                        return null;
                    }
                    return state;
                }
            }

            // 退化路径：扫描所有程序集里名为 *LuaManager* 的类型，找单例上的 LuaState 字段
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type mt in types)
                {
                    if (mt == null || mt.Name.IndexOf("LuaManager", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    PropertyInfo instProp = mt.GetProperty("Instance", flags);
                    FieldInfo instField = mt.GetField("Instance", flags);
                    object inst = null;
                    try
                    {
                        if (instProp != null)
                        {
                            inst = instProp.GetValue(null, null);
                        }
                        else if (instField != null)
                        {
                            inst = instField.GetValue(null);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (inst == null)
                    {
                        continue;
                    }

                    foreach (FieldInfo f in mt.GetFields(flags))
                    {
                        if (!t.IsAssignableFrom(f.FieldType))
                        {
                            continue;
                        }
                        object candidate = f.GetValue(inst);
                        if (candidate != null)
                        {
                            return candidate;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>生成配置字面量，拼在引导脚本之前。</summary>
        internal static string BuildConfigChunk(SnapshotConfig cfg, bool capture)
        {
            NumberFormatInfo inv = CultureInfo.InvariantCulture.NumberFormat;
            StringBuilder sb = new StringBuilder(512);
            // 与 EmbeddedLua.Bootstrap 拼成同一个 Lua chunk，故用 local，
            // 不往 _G 写任何符号。
            sb.AppendLine("local CFG = {");
            sb.Append("  capture = ").Append(capture ? "true" : "false").AppendLine(",");
            sb.Append("  maxFrames = ").Append(cfg.maxFrames.ToString(inv)).AppendLine(",");
            sb.Append("  inspectDepth = ").Append(cfg.inspectDepth.ToString(inv)).AppendLine(",");
            sb.Append("  maxValueLen = ").Append(cfg.maxValueLen.ToString(inv)).AppendLine(",");
            sb.Append("  maxTotalLen = ").Append(cfg.maxTotalLen.ToString(inv)).AppendLine(",");
            sb.Append("  maxNodes = ").Append(cfg.maxNodes.ToString(inv)).AppendLine(",");
            sb.Append("  maxTableKeys = ").Append(cfg.maxTableKeys.ToString(inv)).AppendLine(",");
            sb.Append("  dumpLocals = ").Append(cfg.dumpLocals ? "true" : "false").AppendLine(",");
            sb.Append("  dumpUpvalues = ").Append(cfg.dumpUpvalues ? "true" : "false").AppendLine(",");
            sb.Append("  minInterval = ").Append(cfg.minInterval.ToString("R", inv)).AppendLine(",");
            sb.Append("  customTracebackSlot = ").Append(cfg.customTracebackSlot.ToString(inv)).AppendLine(",");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// 用非泛型 LuaState.DoString(string, string) 执行引导脚本。
        ///
        /// 刻意不使用 DoString&lt;bool&gt;：MakeGenericMethod(typeof(bool)) 在 IL2CPP/AOT
        /// 下需要预先生成 DoString&lt;bool&gt; 的本地代码，宿主工程从未实例化过该泛型，
        /// 真机会抛 AOT 异常。改为「脚本成功则静默返回、失败则 assert 抛运行时错误」，
        /// C# 以是否抛异常判定安装结果，完全避开泛型反射。
        /// </summary>
        internal static bool Execute(object luaState, string chunk, out string error)
        {
            error = null;
            Type t = LuaStateType;
            if (luaState == null || t == null)
            {
                error = "LuaState instance not available";
                return false;
            }

            MethodInfo plain = null;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (m.Name != "DoString" || m.IsGenericMethodDefinition)
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string) &&
                    ps[1].ParameterType == typeof(string) && m.ReturnType == typeof(void))
                {
                    plain = m;
                    break;
                }
            }

            if (plain == null)
            {
                error = "LuaState.DoString(string,string) not found";
                return false;
            }

            try
            {
                plain.Invoke(luaState, new object[] { chunk, ChunkName });
                return true;
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException ?? e;
                error = inner.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>执行卸载脚本。</summary>
        internal static bool ExecuteUninstall(object luaState, out string error)
        {
            const string script =
                "local ok, reg = pcall(debug.getregistry)\n" +
                "if not ok or type(reg) ~= 'table' then return false end\n" +
                "local m = reg.__LuaErrorSnapshot\n" +
                "if type(m) ~= 'table' or type(m.uninstall) ~= 'function' then return false end\n" +
                "local ok2 = pcall(m.uninstall)\n" +
                "reg.__LuaErrorSnapshot = nil\n" +
                "return ok2 and true or false";
            return Execute(luaState, script, out error);
        }
    }
}
