#!/usr/bin/env python3
"""Build the 省/州（admin-1）地名审核源 from Natural Earth (R88).

The reference labels on CMO's map come from Stamen Terrain, whose label style is
built on Natural Earth. Our two WWP sets (Cities 4,287 / Synoptic 321) carry only
cities and countries, so every 省/州 label the original map had disappeared once
the label layer was re-rendered in Chinese (verified: a z5 China tile had
JIANGSU/SHANDONG/HENAN/JIANGSU PROVINCE in the original and two sea names in ours).

Source = ``ne_10m_admin_1_states_provinces`` (Natural Earth, public domain), which
ships ``name_zh`` for every feature plus the label anchor (``latitude``/
``longitude``) and ``min_zoom`` — the same knobs the style used to decide when a
province appears. Building from it keeps "as deep as the original" auditable
instead of hand-guessed.

    python scripts/build_regions_placenames.py --dbf <ne_10m_admin_1_states_provinces.dbf>
    python scripts/build_regions_placenames.py --dbf ... --check      # gate: source matches dbf

The reviewed source (``dict/placenames/regions.json``) is committed; this script
only regenerates it from the upstream DBF, so CI never needs the network.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "dict/placenames/regions.json"

HAN = None
# 与 MapLabelRenderer 的显示规则保持一致：min_zoom > 9 的细分单元原版也不画
MAX_MIN_ZOOM = 9.0


def has_han(text: str) -> bool:
    return any("\u4e00" <= c <= "\u9fff" for c in text)


def read_dbf(path: Path) -> list[dict]:
    raw = path.read_bytes()
    nrec, hlen, rlen = struct.unpack_from("<IHH", raw, 4)
    fields, off = [], 32
    while raw[off] != 0x0D:
        name = raw[off:off + 11].split(b"\0")[0].decode("ascii", "replace")
        fields.append((name, chr(raw[off + 11]), raw[off + 16]))
        off += 32
    rows, pos = [], hlen
    for _ in range(nrec):
        rec = raw[pos:pos + rlen]
        pos += rlen
        if rec[0:1] == b"*":
            continue
        p, row = 1, {}
        for name, _ftype, flen in fields:
            # Natural Earth pads with NUL, not spaces.
            row[name] = rec[p:p + flen].decode("utf-8", "replace").replace("\x00", "").strip()
            p += flen
        rows.append(row)
    return rows


def build_rows(rows: list[dict]) -> tuple[list[dict], dict]:
    out, seen = [], set()
    skipped = {"no_name": 0, "no_zh": 0, "too_deep": 0, "duplicate": 0}
    for row in rows:
        name = row.get("name", "")
        zh = row.get("name_zh", "")
        if not name:
            skipped["no_name"] += 1
            continue
        if not zh or not has_han(zh):
            skipped["no_zh"] += 1
            continue
        try:
            lat = round(float(row["latitude"]), 4)
            lon = round(float(row["longitude"]), 4)
            min_zoom = float(row["min_zoom"])
        except (KeyError, ValueError):
            skipped["no_name"] += 1
            continue
        if min_zoom > MAX_MIN_ZOOM:
            skipped["too_deep"] += 1
            continue
        key = (zh, lat, lon)
        if key in seen:
            skipped["duplicate"] += 1
            continue
        seen.add(key)
        out.append({
            "name": name,
            "lat": lat,
            "lon": lon,
            "min_zoom": round(min_zoom, 1),
            "zh": zh,
            "type": row.get("type_en", ""),
            "region": row.get("adm0_a3", ""),
            "source": "https://www.naturalearthdata.com/downloads/10m-cultural-vectors/10m-admin-1-states-provinces/",
            "source_type": "natural_earth_name_zh",
            "note": "省/州级标签：中文取 Natural Earth 官方 name_zh，锚点取同一记录的 label 点（latitude/longitude）；"
                    "min_zoom 沿用上游建议值，与原版 Stamen 标签层的出现层级对齐。",
        })
    out.sort(key=lambda r: (r["region"], r["min_zoom"], r["name"], r["lat"], r["lon"]))
    return out, skipped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dbf", type=Path, required=True)
    parser.add_argument("--out", type=Path, default=OUT)
    parser.add_argument("--check", action="store_true",
                        help="gate: committed source equals a rebuild from the given DBF")
    args = parser.parse_args()

    rows, skipped = build_rows(read_dbf(args.dbf))
    text = json.dumps(rows, ensure_ascii=False, indent=1) + "\n"
    if args.check:
        current = args.out.read_text(encoding="utf-8") if args.out.is_file() else ""
        if current != text:
            print(f"FAIL: {args.out.relative_to(ROOT)} 与 DBF 重建结果不一致（共 {len(rows)} 条）", file=sys.stderr)
            return 1
        print(f"PASS: {args.out.relative_to(ROOT)} 与 DBF 重建一致（{len(rows)} 条）")
        return 0

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(text, encoding="utf-8")
    bands: dict[str, int] = {}
    for row in rows:
        lo = int(row["min_zoom"])
        bands[f"z{lo}"] = bands.get(f"z{lo}", 0) + 1
    print(f"regions: {len(rows)} 条 -> {args.out.relative_to(ROOT)}")
    print("  按 min_zoom 分带:", dict(sorted(bands.items(), key=lambda kv: int(kv[0][1:]))))
    print("  跳过:", skipped)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
