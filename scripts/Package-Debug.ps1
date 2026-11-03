param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ((Get-Content (Join-Path $root 'ULKHelper.csproj') -Raw) -notmatch '<Version>[^<]*-debug') { throw '此脚本仅用于独立调试构建。' }
if (-not $SkipBuild) {
    & dotnet build (Join-Path $root 'ULKHelper.csproj') -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw '调试构建失败。' }
}
$out = Join-Path $root 'dist/debug'
$debugDll = Join-Path $root 'bin/Debug/net10.0-windows/ULKHelper.dll'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($debugDll).ProductVersion -notlike '*-debug*') { throw 'DLL不是调试分支构建，请先重新编译。' }
New-Item -ItemType Directory -Path $out -Force | Out-Null
$zip = Join-Path $out 'ULKHelper-0.1.0.0-solo-debug.zip'
$files = @($debugDll, (Join-Path $root 'ULKHelper.json'),
    (Join-Path $root 'README.md'), (Join-Path $root 'ThirdPartyNotices'))
Compress-Archive -LiteralPath $files -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Output "已生成独立单人调试包：$zip；不修改正式包或repo.json。"
