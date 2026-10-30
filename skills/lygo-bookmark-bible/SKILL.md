---
name: lygo-bookmark-bible
description: "The BOOKMARK BIBLE - the LYGO shared link brain. Use whenever a link is fed, found, ingested or cited: add it as a record, file it in the canon folder, tag it, and archive the page (HTML + text + Markdown + PNG + SHA-256) so it can be referenced later by a human or an agent. Covers the store schema, the 10-folder taxonomy, the CLI, the archive layout, search, exports and the integrity rules."
---

# BOOKMARK BIBLE — the rule

**Every link we ingest gets bookmarked, archived and linked properly.** No link lives only in a
chat message. If it was worth reading, it is worth a record.

- Store: `<INSTALL_DIR>\data\bible\bookmarks.json` (single source of truth)
- Human/agent view: `data\bible\BIBLE.md` · flat export: `data\bible\bible.csv`
- Archive: `data\archive\<id>\` → `page.html`, `page.txt`, `page.md`, `shot.png`, `meta.json`
- Engine: `python tools\lygo_bible.py` (works with or without the browser running)

## Taxonomy — where a link goes

| Folder | Holds |
|---|---|
| `00_START_HERE` | home, search engines, Wayback, archive.today, the browser itself |
| `01_LYGO` | protocol stack, SkillHub, champions, starcore, the eight champions' anchors |
| `02_AGENTS` | moltbook, moltx, clawchat, 4claw, clawhub, launchpads, X posts we ingested |
| `03_DOCS` | OpenClaw, Lightpanda, pagesift, browser-use, MCP, Playwright, WebView2 |
| `04_RESEARCH` | search, archives, arXiv, OpenAlex, HF, GitHub, Wikipedia |
| `05_MEDIA` | YouTube, Rumble, Audius, Lulu, Patreon, DistroKid |
| `06_OPS` | explorers, dashboards, tokens, DEX, exchange |
| `07_WEBSITES` | domains Justin owns |
| `08_TOOLS` | free APIs and utilities |
| `09_QUEUE` | inbox — unfiled, mined from history, fed later. **Emptying it is the job.** |

A record auto-files by domain when no folder is given. Never invent an eleventh folder; if the
taxonomy truly does not fit, propose the change to Justin first.

## Ingest a link (the three steps)

```powershell
# 1. add (idempotent - same URL never duplicates)
python tools\lygo_bible.py add --url "https://example.com/x" --title "What it is" `
  --folder 03_DOCS --tags rust,ingest --note "why it matters"

# 2. archive the live page (needs the browser running)
python tools\lygo_agent.py archive bm_1a2b3c4d5e

# 3. rebuild the human + agent views
python tools\lygo_bible.py export
```

If the browser is closed, do 1 and 3 now and queue the archive — record it in the note as
`archive: pending` so it is not forgotten.

## Archive guarantees

- `page.html` is the **raw source**, byte-for-byte as served. Never archive only a summary:
  summaries are the thing that rots.
- `page.txt` is `innerText`; `page.md` is a readable extract; `shot.png` is a full-page CDP capture.
- `meta.json` carries `sha256` of the HTML. That hash is also copied into `record.hash`, so drift
  is provable: re-hash the file and compare.
- Archive the page in the session that has the access (login walls, dashboards). An archive of a
  login wall taken later is a lie.

## Search

```powershell
python tools\lygo_bible.py search "lightpanda cdp" --limit 10
python tools\lygo_bible.py search "archive" --json
python tools\lygo_bible.py list --folder 03_DOCS
python tools\lygo_bible.py list --tag champion
python tools\lygo_bible.py open --folder 02_AGENTS --limit 12   # prints URLs, one per line
```
Scoring: title 6, tag 4, folder 3, url 2, note 1. All terms must match (AND); if nothing matches,
it retries any-term and says so by returning lower scores. The browser's address bar uses the
Bible first: a query that exists as a record opens the record list, not Google.

## Integrity rules

1. **Append-mostly.** Retire with `status: retired`, never delete a record.
2. **Deterministic ids.** `bm_` + first 10 hex of `sha1(normalised url)`. Same URL → same id →
   no duplicates, ever. Re-running an import is safe.
3. **URLs are normalised** on write: lowercase host, tracking parameters (`utm_*`, `fbclid`, …)
   stripped, trailing slash removed. `verify` fails if a stored URL is not in normalised form.
4. **No secrets in records.** A URL with a token in it is redacted before it is stored.
5. Run `verify` after any bulk import:

```powershell
python tools\lygo_bible.py verify     # 0 problems required
python tools\lygo_bible.py stats
```

## Yandex migration (already done once, 2026-10-30)

The old agent-era browser was Yandex with two profiles. Re-running the migration:

```powershell
python tools\yandex_import.py                 # re-extract bookmarks + history to data\yandex-import.json
python tools\lygo_bible.py import-yandex --history --history-min 3 --history-cap 150
python tools\lygo_bible.py export
```
Sources: `...\YandexBrowser\User Data\Default\Bookmarks` (61) and `...\User Data\Profile 1\Bookmarks`
(60), plus up to 150 history URLs with ≥3 visits. Records keep `source: yandex-*` and the legacy
folder path in the note, so provenance survives re-filing.

## Feeding the three brains

- **Left brain (observe):** the live page, the screenshot, the raw HTML.
- **Bookmark Bible (reference):** the record — folder, tags, note, archive paths, hash.
- **Right brain (act):** the API call or form fill that the reference justified.

A citation from the Bible must carry three things: `title`, `url`, `sha256` (or the archive path
when the live page has changed). "I remember reading it" is not a reference.
