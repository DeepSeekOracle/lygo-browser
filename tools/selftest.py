#!/usr/bin/env python3
"""End-to-end self-test for the LYGO Browser.

Every check is a claim the build makes. If a check fails, the claim is false.
Run it before saying "it works".

  python tools/selftest.py            full suite (browser must be running)
  python tools/selftest.py --quick    skip network/archive/pdf checks
  python tools/selftest.py --json     machine-readable result

Exit code 0 = all passed, 1 = something failed.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import subprocess
import sys
import time
import urllib.request
from contextlib import redirect_stdout

# A cp1252 console turns the report's arrows and bullets into UnicodeEncodeError, which
# exits 1 mid-run and reads exactly like a failed suite. Output encoding is not a test
# result: force UTF-8 (or replace) so a non-UTF-8 stdout cannot fake a failure.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import lygo_agent as agent  # noqa: E402

RESULTS: list[dict] = []
BASE = "http://127.0.0.1:9337"


def check(name):
    def wrap(fn):
        def run(quick: bool):
            if quick and getattr(run, "needs_slow", False):
                RESULTS.append({"name": name, "ok": None, "detail": "skipped (--quick)"})
                return
            t0 = time.time()
            try:
                detail = fn()
                RESULTS.append({"name": name, "ok": True,
                                "detail": detail, "secs": round(time.time() - t0, 2)})
            except Exception as exc:                    # noqa: BLE001
                RESULTS.append({"name": name, "ok": False,
                                "detail": f"{type(exc).__name__}: {exc}",
                                "secs": round(time.time() - t0, 2)})
        run.needs_slow = False
        return run
    return wrap


def slow(fn):
    fn.needs_slow = True
    return fn


def expect(cond, message):
    if not cond:
        raise AssertionError(message)


def post(path: str, body: dict, timeout: int = 60) -> dict:
    ok, data = agent.call(path, body, timeout=timeout)
    if not ok:
        raise AssertionError(f"POST {path} failed: {data}")
    return data


def get(path: str) -> dict:
    ok, data = agent.call(path, timeout=20)
    if not ok:
        raise AssertionError(f"GET {path} failed: {data}")
    return data


# --------------------------------------------------------------------- checks

@check("health")
def c_health():
    h = get("/api/health")
    expect(h.get("ok") is True, "health not ok")
    expect("Edg" in (h.get("engine") or "") or h.get("engine", "").startswith("15"),
           f"unexpected engine {h.get('engine')}")
    expect(int(h.get("recordCount", 0)) > 100, "bible looks empty")
    return f"engine {h['engine']}, {h['recordCount']} records, {h['archiveCount']} archived"


@check("cdp")
def c_cdp():
    with urllib.request.urlopen(BASE.replace("9337", "9333") + "/json/version", timeout=8) as r:
        data = json.loads(r.read().decode("utf-8"))
    expect("Browser" in data, "no Browser field")
    return data["Browser"]


@check("bible-integrity")
def c_bible():
    import lygo_bible
    doc = lygo_bible.load()
    problems = []
    seen = set()
    for rec in doc["records"]:
        if rec["id"] in seen:
            problems.append("dup " + rec["id"])
        seen.add(rec["id"])
        if lygo_bible.record_id(rec["url"]) != rec["id"]:
            problems.append("id/url " + rec["id"])
        if rec["url"] != lygo_bible.norm_url(rec["url"]):
            problems.append("not normalised " + rec["id"])
    expect(not problems, "; ".join(problems[:5]))
    return f"{len(doc['records'])} records, ids and URLs consistent"


@check("bible-search")
def c_search():
    d = get("/api/bible/search?q=lightpanda&limit=5")
    hits = d.get("hits", [])
    expect(hits, "no hits for lightpanda")
    return f"{len(hits)} hits, first: {hits[0]['title'][:50]}"


@check("bible-stats")
def c_stats():
    d = get("/api/bible/stats")
    expect(int(d.get("records", 0)) > 0, "no records")
    expect(len(d.get("byFolder", {})) == 10, "folder count != 10")
    return f"{d['records']} records / {d['domains']} domains / {d['archived']} archived"


@check("tabs")
def c_tabs():
    d = get("/api/agent/status")
    tabs = d.get("tabs", [])
    expect(len(tabs) >= 1, "no tabs")
    return f"{len(tabs)} tab(s), engine {d.get('engine')}"


@check("open-page")
def c_open():
    target = BASE + "/settings"
    post("/api/agent/open", {"url": target, "newTab": True})
    time.sleep(1.6)
    snap = post("/api/agent/snapshot", {}).get("snapshot", {})
    text = snap.get("text") or ""
    expect("SETTINGS" in text.upper(), "settings page text missing")
    expect("HOMEPAGE" in text.upper(), "settings form not rendered")
    return f"{snap.get('title')} · {len(text)} chars"


@check("eval-js")
def c_eval():
    d = post("/api/agent/eval", {"js": "1+1"})
    expect(d.get("result", "").strip().strip('"') == "2", f"got {d.get('result')}")
    return "JavaScript bridge answers 2"


@check("find-in-page")
def c_find():
    d = post("/api/agent/find", {"text": "HOMEPAGE"})
    expect(d.get("ok") is True, f"no match: {d}")
    expect(int(d.get("matches", 0)) >= 1, "zero matches")
    return f"{d['matches']} matches, active #{d['index']}"


@check("zoom")
def c_zoom():
    up = post("/api/agent/zoom", {"factor": 1.5})
    expect(abs(float(up.get("zoom", 0)) - 1.5) < 0.001, f"zoom not applied: {up}")
    post("/api/agent/zoom", {"factor": 1.0})
    return "1.5x applied and reset to 1.0"


@slow
@check("download")
def c_download():
    before = {t["id"] for t in get("/api/agent/status").get("tabs", [])}
    try:
        post("/api/agent/open", {"url": BASE + "/selftest-sample.bin", "newTab": True})
        time.sleep(2.0)
        items = [i for i in get("/api/downloads").get("items", [])
                 if "selftest-sample.bin" in (i.get("url") or "")]
        expect(items, "download was not tracked")
        entry = items[0]                 # newest first; older runs are marked interrupted
        expect(entry.get("state") in ("completed", "in-progress"), f"state {entry.get('state')}")
        expect(os.path.exists(entry.get("path") or ""), "file missing: " + str(entry.get("path")))
        size = os.path.getsize(entry["path"])
        expect(size == 65536, f"expected 65536 bytes, got {size}")
        return f"{os.path.basename(entry['path'])} · {size} bytes · {entry['state']}"
    finally:
        # a download cancels navigation: the tab it opened stays blank. Close it even if the
        # assertions above failed, or every later check runs against a blank page.
        try:
            for tab in get("/api/agent/status").get("tabs", []):
                if tab["id"] not in before:
                    post("/api/agent/close", {"tab": tab["id"]})
        except Exception:                            # noqa: BLE001
            pass


@check("html")
def c_html():
    d = post("/api/agent/html", {})
    expect(int(d.get("bytes", 0)) > 1000, "html too small")
    return f"{d['bytes']} bytes"


@slow
@check("screenshot")
def c_shot():
    d = post("/api/agent/screenshot", {})
    path = d.get("path") or ""
    expect(os.path.exists(path), "no file at " + str(path))
    size = os.path.getsize(path)
    expect(size > 10000, f"screenshot only {size} bytes")
    return f"{os.path.basename(path)} · {size // 1024} KB"


@slow
@check("pdf-export")
def c_pdf():
    d = post("/api/page/pdf", {})
    path = d.get("path") or ""
    expect(os.path.exists(path), "no pdf at " + str(path))
    head = open(path, "rb").read(5)
    expect(head.startswith(b"%PDF"), "not a PDF")
    return f"{os.path.basename(path)} · {os.path.getsize(path) // 1024} KB"


@slow
@check("archive-capture")
def c_archive():
    d = get("/api/bible/search?q=chatagent&limit=10")
    rec = next((h for h in d["hits"] if h.get("url", "").rstrip("/") == "https://chatagent.ca"), None)
    if rec is None:
        rec = d["hits"][0]
    res = post("/api/bible/archive", {"id": rec["id"]}, timeout=120)
    folder = os.path.join(ROOT, "data", "archive", rec["id"])
    for name in ("page.html", "page.txt", "page.md", "meta.json"):
        expect(os.path.exists(os.path.join(folder, name)), "missing " + name)
    meta = json.load(open(os.path.join(folder, "meta.json"), encoding="utf-8"))
    digest = hashlib.sha256(open(os.path.join(folder, "page.html"), "rb").read()).hexdigest()
    expect(digest == meta["sha256"], "sha256 mismatch")
    expect(digest == res.get("sha256", ""), "api sha mismatch")
    return f"{rec['url']} · {meta['bytes']} bytes · sha {digest[:12]}"


@check("vault-roundtrip")
def c_vault():
    secret = "SELFTEST-" + hashlib.sha1(os.urandom(8)).hexdigest()[:10]
    created = post("/api/vault/set", {"label": "zz selftest", "user": "selftest",
                                      "secret": secret, "url": "https://example.com", "notes": ""})
    vid = created["id"]
    try:
        items = get("/api/vault").get("items", [])
        expect(any(i["id"] == vid for i in items), "credential not listed")
        expect(secret not in json.dumps(items), "SECRET LEAKED IN LIST")
        revealed = post("/api/vault/reveal", {"id": vid})
        expect(revealed.get("secret") == secret, "reveal mismatch")
        blob = open(os.path.join(ROOT, "data", "vault", "vault.dat"), "rb").read()
        expect(secret.encode() not in blob, "PLAINTEXT ON DISK")
    finally:
        post("/api/vault/remove", {"id": vid})
    return "set → list (no secret) → reveal (match) → sealed on disk → removed"


@check("vault-fill-guard")
def c_fill():
    created = post("/api/vault/set", {"label": "zz filltest", "user": "u",
                                      "secret": "s", "url": "https://example.com", "notes": ""})
    vid = created["id"]
    try:
        ok, data = agent.call("/api/vault/fill", {"id": vid}, timeout=30)
        expect(ok, "fill call errored")
        expect(data.get("ok") is False, "fill was allowed on the wrong host")
        expect("example.com" in (data.get("error") or ""), "no host explanation")
        return data["error"]
    finally:
        post("/api/vault/remove", {"id": vid})


@check("history")
def c_history():
    d = get("/api/history?limit=50")
    items = d.get("items", [])
    expect(items, "history empty after navigation")
    return f"{len(items)} recent visits, newest: {items[0].get('url', '')[:60]}"


@check("settings-roundtrip")
def c_settings():
    before = get("/api/settings")
    test = dict(before)
    test["searchEngine"] = "ddg" if before.get("searchEngine") != "ddg" else "google"
    post("/api/settings", test)
    after = get("/api/settings")
    expect(after.get("searchEngine") == test["searchEngine"], "setting did not persist")
    post("/api/settings", before)
    restored = get("/api/settings")
    expect(restored.get("searchEngine") == before.get("searchEngine"), "restore failed")
    return f"changed to {test['searchEngine']} and restored {restored.get('searchEngine')}"


@check("ingest-extract")
def c_ingest():
    import lygo_ingest
    from bs4 import BeautifulSoup
    html_doc = """<html><head><title>T</title></head><body>
      <nav>skip me</nav><article><h2>Heading</h2>
      <p>First <strong>bold</strong> paragraph with <a href="https://x.test">a link</a>.</p>
      <ul><li>one</li><li>two</li></ul><pre>code block</pre></article></body></html>"""
    soup = BeautifulSoup(html_doc, "lxml")
    for tag in soup(["nav", "script", "style"]):
        tag.decompose()
    node = lygo_ingest.main_content(soup)
    md = lygo_ingest.to_markdown(node, "T", "https://example.test")
    expect("## Heading" in md, "heading not converted")
    expect("**bold**" in md, "strong not converted")
    expect("[a link](https://x.test)" in md, "link not converted")
    expect("skip me" not in md, "nav leaked into content")
    return f"{len(md)} chars of clean markdown"


@check("quality-gate")
def c_quality():
    import quality_gate
    buf = io.StringIO()
    with redirect_stdout(buf):
        code = quality_gate.main(["--quiet"])
    out = buf.getvalue().strip().splitlines()
    expect(code == 0, out[-1] if out else "quality gate failed")
    return out[-1] if out else "PASS"


@slow
@check("mcp-server")
def c_mcp():
    script = os.path.join(ROOT, "tools", "lygo_mcp_server.py")
    messages = "\n".join([
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                    "params": {"protocolVersion": "2024-11-05", "capabilities": {},
                               "clientInfo": {"name": "selftest", "version": "1"}}}),
        json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}),
        json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                    "params": {"name": "bible_stats", "arguments": {}}}),
    ]) + "\n"
    proc = subprocess.run([sys.executable, script], input=messages, capture_output=True,
                          text=True, timeout=40, cwd=ROOT)
    lines = [ln for ln in proc.stdout.splitlines() if ln.strip()]
    expect(len(lines) >= 3, f"only {len(lines)} responses: {proc.stderr[:300]}")
    parsed = [json.loads(ln) for ln in lines[:3]]
    tools = parsed[1]["result"]["tools"]
    expect(len(tools) >= 10, f"only {len(tools)} tools")
    stats = parsed[2]["result"]["content"][0]["text"]
    expect("records" in stats, "bible_stats returned nothing")
    return f"{len(tools)} MCP tools, bible_stats responded"


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--quick", action="store_true")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args(argv)

    if not agent.alive():
        print("SELFTEST: LYGO Browser is not running. Start LYGO Browser.bat first.")
        return 2

    checks = [c_health, c_cdp, c_bible, c_search, c_stats, c_tabs, c_open, c_eval, c_find,
              c_zoom, c_download, c_html, c_shot, c_pdf, c_archive, c_vault, c_fill, c_history,
              c_settings, c_ingest, c_quality, c_mcp]
    initial = {t["id"] for t in get("/api/agent/status").get("tabs", [])}
    for fn in checks:
        fn(args.quick)

    # leave the browser as we found it: close only the tabs this suite opened
    try:
        for tab in get("/api/agent/status").get("tabs", []):
            if tab["id"] not in initial:
                post("/api/agent/close", {"tab": tab["id"]})
    except Exception:                                    # noqa: BLE001
        pass

    passed = sum(1 for r in RESULTS if r["ok"] is True)
    failed = [r for r in RESULTS if r["ok"] is False]
    skipped = sum(1 for r in RESULTS if r["ok"] is None)

    if args.json:
        print(json.dumps({"passed": passed, "failed": len(failed),
                          "skipped": skipped, "results": RESULTS}, indent=1))
    else:
        for r in RESULTS:
            mark = "PASS" if r["ok"] else ("SKIP" if r["ok"] is None else "FAIL")
            print(f"  [{mark}] {r['name']:<20} {r['detail']}")
        print(f"\nSELFTEST {'PASS' if not failed else 'FAIL'}  "
              f"{passed} passed · {len(failed)} failed · {skipped} skipped")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
