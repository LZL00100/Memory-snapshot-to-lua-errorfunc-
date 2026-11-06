using System;
using UnityEngine;

namespace YourStudio.LuaErrorSnapshot
{
    /// <summary>
    /// 插件唯一公共入口。宿主代码完全不需要引用本类——
    /// [RuntimeInitializeOnLoadMethod] 会在运行时自动完成安装。
    /// 只有想覆盖默认配置时才需要调用 Configure。
    /// </summary>
    public static class SnapshotRuntime
    {
        private static SnapshotConfig s_config = new SnapshotConfig();
        private static bool s_installed;
        private static string s_status = "not installed";
        private static object s_luaState;

        public static bool Installed { get { return s_installed; } }
        public static string Status { get { return s_status; } }
        public static SnapshotConfig Config { get { return s_config; } }

        /// <summary>可选：覆盖配置。必须在安装之前调用（例如 OnBeforeSceneLoadRuntimeMethod）。</summary>
        public static void Configure(SnapshotConfig config)
        {
            s_config = (config ?? new SnapshotConfig()).Clone();
        }

        /// <summary>取一份配置副本修改，避免外部持有内部引用。</summary>
        public static SnapshotConfig GetConfigCopy()
        {
            return s_config.Clone();
        }

        private static bool ShouldCapture()
        {
            if (!s_config.enabled)
            {
                return false;
            }
            if (!s_config.developmentOnly)
            {
                return true;
            }
            return Debug.isDebugBuild || Application.isEditor;
        }

        /// <summary>
        /// 尝试安装。可重复调用；LuaState 尚未创建时会返回 false，由 Bootstrap 下一帧重试。
        /// 全程 try/catch，任何失败都只记状态、不抛异常，绝不影响游戏启动。
        /// </summary>
        public static bool Install()
        {
            if (s_installed)
            {
                return true;
            }
            if (!s_config.enabled)
            {
                s_status = "disabled by config";
                return false;
            }

            try
            {
                object state = LuaStateBridge.GetMainState();
                if (state == null)
                {
                    s_status = "waiting for LuaState";
                    return false;
                }

                bool capture = ShouldCapture();
                string chunk = LuaStateBridge.BuildConfigChunk(s_config, capture) + EmbeddedLua.Bootstrap;

                string error;
                bool ok = LuaStateBridge.Execute(state, chunk, out error);
                if (!ok)
                {
                    s_status = "install failed: " + (error ?? "unknown");
                    return false;
                }

                s_luaState = state;
                s_installed = true;
                s_status = capture ? "installed (snapshot enabled)" : "installed (traceback only)";
                return true;
            }
            catch (Exception e)
            {
                s_status = "install exception: " + e.Message;
                return false;
            }
        }

        /// <summary>运行时卸载：恢复 ToLua 原有 traceback，清理插件写入的全局符号。</summary>
        public static bool Uninstall()
        {
            if (!s_installed || s_luaState == null)
            {
                s_status = "not installed";
                return false;
            }
            try
            {
                string error;
                bool ok = LuaStateBridge.ExecuteUninstall(s_luaState, out error);
                s_installed = false;
                s_luaState = null;
                s_status = ok ? "uninstalled" : ("uninstall failed: " + (error ?? "unknown"));
                return ok;
            }
            catch (Exception e)
            {
                s_installed = false;
                s_luaState = null;
                s_status = "uninstall exception: " + e.Message;
                return false;
            }
        }

        /// <summary>域重载时重置状态（新的 AppDomain 会重建静态字段，此方法供 Bootstrap 显式调用）。</summary>
        internal static void ResetState()
        {
            s_installed = false;
            s_luaState = null;
            s_status = "not installed";
        }
    }
}
