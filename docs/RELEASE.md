# 发布与卫月更新

正式版使用 `main`，本机调试使用 `codex/testing`。0.4.1 的程序集和清单版本为 `0.4.1.0`，适用国服卫月 API 15。

1. 在 `main` 更新 `ULKHelper.csproj` 的版本与程序集版本，以及 `ULKHelper.json` 的程序集版本和更新说明。
2. 运行 `./scripts/Package.ps1`。如需指定本机卫月依赖目录，传 `-DalamudLibPath`。输出包含安装 ZIP、SHA256 和独立库链副本，本次在 `dist/v0.4.1/`。
3. 将版本文件、根目录 `repo.json` 和新版 `dist` 子目录一起提交并推送到 `main`。卫月直接从仓库的 `dist` 下载安装 ZIP。

推送后可分别检查根目录清单与清单中的安装链接。用户继续使用原来的独立库链。 [在线安装库链](https://raw.githubusercontent.com/DocherPap/ULKHelper/main/repo.json)
