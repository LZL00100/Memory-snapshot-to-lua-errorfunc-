using System;

namespace YourStudio.LuaErrorSnapshot
{
    /// <summary>
    /// 插件运行参数。全部有保守默认值，装了就能用，宿主无需任何配置。
    /// 需要覆盖时调用 SnapshotRuntime.Configure(new SnapshotConfig { ... })（可选，非必须）。
    /// </summary>
    [Serializable]
    public sealed class SnapshotConfig
    {
        /// <summary>总开关。false 时插件完全不安装，运行时零开销。</summary>
        public bool enabled = true;

        /// <summary>
        /// 只在开发构建 / 编辑器下采集快照。
        /// 插件不引用宿主的 Util.IsDebugMode()，用 Debug.isDebugBuild || Application.isEditor 近似；
        /// 需要精确对齐宿主开关时置 false 并由宿主自行 Configure。
        /// </summary>
        public bool developmentOnly = true;

        /// <summary>最多采集多少帧（最近几帧），与原实现一致默认 8。</summary>
        public int maxFrames = 8;

        /// <summary>table 递归展开层数。</summary>
        public int inspectDepth = 2;

        /// <summary>单个变量序列化后的最大字节数。</summary>
        public int maxValueLen = 2048;

        /// <summary>整段错误文本的最大字节数，与原实现一致默认 64KB。</summary>
        public int maxTotalLen = 64 * 1024;

        /// <summary>单次快照允许遍历的最大节点数（防大表拖垮 errfunc）。</summary>
        public int maxNodes = 600;

        /// <summary>单张表最多输出多少个键。</summary>
        public int maxTableKeys = 40;

        /// <summary>是否采集 locals。</summary>
        public bool dumpLocals = true;

        /// <summary>是否采集 upvalues。</summary>
        public bool dumpUpvalues = true;

        /// <summary>
        /// 限频间隔（秒）。默认 0 = 不限频，与原有功能行为一致；
        /// 错误风暴场景可设为 1.0 之类，间隔内只输出纯 traceback。
        /// </summary>
        public float minInterval = 0f;

        /// <summary>是否额外把带快照的错误文本镜像一份到 UnityEngine.Debug.LogError。</summary>
        public bool logToConsole = false;

        /// <summary>
        /// tolua_runtime 中 LUA_RIDX_CUSTOMTRACEBACK 的槽位号，官方 tolua.h 为 28。
        /// 宿主若使用改过该常量的定制 tolua，改这里即可，无需改代码。
        /// </summary>
        public int customTracebackSlot = 28;

        public SnapshotConfig Clone()
        {
            return (SnapshotConfig)MemberwiseClone();
        }
    }
}
