# 0.2.1 发布

正式版使用标签 `v0.2.1`，程序集和清单版本为 `0.2.1.0`，适用国服卫月 API 15。正式分支为 `main`，本机测试使用 `codex/testing`，调试页、记录和离线工具集中在测试分支。

在 main 运行 `./scripts/Package.ps1`。如需指定本机依赖目录，可传 `-DalamudLibPath`。输出在 `dist/v0.2.1/`，包含安装 ZIP、SHA256 和独立 `repo.json`。脚本同步更新仓库根目录的 `repo.json`，仓库暂无自动上传流程。

1. 在本地提交正式版改动，创建并推送指向该提交的 `v0.2.1` 标签。
2. 在 DocherPap/ULKHelper 发布该标签的 Release，上传 `ULKHelper-0.2.1.0.zip` 和同名 `.sha256`。确认 ZIP 可下载后再推送包含新 `repo.json` 的 main。
3. 用户在卫月订阅 ULKHelper 的独立库链并刷新插件列表。 [在线安装库链](https://raw.githubusercontent.com/DocherPap/ULKHelper/main/repo.json)
