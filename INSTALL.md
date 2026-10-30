# Install, build and package — LYGO Browser 1.9.1

## Route A — prebuilt installer

1. Get **Browser 1.9.1** from the LYGO Skill Hub.
2. Verify the published hash.
3. Run the installer. It registers the browser as a **candidate** and offers "make default" — Windows
   still requires your click to change the default browser; nothing forces it.
4. Launch it. Profile data is created under `%LOCALAPPDATA%\LYGO\Browser`.

## Route B — from source

### Prerequisites

| Need | Detail |
|---|---|
| OS | Windows 10/11 x64 |
| SDK | **.NET 9** (`dotnet --version` → 9.x) |
| Runtime | WebView2 Evergreen |
| Python 3.13 | only for `tools\selftest.py` and `tools\quality_gate.py` |

### Build and run

```powershell
git clone <this repo> lygo-browser
cd lygo-browser

# ordinary local build (debug symbols, your paths - fine for your own machine)
dotnet build src\LygoBrowser\LygoBrowser.csproj -c Release

# publish into app\ so the launcher finds it
dotnet publish src\LygoBrowser\LygoBrowser.csproj -c Release -o app

.\"LYGO Browser.bat"
```

The `.bat` starts `app\LygoBrowser.exe`, so `app\` must exist before the launcher will work.

### Publish a PUBLIC build — use the script

```powershell
pwsh -File tools\public\build_public_browser.ps1
```

That is the only supported way to produce a distributable package. It:

1. publishes with `-p:PublicBuild=true` (PathMap + deterministic build, no builder path in the DLL),
2. stages the tree and asserts every expected file arrived,
3. runs `tools\public\sterilize.ps1`, which scrubs `<INSTALL_DIR>` placeholders and then **fails**
   on any secret or developer path — text **and** binary.

A plain `dotnet publish` or a re-run of `sterilize.ps1` alone is **not** enough: both will happily
ship a `LygoBrowser.dll` whose CodeView record contains `C:\Users\<you>\...`. `-p:DebugType=none`
does not remove it either — only `PathMap` does.

### Verify

```powershell
# 22 checks; --quick skips the 5 network/slow ones
C:\Python313\python.exe tools\selftest.py --quick
C:\Python313\python.exe tools\selftest.py

# static gate
C:\Python313\python.exe tools\quality_gate.py
```

Healthy output: `SELFTEST PASS 17 passed · 0 failed · 5 skipped` (`--quick`) and
`quality_gate: PASS`.

### Ports

| Port | Service |
|---|---|
| 9337 | local HTTP API / agent bridge |
| 9333 | CDP (Playwright, browser-use) |

Both bind loopback. Do not expose either on a network interface.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Window opens to a blank page | WebView2 Runtime missing | install the Evergreen Runtime |
| `app\LygoBrowser.exe` not found | never published | run the `dotnet publish ... -o app` step |
| Port 9337 in use | another instance (single-instance forwarding should prevent this) | check `Get-NetTCPConnection -LocalPort 9337` |
| Playwright can't connect on 9333 | browser started without the CDP listener | start it normally; the listener comes up with the app |
| Download kills the app under automation | known defect — WebView2 terminates natively on CDP-attached downloads | call `POST /api/agent/open` instead |
| Logins lost after a restart | the process was force-killed | close the window normally; cookies flush on graceful shutdown |
| `selftest.py` exits 1 with a traceback and no `[FAIL]` | console encoding | set `PYTHONIOENCODING=utf-8`; current versions force UTF-8 themselves |

## Uninstall

Close the browser normally (not via `Stop-Process`), delete the install folder, and optionally delete
`%LOCALAPPDATA%\LYGO\Browser` — that is where your profile, Bible and vault live. Removing the folder
removes your data; there is no cloud copy.
