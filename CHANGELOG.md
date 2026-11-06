# Changelog

## 1.0.0

- 首个零侵入版本。
- 通过替换 ToLua registry 的 `LUA_RIDX_CUSTOMTRACEBACK`(=28) 槽安装 errfunc，
  在栈展开前采集最近 8 帧 locals / upvalues；宿主源码零改动。
- 错误文本统一 UTF-8 合法化，规避 `Marshal.PtrToStringAnsi` 的
  `Illegal byte sequence` 异常。
- 自带预算化序列化、敏感字段脱敏、防重入与可选限频。
- embedded 与 UPM git URL 两种安装形态。
