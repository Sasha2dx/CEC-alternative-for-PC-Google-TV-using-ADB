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
if (Test-Path -LiteralPath $adb) { & $adb -P 5038 kill-server }
Write-Host 'Service removed. Application files, configuration, key and logs retained.'
