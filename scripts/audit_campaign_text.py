#!/usr/bin/env python3
"""Per-campaign localization audit: what a player still reads in English.

Two text surfaces carry campaign briefings, and they fail differently:

* external pages referenced by ``[LOADDOC]name.html[/LOADDOC]`` in the .scen --
  localized by rewriting the HTML file (see localize_html_files.py);
* prose embedded directly in ``<ScenDescription>`` -- localized through the
  dictionary, because the .scen itself is never rewritten.

The audit separates real prose from metadata lines (``Location:``,
``Playable Side:``, ``Date/Time:``) and from the LOADDOC directive itself, so a
"0%" campaign that actually loads translated HTML is not reported as untranslated.

    python scripts/audit_campaign_text.py                 # table
    python scripts/audit_campaign_text.py --json out.json  # machine readable
    python scripts/audit_campaign_text.py --campaign Falklands --samples 20
"""
from __future__ import annotations

import argparse
import html
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict/CMOLocalizer.json"
GAME = Path(os.environ.get("GAME_DIR")
            or "G:/soft/steam/steamapps/common/Command - Modern Operations")

HAN = re.compile(r"[\u4e00-\u9fff]")
TAG = re.compile(r"<[^>]*>")
BLOCK = re.compile(r"</\s*(p|div|li|h[1-6]|tr|td)\s*>|<br\s*/?>", re.I)
RAW = re.compile(r"<(script|style)[^>]*>.*?</\1>", re.S | re.I)
WORD = re.compile(r"[A-Za-z][A-Za-z'-]+")
LOADDOC = re.compile(r"\[LOADDOC\]([^\[]+)\[/LOADDOC\]", re.I)
META_LINE = re.compile(
    r"^(location|playable sides?|date/time|designer|email|version|duration|"
    r"scenario notes|author|difficulty)\s*[:：]", re.I)
CAMPAIGNS = ["Chains Of War", "Desert Storm", "Fail Safe", "Falklands", "Kashmir Fire",
             "Northern Inferno", "Red Tide", "Shifting Sands", "Showcase",
             "The Silent Service", "Tides Of War", "Standalone Scenarios", "Tutorials"]


def decode(raw: bytes) -> str:
    for encoding in ("utf-8-sig", "utf-8", "gb18030"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", "replace")


def chunks(document: str) -> list[str]:
    out = []
    for part in BLOCK.split(RAW.sub(" ", document)):
        if not part:
            continue
        text = re.sub(r"\s+", " ", html.unescape(TAG.sub(" ", part))).strip()
        if text:
            out.append(text)
    return out


def is_prose(text: str) -> bool:
    """Real briefing prose, not a LOADDOC directive or a one-line metadata field."""
    if LOADDOC.search(text) or META_LINE.match(text):
        return False
    if re.fullmatch(r"[^.!?]{0,60}\bvs\.?\b[^.!?]{0,60}", text):
        return False  # "Soviet Union vs. United States"
    return len(WORD.findall(text)) >= 8 or text.count(".") >= 1


def scenario_rows(folder: Path, exact: dict[str, str]) -> tuple[list[dict], set[Path]]:
    rows, referenced = [], set()
    for scen in sorted(folder.rglob("*.scen")):
        title, inline, refs = scen.name, [], []
        try:
            root = ET.parse(scen).getroot()
            title = (root.findtext("ScenTitle") or scen.stem).strip()
            description = root.findtext("ScenDescription") or ""
            refs = [m.strip() for m in LOADDOC.findall(description)]
            inline = [c for c in chunks(description) if is_prose(c)]
        except ET.ParseError:
            description = ""
        for ref in refs:
            referenced.add((scen.parent / ref).resolve())
        untranslated = [c for c in inline if c not in exact]
        rows.append({
            "scen": scen.name, "title": title,
            "pages": [{"ref": r, "exists": (scen.parent / r).is_file()} for r in refs],
            "inline_prose": len(inline), "inline_untranslated": len(untranslated),
            "samples": untranslated[:3],
        })
    return rows, referenced


def page_rows(folder: Path, exact: dict[str, str], referenced: set[Path]) -> dict:
    pages = {}
    for path in sorted(folder.rglob("*.htm*")):
        if path.name.lower().endswith(".orig") or not path.is_file():
            continue
        body = chunks(decode(path.read_bytes()))
        prose = [c for c in body if is_prose(c)]
        english = [c for c in prose if not HAN.search(c)]
        chinese = [c for c in prose if HAN.search(c) and not WORD.search(HAN.sub("", c))]
        pages[str(path.relative_to(folder))] = {
            "prose": len(prose), "english": len(english), "chinese": len(chinese),
            "referenced": path.resolve() in referenced,
            "samples": english[:2],
        }
    return pages


def audit(campaigns: list[str], game: Path) -> dict:
    exact = json.loads(DICT.read_text(encoding="utf-8"))["exact"]
    report = {}
    for name in campaigns:
        folder = game / "Scenarios" / name
        if not folder.is_dir():
            continue
        scenarios, referenced = scenario_rows(folder, exact)
        pages = page_rows(folder, exact, referenced)
        inline_total = sum(r["inline_prose"] for r in scenarios)
        inline_missing = sum(r["inline_untranslated"] for r in scenarios)
        page_prose = sum(p["prose"] for p in pages.values())
        page_english = sum(p["english"] for p in pages.values())
        report[name] = {
            "scenarios": len(scenarios),
            "broken_refs": sum(1 for r in scenarios for p in r["pages"] if not p["exists"]),
            "inline_prose": inline_total, "inline_untranslated": inline_missing,
            "pages": len(pages), "page_prose": page_prose, "page_english": page_english,
            "detail": scenarios, "page_detail": pages,
        }
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=GAME)
    parser.add_argument("--campaign", action="append", dest="campaigns")
    parser.add_argument("--json", type=Path)
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--check", action="store_true",
                        help="gate: exit 1 when any campaign still has English prose")
    args = parser.parse_args()
    report = audit(args.campaigns or CAMPAIGNS, args.game_dir)
    print(f"{'campaign':<20}{'scen':>5}{'内嵌prose':>10}{'未译':>6}{'HTML页':>7}"
          f"{'英文段':>8}{'断裂引用':>9}")
    for name, row in report.items():
        print(f"{name:<20}{row['scenarios']:>5}{row['inline_prose']:>10}"
              f"{row['inline_untranslated']:>6}{row['pages']:>7}"
              f"{row['page_english']:>8}{row['broken_refs']:>9}")
    if args.samples:
        print("\n未译样例：")
        for name, row in report.items():
            shown = 0
            for scen in row["detail"]:
                for sample in scen["samples"][:args.samples]:
                    print(f"  [{name} / {scen['scen'][:34]}] {sample[:120]}")
                    shown += 1
                    if shown >= args.samples:
                        break
                if shown >= args.samples:
                    break
    if args.json:
        args.json.write_text(json.dumps(report, ensure_ascii=False, indent=1),
                             encoding="utf-8")
        print(f"\n-> {args.json}")
    broken = [name for name, row in report.items()
              if row["inline_untranslated"] or row["page_english"] or row["broken_refs"]]
    if broken:
        print("\nFAIL 残留英文: " + ", ".join(broken))
        return 1 if args.check else 0
    print("\n所有战役的简报正文均已汉化，无断裂的 [LOADDOC] 引用")
    return 0


if __name__ == "__main__":
    sys.exit(main())
