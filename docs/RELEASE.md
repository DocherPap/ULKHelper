# 0.1.0 上传与发布

当前安装包为 `dist/v0.1.0/ULKHelper-0.1.0.0.zip`，同目录有SHA256、独立repo.json与可选MakoMaster合并库链。DLL和清单版本0.1.0.0，Release标签v0.1.0，下载目标DocherPap/ULKHelper。发布说明见release/NOTES-v0.1.0.md。已完成Debug/Release构建及ZIP内容检查，尚未上传。

按用户要求先打包再继续调试：此包自动化第四页暂不可用，没有第五测试页。main保存发布快照；恢复自动化和后续单人测试工具只在codex/solo-debug-tests，不提交/合并main。不要用后续调试构建覆盖此发布ZIP或main库链。

## 旧版0.0.1发布记录

本次仅准备本地文件，不会上传 GitHub。显示版本为 **0.0.1**，Dalamud/DLL 四段版本为 **0.0.1.0**，API 15，Windows x64。

## 已生成的文件

`dist/v0.0.1/` 包含：

- `ULKHelper-0.0.1.0.zip`：安装包，根目录有 DLL、插件清单、README，以及 ThirdPartyNotices 目录。
- `ULKHelper-0.0.1.0.zip.sha256`：安装包校验值。
- `repo.json`：仅包含 ULKHelper 的独立库链。
- `MakoMaster.repo.merged.json`：保留现有 MakoMaster 条目，并追加 ULKHelper 的共用库链。

默认目标仓库为 `DocherPap/ULKHelper`，标签 `v0.0.1`。作者沿用 MakoMaster 的 DocherPap。若仓库名称不同，修改 `ULKHelper.json` 的 RepoUrl，并运行 `./scripts/Package.ps1 -Repository 'owner/repo'` 重新生成库链。

## 上传步骤

1. 建立 GitHub 仓库 `DocherPap/ULKHelper`，上传本项目源码。`.gitignore` 排除了 bin、obj、artifacts 和 dist，发布包需要下一步单独上传。根目录 `repo.json` 和 `scripts/Package.ps1` 应一并提交。
2. 创建标签 **v0.0.1** 的 GitHub Release，附件上传 `ULKHelper-0.0.1.0.zip` 及 `.sha256`。发布说明可使用 `release/NOTES-v0.0.1.md`。
3. 确认以下地址可以下载后再更新 MakoMaster 的库链：
   `https://github.com/DocherPap/ULKHelper/releases/download/v0.0.1/ULKHelper-0.0.1.0.zip`
4. 将 `MakoMaster.repo.merged.json` 的内容放入 **MakoMaster 仓库 main 分支根目录的 repo.json**。如果 MakoMaster 在此期间有更新，应先下载最新 repo.json，再运行 `./scripts/Package.ps1 -SkipBuild -ExistingRepo '最新repo.json的本地路径'`，使用新生成的合并文件，避免覆盖其新版本。
5. 原有用户继续使用 `https://raw.githubusercontent.com/DocherPap/MakoMaster/main/repo.json`，刷新后即可看到两款插件。新项目也可单独使用 `https://raw.githubusercontent.com/DocherPap/ULKHelper/main/repo.json`。两条库链择一即可。

仅上传 ZIP 或仅上传源码不会更新原库链；必须完成第 4 步。仓库 JSON 是插件条目的数组，不能把另一个 repo.json 的 URL 当作条目嵌套。

MakoMaster 现有打包脚本会重新写入只有自身条目的 repo.json。以后发布 MakoMaster 时，也要保留/重新合并 ULKHelper 条目；本次仅准备合并文件，没有修改另一个项目的打包脚本。

本次合并基线读取自线上 MakoMaster `repo.json`（版本 2.1.18.0，blob SHA `04776e775ee9c510dcdf2614cb0b53f271246fbb`），副本在 `release/MakoMaster.repo.snapshot.json`。未修改本机 MakoMaster 项目。

## 本地重新打包

```powershell
./scripts/Package.ps1
# 可指定依赖与最新共用库链
./scripts/Package.ps1 -DalamudLibPath 'C:/path/to/Dalamud' -ExistingRepo 'C:/path/to/latest-repo.json'
```

脚本核对清单和程序集身份/版本，只打包允许的插件文件，不包含卫月依赖、游戏地图文件、库存缓存、PDB 或离线测试工具。当前已完成离线验证和构建；游戏内加载、真实库存、地图链接/旗标及通知仍需实测。
