# Changelog — LYGO Browser

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.9.1] — 2026-10-30 — **Public Beta 1**

### Added
- **First public source tree**: `src/LygoBrowser` (WPF/WebView2, .NET 9), the agent bridge on 9337,
  CDP on 9333, the agent tab, and the `tools/` + `skills/` that drive them.
- **A public-package build that cannot silently leak.** `tools/public/build_public_browser.ps1`
  publishes a clean binary, stages the tree, asserts completeness, then runs the sterilizer.
- **A `PublicBuild` property group** in `LygoBrowser.csproj` — `PathMap` + deterministic build, so
  the assembly carries no builder path in its CodeView record.
- **`INSTALL.md`, `DISCLAIMER.md`, `SECURITY.md`, `CHANGELOG.md`.**

### Changed
- **Version stamped 1.9.1** in the health payload, About box, README and LinkProbe — and in the
  csproj `<Version>`, which had been left at `1.0.0`. The shipped executable now reports file
  version `1.9.1.0`.
- **`tools/public/sterilize.ps1` repaired.** It previously passed a package that still contained the
  builder's home path in five files. Four structural holes closed: path separators and JSON escape
  depth are now ignored (`C:\`, `C:/`, `C:\\`, `C:\\\\`); exclusions apply at **any** depth, not just
  the top level; every file is scanned, not only text extensions; and the scan is byte-chunked so
  `.pyc`, `.pdb` and `.bin` are read. The gate is negative-tested — a seeded leak exits 1.
- **`selftest.py` no longer dies on a cp1252 console.** A `UnicodeEncodeError` on the report's arrows
  used to exit 1 with no failing check, which read exactly like a broken build. UTF-8 is forced now.
- **Portable profile** defaults to `%LOCALAPPDATA%\LYGO\Browser`; no developer path is baked in.

### Verify at this tag
`tools\selftest.py --quick` → **17 passed · 0 failed · 5 skipped** (22 checks);
`tools\quality_gate.py` → **PASS** (15 files, 4,245 lines, 283 methods).
The published package built from this tag: **56 files, 3.30 MB**, clean on an independent
binary-safe sweep.

### Known defects — documented, not hidden
1. A **CDP-attached download** can terminate the app (native WebView2 termination, nothing in
   `browser.log`). Standalone downloads pass every run. Use `POST /api/agent/open` from automation.
2. **Dragging a link onto the bookmark bar is unreliable** — Chromium owns the drag. `+ LINK`
   (clipboard pin) is the reliable route.
3. **Force-kill loses cookies** — Chromium flushes on graceful shutdown; close the window normally.

### Not included in this repository
`app/` (compiled output), `data/` (profile, Bible, vault, archives — 940 MB of user state), `*.pdb`.

## Licence

LYGO Fair Usage v1.9.1 — see [`LICENSE.md`](LICENSE.md).
