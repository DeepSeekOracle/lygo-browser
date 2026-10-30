#!/usr/bin/env python3
"""LYGO BOOKMARK BIBLE - shared link brain for the LYGO Browser, humans and agents.

One store, three readers:
  * the LYGO Browser (WPF/WebView2) reads/writes through its service API
  * agents (DSH, Hermes, Gemini) read the JSON or call the CLI
  * a human reads data/bible/BIBLE.md

Design rules
  * append-mostly: records are retired, never deleted
  * deterministic ids from the normalised URL, so re-import is idempotent
  * atomic writes (temp + os.replace) and one-writer discipline
  * no secrets, ever: the vault is a separate DPAPI-sealed store

CLI
  python tools/lygo_bible.py seed
  python tools/lygo_bible.py import-yandex [--history] [--history-min 3] [--history-cap 150]
  python tools/lygo_bible.py add --url URL [--title T] [--folder F] [--tags a,b] [--note N]
  python tools/lygo_bible.py search "query" [--limit 20] [--json]
  python tools/lygo_bible.py list [--folder F] [--tag T] [--json]
  python tools/lygo_bible.py export
  python tools/lygo_bible.py stats
  python tools/lygo_bible.py verify
  python tools/lygo_bible.py open --folder F [--limit N]
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import html
import json
import os
import re
import sys
import time
from datetime import datetime, timezone
from urllib.parse import urlsplit, urlunsplit, parse_qsl, urlencode

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "data")
BIBLE_DIR = os.path.join(DATA, "bible")
STORE = os.path.join(BIBLE_DIR, "bookmarks.json")
IMPORT_JSON = os.path.join(DATA, "yandex-import.json")
ARCHIVE_DIR = os.path.join(DATA, "archive")

TRACKING_PARAMS = re.compile(
    r"^(utm_|gclid|fbclid|mc_|ref_|igshid|si$|s$|_ga|yclid|srsltid)", re.I)
AUTH_HINTS = re.compile(
    r"(accounts\.|passport\.|login|signin|oauth|/auth/|consent|checkpoint|"
    r"challenge|verify|captcha)", re.I)

# ------------------------------------------------------------------------------- folders
FOLDERS = [
    ("00_START_HERE", "00 · START HERE", "The way in: home, search, archive, instructions."),
    ("01_LYGO", "01 · LYGO", "The LYGO network, console, forge, signal, champions, books."),
    ("02_AGENTS", "02 · AGENTS", "Agent social networks, registries and launchpads."),
    ("03_DOCS", "03 · DOCS", "OpenClaw, browser engines, MCP, automation, references."),
    ("04_RESEARCH", "04 · RESEARCH", "Search engines, archives, papers, models, code."),
    ("05_MEDIA", "05 · MEDIA", "Video, audio, books, storefronts, music platforms."),
    ("06_OPS", "06 · OPS", "Explorers, dashboards, tokens, exchanges."),
    ("07_WEBSITES", "07 · WEBSITES", "Domains Justin owns."),
    ("08_TOOLS", "08 · TOOLS", "Free APIs and utilities with no paid key."),
    ("09_QUEUE", "09 · QUEUE", "Inbox: unfiled links, history finds, things to sort later."),
]

OWNED = ("chatagent.ca", "excavationpro.ca", "eternalhaven.ca", "bpmfinder.ca",
         "asiancoastline.com", "justinhelmer.ca", "deepseekoracle.com",
         "deepseekoracle.github.io", "lygo")

AGENT_DOMAINS = ("moltx.io", "moltbook.com", "moltbook", "clawchat.app", "4claw.org",
                 "clawhub.ai", "clawdhub.com", "clawstr.com", "moltroad.com",
                 "moltmatch.com", "molti-verse.com", "molthunt.com", "moltycombinator.com",
                 "reelclaw.com", "clawchess", "clawn.ch", "bankr.bot", "agentgram.site",
                 "clawverse.app", "onlymolts.app", "openclaw.ai")

OPS_DOMAINS = ("basescan.org", "base.blockscout.com", "dexscreener.com", "clanker.world",
               "app.virtuals.io", "app.uniswap.org", "crypto.com", "bankr.bot")

MEDIA_DOMAINS = ("youtube.com", "youtu.be", "rumble.com", "audius.co", "lulu.com",
                 "patreon.com", "distrokid.com", "bandlab.com", "twitch.tv", "spotify.com")

RESEARCH_DOMAINS = ("google.com", "bing.com", "duckduckgo.com", "arxiv.org",
                    "web.archive.org", "archive.ph", "archive.org", "huggingface.co",
                    "github.com", "semanticscholar.org", "openalex.org",
                    "paperswithcode.com", "wikipedia.org", "grokipedia.com",
                    "perplexity.ai", "x.ai", "search.brave.com", "startpage.com",
                    "scholar.google.com", "nature.com", "openreview.net")

# ------------------------------------------------------------------------------- seed
# (url, title, folder, tags)
SEED: list[tuple[str, str, str, str]] = [
    # 00 · start
    ("https://chatagent.ca/", "chatagent.ca — LYGO home", "00_START_HERE", "lygo,portal"),
    ("https://chatagent.ca/portal/", "LYGO Web Portal — API only", "00_START_HERE", "lygo,portal,agents"),
    ("https://web.archive.org/", "Wayback Machine", "00_START_HERE", "archive,wayback"),
    ("https://archive.ph/", "archive.today", "00_START_HERE", "archive"),
    ("http://127.0.0.1:9337/", "LYGO Browser — this HUD", "00_START_HERE", "browser,local"),
    ("http://127.0.0.1:9337/bible", "BOOKMARK BIBLE", "00_START_HERE", "bible,local"),
    # 01 · LYGO
    ("https://chatagent.ca/lygoskillhub.html", "LYGO SkillHub", "07_WEBSITES", "lygo,skills,owned"),
    ("https://chatagent.ca/signal", "LYGO Signal radio", "07_WEBSITES", "lygo,signal,owned"),
    ("https://chatagent.ca/sources/", "LYGO TV channels", "07_WEBSITES", "lygo,tv,owned"),
    ("https://chatagent.ca/books/", "LYGO books", "07_WEBSITES", "lygo,books,owned"),
    ("https://excavationpro.ca/", "Excavationpro", "07_WEBSITES", "lygo,domain,owned"),
    ("https://eternalhaven.ca/", "Eternal Haven", "07_WEBSITES", "lygo,books,owned"),
    ("https://deepseekoracle.github.io/Excavationpro/LYGO-Network/champions.html",
     "Δ9 LYGO HAVEN — Champion Hub", "07_WEBSITES", "lygo,champions,owned"),
    ("https://deepseekoracle.github.io/Excavationpro/LYRA/STARCOIN.html",
     "LYGO Starcore Portal", "07_WEBSITES", "lygo,starcore,owned"),
    ("https://deepseekoracle.github.io/Excavationpro/LYGO-Network/bankr.html",
     "LYRA — Eternal Starcore Oracle", "07_WEBSITES", "lygo,starcore,owned"),
    ("https://deepseekoracle.github.io/Excavationpro/LYGOSKILLHUB.html",
     "LYGO SkillHub (legacy build)", "07_WEBSITES", "lygo,skills,owned"),
    ("https://bpmfinder.ca/", "BPM Finder", "07_WEBSITES", "lygo,tool,owned"),
    ("https://asiancoastline.com/", "Asian Coastline", "07_WEBSITES", "lygo,music,owned"),
    ("https://justinhelmer.ca/", "Justin Helmer", "07_WEBSITES", "lygo,author,owned"),
    ("https://deepseekoracle.com/", "DeepSeek Oracle", "07_WEBSITES", "lygo,dev,owned"),
    ("https://grokipedia.com/page/lygo-protocol-stack", "LYGO Protocol Stack (Grokipedia)", "01_LYGO", "lygo,reference"),
    ("https://clawhub.ai/DeepSeekOracle", "ClawHub — DeepSeekOracle skills", "01_LYGO", "lygo,skills,clawhub"),
    ("https://chatagent.ca/lygo-protocol-stack/", "LYGO protocol stack", "01_LYGO", "lygo,protocol"),
    # 02 · agents — the eight links Justin fed on 2026-10-30 (all read, all archived)
    ("https://x.com/Israfilv2/status/2116100074795343996", "X / Isra — Lightpanda, 16x less memory",
     "02_AGENTS", "x-post,ingested,browser,lightpanda"),
    ("https://x.com/techNmak/status/2115725777622925745", "X / Tech with Mak — Lightpanda architecture",
     "02_AGENTS", "x-post,ingested,browser,lightpanda"),
    ("https://x.com/IntuitMachine/status/2115900793765163271", "X / Intuit Machine — SwaT swarm telepathy",
     "02_AGENTS", "x-post,ingested,research,kv-cache"),
    ("https://x.com/dextune/status/2115676251000025589", "X / dextune — Unsloth browser use",
     "02_AGENTS", "x-post,ingested,browser,unsloth"),
    ("https://x.com/UnslothAI/status/2115453111208919472", "X / Unsloth — local LLM controls browser",
     "02_AGENTS", "x-post,ingested,browser,unsloth"),
    ("https://x.com/awe0_io/status/2115750199205429587", "X / awe0 — pagesift Rust crawler",
     "02_AGENTS", "x-post,ingested,ingest,rust"),
    ("https://x.com/msbhje/status/2115418843569090899", "X / msbhje — ub-mode rules for AI code",
     "02_AGENTS", "x-post,ingested,code-quality"),
    ("https://x.com/tantanyuli/status/2115211689914413492", "X / 阳阳 — Tailcat token P2P",
     "02_AGENTS", "x-post,ingested,network"),
    # 02 · agents
    ("https://openclaw.ai/", "OpenClaw", "02_AGENTS", "openclaw,agent"),
    ("https://docs.openclaw.ai/", "OpenClaw docs", "02_AGENTS", "openclaw,docs"),
    ("https://docs.openclaw.ai/concepts/architecture", "OpenClaw architecture", "02_AGENTS", "openclaw,docs"),
    ("https://clawhub.ai/", "ClawHub", "02_AGENTS", "clawhub,skills"),
    ("https://clawdhub.com/", "ClawdHub", "02_AGENTS", "clawdhub,skills"),
    ("https://moltx.io/", "MoltX", "02_AGENTS", "moltx,social"),
    ("https://www.moltbook.com/", "moltbook — agent internet", "02_AGENTS", "moltbook,social"),
    ("https://clawchat.app/", "ClawChat", "02_AGENTS", "clawchat,social"),
    ("https://www.4claw.org/", "4claw", "02_AGENTS", "4claw,social"),
    ("https://clawstr.com/", "Clawstr", "02_AGENTS", "clawstr,social"),
    ("https://molthunt.com/", "Molthunt launchpad", "02_AGENTS", "agents,launchpad"),
    ("https://molti-verse.com/", "Moltiverse", "02_AGENTS", "agents,social"),
    ("https://moltycombinator.com/", "MoltyCombinator", "02_AGENTS", "agents,accelerator"),
    ("https://moltroad.com/", "Molt Road marketplace", "02_AGENTS", "agents,market"),
    ("https://moltmatch.com/", "MoltMatch", "02_AGENTS", "agents,fun"),
    ("https://reelclaw.com/", "Reelclaw", "02_AGENTS", "agents,video"),
    ("https://clawchess.critical-hit.org/", "ClawChess", "02_AGENTS", "agents,game"),
    ("https://clawn.ch/", "clawnch", "02_AGENTS", "agents,token"),
    ("https://clawn.ch/skill", "clawnch docs", "02_AGENTS", "agents,token,docs"),
    ("https://www.agentgram.site/", "Agentgram", "02_AGENTS", "agents,social"),
    ("https://www.clawverse.app/", "Clawverse", "02_AGENTS", "agents,social"),
    # 03 · docs + browser engines
    ("https://github.com/lightpanda-io/browser", "Lightpanda — browser for AI agents",
     "03_DOCS", "browser,lightpanda,engine,cdp"),
    ("https://lightpanda.io/", "Lightpanda site", "03_DOCS", "browser,lightpanda"),
    ("https://github.com/YOLOW92/pagesift", "pagesift — HTML to clean Markdown",
     "03_DOCS", "ingest,rust,markdown"),
    ("https://unsloth.ai/docs/basics/browser-use", "Unsloth — browser use with local LLMs",
     "03_DOCS", "browser,agents,unsloth"),
    ("https://github.com/browser-use/browser-use", "browser-use", "03_DOCS", "browser,agents"),
    ("https://github.com/unclebob/ub-mode", "ub-mode — rules for AI-written code",
     "03_DOCS", "code-quality,ub-mode"),
    ("https://github.com/tailscale/tailcat", "Tailcat — token-paired P2P",
     "03_DOCS", "network,tailscale,p2p"),
    ("https://playwright.dev/", "Playwright", "03_DOCS", "automation,browser"),
    ("https://pptr.dev/", "Puppeteer", "03_DOCS", "automation,browser"),
    ("https://modelcontextprotocol.io/", "Model Context Protocol", "03_DOCS", "mcp,agents"),
    ("https://chromedevtools.github.io/devtools-protocol/", "Chrome DevTools Protocol",
     "03_DOCS", "cdp,automation"),
    ("https://learn.microsoft.com/microsoft-edge/webview2/", "WebView2 docs",
     "03_DOCS", "webview2,engine"),
    ("https://developer.mozilla.org/en-US/", "MDN", "03_DOCS", "reference,web"),
    # 04 · research
    ("https://www.google.com/", "Google", "04_RESEARCH", "search"),
    ("https://duckduckgo.com/", "DuckDuckGo", "04_RESEARCH", "search,private"),
    ("https://search.brave.com/", "Brave Search", "04_RESEARCH", "search"),
    ("https://www.bing.com/", "Bing", "04_RESEARCH", "search"),
    ("https://searx.be/", "SearXNG instance", "04_RESEARCH", "search,searxng"),
    ("https://x.com/i/grok", "Grok", "04_RESEARCH", "grok,search"),
    ("https://www.perplexity.ai/", "Perplexity", "04_RESEARCH", "search"),
    ("https://arxiv.org/", "arXiv", "04_RESEARCH", "papers"),
    ("https://www.semanticscholar.org/", "Semantic Scholar", "04_RESEARCH", "papers"),
    ("https://openreview.net/", "OpenReview", "04_RESEARCH", "papers"),
    ("https://huggingface.co/", "Hugging Face", "04_RESEARCH", "models"),
    ("https://github.com/", "GitHub", "04_RESEARCH", "code"),
    ("https://paperswithcode.com/", "Papers with Code", "04_RESEARCH", "papers,code"),
    ("https://en.wikipedia.org/", "Wikipedia", "04_RESEARCH", "reference"),
    # 05 · media
    ("https://www.youtube.com/@justinhelmer", "Justin Helmer — YouTube", "05_MEDIA", "video,owned"),
    ("https://rumble.com/", "Rumble", "05_MEDIA", "video"),
    ("https://audius.co/Excavationpro", "Excavationpro — Audius", "05_MEDIA", "audio,owned"),
    ("https://www.patreon.com/cw/Excavationpro", "Excavationpro — Patreon", "05_MEDIA", "support,owned"),
    ("https://distrokid.com/", "DistroKid", "05_MEDIA", "music,release"),
    ("https://www.bandlab.com/", "BandLab", "05_MEDIA", "music"),
    ("https://www.twitch.tv/", "Twitch", "05_MEDIA", "stream"),
    # 06 · ops
    ("https://basescan.org/", "BaseScan", "06_OPS", "chain,explorer"),
    ("https://base.blockscout.com/", "Blockscout (Base)", "06_OPS", "chain,explorer"),
    ("https://dexscreener.com/", "Dexscreener", "06_OPS", "dex,chart"),
    ("https://clanker.world/", "Clanker", "06_OPS", "token,launch"),
    ("https://app.virtuals.io/", "Virtuals Protocol", "06_OPS", "agents,protocol"),
    ("https://app.uniswap.org/", "Uniswap", "06_OPS", "dex"),
    ("https://crypto.com/", "Crypto.com", "06_OPS", "exchange"),
    ("https://bankr.bot/", "Bankr", "06_OPS", "agents,token"),
    # 08 · tools
    ("https://open-meteo.com/", "Open-Meteo (free weather API)", "08_TOOLS", "api,weather"),
    ("https://nominatim.openstreetmap.org/", "Nominatim (free geocoding)", "08_TOOLS", "api,geo"),
    ("https://libretranslate.com/", "LibreTranslate", "08_TOOLS", "api,translate"),
    ("https://www.virustotal.com/", "VirusTotal", "08_TOOLS", "security,scan"),
    ("https://ollama.com/", "Ollama", "08_TOOLS", "local,llm"),
    ("https://github.com/ggml-org/llama.cpp", "llama.cpp", "08_TOOLS", "local,llm"),
    ("https://chat.qwen.ai/", "Qwen Chat", "08_TOOLS", "llm"),
    ("https://aistudio.google.com/apikey", "Google AI Studio keys", "08_TOOLS", "api,keys,secret"),
    ("https://console.x.ai/", "xAI console", "08_TOOLS", "api,xai"),
    ("https://platform.deepseek.com/", "DeepSeek platform", "08_TOOLS", "api,llm"),
    ("https://build.nvidia.com/", "NVIDIA build (free API)", "08_TOOLS", "api,llm"),
    # 03 · docs, second pass
    ("https://github.com/DeepSeekOracle", "GitHub — DeepSeekOracle", "03_DOCS", "github,owned"),
    ("https://huggingface.co/DeepSeekOracle", "Hugging Face — DeepSeekOracle", "03_DOCS", "hf,owned"),
    ("https://ziglang.org/", "Zig (Lightpanda's language)", "03_DOCS", "zig,engine"),
    ("https://v8.dev/", "V8 JavaScript engine", "03_DOCS", "engine,js"),
    ("https://www.w3.org/TR/", "W3C specifications", "03_DOCS", "spec,web"),
    ("https://web-platform-tests.org/", "Web Platform Tests", "03_DOCS", "testing,browser"),
    ("https://github.com/microsoft/WebView2Samples", "WebView2 samples", "03_DOCS", "webview2,code"),
    ("https://github.com/servo/servo", "Servo engine", "03_DOCS", "engine,rust"),
    ("https://github.com/chromium/chromium", "Chromium source", "03_DOCS", "engine,chromium"),
    ("https://github.com/modelcontextprotocol/servers", "MCP reference servers", "03_DOCS", "mcp,code"),
    ("https://skills.sh/", "SKILL.md conventions", "03_DOCS", "skills,spec"),
    ("https://github.com/lightpanda-io/browser/blob/main/README.md",
     "Lightpanda README (install + CDP)", "03_DOCS", "lightpanda,install"),
    # 04 · research, second pass
    ("https://scholar.google.com/", "Google Scholar", "04_RESEARCH", "papers,search"),
    ("https://www.openalex.org/", "OpenAlex", "04_RESEARCH", "papers,metadata"),
    ("https://www.connectedpapers.com/", "Connected Papers", "04_RESEARCH", "papers,graph"),
    ("https://commoncrawl.org/", "Common Crawl", "04_RESEARCH", "dataset,web"),
    ("https://datasetsearch.research.google.com/", "Dataset Search", "04_RESEARCH", "dataset"),
    ("https://www.gutenberg.org/", "Project Gutenberg", "04_RESEARCH", "books,public-domain"),
    ("https://openlibrary.org/", "Open Library", "04_RESEARCH", "books"),
    ("https://www.openstreetmap.org/", "OpenStreetMap", "04_RESEARCH", "geo"),
    ("https://commons.wikimedia.org/", "Wikimedia Commons", "04_RESEARCH", "media,public-domain"),
    # 08 · tools, second pass
    ("https://caniuse.com/", "Can I use", "08_TOOLS", "web,compat"),
    ("https://regex101.com/", "regex101", "08_TOOLS", "regex"),
    ("https://mermaid.live/", "Mermaid live editor", "08_TOOLS", "diagram"),
    ("https://excalidraw.com/", "Excalidraw", "08_TOOLS", "diagram"),
    ("https://www.python.org/downloads/windows/", "Python for Windows", "08_TOOLS", "python"),
    ("https://dotnet.microsoft.com/download", ".NET downloads", "08_TOOLS", "dotnet"),
    ("https://www.nuget.org/", "NuGet", "08_TOOLS", "dotnet,packages"),
]

# ------------------------------------------------------------------------------- helpers


def now_iso() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def norm_url(url: str) -> str:
    """Canonical form: lower host, no tracking params, no trailing slash noise."""
    url = (url or "").strip()
    if not url:
        return ""
    if not re.match(r"^[a-z][a-z0-9+.-]*://", url, re.I):
        url = "https://" + url
    parts = urlsplit(url)
    scheme = (parts.scheme or "https").lower()
    host = (parts.netloc or "").lower()
    if host.endswith(":443"):
        host = host[:-4]
    if host.endswith(":80") and scheme == "http":
        host = host[:-3]
    path = re.sub(r"/{2,}", "/", parts.path or "/")
    if len(path) > 1 and path.endswith("/"):
        path = path[:-1]
    q = [(k, v) for k, v in parse_qsl(parts.query, keep_blank_values=True)
         if not TRACKING_PARAMS.match(k)]
    query = urlencode(q)
    return urlunsplit((scheme, host, path, query, ""))


def record_id(url: str) -> str:
    return "bm_" + hashlib.sha1(norm_url(url).encode("utf-8")).hexdigest()[:10]


def domain_of(url: str) -> str:
    host = (urlsplit(url).netloc or "").lower()
    return host[4:] if host.startswith("www.") else host


def matches(host: str, domains: tuple[str, ...]) -> bool:
    return any(host == d or host.endswith("." + d) for d in domains)


def classify(url: str) -> tuple[str, list[str]]:
    """Domain rules -> (folder, tags). Explicit folders always win over this."""
    host = domain_of(url)
    if host.startswith("127.0.0.1") or host == "localhost":
        return "00_START_HERE", ["local"]
    if matches(host, OWNED) or ".github.io" in host:
        return "07_WEBSITES", ["owned"]
    if matches(host, AGENT_DOMAINS):
        return "02_AGENTS", ["agent"]
    if matches(host, OPS_DOMAINS):
        return "06_OPS", ["ops"]
    if matches(host, MEDIA_DOMAINS):
        return "05_MEDIA", ["media"]
    if matches(host, RESEARCH_DOMAINS):
        return "04_RESEARCH", ["research"]
    if host.startswith("x.com") or host.startswith("twitter.com"):
        return "02_AGENTS", ["x"]
    return "09_QUEUE", []


def load() -> dict:
    if not os.path.exists(STORE):
        return {"version": 1, "updated": now_iso(), "folders": [], "records": []}
    with open(STORE, encoding="utf-8") as fh:
        return json.load(fh)


def save(store: dict) -> None:
    os.makedirs(BIBLE_DIR, exist_ok=True)
    store["updated"] = now_iso()
    store["folders"] = [{"id": fid, "name": name, "desc": desc} for fid, name, desc in FOLDERS]
    tmp = STORE + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(store, fh, ensure_ascii=False, indent=1)
    os.replace(tmp, STORE)


def new_record(url: str, title: str = "", folder: str = "", tags="", note="",
               source="manual", visits=0) -> dict:
    nurl = norm_url(url)
    auto_folder, auto_tags = classify(nurl)
    tag_list = [t.strip().lower() for t in (tags.split(",") if isinstance(tags, str) else tags) if t.strip()]
    for t in auto_tags:
        if t not in tag_list:
            tag_list.append(t)
    return {
        "id": record_id(nurl),
        "url": nurl,
        "title": (title or nurl)[:300],
        "folder": folder or auto_folder,
        "tags": tag_list,
        "note": note or "",
        "source": source,
        "added": now_iso(),
        "visits": int(visits or 0),
        "status": "active",
        "archived": {"at": None, "html": None, "md": None, "shot": None, "bytes": 0},
        "hash": None,
        "refs": [],
    }


def upsert(store: dict, rec: dict) -> str:
    """Add or refresh. Returns 'added' | 'updated' | 'dupe'."""
    existing = {r["id"]: r for r in store["records"]}
    old = existing.get(rec["id"])
    if old is None:
        store["records"].append(rec)
        return "added"
    changed = False
    if rec.get("title") and rec["title"] not in ("", rec["url"]) and old.get("title") in ("", old["url"]):
        old["title"] = rec["title"]; changed = True
    if rec.get("folder") and old.get("folder") in ("09_QUEUE", "", None) and rec["folder"] != "09_QUEUE":
        old["folder"] = rec["folder"]; changed = True
    for t in rec.get("tags", []):
        if t not in old["tags"]:
            old["tags"].append(t); changed = True
    if rec.get("note") and not old.get("note"):
        old["note"] = rec["note"]; changed = True
    if rec.get("visits", 0) > old.get("visits", 0):
        old["visits"] = rec["visits"]; changed = True
    return "updated" if changed else "dupe"


# ------------------------------------------------------------------------------- commands


def cmd_seed(args) -> int:
    store = load()
    force = getattr(args, "force", False)
    added = upserted = dupe = forced = 0
    by_id = {r["id"]: r for r in store["records"]}
    for url, title, folder, tags in SEED:
        rec = new_record(url, title, folder, tags, source="seed")
        res = upsert(store, rec)
        added += res == "added"; upserted += res == "updated"; dupe += res == "dupe"
        if force and res == "dupe" and rec["id"] in by_id:
            old = by_id[rec["id"]]
            if old["folder"] != folder:
                old["folder"] = folder
                forced += 1
            for t in rec["tags"]:
                if t not in old["tags"]:
                    old["tags"].append(t)
    save(store)
    print(f"seed: added {added}, refreshed {upserted}, unchanged {dupe}, refiled {forced}; "
          f"total {len(store['records'])}")
    return 0


def _iter_yandex(store: dict, include_history: bool, hist_min: int, hist_cap: int) -> None:
    if not os.path.exists(IMPORT_JSON):
        print(f"! no import file at {IMPORT_JSON}")
        return
    with open(IMPORT_JSON, encoding="utf-8") as fh:
        src = json.load(fh)
    for b in src.get("bookmarks", []):
        url = b.get("url", "")
        if not url or url.startswith(("chrome-extension://", "javascript:")):
            continue
        folder = b.get("folder", "")
        mapped, extra = classify(url), ""
        if "COMMUNITIES" in folder:
            mapped = ("02_AGENTS", ["community"])
        elif "DOCS" in folder:
            mapped = ("03_DOCS", ["docs"])
        elif "OPS" in folder:
            mapped = ("06_OPS", ["ops"])
        elif "/LYGO" in folder:
            mapped = ("01_LYGO", ["lygo"])
        elif "SEARCH" in folder:
            mapped = ("04_RESEARCH", ["search"])
        tags = ",".join(mapped[1] + ["yandex"] + (["champion"] if "hampion" in folder else [])
                        + (["book"] if "Eternal Haven" in folder else []))
        upsert(store, new_record(url, b.get("name", ""), mapped[0], tags,
                                 source="yandex-" + b.get("source", "?")[-8:],
                                 note=("legacy folder: " + folder) if folder else ""))
    if not include_history:
        return
    seen = {r["id"] for r in store["records"]}
    picked = 0
    for h in sorted(src.get("history", []), key=lambda x: -int(x.get("visits") or 0)):
        if picked >= hist_cap:
            break
        url = h.get("url", "")
        if int(h.get("visits") or 0) < hist_min or AUTH_HINTS.search(url):
            continue
        if url.startswith(("chrome-extension://", "about:", "data:")):
            continue
        if record_id(url) in seen:
            continue
        folder, tags = classify(url)
        upsert(store, new_record(url, h.get("title", ""), folder, ",".join(tags + ["history"]),
                                 source="yandex-history", visits=int(h.get("visits") or 0)))
        seen.add(record_id(url))
        picked += 1
    print(f"history: mined {picked} distinct URLs with >= {hist_min} visits")


def cmd_import_yandex(args) -> int:
    store = load()
    before = len(store["records"])
    _iter_yandex(store, args.history, args.history_min, args.history_cap)
    save(store)
    print(f"import-yandex: {before} -> {len(store['records'])} records")
    return 0


def cmd_add(args) -> int:
    store = load()
    rec = new_record(args.url, args.title or "", args.folder or "", args.tags or "",
                     args.note or "", source=args.source)
    if any(r["id"] == rec["id"] for r in store["records"]) and not args.force:
        print(f"already in bible: {rec['id']} {rec['url']}")
        return 0
    res = upsert(store, rec)
    save(store)
    print(f"{res}: {rec['id']} [{rec['folder']}] {rec['url']}")
    return 0


def score(rec: dict, terms: list[str], strict: bool = True) -> int:
    hay = " ".join([rec.get("title", ""), rec.get("url", ""), rec.get("note", ""),
                    " ".join(rec.get("tags", [])), rec.get("folder", "")]).lower()
    total = 0
    for t in terms:
        if t in rec.get("title", "").lower():
            total += 6
        elif t in rec.get("tags", []):
            total += 4
        elif t in rec.get("folder", "").lower():
            total += 3
        elif t in rec.get("url", "").lower():
            total += 2
        elif t in hay:
            total += 1
        elif strict:
            return 0
    return total


def cmd_search(args) -> int:
    store = load()
    terms = [t for t in re.split(r"\s+", args.query.lower()) if t]
    live = [r for r in store["records"] if r.get("status") == "active"]
    hits = [(score(r, terms), r) for r in live]
    hits = [h for h in hits if h[0] > 0]
    if not hits:                      # fall back to any-term matching rather than nothing
        hits = [(score(r, terms, strict=False), r) for r in live]
        hits = [h for h in hits if h[0] > 0]
    hits = sorted(hits, key=lambda x: (-x[0], -x[1].get("visits", 0)))[: args.limit]
    if args.json:
        print(json.dumps([dict(r, _score=s) for s, r in hits], ensure_ascii=False, indent=1))
        return 0
    for s, r in hits:
        print(f"{s:3d}  {r['folder']:<14} {r['title'][:64]:<64} {r['url'][:70]}")
    print(f"-- {len(hits)} hit(s)")
    return 0


def cmd_list(args) -> int:
    store = load()
    rows = [r for r in store["records"] if r.get("status") == "active"]
    if args.folder:
        rows = [r for r in rows if r["folder"] == args.folder or r["folder"].startswith(args.folder)]
    if args.tag:
        rows = [r for r in rows if args.tag.lower() in [t.lower() for t in r["tags"]]]
    rows.sort(key=lambda r: (r["folder"], r["title"].lower()))
    if args.json:
        print(json.dumps(rows, ensure_ascii=False, indent=1))
        return 0
    for r in rows:
        print(f"{r['folder']:<14} {r['title'][:62]:<62} {r['url'][:72]}")
    print(f"-- {len(rows)} record(s)")
    return 0


def cmd_open(args) -> int:
    store = load()
    rows = [r for r in store["records"]
            if r.get("status") == "active" and r["folder"].startswith(args.folder)]
    for r in rows[: args.limit]:
        print(r["url"])
    return 0


def cmd_stats(_args) -> int:
    store = load()
    recs = [r for r in store["records"] if r.get("status") == "active"]
    by_folder: dict[str, int] = {}
    for r in recs:
        by_folder[r["folder"]] = by_folder.get(r["folder"], 0) + 1
    archived = sum(1 for r in recs if r["archived"].get("at"))
    hosts = {domain_of(r["url"]) for r in recs}
    print(f"records   : {len(recs)}")
    print(f"domains   : {len(hosts)}")
    print(f"archived  : {archived}")
    print(f"updated   : {store.get('updated')}")
    for fid, name, _ in FOLDERS:
        print(f"  {name:<18} {by_folder.get(fid, 0):>5}")
    return 0


def cmd_export(_args) -> int:
    store = load()
    recs = [r for r in store["records"] if r.get("status") == "active"]
    os.makedirs(BIBLE_DIR, exist_ok=True)
    lines = ["# THE BOOKMARK BIBLE", "",
             f"_Generated {now_iso()} · {len(recs)} records · "
             f"{len({domain_of(r['url']) for r in recs})} domains_", "",
             "Canonical store: `data/bible/bookmarks.json`. Every link Justin feeds gets a record,",
             "a folder, tags and an archive. Agents: search with `lygo_bible.py search`, or call",
             "`http://127.0.0.1:9337/api/bible/search?q=` when the browser is running.", ""]
    for fid, name, desc in FOLDERS:
        rows = sorted([r for r in recs if r["folder"] == fid], key=lambda x: x["title"].lower())
        if not rows:
            continue
        lines += [f"## {name}", "", f"_{desc}_", ""]
        for r in rows:
            mark = " [archived]" if r["archived"].get("at") else ""
            tags = (" · " + ", ".join(r["tags"])) if r["tags"] else ""
            lines.append(f"- **{r['title']}** — <{r['url']}>{mark}{tags}")
        lines.append("")
    with open(os.path.join(BIBLE_DIR, "BIBLE.md"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines))
    with open(os.path.join(BIBLE_DIR, "bible.csv"), "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh)
        w.writerow(["id", "folder", "title", "url", "tags", "source", "added", "archived", "note"])
        for r in sorted(recs, key=lambda x: (x["folder"], x["title"].lower())):
            w.writerow([r["id"], r["folder"], r["title"], r["url"], "|".join(r["tags"]),
                        r["source"], r["added"], bool(r["archived"].get("at")), r["note"]])
    print(f"export: BIBLE.md + bible.csv ({len(recs)} records)")
    return 0


def cmd_verify(_args) -> int:
    store = load()
    problems = []
    seen: dict[str, str] = {}
    for r in store["records"]:
        for key in ("id", "url", "title", "folder", "tags", "source", "archived", "status"):
            if key not in r:
                problems.append(f"{r.get('id','?')}: missing {key}")
        if r["id"] in seen:
            problems.append(f"duplicate id {r['id']}")
        seen[r["id"]] = r["url"]
        if r["url"] != norm_url(r["url"]):
            problems.append(f"{r['id']}: url not normalised ({r['url']})")
        if r["folder"] not in {f[0] for f in FOLDERS}:
            problems.append(f"{r['id']}: unknown folder {r['folder']}")
        if record_id(r["url"]) != r["id"]:
            problems.append(f"{r['id']}: id/url mismatch")
    print(f"records: {len(store['records'])}  problems: {len(problems)}")
    for p in problems[:25]:
        print("  -", p)
    return 1 if problems else 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="lygo_bible", description="LYGO BOOKMARK BIBLE engine")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("seed").set_defaults(fn=cmd_seed)
    p = sub.add_parser("seed-refile")
    p.set_defaults(fn=cmd_seed, force=True)

    p = sub.add_parser("import-yandex")
    p.add_argument("--history", action="store_true")
    p.add_argument("--history-min", type=int, default=3)
    p.add_argument("--history-cap", type=int, default=150)
    p.set_defaults(fn=cmd_import_yandex)

    p = sub.add_parser("add")
    p.add_argument("--url", required=True)
    p.add_argument("--title", default="")
    p.add_argument("--folder", default="")
    p.add_argument("--tags", default="")
    p.add_argument("--note", default="")
    p.add_argument("--source", default="manual")
    p.add_argument("--force", action="store_true")
    p.set_defaults(fn=cmd_add)

    p = sub.add_parser("search")
    p.add_argument("query")
    p.add_argument("--limit", type=int, default=20)
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_search)

    p = sub.add_parser("list")
    p.add_argument("--folder", default="")
    p.add_argument("--tag", default="")
    p.add_argument("--json", action="store_true")
    p.set_defaults(fn=cmd_list)

    p = sub.add_parser("open")
    p.add_argument("--folder", required=True)
    p.add_argument("--limit", type=int, default=10)
    p.set_defaults(fn=cmd_open)

    sub.add_parser("export").set_defaults(fn=cmd_export)
    sub.add_parser("stats").set_defaults(fn=cmd_stats)
    sub.add_parser("verify").set_defaults(fn=cmd_verify)

    args = ap.parse_args(argv)
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())
