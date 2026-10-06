# CEC alternative for PC / Google TV using ADB

[Русская версия](README.ru.md)

A Windows C# service that coordinates an Android / Google TV with its use as a PC monitor, using ADB instead of HDMI-CEC. Developed and tested with a TCL 55C825.

**Code author: ChatGPT (OpenAI).** Sasha2dx provided requirements, hardware access and real-device testing.

## Behavior

“Active” means the TV participates in the Windows desktop configuration, not that its panel is powered on. All actions target the display and TV input specified in configuration (HDMI1 in the supplied example).

| Scenario | Action |
| --- | --- |
| Service starts, including before user sign-in | If the TV is an active Windows display, connect through ADB, wake it, confirm Awake / ON and select the configured HDMI input. |
| PC resumes from sleep | Run the same wake-and-select sequence. |
| Display changes from inactive to active | Wake the TV and select the configured HDMI input. |
| Display changes from active to inactive | If the TV is Awake / ON and its current main session is the configured input, send Home. Recheck that the display is still inactive before sending. |
| PC enters sleep | If the display is active and the TV is Awake / ON on the configured input, send Home. Total budget: 1.5 seconds. |
| PC shuts down or restarts | Perform the same conditional Home action during PRESHUTDOWN, with a 10-second budget. |
| Service is manually stopped or restarted | Stopping does not send Home. Starting runs the normal startup sequence. |
| TV shows another input or Google TV Home | Conditional Home is skipped. Startup/resume/activation still select the configured input when the display is active. |

The TV is never powered off. Home uses `KEYCODE_HOME`; waking uses `KEYCODE_WAKEUP`.

The selected input is determined by matching `mainSessionToken` to `sessionToken` in the current user's `dumpsys tv_input` session map and reading that session's `inputId`. Searching the entire dump for an input ID is insufficient because it also lists all available inputs. Unknown, ambiguous or incomplete session data causes Home to be skipped.

Display observation uses a hidden console-session window, `WM_DISPLAYCHANGE` / `WM_DEVICECHANGE`, a 400 ms settling delay and a 3-second fallback check. The first reading establishes a baseline. Repeated unchanged readings do nothing; read errors are not treated as disconnection. Reversing a transition cancels its pending action. Observation resets across sleep/resume.

## Requirements

- Windows x64; administrative rights to install/manage the service.
- Stable .NET 9 SDK, version 9.0.305 or a newer 9.0 feature band (`global.json`), to build. Published binaries include the runtime.
- Android SDK Platform-Tools for Windows: `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`.
- An Android / Google TV that permits authorized network ADB and remains reachable in standby. Enable network standby / quick start if required by your TV.
- A stable TV network address and HDMI connection to the PC.

This is tested on a TCL 55C825. Other TVs may expose different input URIs or different diagnostic output; configure and verify them before relying on automation.

## Build from source

```powershell
git clone git@github.com:Sasha2dx/CEC-alternative-for-PC-Google-TV-using-ADB.git
cd CEC-alternative-for-PC-Google-TV-using-ADB
dotnet build TCL-ADB-CEC.sln -c Release
dotnet run --project Tests\Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publish.ps1 -AdbDirectory 'C:\Android\platform-tools' -OutputDirectory publish
```

Replace the Platform-Tools path with yours. `-AdbDirectory` can be omitted when `adb.exe` is in PATH. Publishing creates a self-contained `win-x64` executable, copies configuration and the three ADB files into `publish`. ADB keys and build outputs are ignored by Git. `-ExecutionPolicy Bypass` applies to this PowerShell process only.

## Configure your TV

Edit `publish\appsettings.json` **before installation**. `TclTvWake\appsettings.json` is a working TCL example, not universal settings; customize it before publishing if you want your settings copied automatically.

| Setting | Meaning |
| --- | --- |
| `Endpoint` | TV ADB address, including port, such as `192.168.1.95:5555`. |
| `MonitorId` | Windows monitor ID prefix, such as `MONITOR\XXX9615`; JSON requires doubled backslashes. |
| `HdmiUri` | Full passthrough URI for the desired TV input. |
| `AdbPath` | ADB executable path; relative paths resolve from the application directory. Default: `adb/adb.exe`. |
| `AdbServerPort` | Dedicated local ADB server port. Default: 5038; keep it separate from the usual 5037. |
| `CommandTimeoutSeconds` | Per-command ADB timeout. Default: 15; Home also has a shorter overall deadline. |
| `Attempts` | Maximum startup/resume/activation attempts. Default: 12. |
| `RetrySeconds` | Pause between attempts. Default: 5. |
| `WakeDelaySeconds` | Delay after waking before checking power. Default: 3. |

