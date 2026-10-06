#Requires -RunAsAdministrator
param([string]$AdbKey = (Join-Path $env:USERPROFILE '.android\adbkey'), [string]$PublishDirectory = 'publish')
$ErrorActionPreference = 'Stop'
$source = if ([IO.Path]::IsPathRooted($PublishDirectory)) { [IO.Path]::GetFullPath($PublishDirectory) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PublishDirectory)) }
$target = Join-Path $env:ProgramFiles 'TclTvWake'
$data = Join-Path $env:ProgramData 'TclTvWake'
if (!(Test-Path -LiteralPath (Join-Path $source 'TclTvWake.exe'))) { throw 'Run Publish.ps1 first.' }
if (!(Test-Path -LiteralPath $AdbKey)) { throw 'Authorized ADB key not found. Pass -AdbKey with its path.' }
$existing = Get-Service TclTvWake -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') { Stop-Service TclTvWake; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
$installedAdb = Join-Path $target 'adb\adb.exe'
if (Test-Path -LiteralPath $installedAdb) {
    $installedConfig = Get-Content -LiteralPath (Join-Path $target 'appsettings.json') -Raw | ConvertFrom-Json
    $installedPort = if ($installedConfig.AdbServerPort) { $installedConfig.AdbServerPort } else { 5038 }
    # An absent server is normal during a repeated installation attempt.
    $cleanupPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $installedAdb -P $installedPort kill-server 2>&1 | ForEach-Object { Write-Host "ADB cleanup: $_" }
    } finally { $ErrorActionPreference = $cleanupPreference }
}
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
if ($existing) {
    # Use a structured API: Windows PowerShell 5.1 strips embedded quotes
    # from native arguments, breaking sc.exe config with a Program Files path.
    $serviceConfig = Get-CimInstance Win32_Service -Filter "Name='TclTvWake'"
    $changed = Invoke-CimMethod -InputObject $serviceConfig -MethodName Change -Arguments @{
        PathName = $binary
        StartMode = 'Automatic'
        StartName = 'LocalSystem'
    }
    if ($changed.ReturnValue -ne 0) { throw "Service configuration failed: Win32_Service.Change returned $($changed.ReturnValue)" }
}
else { New-Service -Name TclTvWake -BinaryPathName $binary -DisplayName 'TCL TV Wake (ADB HDMI1)' -StartupType Automatic | Out-Null }
& sc.exe description TclTvWake 'Controls TCL TV at startup, resume, suspend and shutdown.'
if ($LASTEXITCODE -ne 0) { throw "Service description failed: sc.exe exit $LASTEXITCODE" }
& sc.exe failure TclTvWake reset= 86400 actions= restart/10000/restart/30000/restart/60000
if ($LASTEXITCODE -ne 0) { throw "Service recovery configuration failed: sc.exe exit $LASTEXITCODE" }
Start-Service TclTvWake
Get-Service TclTvWake
Write-Host "Log: $data\service.log"
