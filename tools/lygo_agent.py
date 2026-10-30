#!/usr/bin/env python3
"""LYGO Browser agent limb - drive the browser and the Bible from any agent.

Works in two modes:
  * browser running  -> talks to http://127.0.0.1:9337 (richer: live tabs, screenshots)
  * browser closed   -> reads/writes the Bible files directly (search, list, add)

Examples
  python tools/lygo_agent.py status
  python tools/lygo_agent.py open https://lightpanda.io/
  python tools/lygo_agent.py snapshot --json
  python tools/lygo_agent.py shot
  python tools/lygo_agent.py search lightpanda
  python tools/lygo_agent.py add https://example.com --title "Example" --folder 09_QUEUE
  python tools/lygo_agent.py archive bm_xxxxxxxxxx
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TOKEN_FILE = os.path.join(ROOT, "data", "state", "bridge.token")
BASE = "http://127.0.0.1:9337"


def token() -> str:
    try:
        with open(TOKEN_FILE, encoding="utf-8") as fh:
            return fh.read().strip()
    except OSError:
        return ""


def call(path: str, payload: dict | None = None, timeout: int = 40):
    """GET when payload is None, POST otherwise. Returns (ok, data)."""
    url = BASE + path
    data = None
    headers = {"Accept": "application/json"}
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
        headers["X-LYGO-Token"] = token()
    req = urllib.request.Request(url, data=data, headers=headers,
                                method="POST" if payload is not None else "GET")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return True, json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            return False, json.loads(e.read().decode("utf-8"))
        except Exception:
            return False, {"error": f"HTTP {e.code}"}
    except Exception as e:
        return False, {"error": str(e), "offline": True}


def alive() -> bool:
    ok, _ = call("/api/health", timeout=3)
    return ok


def require_browser() -> bool:
    if alive():
        return True
    print("browser is not running - start it with: LYGO Browser.bat", file=sys.stderr)
    return False


# ------------------------------------------------------------------ bible fallback
def bible_local():
    sys.path.insert(0, os.path.join(ROOT, "tools"))
    import lygo_bible  # noqa: E402  (local import keeps the CLI dependency-free)
    return lygo_bible


def cmd_status(_a) -> int:
    ok, health = call("/api/health", timeout=3)
    if ok:
        print(json.dumps(health, indent=1))
        return 0
    local = bible_local()
    doc = local.load()
    print(json.dumps({
        "ok": False, "browser": "offline",
        "bible_local": len(doc.get("records", [])),
        "hint": "start LYGO Browser.bat for live tabs and archiving"
    }, indent=1))
    return 1


def cmd_open(a) -> int:
    if not require_browser():
        return 1
    ok, data = call("/api/agent/open", {"url": a.url, "newTab": not a.same_tab})
    print(json.dumps(data, indent=1))
    return 0 if ok else 1


def cmd_snapshot(a) -> int:
    if not require_browser():
        return 1
    ok, data = call("/api/agent/snapshot", {"tab": a.tab})
    if not ok:
        print(json.dumps(data, indent=1)); return 1
    snap = data.get("snapshot", {})
    if a.json:
        print(json.dumps(snap, ensure_ascii=False, indent=1))
    else:
        print(f"title: {snap.get('title')}")
        print(f"url  : {snap.get('url')}")
        print(f"chars: {len(snap.get('text',''))}")
        print("-" * 70)
        print((snap.get("text") or "")[: a.chars])
    return 0


def cmd_shot(a) -> int:
    if not require_browser():
        return 1
    ok, data = call("/api/agent/screenshot", {"tab": a.tab})
    print(data.get("path") or json.dumps(data))
    return 0 if ok else 1


def cmd_tabs(_a) -> int:
    if not require_browser():
        return 1
    ok, data = call("/api/agent/status")
    for t in data.get("tabs", []):
        mark = "*" if t.get("active") else " "
        print(f"{mark} {t.get('id'):>3}  {t.get('title','')[:60]:<60} {t.get('url','')[:70]}")
    return 0


def cmd_search(a) -> int:
    if alive():
        ok, data = call(f"/api/bible/search?q={urllib.parse.quote(a.query)}&limit={a.limit}")
        hits = data.get("hits", [])
    else:
        local = bible_local()
        doc = local.load()
        terms = a.query.lower().split()
        hits = sorted(doc["records"], key=lambda r: -local.score(r, terms, strict=False))[: a.limit]
    if a.json:
        print(json.dumps(hits, ensure_ascii=False, indent=1)); return 0
    for r in hits:
        arch = " [archived]" if (r.get("archived") or {}).get("at") else ""
        print(f"{r['folder']:<14} {r['title'][:60]:<60} {r['url'][:70]}{arch}")
    print(f"-- {len(hits)} hit(s)")
    return 0


def cmd_add(a) -> int:
    if alive():
        ok, data = call("/api/bible/add", {
            "url": a.url, "title": a.title, "folder": a.folder,
            "tags": a.tags, "note": a.note, "source": "agent"})
        print(json.dumps(data, ensure_ascii=False, indent=1))
        return 0 if ok else 1
    local = bible_local()
    store = local.load()
    rec = local.new_record(a.url, a.title, a.folder, a.tags, a.note, source="agent")
    res = local.upsert(store, rec)
    local.save(store)
    print(f"{res}: {rec['id']} [{rec['folder']}] {rec['url']}")
    return 0


def cmd_archive(a) -> int:
    if not require_browser():
        return 1
    payload = {"id": a.id} if a.id else {"url": a.url}
    ok, data = call("/api/bible/archive", payload, timeout=90)
    print(json.dumps(data, ensure_ascii=False, indent=1))
    return 0 if ok and data.get("ok") else 1


def cmd_folders(_a) -> int:
    local = bible_local()
    doc = local.load()
    counts: dict[str, int] = {}
    for r in doc["records"]:
        if r.get("status") == "active":
            counts[r["folder"]] = counts.get(r["folder"], 0) + 1
    for fid, name, desc in local.FOLDERS:
        print(f"{counts.get(fid,0):>5}  {name:<18} {desc}")
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="lygo_agent", description="LYGO Browser agent limb")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status").set_defaults(fn=cmd_status)
    sub.add_parser("tabs").set_defaults(fn=cmd_tabs)
    sub.add_parser("folders").set_defaults(fn=cmd_folders)

    p = sub.add_parser("open"); p.add_argument("url")
    p.add_argument("--same-tab", action="store_true"); p.set_defaults(fn=cmd_open)

    p = sub.add_parser("snapshot"); p.add_argument("--tab", type=int, default=None)
    p.add_argument("--chars", type=int, default=4000); p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_snapshot)

    p = sub.add_parser("shot"); p.add_argument("--tab", type=int, default=None)
    p.set_defaults(fn=cmd_shot)

    p = sub.add_parser("search"); p.add_argument("query")
    p.add_argument("--limit", type=int, default=15); p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_search)

    p = sub.add_parser("add"); p.add_argument("url")
    p.add_argument("--title", default=""); p.add_argument("--folder", default="")
    p.add_argument("--tags", default=""); p.add_argument("--note", default="")
    p.set_defaults(fn=cmd_add)

    p = sub.add_parser("archive"); p.add_argument("id", nargs="?", default="")
    p.add_argument("--url", default=""); p.set_defaults(fn=cmd_archive)

    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
