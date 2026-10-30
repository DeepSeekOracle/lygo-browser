---
name: lygo-browser-reference-brain
description: "The LYGO 3-brain information recall system that plugs the Browser and the Bookmark Bible into the existing brain files. Use when an agent must find where something was seen, verify a claim against the real page, recall a link days later, turn screenshots plus bookmarks into a durable reference, or hand a citation to another agent. Covers the observe -> reference -> act loop, the reference record format, screenshot protocol, resolution precedence and anti-drift rules."
---

# LYGO BROWSER REFERENCE BRAIN — the 3-brain loop, wired

This is the browser limb of the existing LYGO 3-brain system (`lyra-brain`, `book-brain`,
`book-brain-visual-reader`). Those skills own the *folders and logging*. This one owns how the
**browser observes** and how the **Bookmark Bible references**.

## The three brains, with owners

| Brain | Role | Where it lives | Tool |
|---|---|---|---|
| **LEFT — observation** | see the real page | the LYGO Browser tab | `lygo_agent.py snapshot` / `shot` |
| **BIBLE — reference** | where to look, and proof | `data\bible\bookmarks.json` + `data\archive\` | `lygo_bible.py` |
| **RIGHT — action** | do the thing | API calls, forms, posts | `lygo_agent.py` / CDP |

Local brain files (`brainwave/`, `reference/`, memory notes) sit **above** all three: check them
first, because they are the compressed form of past observations.

## Resolution precedence — do not skip steps

1. **Local brain files** — has this already been read, decided or written down?
2. **Bookmark Bible** — is there a record with an archive? Then you have the page *as it was*.
3. **Browser observation** — open it, snapshot the text, take a screenshot.
4. **API / tool** — structured access when the page is a wall.
5. **Search last** — and whatever you find, bookmark it (step 1 of the canon).

A claim sourced at step 3+ must produce a Bible record before the task ends. That is the only
thing that stops the same research being redone next month.

## Observe: the snapshot contract

```powershell
python tools\lygo_agent.py open "https://example.com/article"
python tools\lygo_agent.py snapshot --json        # {title,url,text}
python tools\lygo_agent.py shot                   # full-window PNG path
```

For anything read for meaning, take **both**: text (to quote) and screenshot (to prove layout,
prices, dates, or a chart that text does not carry). A screenshot without the URL is a rumour.

Screenshot naming rule: `data\state\shot-YYYYMMDD-HHMMSS-fff.png` is automatic; when a screenshot
belongs to a record, archive it with the record so the image and the hash live together.

## Reference: the record is the memory

Every record already carries what a future agent needs:

```json
{ "id": "bm_1a2b3c4d5e", "url": "...", "title": "...", "folder": "03_DOCS",
  "tags": ["rust","ingest"], "note": "why it matters",
  "source": "yandex-default|seed|agent|human", "added": "...", "visits": 0,
  "archived": {"at":"...","html":"archive/bm_.../page.html","md":"...","shot":"...","bytes":123},
  "hash": "sha256 of the raw html" }
```

Write `note` as **the claim**, not a description: "Lightpanda: CDP+BiDi, 123 MB vs 2 GB, 100 pages
in 5 s (benchmark)" beats "an article about a browser". Notes are what search scores last, but
what humans read first.

## Recall: answering "where did we see X"

1. `python tools\lygo_bible.py search "X" --json`
2. If a hit has `archived.at`, read `data\archive\<id>\page.md` — that is the page as observed,
   not as the web looks today.
3. Re-hash `page.html` and compare to `record.hash` before quoting it as unchanged.
4. If the page has changed and the claim still matters: re-open, re-snapshot, re-archive, and note
   "superseded <date>" in the note. **Do not silently overwrite history** — the old hash is the
   proof of what was true then.
5. Answer with `title · url · sha256` (or archive path). Never answer from memory alone when an
   archive exists; memory is the index, the archive is the evidence.

## The ingest loop (what "from here forward" means)

When Justin feeds a link — chat, X post, PDF, voice note — the loop is:

1. `lygo_bible.py add --url … --title … --tags …` (auto-files by domain if no folder).
2. Open it in the Browser (live session, real logins), `snapshot` + `shot`.
3. `lygo_agent.py archive <id>` → raw HTML, text, Markdown, PNG, SHA-256, meta.
4. `lygo_bible.py export` → `BIBLE.md` + `bible.csv` refresh.
5. If the link taught a technique, write one line into the relevant skill or brain file that
   points at the record id. The skill is the lesson; the Bible is the receipt.

## What breaks this system (and the guard)

| Drift | Guard |
|---|---|
| Archiving a login wall from a fresh session | archive in the session that has access; note it |
| Quoting a page that has changed | re-hash; mark superseded |
| A link that lives only in chat | step 1 of the canon: add the record first |
| Folder sprawl | 10 canon folders; propose changes, never invent |
| Secrets in URLs | strip tokens before storing; vault handles credentials |
| Two writers corrupting the JSON | browser service is the writer when open; tools fall back to files only when it is closed; atomic replace either way |

## Handing off

To give Hermes, Gemini or another agent a reference, send the **record id plus hash**, not the
prose:

```
REF bm_1a2b3c4d5e  sha256=<hash>
https://example.com/article  (03_DOCS, tag rust)
archive: <INSTALL_DIR>\data\archive\bm_1a2b3c4d5e\page.md
```
The receiver can verify the file is still the file before it cites it. That is the whole point of
a legacy information recall system: **the receipt travels with the memory**.
