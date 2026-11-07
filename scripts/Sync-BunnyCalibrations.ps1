param(
    [string]$SettingsPath = (Join-Path $env:APPDATA 'XIVLauncherCN/pluginConfigs/ULKHelper/settings.json'),
    [string]$PointsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Bunnies/points.json')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $SettingsPath)) { return }
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$points = @(Get-Content -LiteralPath $PointsPath -Raw | ConvertFrom-Json)
$changed = 0
foreach ($saved in @($settings.'bunny.calibratedPoints')) {
    if ($null -eq $saved -or $saved.Territory -notin @(763,795,827) -or !$saved.Id -or $null -eq $saved.Height -or ([double]::IsNaN([double]$saved.Height) -or [double]::IsInfinity([double]$saved.Height)) -or ([double]::IsNaN([double]$saved.X) -or [double]::IsInfinity([double]$saved.X)) -or ([double]::IsNaN([double]$saved.Y) -or [double]::IsInfinity([double]$saved.Y)) -or $saved.X -lt 1 -or $saved.X -gt 42 -or $saved.Y -lt 1 -or $saved.Y -gt 42) { continue }
    $index = -1
    for ($i = 0; $i -lt $points.Count; $i++) {
        if ($points[$i].Territory -eq $saved.Territory -and $points[$i].Id -eq $saved.Id) { $index = $i; break }
    }
    if ($index -lt 0 -and !$saved.Id.StartsWith('local-')) { continue }
    if ($index -ge 0 -and $points[$index].Exact -and $points[$index].ObservedAt -and $saved.ObservedAt -and [DateTimeOffset]$points[$index].ObservedAt -gt [DateTimeOffset]$saved.ObservedAt) { continue }
    $point = [ordered]@{ Territory=$saved.Territory; Id=$saved.Id; X=$saved.X; Y=$saved.Y; Height=$saved.Height; Exact=$true; Origin=0 }
    if ($saved.ObservedAt) { $point['ObservedAt'] = $saved.ObservedAt }
    if ($index -ge 0) {
        if (($point | ConvertTo-Json -Compress) -eq ($points[$index] | ConvertTo-Json -Compress)) { continue }
        $points[$index] = $point
    } else { $points += ,$point }
    $changed++
}
if ($changed -gt 0) {
    [IO.File]::WriteAllText($PointsPath, ($points | ConvertTo-Json -Depth 8) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
Write-Output "已合并 $changed 个实挖精确点位；点位库共 $($points.Count) 个。"
