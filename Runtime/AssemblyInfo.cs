using UnityEngine.Scripting;

// 防止 IL2CPP / 代码裁剪把「没有被任何程序集显式引用」的插件程序集整个裁掉，
// 从而保证 [RuntimeInitializeOnLoadMethod] 在真机上依然会被执行。
[assembly: AlwaysLinkAssembly]