Discover the Windows monitor ID:

```powershell
.\publish\TclTvWake.exe --list-displays
```

Authorize ADB using your own TV address, confirm the prompt with its remote (allow future connections if offered), then select the intended HDMI input with the remote:

```powershell
adb connect 192.168.1.95:5555
adb -s 192.168.1.95:5555 shell dumpsys tv_input
```

Find the current main session's `inputId`. URL-encode that ID and append it to `content://android.media.tv/passthrough/` to obtain `HdmiUri`. The supplied configuration contains the tested TCL HDMI1 URI. Address, monitor ID and input URI are required in configuration; production C# code has no TV-specific fallback values.

## Install and run

From an **administrator PowerShell**, in the repository directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Service.ps1 -PublishDirectory publish
```

The script copies the application to `%ProgramFiles%\TclTvWake`, installs/updates the automatic `TclTvWake` LocalSystem service, configures recovery and starts it immediately. Starting may wake the TV and select the configured input.

It copies the authorized private ADB key from `%USERPROFILE%\.android\adbkey`. If using a different administrator account, pass `-AdbKey 'C:\Users\YourUser\.android\adbkey'`. Never commit or share the private key. The service's key may need a one-time confirmation on the TV. Application/key and data directories are restricted to SYSTEM and administrators.

Session 0 cannot reliably inspect the physical console's displays. The service therefore launches short-lived, invisible helpers using the console session's SYSTEM winlogon token. No user password or startup shortcut is needed.

After installation, edit `%ProgramFiles%\TclTvWake\appsettings.json` from an elevated editor and restart the service. Updating from a publish folder replaces the installed settings, so preserve custom settings in that folder first.

```powershell
Get-Service TclTvWake
Restart-Service TclTvWake
Get-Content "$env:ProgramData\TclTvWake\service.log" -Tail 30
```

Manual commands (from an elevated shell for the installed application):

```powershell
& "$env:ProgramFiles\TclTvWake\TclTvWake.exe" --once
& "$env:ProgramFiles\TclTvWake\TclTvWake.exe" --home-if-hdmi1
& "$env:ProgramFiles\TclTvWake\TclTvWake.exe" --home-sleep-budget
```

`--once` runs the wake/input sequence. The Home commands require an active Windows display, with 10-second and 1.5-second deadlines respectively. Exit code 0 means the action succeeded; 1 means skipped/failed. For manual use of a published copy, place an authorized `adbkey` in its `keys` directory unless its dedicated ADB server is already running with that key. `--service` is for Service Control Manager; `--probe` and `--watch-change` are internal helper modes.

## Verification and limitations

Test startup before sign-in, sleep/resume, shutdown, both display transitions, inactive-display protection, and skipping Home when another input is selected. Those core scenarios were exercised on the TCL setup; separate shutdown-on-HDMI2 and display-deactivation-on-HDMI2 checks are not recorded in the provided test logs. Automated tests cover input parsing, transition tracking and configuration validation.

- Sleep networking can disappear before the 1.5-second Home attempt finishes. Timeouts/errors skip the action; sleep is not vetoed. Added display checks have completed close to the deadline in real tests.
- Network/ADB recovery can delay wake/input selection. Bounded retries handle `offline`, closed connections and temporary unavailability.
- Windows may already mark a display inactive during sleep/shutdown, in which case Home is skipped. Physical connection is inferred from Windows display state, which can lag behind cable removal.
- Input checking and command delivery are not atomic; using the TV remote during the check can race the action.
- `Activity not started ... brought to the front` can be normal when the TV activity is already running. Confirm power and the delivered intent in the log.
- The log is `%ProgramData%\TclTvWake\service.log`, approximately 2 MB plus one rotated file, readable by administrators.

## Update, rollback and uninstall

Publish to another folder (for example `-OutputDirectory publish-v2`) and install it with `-PublishDirectory publish-v2`. Keep a known-good published folder for rollback, then install that folder to revert. Git history also preserves the earlier versions.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-Service.ps1
```

Run uninstall as administrator. It removes the service and stops its configured ADB server. Application files, configuration, keys and logs remain for recovery.
