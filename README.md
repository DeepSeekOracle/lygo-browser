# LYGO Browser — v1.9.1 Public Beta

A branded agent-and-human browser: WPF + WebView2 on .NET 9, with a bookmark brain, an encrypted
vault, a page archive and a screenshot vault. It is a real browser you can use daily **and** an
addressable target an agent can drive over CDP.

**This tag is the first Public Beta of the working system.**

- **Prebuilt installer:** LYGO Skill Hub → Browser 1.9.1
- **From source:** [`INSTALL.md`](INSTALL.md)
- **License:** LYGO Fair Usage v1.9.1 — [`LICENSE.md`](LICENSE.md)

---

## What it does

| Surface | Port | Purpose |
|---|---|---|
| Local HTTP API | **9337** | agent bridge: open a URL, read the page, search the Bible |
| CDP endpoint | **9333** | Playwright / browser-use / any CDP client |
| Browser UI | — | tabs, address bar, bookmarks bar, downloads, history, settings |

Features that ship in this build:

- **Bookmark Bible** — records with folders, tags, pins (`fav`), search, and a stats view.
- **Vault** — credentials sealed with Windows DPAPI; nothing in plaintext on disk; reveal is audited.
- **Page archive** — raw HTML + text + Markdown + full-page PNG + SHA-256 in one action.
- **Shot Vault** — screenshots filed under `data/vault/screenshots/<date>/`.
- **Agent tab** — console → NVIDIA → local model fallback, with a 30 s console budget and a cooldown.
- **Everyday browser work** — find bar, zoom, print, save-as-PDF, view source, DevTools, downloads
  ledger, history page, session restore, reopen-closed-tab, per-tab favicons, address suggestions,
  permission prompts, context-menu archive.
- **Portable profile** — defaults to `%LOCALAPPDATA%\LYGO\Browser`, so no developer path is baked in.
- **Single instance** — a second launch forwards its URL to the running window.

## Requirements

- Windows 10/11 x64.
- **.NET 9 SDK** to build; the runtime is enough to run a published build.
- **WebView2 Evergreen Runtime** (present on current Windows; the installer checks).

## Quick start

```powershell
git clone <this repo> lygo-browser
cd lygo-browser

dotnet publish src\LygoBrowser\LygoBrowser.csproj -c Release -o app
.\"LYGO Browser.bat"
```

Full detail, including the public-package build and the leak gate: [`INSTALL.md`](INSTALL.md).

## Verify

```powershell
# 22-check end-to-end suite (browser must be running)
C:\Python313\python.exe tools\selftest.py --quick     # 17 pass / 5 skip
C:\Python313\python.exe tools\selftest.py             # full, needs network for archive/PDF

# static quality gate: method length, complexity, duplicate blocks
C:\Python313\python.exe tools\quality_gate.py
```

Expected on a healthy build: `SELFTEST PASS 17 passed · 0 failed · 5 skipped` for `--quick`
(22 checks total, 5 network/slow ones skipped), and `quality_gate: PASS`.

## Building a public package — do not do this by hand

This is the part that is easy to get wrong, so it is automated:

```powershell
pwsh -File tools\public\build_public_browser.ps1
```

That script publishes a clean binary, stages the tree, asserts the stage is complete, then runs
[`tools/public/sterilize.ps1`](tools/public/sterilize.ps1), which **fails** on any surviving secret
or developer path — in text *and* in binaries.

Why it exists: a naive copy of `app\` leaks the builder's home directory into `LygoBrowser.dll` as a
CodeView/PDB path. `-p:DebugType=none` does **not** remove it. Only a `PathMap` build does, which is
why the csproj carries a `PublicBuild` property group:

```xml
<PropertyGroup Condition="'$(PublicBuild)' == 'true'">
  <Deterministic>true</Deterministic>
  <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
  <PathMap>$(MSBuildProjectDirectory)=/_/src</PathMap>
  <DebugType>portable</DebugType>
</PropertyGroup>
```

## Known defects — stated so nobody "fixes" them by guessing

1. **A CDP-attached download can terminate the app.** Reproduced three times; nothing appears in
   `browser.log`, so WebView2 dies natively rather than throwing. Standalone downloads pass every
   run. Mitigation: automation should call `POST /api/agent/open` instead of driving a download
   through Playwright.
2. **Dragging a link onto the bookmark bar is unreliable** — Chromium owns the drag. `+ LINK`
   (clipboard pin) is the reliable path; do not remove it.
3. **Force-kill loses cookies.** Chromium flushes on graceful shutdown. Closing the window persists
   logins; `Stop-Process` does not.

## What is not in this repository

`app/` (compiled output — build it), `data/` (profile, vault, Bible, archives — 940 MB of your own
state), and any `*.pdb`. A clone is source, not a ready install.

## Documentation

| File | |
|---|---|
| [`INSTALL.md`](INSTALL.md) | build, run, package, troubleshoot |
| [`docs/LYGO-BROWSER-DESIGN.md`](docs/LYGO-BROWSER-DESIGN.md) | design and rationale |
| [`docs/TEST-REPORT.md`](docs/TEST-REPORT.md) | the measured test record |
| [`CHANGELOG.md`](CHANGELOG.md) | what 1.9.1 changed |
| [`DISCLAIMER.md`](DISCLAIMER.md) | what you accept by running this |
| [`SECURITY.md`](SECURITY.md) | reporting, secret rules, posture |
| [`public/`](public/) | the end-user install notes and licence |

**Steward:** Justin Helmer — Lightfather · Excavationpro · DeepSeekOracle
