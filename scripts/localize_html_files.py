#!/usr/bin/env python3
"""Translate the tutorial / campaign HTML files on disk.

Runtime DOM injection into WebView2 leaves the on-disk files English: it races
the document load (flicker), can miss nodes, and rewrites paragraph structure.
This script renders the same text segments through the localization engine's own
``XLATE_BATCH`` command - so the file content matches exactly what the engine
would inject - and writes the result back to disk with a ``.orig`` backup.

Usage:  python scripts/localize_html_files.py [--dry-run] [--limit N]
"""
from __future__ import annotations

import html
import json
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import live_harness as H  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
GAME = H.GAME
SCEN = GAME / "Scenarios"
REPORT = ROOT / "build" / "html_localization_report.json"
BATCH = GAME / "CMOLocalizer_XlateBatch.json"

TEXT_NODE = re.compile(r">([^<>]+)<", re.S)
HAN = re.compile(r"[\u4e00-\u9fff]")
SKIP_TAGS = re.compile(r"<(style|script)\b.*?</\1>", re.S | re.I)
# Short label-ish fragments ("<p>OK</p>") are not worth a round trip.
MIN_ASCII = 2


def harvest(path: Path) -> list[str]:
    raw = path.read_text(encoding="utf-8", errors="replace")
    stripped = SKIP_TAGS.sub(" ", raw)
    segments: list[str] = []
    seen: set[str] = set()
    for m in TEXT_NODE.finditer(stripped):
        text = html.unescape(m.group(1))
        for line in text.split("\n"):
            seg = line.strip()
            if len(seg) < 4 or seg in seen:
                continue
            if HAN.search(seg):
                continue
            if len(re.findall(r"[A-Za-z]", seg)) < MIN_ASCII:
                continue
            seen.add(seg)
            segments.append(seg)
    return segments


def translate_batch(segments: list[str]) -> dict[str, str]:
    BATCH.write_text(json.dumps(segments, ensure_ascii=False), encoding="utf-8")
    for _ in range(3):
        res = H.send("XLATE_BATCH " + str(BATCH), timeout=180)
        if res.get("status") == "ok" and res.get("items"):
            out = {}
            for item in res["items"]:
                src = item.get("in", "")
                dst = item.get("briefing") or item.get("string") or ""
                if src and dst and dst != src:
                    out[src] = dst
            return out
        time.sleep(2.0)
    return {}


def main() -> int:
    dry = "--dry-run" in sys.argv
    limit = 0
    for i, a in enumerate(sys.argv):
        if a == "--limit" and i + 1 < len(sys.argv):
            limit = int(sys.argv[i + 1])

    files = sorted(SCEN.rglob("*.html"))
    if limit:
        files = files[:limit]
    print(f"html files: {len(files)}", flush=True)

    per_file: dict[str, list[str]] = {}
    all_segments: list[str] = []
    for f in files:
        segs = harvest(f)
        if segs:
            per_file[str(f)] = segs
            all_segments.extend(segs)
    uniq = sorted(set(all_segments))
    print(f"segments: {len(all_segments)} total, {len(uniq)} unique", flush=True)
    if dry:
        print(json.dumps(uniq[:20], ensure_ascii=False, indent=1))
        return 0

    table: dict[str, str] = {}
    CHUNK = 4000
    for i in range(0, len(uniq), CHUNK):
        part = uniq[i:i + CHUNK]
        table.update(translate_batch(part))
        print(f"  translated {min(i + CHUNK, len(uniq))}/{len(uniq)} "
              f"({len(table)} hits)", flush=True)

    report = {"files": [], "translated_segments": len(table), "missing": []}
    changed = 0
    for f, segs in per_file.items():
        p = Path(f)
        raw = p.read_text(encoding="utf-8", errors="replace")
        missing = [s for s in segs if s not in table]
        out = raw
        hits = 0
        for seg in segs:
            tr = table.get(seg)
            if not tr:
                continue
            # The text node is padded ("<p>Text </p>") and long paragraphs wrap
            # onto several source lines, so the segment is bounded by whitespace
            # or a tag edge rather than by '>' and '<' alone.
            pattern = re.compile(r"(?<=[\s>])" + re.escape(seg) + r"(?=[\s<])")
            new_out, n = pattern.subn(lambda m: tr, out)
            # Some files escape only &lt;/&gt;, some leave '&' raw: try both forms.
            for esc in (seg.replace("<", "&lt;").replace(">", "&gt;"),
                        html.escape(seg, quote=False)):
                if n or esc == seg:
                    break
                pattern = re.compile(r"(?<=[\s>])" + re.escape(esc) + r"(?=[\s<])")
                new_out, n = pattern.subn(lambda m: tr, out)
            if n:
                out = new_out
                hits += 1
        if hits:
            if not dry:
                orig = Path(str(p) + ".orig")
                if not orig.exists():
                    orig.write_text(raw, encoding="utf-8")
                p.write_text(out, encoding="utf-8")
            changed += 1
        report["files"].append({"file": str(p.relative_to(GAME)), "hits": hits,
                                "missing": len(missing)})
        report["missing"].extend(missing[:3])

    has_cjk = 0
    for f in files:
        if HAN.search(f.read_text(encoding="utf-8", errors="replace")):
            has_cjk += 1
    report["files_with_chinese"] = has_cjk
    report["files_total"] = len(files)
    report["files_changed"] = changed
    REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"changed {changed}/{len(files)} files; files with Chinese now {has_cjk}/{len(files)}")
    print(f"report: {REPORT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
