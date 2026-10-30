#!/usr/bin/env python3
"""ub-mode style quality gate for the LYGO Browser C# source.

Three checks, the same three unclebob's ub-mode enforces for AI-written code:
  1. method length      - a method that does not fit on a screen is hiding a second job
  2. complexity         - decision points per method (the classic <= 10 rule)
  3. duplication        - identical normalised blocks repeated across the codebase

Exit 0 when the code passes, 1 when it does not. Intended to run before every publish.

Usage:
  python tools/quality_gate.py [--src DIR] [--max-lines 60] [--max-complexity 12] [--quiet]
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_SRC = os.path.join(ROOT, "src", "LygoBrowser")

# A method head: modifiers + return type (incl. tuples) + name + params, then { or =>
METHOD = re.compile(
    r"^[ \t]*(?:(?:public|private|protected|internal|static|async|sealed|override|virtual|"
    r"new|partial|extern|unsafe)\s+)+"
    r"(?:(?:[\w<>\[\],\.\?]+|\([^()]*\))\s+)?(\w+)\s*\(([^;{}]*)\)\s*(?:\{|=>)",
    re.MULTILINE)
DECISIONS = re.compile(r"\b(if|for|foreach|while|case|catch|switch|when)\b|&&|\|\||\?\?")
STRING_OR_COMMENT = re.compile(r'("(?:[^"\\]|\\.)*")|(//[^\n]*)|(/\*.*?\*/)', re.DOTALL)


def strip_noise(text: str) -> str:
    """Blank out comments and string bodies so they never count as decisions."""
    def repl(m):
        if m.group(1) is not None:
            return '""'
        return " "
    return STRING_OR_COMMENT.sub(repl, text)


def brace_span(lines: list[str], start: int) -> int:
    """Return the 1-based line where the body opened on `start` closes.

    Expression-bodied members (=> ...;) are one logical line even when wrapped, so they
    never swallow the methods that follow them.
    """
    head = lines[start] if start < len(lines) else ""
    if "=>" in head and "{" not in head.split("=>", 1)[1]:
        depth_paren = 0
        for i in range(start, len(lines)):
            for ch in lines[i]:
                if ch in "([{":
                    depth_paren += 1
                elif ch in ")]}":
                    depth_paren -= 1
            if lines[i].rstrip().endswith(";") and depth_paren <= 0:
                return i + 1
        return start + 1

    depth = 0
    opened = False
    for i in range(start, len(lines)):
        for ch in lines[i]:
            if ch == "{":
                depth += 1
                opened = True
            elif ch == "}":
                depth -= 1
                if opened and depth <= 0:
                    return i + 1
        if opened and depth == 0:
            return i + 1
    return len(lines)


def scan_file(path: str) -> tuple[list[dict], list[str]]:
    raw = open(path, encoding="utf-8", errors="replace").read()
    clean = strip_noise(raw)
    lines = clean.splitlines()
    raw_lines = raw.splitlines()
    findings: list[dict] = []

    for m in METHOD.finditer(clean):
        name = m.group(1)
        if name in ("if", "for", "foreach", "while", "switch", "catch", "using"):
            continue
        start_line = clean[: m.start()].count("\n")
        end_line = brace_span(lines, start_line)
        body = "\n".join(lines[start_line:end_line])
        length = end_line - start_line
        complexity = 1 + len(DECISIONS.findall(body))
        findings.append({
            "file": os.path.relpath(path, ROOT), "name": name,
            "line": start_line + 1, "length": length, "complexity": complexity,
            "signature": raw_lines[start_line].strip()[:110] if start_line < len(raw_lines) else "",
        })
    return findings, raw_lines


def duplication(files: list[str], window: int = 6, min_count: int = 3) -> list[tuple[str, int]]:
    """Hash normalised windows of `window` lines; report blocks seen `min_count`+ times."""
    buckets: dict[str, list[tuple[str, int]]] = {}
    for path in files:
        lines = [ln.strip() for ln in open(path, encoding="utf-8", errors="replace").read().splitlines()]
        lines = [ln for ln in lines if ln and ln not in ("{", "}", "};", ");")]
        for i in range(0, max(0, len(lines) - window)):
            block = "\n".join(lines[i:i + window])
            if len(block) < 160:          # tiny windows are noise
                continue
            key = hashlib.sha1(block.encode("utf-8")).hexdigest()
            buckets.setdefault(key, []).append((os.path.relpath(path, ROOT), i + 1))
    dupes = []
    for hits in buckets.values():
        if len(hits) >= min_count:
            first = hits[0]
            dupes.append((f"{first[0]}:{first[1]}", len(hits)))
    return dupes


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=DEFAULT_SRC)
    ap.add_argument("--max-lines", type=int, default=60)
    ap.add_argument("--max-complexity", type=int, default=12)
    ap.add_argument("--quiet", action="store_true")
    args = ap.parse_args(argv)

    files = sorted(
        os.path.join(dp, f)
        for dp, _, fs in os.walk(args.src)
        for f in fs if f.endswith(".cs") and "obj" not in dp and "bin" not in dp)
    if not files:
        print("quality_gate: no C# sources found at " + args.src)
        return 1

    all_findings: list[dict] = []
    total_lines = 0
    for path in files:
        found, lines = scan_file(path)
        total_lines += len(lines)
        all_findings.extend(found)

    long_methods = [f for f in all_findings if f["length"] > args.max_lines]
    complex_methods = [f for f in all_findings if f["complexity"] > args.max_complexity]
    dupes = duplication(files)

    if not args.quiet:
        print(f"quality_gate  files {len(files)}  lines {total_lines}  methods {len(all_findings)}")
        print(f"  limits      method <= {args.max_lines} lines, complexity <= {args.max_complexity}")
        for f in sorted(long_methods, key=lambda x: -x["length"])[:12]:
            print(f"  LONG        {f['file']}:{f['line']} {f['name']}  {f['length']} lines")
        for f in sorted(complex_methods, key=lambda x: -x["complexity"])[:12]:
            print(f"  COMPLEX     {f['file']}:{f['line']} {f['name']}  cc={f['complexity']}")
        for where, count in dupes[:12]:
            print(f"  DUPLICATE   {where}  seen {count}x")

    failures = len(long_methods) + len(complex_methods) + len(dupes)
    print(f"quality_gate: {'PASS' if failures == 0 else 'FAIL'} "
          f"({len(long_methods)} long, {len(complex_methods)} complex, {len(dupes)} duplicate blocks)")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
