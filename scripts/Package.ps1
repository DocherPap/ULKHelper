param(
    [string]$DalamudLibPath = $env:DALAMUD_HOME,
    [string]$Repository = 'DocherPap/ULKHelper',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ((Get-Content (Join-Path $root 'ULKHelper.csproj') -Raw) -match '<Version>[^<]*-debug') {
    throw '调试分支不能生成正式库链或覆盖0.1.0发布包。请使用 scripts/Package-Debug.ps1。'
}
if (Test-Path (Join-Path $root '.git')) {
    $currentBranch = & git -C $root branch --show-current
    if ($currentBranch -eq 'codex/solo-debug-tests') { throw '调试分支不能生成正式库链或覆盖0.1.0发布包。请使用 scripts/Package-Debug.ps1。' }
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository 应为 owner/repo。' }
if (-not $SkipBuild) {
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
$version = $identity.Version.ToString()
$tag = 'v' + (($version.Split('.')[0..2]) -join '.')
$dist = Join-Path $root "dist/$tag"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zipName = "ULKHelper-$version.zip"
$zip = Join-Path $dist $zipName
# Explicit allowlist: no game files, caches, debug symbols or Dalamud runtime DLLs.
$files = @($dll, $manifestFile, (Join-Path $root 'README.md'), (Join-Path $root 'ThirdPartyNotices'))
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $zipName`n", [Text.UTF8Encoding]::new($false))
$entry = $manifest | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$download = "https://github.com/$Repository/releases/download/$tag/$zipName"
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
Write-Output "已生成 $zip、SHA256 和 ULKHelper 独立 repo.json。尚未上传或发布。"
