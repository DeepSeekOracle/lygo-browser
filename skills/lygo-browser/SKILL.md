---
name: lygo-browser
description: "Drive the LYGO Browser - the branded agent+human WebView2 browser on this PC. Use when an agent needs to open a URL, read a page as clean text, screenshot a site, click or fill a form, file a link into the Bookmark Bible, archive a page, or read/write the credential vault. Covers the loopback HTTP API (:9337), the CDP endpoint (:9333) for Playwright/browser-use, the token model, and the exact failure modes."
---

# LYGO BROWSER — operator card

One browser, two users. **Humans** get a LYGO-chromed window. **Agents** get the same session
over a loopback API and CDP — no second browser, no re-login, no headless replica.

- App root: `<INSTALL_DIR>`
- Exe: `app\LygoBrowser.exe` · launcher: `LYGO Browser.bat`
- Bridge: `http://127.0.0.1:9337` · CDP: `http://127.0.0.1:9333`
- Agent CLI: `python tools\lygo_agent.py`
- Design + non-goals: `docs\LYGO-BROWSER-DESIGN.md`

## Start / stop

```powershell
& '<INSTALL_DIR>\LYGO Browser.bat' --check   # 0 = engine up, prints URL
& '<INSTALL_DIR>\LYGO Browser.bat'           # normal launch, no console left behind
```
Never start a second instance: the bridge port is single-owner. If `--check` returns 1, read
`data\state\browser.log` before retrying.

Stop: close the window. From a script, kill the process that owns 9337:

```powershell
$pid = (Get-NetTCPConnection -LocalPort 9337 -State Listen).OwningProcess
Stop-Process -Id $pid
```

## Auth model

Reads are open on loopback. **Every POST needs the per-install token.**

```powershell
$t = Get-Content '<INSTALL_DIR>\data\state\bridge.token' -Raw
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:9337/api/bible/add' `
  -Headers @{ 'X-LYGO-Token' = $t.Trim() } -ContentType 'application/json' `
  -Body '{"url":"https://example.com","title":"Example","folder":"09_QUEUE"}'
```

The token is generated once per install and is not a secret to be published — it gates local
mutation, nothing more.

## The API (all JSON, camelCase)

| Call | Body | Returns |
|---|---|---|
| `GET /api/health` | – | engine, cdp, counts, root |
| `GET /api/bible` | – | folders + active records |
| `GET /api/bible/search?q=&limit=` | – | `{hits:[record]}` |
| `GET /api/bible/stats` | – | records, domains, archived, byFolder |
| `POST /api/bible/add` | `{url,title,folder,tags,note}` | `{ok,status,record}` |
| `POST /api/bible/update` | `{id,title,folder,tags,note,status}` | `{ok,record}` |
| `POST /api/bible/archive` | `{id}` or `{url}` | `{ok,dir,sha256,bytes,files}` |
| `GET /api/archive` | – | archived items with paths |
| `GET /api/vault` | – | labels only, never secrets |
| `POST /api/vault/set` | `{label,user,secret,url,notes}` | `{ok,id}` |
| `POST /api/vault/reveal` | `{id}` | `{ok,secret}` |
| `POST /api/agent/open` | `{url,newTab}` | `{ok,url}` |
| `GET /api/agent/status` | – | tabs, cdpPort, engine |
| `POST /api/agent/snapshot` | `{tab}` | `{ok,snapshot:{title,url,text}}` |
| `POST /api/agent/screenshot` | `{tab}` | `{ok,path}` |
| `POST /api/agent/html` | `{tab}` | `{ok,html,bytes}` |
| `POST /api/agent/eval` | `{js,tab}` | `{ok,result}` |
| `POST /api/agent/click` | `{selector,tab}` | `{ok}` |
| `POST /api/agent/type` | `{selector,text,tab}` | `{ok}` |
| `POST /api/agent/find` | `{text,forward,tab}` | `{ok,matches,index}` |
| `POST /api/agent/zoom` | `{factor,tab}` | `{ok,zoom}` |
| `POST /api/agent/close` | `{tab}` | `{ok}` |
| `GET /api/history` | `?q=&limit=` | `{items:[{ts,url,title}]}` |
| `POST /api/history/clear` | – | `{ok}` |
| `GET /api/downloads` | – | `{items:[{id,url,path,bytes,state}]}` |
| `GET /api/settings` · `POST /api/settings` | full settings object | `{ok,settings}` |
| `POST /api/page/pdf` | `{tab}` | `{ok,path}` |
| `POST /api/vault/fill` | `{id,force}` | `{ok,filled}` — refuses on a host mismatch |

