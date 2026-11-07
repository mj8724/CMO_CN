#!/usr/bin/env python3
"""Briefing paragraph coverage gate.

The DLC briefings are Word-pasted HTML: one `<P>` holds dozens of `<SPAN>`s, and the
runtime only sees whole paragraphs after `InnerText`. A paragraph therefore needs a
**whole-sentence** dictionary entry; fragment entries ("The" -> 这) can only produce
word salad (R81).

This gate keeps that honest without launching the game:

    python scripts/audit_briefing_paragraphs.py --check
        every paragraph in dict/briefing_source_paragraphs.json must have a Chinese
        translation in dict/CMOLocalizer.json

    python scripts/audit_briefing_paragraphs.py --export DIR
        rebuild the manifest from captured `BRIEFING_RAW_DUMP` HTML files (dev only)

The manifest is the English text exactly as the engine normalizes it (whitespace
collapsed, `&nbsp;` to space), so a manifest entry *is* the dictionary key.
"""
from __future__ import annotations

import argparse
import html
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict/CMOLocalizer.json"
MANIFEST = ROOT / "dict/briefing_source_paragraphs.json"
BLOCKS = ("p", "li", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6", "div")
NAME = re.compile(r"[A-Za-z][A-Za-z'-]*")
HAN = re.compile(r"[\u4e00-\u9fff]")
# A translation may keep designations in Latin; what it must not keep is a run of
# ordinary English words (that is the "half translated" signature).
LATIN_RUN = re.compile(r"[A-Za-z][A-Za-z'-]*(?:\s+[A-Za-z][A-Za-z'-]*){3,}")


def normalize(text: str) -> str:
    """Mirror CMOLocalizer.NormalizeBriefingWhitespace."""
    text = html.unescape(text)
    text = text.replace("\u00a0", " ").replace("\r", " ").replace("\n", " ").replace("\t", " ")
    return re.sub(r"\s+", " ", text).strip()


def paragraphs(document: str) -> list[str]:
    out = []
    for tag in BLOCKS:
        for m in re.finditer(rf"<{tag}\b[^>]*>(.*?)</{tag}>", document, re.S | re.I):
            body = m.group(1)
            if re.search(r"<(p|li|td|th|h[1-6]|div|table|img)\b", body, re.I):
                continue
            if re.search(r"<(a|img|table|input|select|textarea|button|object|iframe)\b", body, re.I):
                continue
            text = normalize(re.sub(r"<[^>]+>", "", body))
            if text:
                out.append(text)
    return out


def export(raw_dir: Path) -> int:
    seen: set[str] = set()
    for path in sorted(raw_dir.glob("*.html")):
        for text in paragraphs(path.read_text(encoding="utf-8", errors="replace")):
            if len(NAME.findall(text)) < 4:
                continue          # 编号/短标签行
            if HAN.search(text):
                continue          # 已经是半汉化产物，不能当原文
            seen.add(text)
    rows = sorted(seen)
    MANIFEST.write_text(json.dumps(rows, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"manifest: {len(rows)} 段 -> {MANIFEST.relative_to(ROOT)}")
    return 0


def check() -> int:
    if not MANIFEST.is_file():
        print(f"FAIL: 缺少清单 {MANIFEST.relative_to(ROOT)}", file=sys.stderr)
        return 1
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    exact = json.loads(DICT.read_text(encoding="utf-8"))["exact"]
    missing, half = [], []
    for text in manifest:
        value = exact.get(text)
        if value is None:
            missing.append(text)
        elif not HAN.search(value) or LATIN_RUN.search(value):
            half.append((text, value))
    for text in missing[:12]:
        print(f"  [缺译文] {text[:110]}", file=sys.stderr)
    for text, value in half[:12]:
        print(f"  [半译] {value[:110]}", file=sys.stderr)
    total = len(manifest)
    if missing or half:
        print(f"FAIL: 简报整段覆盖 {total - len(missing) - len(half)}/{total}"
              f"（缺译文 {len(missing)}，半译 {len(half)}）", file=sys.stderr)
        return 1
    print(f"PASS: 简报整段覆盖 {total}/{total}（整段键命中且无英文残留）")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="门禁：清单里每段都必须有整段中文")
    parser.add_argument("--export", type=Path, help="从 BRIEFING_RAW_DUMP 抓取的 HTML 目录重建清单")
    args = parser.parse_args()
    if args.export:
        return export(args.export)
    if args.check:
        return check()
    parser.error("需要 --check 或 --export DIR")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
