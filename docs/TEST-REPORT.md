# LYGO BROWSER v1.1 — TEST REPORT

Run: 2026-10-30 09:57 (America/Edmonton) · machine: this PC · engine: WebView2 156.0.4314.50
Command: `python tools\selftest.py`

```
  [PASS] health               engine 156.0.4314.50, 213 records, 6 archived
  [PASS] cdp                  Edg/156.0.4314.50
  [PASS] bible-integrity      213 records, ids and URLs consistent
  [PASS] bible-search         5 hits, first: Lightpanda — browser for AI agents
  [PASS] bible-stats          213 records / 106 domains / 6 archived
  [PASS] tabs                 1 tab(s), engine 156.0.4314.50
  [PASS] open-page            LYGO Browser — Settings · 895 chars
  [PASS] eval-js              JavaScript bridge answers 2
  [PASS] find-in-page         1 matches, active #2
  [PASS] zoom                 1.5x applied and reset to 1.0
  [PASS] download             selftest-sample-095712.bin · 65536 bytes · in-progress
  [PASS] html                 7557 bytes
  [PASS] screenshot           shot-20261030-095714-420.png · 110 KB
  [PASS] pdf-export           LYGO Browser — Settings-20261030-095714.pdf · 108 KB
  [PASS] archive-capture      https://chatagent.ca/ · 36167 bytes · sha 08f056b0c822
  [PASS] vault-roundtrip      set → list (no secret) → reveal (match) → sealed on disk → removed
  [PASS] vault-fill-guard     active tab is 127.0.0.1; this credential is for example.com (pass force to override)
  [PASS] history              16 recent visits, newest: https://chatagent.ca/
  [PASS] settings-roundtrip   changed to ddg and restored google
  [PASS] ingest-extract       168 chars of clean markdown
  [PASS] quality-gate         quality_gate: PASS (0 long, 0 complex, 0 duplicate blocks)
  [PASS] mcp-server           13 MCP tools, bible_stats responded

SELFTEST PASS  22 passed · 0 failed · 0 skipped
```