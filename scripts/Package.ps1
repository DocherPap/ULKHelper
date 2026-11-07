param(
    [string]$DalamudLibPath = $env:DALAMUD_HOME,
    [string]$Repository = 'DocherPap/ULKHelper',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$currentBranch = & git -C $root branch --show-current
if ($LASTEXITCODE -ne 0 -or $currentBranch -ne 'main') { throw '正式打包只允许在 main 分支运行。' }
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository 应为 owner/repo。' }
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Sync-BunnyCalibrations.ps1')
    $buildArgs = @('build', (Join-Path $root 'ULKHelper.csproj'), '-c', 'Release', '--nologo')
    if ($DalamudLibPath) { $buildArgs += "-p:DalamudLibPath=$DalamudLibPath" }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Release 构建失败。' }
}
$manifestFile = Join-Path $root 'ULKHelper.json'
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
$dll = Join-Path $root 'bin/Release/net10.0-windows/ULKHelper.dll'
$identity = [Reflection.AssemblyName]::GetAssemblyName($dll)
if ($identity.Name -ne 'ULKHelper' -or $manifest.InternalName -ne 'ULKHelper' -or $identity.Version.ToString() -ne $manifest.AssemblyVersion) { throw 'DLL 与清单名称或版本不一致。' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -match '-(?:testing|debug)') {
    throw '检测到测试分支的 DLL。请在 main 重新生成 Release 构建。'
}
$version = $identity.Version.ToString()
$tag = 'v' + (($version.Split('.')[0..2]) -join '.')
$dist = Join-Path $root "dist/$tag"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zipName = "ULKHelper-$version.zip"
$zip = Join-Path $dist $zipName
# Explicit allowlist: no game files, caches, debug symbols or Dalamud runtime DLLs.
$bridge = Join-Path (Split-Path -Parent $dll) 'bridge.py'
$files = @($dll, $manifestFile, $bridge, (Join-Path $root 'README.md'), (Join-Path $root 'CHANGELOG.md'), (Join-Path $root 'ThirdPartyNotices'), (Join-Path $root 'DiscordBridge'))
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $zipName`n", [Text.UTF8Encoding]::new($false))
$entry = $manifest | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$download = "https://raw.githubusercontent.com/$Repository/main/dist/$tag/$zipName"
foreach ($pair in @{
    RepoUrl = "https://github.com/$Repository"
    DownloadLinkInstall = $download
    DownloadLinkUpdate = $download
    IsHide = $false
    IsTestingExclusive = $false
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}.GetEnumerator()) { $entry | Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force }
$utf8 = [Text.UTF8Encoding]::new($false)
$standalone = (ConvertTo-Json -InputObject @($entry) -Depth 20) + "`n"
[IO.File]::WriteAllText((Join-Path $root 'repo.json'), $standalone, $utf8)
[IO.File]::WriteAllText((Join-Path $dist 'repo.json'), $standalone, $utf8)
Write-Output "已生成 $zip、SHA256 和 ULKHelper 独立 repo.json。将新版 dist 目录和根目录 repo.json 一起提交并推送到 main。"
