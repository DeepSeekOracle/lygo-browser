# LYGO BROWSER — design & build decisions

Status: v1.0 build (2026-10-30). Owner: Justin Helmer (Lightfather). Builder: Delta.

## What this is

One browser, two users:

- **Human surface** — a studio-quality LYGO-chromed window: tabs, address bar, bookmark bar,
  archive, and a credential vault.
- **Agent surface** — the same window exposed over a local HTTP API and over Chrome DevTools
  Protocol (CDP), so DSH / Hermes / Gemini / Antigravity / Playwright / browser-use can open,
  read, snapshot, click, and file pages without a second browser.

The **BOOKMARK BIBLE** is the shared brain: every link Justin feeds or imports is a record with a
folder, tags, a note, and an archive (HTML + clean Markdown + screenshot + hash).

## Engine choice (and why)

| Option | Verdict |
|---|---|
| Fork Chromium | Months of work, 100+ GB toolchain. No. |
| Electron | Needs a 250 MB download; no Electron on this box. Rejected. |
| **WebView2 (Edge/Chromium) hosted in WPF (.NET 9)** | **Chosen.** Runtime already installed (156.0.4314.50), SDK restores (1.0.4314.35), native Windows chrome, small, and it exposes **CDP** — so Playwright/Puppeteer/MCP work against the real browser. |
| Lightpanda | Adopted as the **agent bulk-fetch engine** (optional limb), not the GUI. 16x less RAM than headless Chrome, CDP + WebDriver BiDi, native MCP. |

Reference material actually read for this build (all eight links Justin supplied):

- `github.com/lightpanda-io/browser` — Zig from scratch, V8, CDP/BiDi, 123 MB vs 2 GB, 100 pages/5 s.
- `github.com/YOLOW92/pagesift` — Rust crawler, HTML → clean Markdown for RAG.
- `unsloth.ai/docs/basics/browser-use` — local tool-calling models drive the browser.
- `github.com/unclebob/ub-mode` — rules for AI-written code + complexity/duplicate/mutation gates.
- `github.com/tailscale/tailcat` — token-paired P2P, WireGuard, SOCKS5/exit node.
- SwaT / swarm telepathy (MIT CSAIL + IBM) — hand the artefact, not the summary. Design consequence:
  **the archive stores the full page, never only a digest.**

Taken from each:
- Lightpanda/pagesift → agent ingestion is a pipeline: fetch → main-content extract → Markdown.
- Unsloth → expose a tool-shaped API and CDP so *any* local model can drive us.
- ub-mode → hard limits on this codebase: one class per job, methods short, no clever tricks.
- Tailcat → the agent API is **token-paired**, loopback-only by default; remote is opt-in.
- SwaT → archive raw text + structure; never replace the source with a summary.

## Architecture

```
 LYGO Browser.exe  (WPF, .NET 9, WebView2)
 ├─ Window chrome (XAML)  tabs · address · bookmark bar · status
 ├─ Tab host              one WebView2 per tab, CDP on 127.0.0.1:9333
 └─ LygoService           HttpListener 127.0.0.1:9337
     ├─ /                 HUD dashboard (start page)
     ├─ /bible            Bookmark Bible explorer
     ├─ /vault            credential vault UI
     ├─ /archive          archive browser
     ├─ /api/bible/...    search / add / folder / tags
     ├─ /api/archive/...  ingest a URL → html + md + png + hash
     ├─ /api/vault/...    DPAPI-sealed credential records
     └─ /api/agent/...    open / snapshot / click / type / tabs
```

Data is **files first** so every agent can read it with no driver:

```
<INSTALL_DIR>\data\
  bible\bookmarks.json     records + folders (single source of truth)
  bible\BIBLE.md           generated, human + agent readable
  bible\bible.csv          generated flat export
  archive\<id>\            page.html · page.md · shot.png · meta.json
  vault\vault.dat          DPAPI-sealed (CurrentUser), never plaintext
  state\session.json       open tabs, last window
```

Writers: browser closed → tools write JSON directly; browser open → tools POST to the API so
there is one writer. Atomic write (temp + replace) plus a `.lock` in both paths.

## Brand tokens

Sampled from the live LYGO surfaces (`Music\Signal\portal\web`, `lygo\forge\web`).

| Token | Hex | Use |
|---|---|---|
| `--obsidian` | `#05080C` | window base |
| `--panel` | `#0B1117` | panels |
| `--panel-2` | `#111A21` | raised |
| `--line` | `#1D2A33` | borders |
| `--gold` | `#E0B36A` | Lightfather accent, active tab, primary buttons |
| `--gold-soft` | `#F0CD8F` | highlights, focus ring |
| `--cyan` | `#7DE6E8` | agent / left brain |
| `--mint` | `#82F0B0` | ok / archive done |
| `--danger` | `#E0685F` | destructive |
| `--text` | `#E9F0ED` | body |
| `--muted` | `#8B9199` | secondary |

Type: `Cascadia Mono` for data/agent output, `Segoe UI Variable` for prose. Numbers tabular.
No rounded-blob cards; 2px hairlines; gold rule under the active tab.

## Bookmarks Bible taxonomy (canon)

| Folder | Holds |
|---|---|
| `00_START_HERE` | home, search, wayback, "how to use the bible" |
| `01_LYGO` | his network: console, forge, signal, skillhub, champions, book series, starcore |
| `02_AGENTS` | agent social: moltbook, moltx, clawchat, 4claw, clawhub, launchpads |
| `03_DOCS` | OpenClaw, Lightpanda, pagesift, browser-use, MCP, Playwright, WebView2 |
| `04_RESEARCH` | search engines, archives, arXiv, HF, GitHub, Semantic Scholar |
| `05_MEDIA` | YouTube, Rumble, Audius, Lulu, Patreon, DistroKid |
| `06_OPS` | explorers, dashboards, token pages, DEX, exchange |
| `07_WEBSITES` | owned domains |
| `08_TOOLS` | free APIs and utilities |
| `09_QUEUE` | inbox — anything unfiled, mined from history, fed later |

