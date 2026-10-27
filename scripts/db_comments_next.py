#!/usr/bin/env python3
"""列出 Comments 词表里下一个批次的待译段（按出现行数排序，剔除型号批次词）。

配套 `dict/src/db_comments.json` 的人工翻译循环：
  python scripts/db_comments_next.py --limit 80        # 打印下一批待译段
  # 译完写回 db_comments.json，然后跑
  python scripts/db_comments_work.py --check

为什么剔除 `Mod`/`Blk`/`Mk`/`Flight`/`Lot` 这类词：中文军事文献本就保留原文
（`F17 Mod 2`、`Mk48 Mod 3`），把它们算成"待译"会让清单虚高、诱导错误改法。
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TABLE = ROOT / "dict" / "src" / "db_comments.json"
FREQ = ROOT / "build" / "comments_freq.json"      # 由 db_comments_work.py 生成
DBS = ROOT / "build" / "comments_segments.json"

DESIGNATION = {"Mod", "Blk", "Block", "Mk", "Mark", "Series", "Model", "Flight",
               "Batch", "Tranche", "Lot", "Phase", "Type", "B/L", "No", "Mods"}
LATIN = re.compile(r"[A-Za-z]")
DIGIT = re.compile(r"\d")

# 数量规则是**单一事实源**在 db_comments_work.py 里；这里 import 复用，
# 不复制一份——两份拷贝漂移过一次（扩展规则后本清单仍把已处理的段算作待译）。
sys.path.insert(0, str(Path(__file__).resolve().parent))
from db_comments_work import QUANTITY  # noqa: E402


def real_words(seg: str) -> list[str]:
    out = []
    for m in re.finditer(r"[A-Za-z0-9][A-Za-z0-9\-/._]*", seg):
        chunk = m.group(0)
        if DIGIT.search(chunk):
            continue
        out += [w for w in re.findall(r"[A-Za-z][A-Za-z']+", chunk)
                if len(w) >= 3 and not w.isupper()]
    return [w for w in out if w not in DESIGNATION]


def covered(seg: str, table: dict[str, str]) -> bool:
    if seg in table:
        return True
    if any(p.match(seg) for p in QUANTITY):   # 结构规则优先，与引擎同序
        return True
    if not DIGIT.search(seg):
        return False
    if not LATIN.search(seg):
        return True
    return seg[:1].isdigit() and len(seg) <= 6


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=80)
    ap.add_argument("--min-rows", type=int, default=1)
    args = ap.parse_args()

    table = json.loads(TABLE.read_text(encoding="utf-8"))
    freq = json.loads(FREQ.read_text(encoding="utf-8")) if FREQ.is_file() else []

    counts: dict[str, int] = {}
    for value, rows in freq:
        for seg in [s.strip() for s in value.split(", ") if s.strip()]:
            if covered(seg, table) or not real_words(seg):
                continue
            counts[seg] = counts.get(seg, 0) + rows

    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    ranked = [(s, n) for s, n in ranked if n >= args.min_rows]
    print(f"待译段 {len(ranked)} 个（已剔除型号批次词与纯代号）")
    for seg, rows in ranked[:args.limit]:
        print(f"{rows:>4}|{seg}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
