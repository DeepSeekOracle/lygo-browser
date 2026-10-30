#!/usr/bin/env python3
"""Extract Yandex Browser bookmarks + history into data/yandex-import.json.

Reads every Yandex profile it can find, never writes to the browser:
  * User Data\\Default\\Bookmarks        (the old BOOKMARK BRAIN tree)
  * User Data\\Profile 1\\Bookmarks      (the agent-era flat bar)
  * History databases, copied to %TEMP% first so a running browser cannot lock us out

Usage:  python tools/yandex_import.py [--out PATH]
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sqlite3
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(ROOT, "data", "yandex-import.json")

BASES = [
    os.path.join(os.environ.get("LOCALAPPDATA", ""), "Yandex", "YandexBrowser"),
    os.path.join(os.environ.get("APPDATA", ""), "Yandex", "YandexBrowser"),
]

PROFILES = [
    ("yandex-default", os.path.join("User Data", "Default")),
    ("yandex-profile1", os.path.join("User Data", "Profile 1")),
    ("yandex-user-default", os.path.join("User", "Default")),
    ("yandex-openclaw", os.path.join("OpenClawProfile", "Default")),
]


def walk(node, folder, out, source):
    for child in node.get("children", []) or []:
        kind = child.get("type")
        if kind == "url":
            out.append({
                "source": source,
                "folder": folder,
                "name": child.get("name", ""),
                "url": child.get("url", ""),
                "added": child.get("date_added", ""),
            })
        elif kind == "folder":
            walk(child, (folder + "/" + child.get("name", "")).strip("/"), out, source)


def read_bookmarks(path, source, out):
    if not os.path.exists(path):
        return 0
    try:
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as exc:                       # noqa: BLE001 - report, never crash
        print(f"! {path}: {exc}", file=sys.stderr)
        return 0
    before = len(out)
    for root_name, root in (doc.get("roots") or {}).items():
        if isinstance(root, dict):
            walk(root, root.get("name", root_name), out, source)
    return len(out) - before


def read_history(path, source, out):
    if not os.path.exists(path):
        return 0
    tmp = os.path.join(os.environ.get("TEMP", "."), "lygo_yx_" + source + ".sqlite")
    try:
        shutil.copy2(path, tmp)
        con = sqlite3.connect(tmp)
        rows = con.execute(
            "SELECT url, title, visit_count FROM urls ORDER BY visit_count DESC").fetchall()
        con.close()
        os.remove(tmp)
    except Exception as exc:                       # noqa: BLE001
        print(f"! history {path}: {exc}", file=sys.stderr)
        return 0
    for url, title, visits in rows:
        out.append({"source": source, "url": url, "title": title or "", "visits": visits or 0})
    return len(rows)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=DEFAULT_OUT)
    args = ap.parse_args(argv)

    bookmarks: list[dict] = []
    history: list[dict] = []
    for base in BASES:
        if not os.path.isdir(base):
            continue
        for tag, rel in PROFILES:
            prof = os.path.join(base, rel)
            bm = read_bookmarks(os.path.join(prof, "Bookmarks"), tag, bookmarks)
            hs = read_history(os.path.join(prof, "History"), tag, history)
            if bm or hs:
                print(f"  {tag:<22} bookmarks {bm:>4}  history {hs:>5}  ({prof})")

    seen = set()
    unique = []
    for b in bookmarks:
        key = (b.get("url") or "").strip().lower()
        if key and key not in seen:
            seen.add(key)
            unique.append(b)

    payload = {
        "generated": __import__("datetime").datetime.now().isoformat(timespec="seconds"),
        "bookmarks": unique,
        "history": history,
    }
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=1)
    print(f"wrote {args.out}: {len(unique)} unique bookmarks, {len(history)} history rows")
    return 0


if __name__ == "__main__":
    sys.exit(main())
