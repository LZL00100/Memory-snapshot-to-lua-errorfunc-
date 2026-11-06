using UnityEditor;
using UnityEngine;

namespace YourStudio.LuaErrorSnapshot.Editor
{
    internal static class SnapshotMenu
    {
        private const string Root = "Tools/Lua Error Snapshot/";

        [MenuItem(Root + "Enable", false, 1)]
        private static void Enable()
        {
            SnapshotEditorPrefs.Enabled = true;
            ApplyPrefs();
            Debug.Log("[LuaErrorSnapshot] enabled. Re-enter Play Mode to apply.");
        }

        [MenuItem(Root + "Enable", true, 1)]
        private static bool EnableValidate()
        {
            Menu.SetChecked(Root + "Enable", SnapshotEditorPrefs.Enabled);
            return true;
        }

        [MenuItem(Root + "Disable", false, 2)]
        private static void Disable()
        {
            SnapshotEditorPrefs.Enabled = false;
            ApplyPrefs();
            Debug.Log("[LuaErrorSnapshot] disabled. Re-enter Play Mode to apply.");
        }

        [MenuItem(Root + "Development Build Only", false, 10)]
        private static void ToggleDevOnly()
        {
            SnapshotEditorPrefs.DevelopmentOnly = !SnapshotEditorPrefs.DevelopmentOnly;
            ApplyPrefs();
        }

        [MenuItem(Root + "Development Build Only", true, 10)]
        private static bool ToggleDevOnlyValidate()
        {
            Menu.SetChecked(Root + "Development Build Only", SnapshotEditorPrefs.DevelopmentOnly);
            return true;
        }

        [MenuItem(Root + "Mirror To Console", false, 11)]
        private static void ToggleLogConsole()
        {
            SnapshotEditorPrefs.LogToConsole = !SnapshotEditorPrefs.LogToConsole;
            ApplyPrefs();
        }

        [MenuItem(Root + "Mirror To Console", true, 11)]
        private static bool ToggleLogConsoleValidate()
        {
            Menu.SetChecked(Root + "Mirror To Console", SnapshotEditorPrefs.LogToConsole);
            return true;
        }

        [MenuItem(Root + "Show Status", false, 20)]
        private static void ShowStatus()
        {
            string msg =
                "Installed: " + SnapshotRuntime.Installed + "\n" +
                "Status   : " + SnapshotRuntime.Status + "\n" +
                "Enabled  : " + SnapshotEditorPrefs.Enabled + "\n" +
                "DevOnly  : " + SnapshotEditorPrefs.DevelopmentOnly + "\n" +
                "Mirror   : " + SnapshotEditorPrefs.LogToConsole + "\n" +
                "MaxFrames: " + SnapshotRuntime.Config.maxFrames + "\n" +
                "MaxTotal : " + SnapshotRuntime.Config.maxTotalLen + " bytes";
            EditorUtility.DisplayDialog("Lua Error Snapshot", msg, "OK");
        }

        [MenuItem(Root + "Force Reinstall (Play Mode)", false, 30)]
        private static void ForceReinstall()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[LuaErrorSnapshot] enter Play Mode first.");
                return;
            }
            SnapshotRuntime.Uninstall();
            bool ok = SnapshotRuntime.Install();
            Debug.Log("[LuaErrorSnapshot] reinstall " + (ok ? "ok: " : "failed: ") +
                      SnapshotRuntime.Status);
        }

        [MenuItem(Root + "Uninstall (Play Mode)", false, 31)]
        private static void Uninstall()
        {
            if (!EditorApplication.isPlaying)
            {
                Debug.LogWarning("[LuaErrorSnapshot] enter Play Mode first.");
                return;
            }
            bool ok = SnapshotRuntime.Uninstall();
            Debug.Log("[LuaErrorSnapshot] uninstall " + (ok ? "ok: " : "failed: ") +
                      SnapshotRuntime.Status);
        }

        private static void ApplyPrefs()
        {
            SnapshotConfig cfg = SnapshotRuntime.GetConfigCopy();
            cfg.enabled = SnapshotEditorPrefs.Enabled;
            cfg.developmentOnly = SnapshotEditorPrefs.DevelopmentOnly;
            cfg.logToConsole = SnapshotEditorPrefs.LogToConsole;
            SnapshotRuntime.Configure(cfg);
        }
    }
}
