param([string]$Python = 'py')

$ErrorActionPreference = 'Stop'
$owner = Read-Host 'Discord 用户 ID'
if ($owner -notmatch '^\d{17,20}$') { throw '请输入 Discord 用户 ID（数字），可在开发者模式下复制。' }

$token = Read-Host 'Discord Bot Token' -AsSecureString
if ($token.Length -eq 0) { throw '请输入 Bot Token。' }

$deps = Join-Path $PSScriptRoot '../bin/discord-python'
$previousPythonPath = $env:PYTHONPATH
try {
    if (Test-Path -LiteralPath $deps) {
        $env:PYTHONPATH = if ($previousPythonPath) { "$deps;$previousPythonPath" } else { $deps }
    }
    $env:ULK_DISCORD_OWNER_ID = $owner
    $env:ULK_DISCORD_TOKEN = [System.Net.NetworkCredential]::new('', $token).Password
    & $Python (Join-Path $PSScriptRoot 'bridge.py')
    if ($LASTEXITCODE) { throw "Discord 桥接退出（$LASTEXITCODE）。" }
}
finally {
    Remove-Item Env:ULK_DISCORD_TOKEN,Env:ULK_DISCORD_OWNER_ID -ErrorAction SilentlyContinue
    $env:PYTHONPATH = $previousPythonPath
    $token.Dispose()
}
