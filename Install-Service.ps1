#Requires -RunAsAdministrator
param([string]$AdbKey = (Join-Path $env:USERPROFILE '.android\adbkey'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'publish'
$target = Join-Path $env:ProgramFiles 'TclTvWake'
$data = Join-Path $env:ProgramData 'TclTvWake'
if (!(Test-Path -LiteralPath (Join-Path $source 'TclTvWake.exe'))) { throw 'Run Publish.ps1 first.' }
if (!(Test-Path -LiteralPath $AdbKey)) { throw 'Authorized ADB key not found. Pass -AdbKey with its path.' }
$existing = Get-Service TclTvWake -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service TclTvWake; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
$installedAdb = Join-Path $target 'adb\adb.exe'
if (Test-Path -LiteralPath $installedAdb) { & $installedAdb -P 5038 kill-server }
New-Item $target -ItemType Directory -Force | Out-Null
New-Item $data -ItemType Directory -Force | Out-Null
# Service executable, settings and ADB private key must not be writable by ordinary users.
foreach ($folder in @($target, $data)) {
    & icacls.exe $folder /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot secure $folder" }
    & icacls.exe $folder /setowner '*S-1-5-32-544' /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot set administrator ownership on $folder" }
}
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
New-Item (Join-Path $target 'keys') -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath $AdbKey -Destination (Join-Path $target 'keys\adbkey') -Force
$binary = '"' + (Join-Path $target 'TclTvWake.exe') + '" --service'
if ($existing) { & sc.exe config TclTvWake binPath= $binary start= auto obj= LocalSystem | Out-Null }
else { New-Service -Name TclTvWake -BinaryPathName $binary -DisplayName 'TCL TV Wake (ADB HDMI1)' -StartupType Automatic | Out-Null }
if ($existing -and $LASTEXITCODE -ne 0) { throw 'Service configuration failed' }
& sc.exe description TclTvWake 'Wakes the active TCL display and selects HDMI1 at startup and resume.' | Out-Null
& sc.exe failure TclTvWake reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
Start-Service TclTvWake
Get-Service TclTvWake
Write-Host "Log: $data\service.log"
