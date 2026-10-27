#!/usr/bin/env python3
"""Regression test: parse dict/CMOLocalizer.json the way the engine does.

The engine (`src/engine/CMOLocalizer.cs`, `ParseJsonSections`) is a hand-rolled
scanner that locates the top-level `"offsets"` / `"exact"` / `"partial"` /
`"words"` keys by index and then slices the file between them.  A `_meta` block
carrying fields named `offsets` etc. shifts every boundary and makes the engine
load zero entries -- silently, with a healthy-looking log line.

This module reimplements that scanner faithfully (including its `_meta` hazard)
and asserts the engine would load the same entries the JSON actually contains.
"""
from __future__ import annotations

import json
import re
import sys
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MAIN = ROOT / "dict/CMOLocalizer.json"


def find_top_level_key(text: str, key: str) -> int:
    """Index of `"key"` at depth 1, or -1. Mirrors the C# implementation."""
    depth = 0
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            matches = (j + len(key) < n and text[j + len(key)] == '"'
                       and text[j:j + len(key)] == key)
            if matches and depth == 1:
                return i
            i = j
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    break
                i += 1
            i += 1
            continue
        if c in "{[":
            depth += 1
        elif c in "}]":
            depth -= 1
        i += 1
    return -1


def find_top_level_key_naive(text: str, key: str) -> int:
    """The original buggy lookup, kept to detect the hazard."""
    return text.find(f'"{key}"')


def _decode_escape(nxt: str, block: str, pos: int) -> tuple[str, int]:
    """Mirror the engine's escape handling.  Returns (text, chars_consumed)."""
    simple = {"n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f",
              '"': '"', "\\": "\\", "/": "/"}
    if nxt in simple:
        return simple[nxt], 2
    if nxt == "u" and pos + 5 < len(block):
        hex4 = block[pos + 2:pos + 6]
        if re.fullmatch(r"[0-9a-fA-F]{4}", hex4):
            return chr(int(hex4, 16)), 6
    return "\\" + nxt, 2


def parse_pairs(block: str) -> dict[str, str]:
    """Mirror ParsePairs: pull `"key":"value"` pairs out of a section block."""
    out: dict[str, str] = {}
    pos = 0
    n = len(block)
    while pos < n:
        k0 = block.find('"', pos)
        if k0 < 0:
            break
        pos = k0 + 1
        buf = []
        while pos < n:
            ch = block[pos]
            if ch == "\\" and pos + 1 < n:
                text, used = _decode_escape(block[pos + 1], block, pos)
                buf.append(text)
                pos += used
            elif ch == '"':
                pos += 1
                break
            else:
                buf.append(ch)
                pos += 1
        key = "".join(buf)
        colon = block.find(":", pos)
        if colon < 0:
            break
        pos = colon + 1
        while pos < n and block[pos] not in '"{[':
            pos += 1
        if pos >= n or block[pos] != '"':
            continue
        pos += 1
        buf = []
        while pos < n:
            ch = block[pos]
            if ch == "\\" and pos + 1 < n:
                text, used = _decode_escape(block[pos + 1], block, pos)
                buf.append(text)
                pos += used
            elif ch == '"':
                pos += 1
                break
            else:
                buf.append(ch)
                pos += 1
        if key in ("offsets", "exact", "partial", "words", "comments"):
            continue
        out[key] = "".join(buf)
    return out


def engine_load(text: str) -> dict[str, dict]:
    idx = {name: find_top_level_key(text, name)
           for name in ("comments", "offsets", "exact", "partial", "words")}
    present = sorted(((pos, name) for name, pos in idx.items() if pos >= 0))
    result = {"comments": {}, "offsets": {}, "exact": {}, "partial": {}, "words": {}}
    for i, (start, name) in enumerate(present):
        end = present[i + 1][0] if i + 1 < len(present) else len(text)
        result[name] = parse_pairs(text[start:end])
    return result


def main() -> int:
    text = MAIN.read_text(encoding="utf-8")
    truth = json.loads(text)
    loaded = engine_load(text)

    print(f"file size            : {len(text)} bytes")
    for name in ("comments", "offsets", "exact", "partial", "words"):
        print(f"  {name:<8} json={len(truth.get(name, {})):<7} "
              f"engine={len(loaded[name])}")

    problems = []
    for name in ("comments", "offsets", "exact", "partial", "words"):
        want = truth.get(name, {})
        got = loaded[name]
        if len(want) != len(got):
            problems.append(f"{name}: engine would load {len(got)} of {len(want)}")
        elif want != got:
            diff = [k for k in want if want.get(k) != got.get(k)][:3]
            problems.append(f"{name}: values differ, e.g. {diff}")

    # Reproduce the historical hazard so a future _meta field cannot reintroduce it.
    naive_positions = {n: find_top_level_key_naive(text, n)
                       for n in ("offsets", "exact", "partial", "words")}
    real_positions = {n: find_top_level_key(text, n)
                      for n in ("comments", "offsets", "exact", "partial", "words")}
    hazard = [n for n in naive_positions
              if naive_positions[n] != real_positions[n]]
    if hazard:
        print(f"\nNOTE: naive IndexOf would mis-locate {hazard}; the depth-aware "
              f"scanner is required and is what the engine now uses.")
        order = sorted((p, n) for n, p in naive_positions.items() if p >= 0)
        if order and order[0][1] != "offsets":
            problems.append("naive scan still picks a non-section key first")

    if problems:
        print(f"\nFAIL: {len(problems)} engine-parse problem(s)")
        for p in problems:
            print(f"  - {p}")
        return 1
    print("\nPASS: engine parser would load every entry correctly")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
