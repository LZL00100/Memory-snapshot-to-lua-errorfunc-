# 作为独立 git 仓库发布

`Packages/com.yourstudio.lua-error-snapshot/` 本身已是合规 UPM 包根：
`package.json` 位于目录顶层，源码按 `Runtime/` 与 `Editor/` + asmdef 拆分。
UPM git 包要求 package.json 在仓库根（或用 `#path=` 指定子目录），
因此**直接把该目录作为仓库根**即可，无需重组目录。

## 仓库根结构

```
com.yourstudio.lua-error-snapshot/     <- git 仓库根
├── package.json                       <- 必须位于根
├── README.md
├── CHANGELOG.md
├── LICENSE.md
├── Runtime/
│   ├── YourStudio.LuaErrorSnapshot.asmdef
│   ├── AssemblyInfo.cs
│   ├── SnapshotConfig.cs
│   ├── SnapshotRuntime.cs
│   ├── SnapshotBootstrap.cs
│   ├── LuaStateBridge.cs
│   └── EmbeddedLua.cs
├── Editor/
│   ├── YourStudio.LuaErrorSnapshot.Editor.asmdef
│   ├── SnapshotEditorPrefs.cs
│   └── SnapshotMenu.cs
└── Samples~/                         <- ~ 结尾，Unity 忽略、不生成 .meta
```

## 发布步骤

```bash
git init
git add .
git commit -m "Lua Error Snapshot v1.0.0"
git tag v1.0.0
git remote add origin https://github.com/LZL00100/Memory-snapshot-to-lua-errorfunc-.git
git push -u origin main --tags
```

## 宿主侧接入

`Packages/manifest.json` 加一行（唯一需要修改的宿主文件，可随版本管理）：

```json
{
  "dependencies": {
    "com.yourstudio.lua-error-snapshot": "https://github.com/LZL00100/Memory-snapshot-to-lua-errorfunc-.git#v1.0.0"
  }
}
```

版本锁定建议用 tag（`#v1.0.0`）或 commit hash，避免 branch 漂移。

## 若仓库中包位于子目录

若必须把包放在仓库子目录（例如 monorepo），用 `#path=`：

```json
"com.yourstudio.lua-error-snapshot": "https://github.com/<org>/<mono>.git?path=Packages/com.yourstudio.lua-error-snapshot#v1.0.0"
```

## 与 embedded 形态的差异

| 形态 | 宿主改动 | 版本管理 | 卸载 |
|---|---|---|---|
| embedded（拷进 `Packages/`） | 无需改 manifest | 随宿主 VCS | 删目录即净 |
| git URL | manifest 加一行 | tag / commit 锁定 | 删该行 |

两者共用同一份代码与 `package.json`，不需要维护两套源码。
