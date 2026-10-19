#!/usr/bin/env python3
"""Repair briefing fragments that were translated as whole sentences.

The DLC side-briefing batches were machine translated line by line, but some
lines are short labels ("Air Force", "Active Bases", "DD-963 Spruance").  The
translator answered with a full telegraphic sentence that re-embeds the English
label, e.g. ``Active Bases`` -> ``现用基地Active Bases如令掌握。``.  Rendered in
the UI that reads as gibberish.

This script finds every entry whose value re-embeds its own (short, ASCII) key
plus Chinese sentence filler, strips the filler, and writes the cleaned value to
a late-merged source file so it overrides the broken one.

Usage:  python scripts/fix_sentences.py [--check]
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"
OUT = DICT / "src" / "sentence_fragment_fixes.json"
KEY_RE = re.compile(r"^[ -~]{2,24}$")
HAN = re.compile(r"[\u4e00-\u9fff]")
FILLER = [
    r"如令[^。；，]*",
    r"各部[^。；，]*",
    r"各艇[^。；，]*",
    r"各舰[^。；，]*",
    r"诸部[^。；，]*",
    r"特急",
    r"原文为[^。；，]*",
    r"遵照执行",
    r"各部知悉",
    r"通令",
    r"战备待命",
    r"遵行",
    r"须警惕",
    r"亦有部署",
    r"之部",
    r"迅即出动[^。；，]*",
    r"占领阵位",
    r"如令办理",
    r"即刻作业",
    r"协同行动",
    r"编队协同",
    r"特此通报",
    r"予以掌握",
    r"掌握",
    r"节制",
    r"办理",
    r"研读",
    r"查收",
    r"搜歼",
    r"待战",
    r"清点",
    r"参阅",
    r"监视",
    r"侦察",
    r"待命",
    r"戒备",
]
FILLER_RE = re.compile("|".join(FILLER))


def load(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


REJECT_END = re.compile(
    r"(护航|消灭|执行|待机|增援|监视|巡逻|命令|战备|北上|进驻|潜伏|随行|查明|掌握|节制|办理|所部|基地|战区|第条)$")
REJECT_EXACT = {"基地", "战区", "第条", "所部", "次舰舰名为", "行动命令如下", "番号", "支队", "分遣队", "巡洋舰", "驱逐舰", "护卫舰", "航母"}

# UI-visible labels where the auto-cleanup would lose meaning.
OVERRIDES = {
    "Command": "现代行动：指挥",
    "Active Bases": "现用基地",
    "Air Force": "空军",
    "Air Forces": "空中力量",
    "Air Defence": "防空力量",
    "Coalition Forces": "联军",
    "Civil traffic": "民船动态",
    "Dynamic Targets": "动态目标",
    "Disrupt": "破袭任务",
    "Designers Notes": "设计者注",
    "CONDUCT": "行动命令",
    "Environmental groups": "环保团体",
    "First": "第一",
    "Naval Forces": "海军力量",
    "Submarine Forces": "潜艇部队",
    "Surface Forces": "水面舰艇部队",
    "Friendly Forces": "友军部队",
    "Enemy Forces:": "敌情通报：",
    "Air Threats:": "空中威胁：",
    "Air threats:": "空中威胁：",
    "Air Assets:": "空中兵力：",
    "Baltic Fleet:": "波罗的海舰队：",
    "Air Army:": "航空集团军：",
}


def clean(key: str, value: str) -> str:
    v = re.sub(re.escape(key), "", value, flags=re.I)
    v = FILLER_RE.sub("", v)
    v = re.sub(r"[（(]\s*[)）]", "", v)
    v = re.sub(r"\s+", " ", v)
    v = re.sub(r"[，、]{2,}", "，", v)
    v = v.strip(" ，、。；:：-")
    # A proper name translated into a bare generic noun, or a leftover sentence
    # tail, is worse than leaving the original English in place.
    if not v or v in REJECT_EXACT or REJECT_END.search(v):
        return ""
    if len(v) < 2:
        return ""
    return v


def main() -> int:
    check = "--check" in sys.argv
    doc = load(DICT / "CMOLocalizer.json")
    fixes: dict[str, str] = {}
    dropped: list[str] = []
    report: list[tuple[str, str, str]] = []
    for key, value in doc.get("exact", {}).items():
        if not KEY_RE.match(key):
            continue
        value = str(value)
        if not HAN.search(value):
            continue
        if key.lower() not in value.lower():
            continue
        if not re.search(r"[。；]", value):
            continue
        fixed = OVERRIDES.get(key) or clean(key, value)
        if fixed and fixed != value:
            fixes[key] = fixed
            report.append((key, value, fixed))
        elif not fixed:
            dropped.append(key)

    print(f"suspect entries: {len(report) + len(dropped)}")
    print(f"repaired: {len(report)}  dropped(will fall back to English): {len(dropped)}")
    for key, old, new in report[:40]:
        print(f"  {key!r}: {old!r} -> {new!r}")
    for key in dropped[:20]:
        print(f"  DROP {key!r}")

    new_fixes: dict[str, str] = {}
    for key, _old, new in report:
        new_fixes[key] = new

    existing: dict[str, str] = {}
    if OUT.is_file():
        try:
            existing = {k: v for k, v in load(OUT).items() if isinstance(v, str)}
        except Exception:
            existing = {}
    fixes = dict(existing)
    fixes.update(new_fixes)

    if check:
        return 0
    OUT.write_text(json.dumps(fixes, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({len(fixes)} entries)")

    # Broken entries that cannot be repaired are purged so the UI falls back to
    # the original English instead of showing a telegraphic pseudo-translation.
    bl_path = DICT / "src" / "blacklist.json"
    bl = set(load(bl_path)) if bl_path.is_file() else set()
    before = len(bl)
    bl.update(dropped)
    if len(bl) != before:
        bl_path.write_text(json.dumps(sorted(bl), ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"blacklist += {len(bl) - before} keys ({len(bl)} total)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
