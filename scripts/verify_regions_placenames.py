#!/usr/bin/env python3
"""省/州级地图标签的覆盖门禁（R88）。

背景：地图标签层是我们自己重绘的，而两套 WWP 只有城市（4,287）与国家/海域（321）。
原版 Stamen 标签层里的省/州名（JIANGSU PROVINCE 之类）因此整体消失——
用户看到的就是「地名只到大区，省/市都没了」。

数据来源：Natural Earth `ne_10m_admin_1_states_provinces`（官方 name_zh + label 点 +
min_zoom），由 `scripts/build_regions_placenames.py` 生成 `dict/placenames/regions.json`，
再由 `build_dict.py` 写进词典 `places` 分区（排在 `comments` 之前，旧引擎忽略）。

    python scripts/verify_regions_placenames.py            # 门禁
    python scripts/verify_regions_placenames.py --json     # 机读摘要

检查项：
  1. 审核源存在、条数不少于 3000（防止误删整段数据）；
  2. 每条都有中文、坐标在范围内、min_zoom 落在 0..9（>9 原版也不画）；
  3. (中文, lat, lon) 无重复；
  4. 词典 `places` 分区与审核源一一对应（条数、名字、坐标、min_zoom 全等）。
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "dict/placenames/regions.json"
DICT = ROOT / "dict/CMOLocalizer.json"
MIN_ENTRIES = 3000
MAX_MIN_ZOOM = 9.0


def has_han(text: str) -> bool:
    return any("\u4e00" <= c <= "\u9fff" for c in text)


def collect_problems() -> tuple[list[str], dict]:
    problems: list[str] = []
    if not SOURCE.is_file():
        return [f"缺少审核源 {SOURCE.relative_to(ROOT)}"], {}
    rows = json.loads(SOURCE.read_text(encoding="utf-8"))
    seen = set()
    for i, row in enumerate(rows):
        zh = row.get("zh") or ""
        lat, lon, min_zoom = row.get("lat"), row.get("lon"), row.get("min_zoom")
        if not zh or not has_han(zh):
            problems.append(f"第 {i} 条没有中文名：{row.get('name')!r}")
            continue
        if lat is None or lon is None or not (-90 <= lat <= 90) or not (-180 <= lon <= 180):
            problems.append(f"第 {i} 条坐标非法：{zh}")
        if min_zoom is None or not (0 <= float(min_zoom) <= MAX_MIN_ZOOM):
            problems.append(f"第 {i} 条 min_zoom 非法：{zh} {min_zoom}")
        key = (zh, round(float(lat), 4), round(float(lon), 4))
        if key in seen:
            problems.append(f"重复条目：{zh} {lat},{lon}")
        seen.add(key)
    if len(rows) < MIN_ENTRIES:
        problems.append(f"条数 {len(rows)} 少于下限 {MIN_ENTRIES}（防止整段数据被误删）")

    summary = {"source_entries": len(rows)}
    if not DICT.is_file():
        problems.append("缺少词典产物 dict/CMOLocalizer.json")
        return problems, summary
    places = json.loads(DICT.read_text(encoding="utf-8")).get("places")
    if not isinstance(places, list):
        problems.append("词典缺少职业标签分区 places（R88）")
        return problems, summary
    summary["dictionary_places"] = len(places)
    expected = {(row["zh"], round(float(row["lat"]), 4), round(float(row["lon"]), 4),
                 round(float(row["min_zoom"]), 1)) for row in rows}
    actual = set()
    for entry in places:
        if not isinstance(entry, list) or len(entry) != 4:
            problems.append(f"places 条目形状非法：{entry!r}")
            continue
        actual.add((entry[0], round(float(entry[1]), 4), round(float(entry[2]), 4),
                    round(float(entry[3]), 1)))
    missing = expected - actual
    extra = actual - expected
    if missing:
        problems.append(f"词典缺少 {len(missing)} 条省/州标签，例如 {sorted(missing)[:2]}")
    if extra:
        problems.append(f"词典多出 {len(extra)} 条不在审核源里的标签，例如 {sorted(extra)[:2]}")
    summary["matched"] = len(expected & actual)
    return problems, summary


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()
    problems, summary = collect_problems()
    if args.json:
        print(json.dumps({"problems": problems, **summary}, ensure_ascii=False, indent=1))
    if problems:
        for line in problems[:12]:
            print("  [FAIL] " + line, file=sys.stderr)
        print(f"FAIL: 省/州标签 {summary.get('matched', 0)}/{summary.get('source_entries', 0)}"
              f"（问题 {len(problems)} 条）", file=sys.stderr)
        return 1
    print(f"PASS: 省/州标签 {summary['matched']}/{summary['source_entries']} 全部进词典"
          f"（places={summary['dictionary_places']}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