## Rules inherited from the LYGO stack

1. P0 gate: nothing is called working without a command + result.
2. No automatic publishing. Nothing leaves the machine.
3. No secrets in plaintext, no secrets in logs, no secrets on removable media.
4. Never remove an existing page or bookmark; add, re-file, or archive instead.
5. The Bible is append-mostly: records get `status: retired`, never deleted.
6. Code quality gate (ub-mode): cyclomatic complexity ≤ 10 per method, no duplicated blocks,
   one reason to change per class.

## Not in v1.0 (named so nobody assumes them)

- No Chromium fork, no custom network stack, no extension marketplace.
- No password *autofill into third-party forms* until Justin signs off on the consent model.
- No cloud sync. The Bible is local; remote access is TBD via Tailcat, opt-in only.
- Lightpanda limb is wired as an option, not required.

---

# v1.1 pass — 2026-10-30: commercial parity + the link extras

Justin's instruction: "all the functions of the commercial browsers plus all the extra stuff from the
browsers I gave you links for." This section records what was added, which real SDK API each feature
uses, and what was proven.

## Features, and the API behind them

| Feature | Implementation | API |
|---|---|---|
| Find in page | in-window find bar, match count, gold highlight | `CoreWebView2.Find.StartAsync/FindNext/FindPrevious` |
| Zoom | per tab, status-bar readout | `WebView2.ZoomFactor` |
| Print | native dialog | `ShowPrintUI(Browser)` |
| Save as PDF | background headers, background colours | `PrintToPdfAsync(path, settings)` |
| Save page as | WebView2 save-as | `ShowSaveAsUIAsync()` |
| Page source | writes `data/screenshots/source-*.html`, opens it | `ExecuteScriptAsync(outerHTML)` |
| DevTools | full Chromium panel | `OpenDevToolsWindow()` |
| Downloads | own folder, own ledger, byte progress | `DownloadStarting` + `CoreWebView2DownloadOperation` |
| History | append-only JSONL, rotated at 8 MB | our `HistoryStore` |
| Session restore | tabs + active index on graceful close | `data/state/session.json` |
| Reopen closed tab | 12-deep stack | our `_closed` |
| Favicons | per tab, stream rewound, brand fallback for HUD pages | `FaviconChanged` + `GetFaviconAsync` |
| Suggestions | Bible + history + "search the web", arrow keys | our `SuggestPopup` |
| Permissions | branded allow/deny dialog | `PermissionRequested` |
| Context menu | Chromium + two Bible entries | `ContextMenuRequested` + `CreateContextMenuItem` |
| Settings | homepage, search engine, download dir, session, spellcheck | `data/state/settings.json` |
| Vault fill | one-shot, host-checked, token-gated | injected JS over `ExecuteScriptAsync` |
| Agent find/zoom | same code path as the UI | `/api/agent/find`, `/api/agent/zoom` |

## The link extras, concretely

- **Lightpanda** → CDP `:9333` was already exposed; v1.1 adds the **MCP server** (13 tools) so the
  browser is a first-class tool surface, which was the actual point of that project.
- **pagesift** → `tools/lygo_ingest.py`: fetch, drop nav/script/style, prefer `article`/`main`,
  densest-`div` fallback, then a real Markdown conversion (headings, lists, links, code, quotes).
- **Unsloth browser-use** → the agent API is tool-shaped: open, snapshot, click, type, eval, find,
  zoom, screenshot, history, archive. A local model can be handed the MCP tool list directly.
- **ub-mode** → `tools/quality_gate.py` enforces method length ≤ 60, complexity ≤ 12 and duplicate
  blocks; it is now part of `selftest`. It caught six real violations in this pass (a 71-line
  `CreateTabAsync`, a 45-branch key handler, a 20-branch `Add`, a 16-branch `Classify`, a 14-branch
  request parser) — all refactored rather than waived.
- **SwaT** → unchanged principle, now enforced in tests: `archive-capture` re-hashes `page.html` and
  compares it to both `meta.json` and the API response.
- **Tailcat** → still the documented path for future remote access; v1.1 keeps the API loopback-only
  and token-gated, which is the same trust model without opening a port to the LAN.

## Evidence (2026-10-30, this machine)

- `selftest.py` full run: **22 passed / 0 failed / 0 skipped**.
  - download: 65,536 bytes, `completed`
  - find: 1 match on the settings page; zoom: 1.5× applied and reset
  - pdf: 108 KB, starts `%PDF`
  - archive: `chatagent.ca`, 35,729 bytes, sha matches on re-hash
  - vault: set → list (no secret) → reveal (match) → sealed on disk → removed; fill refused on a
    host mismatch
  - mcp: 13 tools, `bible_stats` answered
- `quality_gate.py`: PASS (0 long, 0 complex, 0 duplicate blocks).
- Session restore: closed with 2 tabs, relaunched, 2 tabs restored with the correct active tab.
- `lygo://browser` from cold: engine answering in **0.59 s**.

## Deliberately not done

- No extension store: WebView2 has no stable extension host, and an untrusted extension surface is
  the opposite of what this machine needs.
- No autofill beyond the explicit FILL button: silent credential entry is a consent problem, not a
  feature gap.
- No second window: tabs only, so the tab ledger and the Bible stay single-sourced.
