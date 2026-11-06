using System;
using UnityEngine;

namespace YourStudio.LuaErrorSnapshot
{
    /// <summary>
    /// 零侵入自动初始化：
    ///   1. [RuntimeInitializeOnLoadMethod] 由 Unity 反射调用，宿主无需任何代码；
    ///   2. LuaState 要到场景加载 / LuaManager.Awake 之后才创建，因此挂一个常驻组件
    ///      按帧重试，一旦拿到 LuaState 就完成注入；
    ///   3. 安装成功后组件自停用；卸载时注销日志回调，不留运行期常驻开销。
    /// </summary>
    internal static class SnapshotBootstrap
    {
        internal const int MaxRetries = 600;   // 约 10 秒 @60fps
        internal const string HostName = "[LuaErrorSnapshot] (auto)";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void OnBeforeSceneLoad()
        {
            try
            {
                SnapshotRuntime.ResetState();

                if (!SnapshotRuntime.Config.enabled)
                {
                    return;
                }

                GameObject host = GameObject.Find(HostName);
                if (host == null)
                {
                    host = new GameObject(HostName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                }
                SnapshotInstaller installer = host.GetComponent<SnapshotInstaller>();
                if (installer == null)
                {
                    host.AddComponent<SnapshotInstaller>();
                }
                else
                {
                    // 域重载后静态状态会重置，必须重新启用组件，否则注入不会重跑。
                    installer.Reset();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LuaErrorSnapshot] bootstrap failed: " + e.Message);
            }
        }
    }

    /// <summary>按帧等待 LuaState 就绪并完成一次性注入。</summary>
    internal sealed class SnapshotInstaller : MonoBehaviour
    {
        private int _retries;
        private bool _logSinkAttached;

        internal void Reset()
        {
            _retries = 0;
            _logSinkAttached = false;
            enabled = true;
        }

        private void Update()
        {
            if (SnapshotRuntime.Installed)
            {
                AttachLogSinkOnce();
                enabled = false;
                return;
            }

            _retries++;
            if (_retries > SnapshotBootstrap.MaxRetries)
            {
                Debug.LogWarning("[LuaErrorSnapshot] gave up waiting for LuaState: " +
                                 SnapshotRuntime.Status);
                enabled = false;
                return;
            }

            if (SnapshotRuntime.Install())
            {
                AttachLogSinkOnce();
                enabled = false;
            }
        }

        private void AttachLogSinkOnce()
        {
            if (_logSinkAttached || !SnapshotRuntime.Config.logToConsole)
            {
                return;
            }
            _logSinkAttached = true;
            Application.logMessageReceived += SnapshotLogSink.OnLogMessage;
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= SnapshotLogSink.OnLogMessage;
        }
    }

    /// <summary>
    /// 可选展示兜底。宿主原有错误链（LuaManager.LuaError -> error -> ErrorView.Show）
    /// 已会把带快照的错误文本上屏，故默认关闭；logToConsole 开启时镜像一份到 Console。
    /// </summary>
    internal static class SnapshotLogSink
    {
        private const string Marker = "--- Memory Frames";
        private const int MaxMirrorLen = 8 * 1024;

        internal static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }
            if (string.IsNullOrEmpty(condition) || condition.IndexOf(Marker, StringComparison.Ordinal) < 0)
            {
                return;
            }
            string text = condition;
            if (text.Length > MaxMirrorLen)
            {
                text = text.Substring(0, MaxMirrorLen) + "\n...<mirror truncated>";
            }
            // LogType.Log 不会再触发本回调，不会自激循环。
            Debug.Log("[LuaErrorSnapshot] captured error with stack snapshot:\n" + text);
        }
    }
}
