#!/usr/bin/env python3
"""LYGO Browser MCP server (stdio).

Exposes the browser + Bookmark Bible as Model Context Protocol tools, so any MCP client
(Claude Desktop, Hermes, an IDE agent, a local model runner) can drive the same session the
human is looking at. This is the "works with MCP" limb from the Lightpanda/browser-use links.

Transport: newline-delimited JSON-RPC 2.0 on stdin/stdout. Logs go to stderr only.

Run:
  python tools/lygo_mcp_server.py

Register (example, Claude Desktop style):
  { "mcpServers": { "lygo-browser": {
      "command": "C:\\\\Python313\\\\python.exe",
      "args": ["<INSTALL_DIR>\\\\tools\\\\lygo_mcp_server.py"] } } }
"""
from __future__ import annotations

import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))

import lygo_agent as agent  # noqa: E402  (shared API client + token)

SERVER = {"name": "lygo-browser", "version": "1.1.0"}
PROTOCOL = "2024-11-05"


def tool(name, description, props=None, required=None):
    return {
        "name": name,
        "description": description,
        "inputSchema": {
            "type": "object",
            "properties": props or {},
            "required": required or [],
            "additionalProperties": False,
        },
    }


TOOLS = [
    tool("browser_status", "Health of the LYGO Browser plus the open tab list.",
         {}, []),
    tool("browser_open", "Open a URL in a tab and return its clean text.",
         {"url": {"type": "string", "description": "http(s) URL"},
          "new_tab": {"type": "boolean", "default": True},
          "chars": {"type": "integer", "default": 4000},
          "wait_ms": {"type": "integer", "default": 2500}}, ["url"]),
    tool("browser_snapshot", "Title, URL and text of the active (or given) tab.",
         {"tab": {"type": "integer"}, "chars": {"type": "integer", "default": 8000}}, []),
    tool("browser_html", "Raw outerHTML of the active tab, for parsers.",
         {"tab": {"type": "integer"}}, []),
    tool("browser_screenshot", "Full-page PNG of the active tab; returns the file path.",
         {"tab": {"type": "integer"}}, []),
    tool("browser_click", "Click the first element matching a CSS selector.",
         {"selector": {"type": "string"}, "tab": {"type": "integer"}}, ["selector"]),
    tool("browser_type", "Focus a field matching a selector and set its value.",
         {"selector": {"type": "string"}, "text": {"type": "string"},
          "tab": {"type": "integer"}}, ["selector", "text"]),
    tool("browser_eval", "Run JavaScript in the active tab and return the result.",
         {"js": {"type": "string"}, "tab": {"type": "integer"}}, ["js"]),
    tool("bible_search", "Search the Bookmark Bible; returns records with archive state.",
         {"query": {"type": "string"}, "limit": {"type": "integer", "default": 10}}, ["query"]),
    tool("bible_add", "File a link in the Bible (auto-files by domain when no folder given).",
         {"url": {"type": "string"}, "title": {"type": "string"},
          "folder": {"type": "string"}, "tags": {"type": "string"},
          "note": {"type": "string"}}, ["url"]),
    tool("bible_archive", "Archive a record: raw HTML + text + Markdown + PNG + SHA-256.",
         {"id": {"type": "string"}}, ["id"]),
    tool("bible_stats", "Counts per folder, domains, archived.", {}, []),
    tool("history_search", "Search browsing history (raw visits, not curated).",
         {"query": {"type": "string"}, "limit": {"type": "integer", "default": 20}}, []),
]


def text(payload) -> dict:
    if not isinstance(payload, str):
        payload = json.dumps(payload, ensure_ascii=False, indent=1)
    return {"content": [{"type": "text", "text": payload}]}


