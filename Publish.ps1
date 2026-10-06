param([string]$AdbDirectory = '', [string]$OutputDirectory = 'publish')
$ErrorActionPreference = 'Stop'
if (!$AdbDirectory) {
    $adbCommand = Get-Command adb.exe -ErrorAction SilentlyContinue
    if (!$adbCommand) { throw 'ADB not found in PATH. Pass -AdbDirectory with your Android platform-tools directory.' }
    $AdbDirectory = Split-Path -Parent $adbCommand.Source
}
foreach ($name in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $AdbDirectory $name))) { throw "Missing $name in $AdbDirectory" }
}
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory)) }
dotnet publish (Join-Path $PSScriptRoot 'TclTvWake\TclTvWake.csproj') -c Release -r win-x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
New-Item (Join-Path $destination 'adb') -ItemType Directory -Force | Out-Null
foreach ($name in @('adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll')) {
    Copy-Item -LiteralPath (Join-Path $AdbDirectory $name) -Destination (Join-Path $destination 'adb') -Force
}
Write-Host "Published to $destination"
