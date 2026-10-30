#!/usr/bin/env python3
"""pagesift-style ingest: URL -> clean Markdown for agents.

The lesson from github.com/YOLOW92/pagesift: an agent does not need the whole noisy page,
it needs the main content as text it can reason over. This does that, locally, with no
browser and no API key, and files the result in the Bookmark Bible when asked.

Usage
  python tools/lygo_ingest.py https://example.com/article
  python tools/lygo_ingest.py https://a https://b --tags rust,ingest
  python tools/lygo_ingest.py --batch links.txt --folder 09_QUEUE
  python tools/lygo_ingest.py https://example.com --no-bible

Outputs
  data/ingest/<slug>.md      the clean Markdown
  data/ingest/<slug>.json    metadata + sha256 + source stats
"""
from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import re
import sys
from datetime import datetime, timezone
from urllib.parse import urlsplit

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "data", "ingest")
sys.path.insert(0, os.path.join(ROOT, "tools"))

DROP_TAGS = ("script", "style", "noscript", "nav", "header", "footer", "aside",
             "form", "iframe", "svg", "button", "template")
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LYGO-Browser/1.1 ingest"


def slugify(url: str) -> str:
    host = (urlsplit(url).hostname or "page").replace(".", "-")
    path = re.sub(r"[^a-zA-Z0-9]+", "-", urlsplit(url).path).strip("-")[:60]
    digest = hashlib.sha1(url.encode("utf-8")).hexdigest()[:8]
    return f"{host}-{path}-{digest}".strip("-")


def fetch(url: str, timeout: int = 25) -> tuple[str, int]:
    import requests
    resp = requests.get(url, timeout=timeout, headers={"User-Agent": UA})
    resp.raise_for_status()
    ctype = resp.headers.get("content-type", "")
    if "html" not in ctype and "xml" not in ctype and "text" not in ctype:
        raise ValueError(f"not a text page: {ctype or 'unknown'}")
    return resp.text, len(resp.content)


def main_content(doc):
    """Prefer <article>/<main>, else the densest <div>, else <body>."""
    for selector in ("article", "main", '[role="main"]', "#content", ".post", ".article"):
        node = doc.select_one(selector)
        if node and len(node.get_text(strip=True)) > 400:
            return node
    body = doc.body or doc
    best, best_len = body, 0
    for div in body.find_all("div", recursive=True)[:400]:
        text = div.get_text(strip=True)
        if len(text) > best_len and len(div.find_all("p")) >= 3:
            best, best_len = div, len(text)
    return best


def to_markdown(node, title: str, url: str) -> str:
    from bs4 import NavigableString, Tag

    out: list[str] = [f"# {title}", "", f"> source: {url}", f"> ingested: {datetime.now(timezone.utc):%Y-%m-%d %H:%M}Z", ""]

    def inline(el) -> str:
        parts = []
        for child in el.children:
            if isinstance(child, NavigableString):
                parts.append(html.unescape(str(child)))
            elif isinstance(child, Tag):
                name = child.name
                text = inline(child)
                if name in ("strong", "b"):
                    parts.append(f"**{text}**")
                elif name in ("em", "i"):
                    parts.append(f"*{text}*")
                elif name == "code":
                    parts.append(f"`{text}`")
                elif name == "a" and child.get("href"):
                    parts.append(f"[{text}]({child['href']})")
                elif name == "br":
                    parts.append("\n")
                else:
                    parts.append(text)
        return re.sub(r"[ \t]+", " ", "".join(parts)).strip()

    for el in node.find_all(["h1", "h2", "h3", "h4", "p", "li", "pre", "blockquote", "td", "th"]):
        name = el.name
        if name in ("h1", "h2", "h3", "h4"):
            out += ["", "#" * int(name[1]) + " " + inline(el), ""]
        elif name == "p":
            text = inline(el)
            if text:
                out += [text, ""]
        elif name == "li":
            text = inline(el)
            if text:
                out.append(f"- {text}")
        elif name == "pre":
            code = el.get_text()
            if code.strip():
                out += ["", "```", code.rstrip(), "```", ""]
        elif name == "blockquote":
            text = inline(el)
            if text:
                out += [f"> {text}", ""]
        elif name in ("td", "th"):
            text = inline(el)
            if text:
                out.append(f"| {text} |")
    text = "\n".join(out)
    return re.sub(r"\n{3,}", "\n\n", text).strip() + "\n"


def ingest(url: str, folder: str, tags: str, note: str, add_bible: bool) -> dict:
    import requests  # noqa: F401  (imported here so --help works without deps)
    from bs4 import BeautifulSoup

    raw, raw_bytes = fetch(url)
    soup = BeautifulSoup(raw, "lxml")
    for tag in soup(list(DROP_TAGS)):
        tag.decompose()
    title = (soup.title.get_text(strip=True) if soup.title else "") or url
    node = main_content(soup)
    body = node.get_text(" ", strip=True) if node else ""
    words = len(body.split())
    md = to_markdown(node, title, url)

    os.makedirs(OUT, exist_ok=True)
    slug = slugify(url)
    md_path = os.path.join(OUT, slug + ".md")
    with open(md_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(md)
    meta = {
        "url": url, "title": title, "slug": slug, "markdown": md_path,
        "sha256": hashlib.sha256(md.encode("utf-8")).hexdigest(),
        "chars": len(md), "words": words, "raw_bytes": raw_bytes,
        "fetched": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "extractor": "lygo_ingest/1.1 (pagesift-style main-content)",
    }
    with open(os.path.join(OUT, slug + ".json"), "w", encoding="utf-8") as fh:
        json.dump(meta, fh, indent=1, ensure_ascii=False)

    if add_bible:
        try:
            import lygo_bible
            store = lygo_bible.load()
            rec = lygo_bible.new_record(url, title, folder, tags,
                                       note or f"ingested -> {os.path.basename(md_path)}",
                                       source="ingest")
            lygo_bible.upsert(store, rec)
            lygo_bible.save(store)
            meta["bible_id"] = rec["id"]
        except Exception as exc:                      # noqa: BLE001
            meta["bible_error"] = str(exc)
    return meta


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="lygo_ingest", description="URL -> clean Markdown + Bible record")
    ap.add_argument("urls", nargs="*")
    ap.add_argument("--batch", help="file with one URL per line")
    ap.add_argument("--folder", default="09_QUEUE")
    ap.add_argument("--tags", default="ingest")
    ap.add_argument("--note", default="")
    ap.add_argument("--no-bible", action="store_true")
    args = ap.parse_args(argv)

    urls = list(args.urls)
    if args.batch:
        with open(args.batch, encoding="utf-8") as fh:
            urls += [ln.strip() for ln in fh if ln.strip() and not ln.startswith("#")]
    if not urls:
        ap.print_help()
        return 2

    failures = 0
    for url in urls:
        try:
            meta = ingest(url, args.folder, args.tags, args.note, not args.no_bible)
            print(f"OK   {meta['words']:>5} words  {meta['chars']:>7} chars  "
                  f"{meta['sha256'][:12]}  {os.path.basename(meta['markdown'])}")
        except Exception as exc:                      # noqa: BLE001
            failures += 1
            print(f"FAIL {url}  {type(exc).__name__}: {exc}", file=sys.stderr)
    print(f"-- ingested {len(urls) - failures}/{len(urls)}")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