def call(name: str, a: dict) -> dict:
    if not agent.alive() and name != "bible_stats":
        return {"content": [{"type": "text",
                             "text": "LYGO Browser is not running. Start LYGO Browser.bat."}],
                "isError": True}

    if name == "browser_status":
        ok, data = agent.call("/api/health")
        tabs = agent.call("/api/agent/status")[1] if ok else {}
        return text({"health": data, "tabs": tabs.get("tabs", [])})

    if name == "browser_open":
        ok, opened = agent.call("/api/agent/open",
                                {"url": a["url"], "newTab": a.get("new_tab", True)}, timeout=60)
        if not ok:
            return text(opened or {"error": "open failed"})
        import time
        time.sleep(max(0, a.get("wait_ms", 2500)) / 1000.0)
        ok, snap = agent.call("/api/agent/snapshot", {})
        s = (snap or {}).get("snapshot", {})
        body = (s.get("text") or "")[: int(a.get("chars", 4000))]
        return text(f"{s.get('title')}\n{s.get('url')}\n\n{body}")

    if name == "browser_snapshot":
        ok, snap = agent.call("/api/agent/snapshot", {"tab": a.get("tab")})
        s = (snap or {}).get("snapshot", {})
        body = (s.get("text") or "")[: int(a.get("chars", 8000))]
        return text(f"{s.get('title')}\n{s.get('url')}\n\n{body}")

    if name == "browser_html":
        ok, data = agent.call("/api/agent/html", {"tab": a.get("tab")}, timeout=60)
        return text((data or {}).get("html", "")[:120000])

    if name == "browser_screenshot":
        ok, data = agent.call("/api/agent/screenshot", {"tab": a.get("tab")}, timeout=60)
        return text(data.get("path") or json.dumps(data))

    if name == "browser_click":
        ok, data = agent.call("/api/agent/click",
                              {"selector": a["selector"], "tab": a.get("tab")}, timeout=45)
        return text({"clicked": bool(data.get("ok"))})

    if name == "browser_type":
        ok, data = agent.call("/api/agent/type",
                              {"selector": a["selector"], "text": a["text"], "tab": a.get("tab")},
                              timeout=45)
        return text({"typed": bool(data.get("ok"))})

    if name == "browser_eval":
        ok, data = agent.call("/api/agent/eval", {"js": a["js"], "tab": a.get("tab")}, timeout=45)
        return text({"result": (data or {}).get("result", "")})

    if name == "bible_search":
        ok, data = agent.call(f"/api/bible/search?q={agent.urllib.parse.quote(a['query'])}"
                              f"&limit={int(a.get('limit', 10))}")
        hits = (data or {}).get("hits", [])
        lines = [f"{h['folder']:<14} {h['title'][:64]}  {h['url']}"
                 + ("  [archived]" if (h.get('archived') or {}).get('at') else "") for h in hits]
        return text("\n".join(lines) or "no hits")

    if name == "bible_add":
        ok, data = agent.call("/api/bible/add", {
            "url": a["url"], "title": a.get("title", ""), "folder": a.get("folder", ""),
            "tags": a.get("tags", ""), "note": a.get("note", ""), "source": "mcp"})
        return text(data)

    if name == "bible_archive":
        ok, data = agent.call("/api/bible/archive", {"id": a["id"]}, timeout=120)
        return text(data)

    if name == "bible_stats":
        ok, data = agent.call("/api/bible/stats")
        if not ok:
            import lygo_bible
            doc = lygo_bible.load()
            counts = {}
            for r in doc["records"]:
                counts[r["folder"]] = counts.get(r["folder"], 0) + 1
            return text({"offline": True, "records": len(doc["records"]), "byFolder": counts})
        return text(data)

    if name == "history_search":
        ok, data = agent.call(f"/api/history?limit={int(a.get('limit', 20))}"
                              f"&q={agent.urllib.parse.quote(a.get('query', ''))}")
        items = (data or {}).get("items", [])
        return text("\n".join(f"{i.get('ts','')}  {i.get('title','')[:60]}  {i.get('url','')}"
                              for i in items) or "no history")

    return {"content": [{"type": "text", "text": f"unknown tool: {name}"}], "isError": True}


def handle(msg: dict) -> dict | None:
    method = msg.get("method")
    msg_id = msg.get("id")

    if method == "initialize":
        return {"jsonrpc": "2.0", "id": msg_id, "result": {
            "protocolVersion": msg.get("params", {}).get("protocolVersion", PROTOCOL),
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": SERVER}}
    if method in ("notifications/initialized", "initialized", "notifications/cancelled"):
        return None
    if method == "ping":
        return {"jsonrpc": "2.0", "id": msg_id, "result": {}}
    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": msg_id, "result": {"tools": TOOLS}}
    if method == "tools/call":
        params = msg.get("params", {})
        try:
            result = call(params.get("name", ""), params.get("arguments") or {})
        except Exception as exc:                       # noqa: BLE001
            result = {"content": [{"type": "text", "text": f"{type(exc).__name__}: {exc}"}],
                      "isError": True}
        return {"jsonrpc": "2.0", "id": msg_id, "result": result}
    if method == "resources/list":
        return {"jsonrpc": "2.0", "id": msg_id, "result": {"resources": []}}
    if msg_id is None:
        return None
    return {"jsonrpc": "2.0", "id": msg_id,
            "error": {"code": -32601, "message": f"method not found: {method}"}}


def main() -> int:
    print("lygo-browser MCP server ready on stdio", file=sys.stderr, flush=True)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as exc:
            print(f"bad json: {exc}", file=sys.stderr, flush=True)
            continue
        reply = handle(msg)
        if reply is not None:
            sys.stdout.write(json.dumps(reply, ensure_ascii=False) + "\n")
            sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