`tab` is optional; omit it to act on the active tab.

## CLI (preferred for one-shot work)

```powershell
python <INSTALL_DIR>\tools\lygo_agent.py status
python ...\lygo_agent.py open https://lightpanda.io/
python ...\lygo_agent.py snapshot --chars 6000
python ...\lygo_agent.py shot
python ...\lygo_agent.py search lightpanda
python ...\lygo_agent.py add https://example.com --title "Example" --tags rust,ingest
python ...\lygo_agent.py archive bm_1a2b3c4d5e
```
The CLI degrades gracefully: with the browser closed, `search`, `add` and `folders` still work
against the files; `open`, `snapshot`, `shot` and `archive` need the engine.

## MCP, ingest, quality — the other three limbs

```powershell
# MCP stdio server: 13 tools (status, open, snapshot, html, screenshot, click, type, eval,
# bible_search/add/archive/stats, history_search)
python <INSTALL_DIR>\tools\lygo_mcp_server.py

# URL -> clean Markdown, no browser needed (pagesift-style content extraction)
python ...\tools\lygo_ingest.py https://example.com/article --folder 03_DOCS --tags rust

# code-quality gate (ub-mode rules): method length, complexity, duplication
python ...\tools\quality_gate.py

# end-to-end proof that the build still works
python ...\tools\selftest.py --quick
```

Register the MCP server with any client:

```json
{ "mcpServers": { "lygo-browser": {
    "command": "C:\\Python313\\python.exe",
    "args": ["<INSTALL_DIR>\\tools\\lygo_mcp_server.py"] } } }
```

## Keyboard the human will use (do not fight these)

`Ctrl+T` new tab · `Ctrl+Shift+T` reopen closed · `Ctrl+W` close · `Ctrl+L` address ·
`Ctrl+D` bookmark · `Ctrl+F` find · `F3` next · `Ctrl+H` history · `Ctrl+J` downloads ·
`Ctrl+P` print · `Ctrl+U` source · `F12` DevTools · `F5` reload · `Ctrl+±/0` zoom ·
`Alt+←/→` back/forward · `Esc` closes find and suggestions.

## CDP — Playwright / Puppeteer / browser-use

The window is a real Chromium with a debugging port. Point any client at it:

```python
from playwright.sync_api import sync_playwright
with sync_playwright() as p:
    b = p.chromium.connect_over_cdp("http://127.0.0.1:9333")
    page = b.contexts[0].pages[0]
    print(page.title(), page.url)
```

This is the same session Justin is looking at, including his logins. Treat it as privileged:
never navigate somewhere, type a secret, or submit a form without an explicit instruction.

## Vault rules (hard)

1. Secrets live only in `data\vault\vault.dat`, sealed with DPAPI (CurrentUser).
2. Never print a secret, never echo one into a log, never paste one into a page you were not
   asked to fill.
3. `GET /api/vault` returns labels only. `POST /api/vault/reveal` is the only path to a secret
   and it is audited in `data\state\browser.log`.
4. Nothing is autofilled automatically. That is a deliberate v1.0 decision.

## Failure modes and what they mean

| Symptom | Cause | Fix |
|---|---|---|
| `--check` exit 1, no window | WebView2 runtime missing | install **WebView2 Evergreen Runtime**; verify 156.x is present |
| bridge 401 | wrong/missing `X-LYGO-Token` | read `data\state\bridge.token`, trim whitespace |
| bridge down but window open | port 9337 taken | `Get-NetTCPConnection -LocalPort 9337`; kill the squatter |
| screenshot null | page still painting, or CDP blocked | wait 800 ms after navigation, retry once |
| archive ok but `md` empty | page has no body text (canvas/PDF) | the PNG + HTML are still the record; note it |
| tabs list empty | window opened before engine finished | wait for `/api/health` then retry |

## P0 verification (run these, don't trust adjectives)

```powershell
curl.exe -s http://127.0.0.1:9337/api/health
python <INSTALL_DIR>\tools\lygo_agent.py tabs
python <INSTALL_DIR>\tools\lygo_bible.py verify
```
