using UnityEditor;
using UnityEngine;

namespace YourStudio.LuaErrorSnapshot.Editor
{
    /// <summary>
    /// 编辑器侧偏好。用 EditorPrefs 而非工程内资源文件，删包后零残留。
    /// 仅在 Editor 下生效；真机使用 SnapshotConfig 的代码默认值，
    /// 或由宿主在安装前调用 SnapshotRuntime.Configure() 覆盖。
    /// </summary>
    [InitializeOnLoad]
    internal static class SnapshotEditorPrefs
    {
        private const string KeyEnabled = "YourStudio.LuaErrorSnapshot.Enabled";
        private const string KeyDevOnly = "YourStudio.LuaErrorSnapshot.DevOnly";
        private const string KeyLogConsole = "YourStudio.LuaErrorSnapshot.LogConsole";

        internal static bool Enabled
        {
            get { return EditorPrefs.GetBool(KeyEnabled, true); }
            set { EditorPrefs.SetBool(KeyEnabled, value); }
        }

        internal static bool DevelopmentOnly
        {
            get { return EditorPrefs.GetBool(KeyDevOnly, true); }
            set { EditorPrefs.SetBool(KeyDevOnly, value); }
        }

        internal static bool LogToConsole
        {
            get { return EditorPrefs.GetBool(KeyLogConsole, false); }
            set { EditorPrefs.SetBool(KeyLogConsole, value); }
        }

        static SnapshotEditorPrefs()
        {
            // 域重载 / 进入 Play 前把偏好推给 Runtime，保证开关即时生效。
            try
            {
                SnapshotConfig cfg = SnapshotRuntime.GetConfigCopy();
                cfg.enabled = Enabled;
                cfg.developmentOnly = DevelopmentOnly;
                cfg.logToConsole = LogToConsole;
                SnapshotRuntime.Configure(cfg);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[LuaErrorSnapshot] prefs apply failed: " + e.Message);
            }
        }
    }
}
