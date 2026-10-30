# LYGO BROWSER — first run (public build)

This is the clean public build. No bookmarks, no credentials, no history, no profile, no
developer paths. It starts empty, with **excavationpro.ca** as the home page.

## Install
1. Unzip anywhere you own (e.g. `%LOCALAPPDATA%\LYGO\Browser`).
2. Run `LYGO Browser.exe`.
3. It asks to be your default browser — Windows will only accept the choice from **you**
   (`Settings → Default apps → Web browser → LYGO Browser`).
4. Data is created on first run under `%LOCALAPPDATA%\LYGO\Browser\data`:
   `bible\`, `archive\`, `vault\`, `state\`, `profile\`.

## What works out of the box
- Chromium browsing, tabs, find, zoom, print/PDF, downloads, history, session restore.
- **Bookmark Bible** — your own link archive with raw page archives + SHA-256 receipts. Empty at
  first run; press ★ (Ctrl+D) to pin, or `+ LINK` to pin a copied URL.
- **Shot Vault** — camera button / Ctrl+Shift+S, filed by day under `data\vault\screenshots`.
- **Credential vault** — DPAPI-sealed on your Windows account.
- **Agent bridge** — `http://127.0.0.1:9337` with a per-install token in
  `data\state\bridge.token`; CDP on `:9333`.
- **Cookies** — full Chromium jar in `data\profile`; Settings has a count + "clear all".

## What you must install yourself (stubbed on purpose)
The browser is the hub, not the brain. To wire it to agents and the LYGO console:

### 1. OpenCode (agent CLI the console/forge dispatch to)
```powershell
npm install -g opencode-ai        # then: opencode --version
```
The console's `tools\opencode_bridge.py` and the Forge call `opencode run`. Without it, those
paths fall back to a local model or report `opencode` missing.

### 2. A local model (if you want a brain on your own machine)
- Install **llama.cpp** (or the LYGO Engine if you have it) and serve an OpenAI-compatible
  endpoint, e.g. `llama-server -m model.gguf --port 11441 --api-key local`.
- The browser chat looks for `http://127.0.0.1:8081` (LYGO local bridge) and
  `http://127.0.0.1:8082` (NVIDIA bridge). Point your own bridge at your model.
- Until then, chat returns a clear "no brain answered" message — it will not pretend.

### 3. Paid / API providers (optional)
Put keys in your console's `config\api.json` (never in this browser). The browser never stores
provider keys; it only forwards your prompt to whatever brain you configured.

### 4. Wire it to the LYGO Console
Install the LYGO Console (separate download, ships with a small local model). Then:
- Console: `http://127.0.0.1:9641/`
- Forge: `http://127.0.0.1:8796/`
- Both are pinnable on the bookmark bar — click the status light in the title bar to boot them.

## Uninstall
Delete the folder and `%LOCALAPPDATA%\LYGO\Browser`. Nothing is written to the registry except the
optional browser registration (removable via `tools\register_browser.ps1 -Uninstall`).
