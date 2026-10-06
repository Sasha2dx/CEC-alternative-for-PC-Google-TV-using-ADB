#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$service = Get-Service TclTvWake -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service TclTvWake
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    & sc.exe delete TclTvWake
    if ($LASTEXITCODE -ne 0) { throw 'Service removal failed' }
}
$adb = Join-Path $env:ProgramFiles 'TclTvWake\adb\adb.exe'
if (Test-Path -LiteralPath $adb) {
    $installedConfig = Get-Content -LiteralPath (Join-Path $env:ProgramFiles 'TclTvWake\appsettings.json') -Raw | ConvertFrom-Json
    $installedPort = if ($installedConfig.AdbServerPort) { $installedConfig.AdbServerPort } else { 5038 }
    & $adb -P $installedPort kill-server
}
Write-Host 'Service removed. Application files, configuration, key and logs retained.'
