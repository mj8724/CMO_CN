#!/usr/bin/env python3
"""Check reviewed city labels; optionally prove that only approved names changed.

This gate checks reviewed translations, not merely the presence of Chinese.
Unreviewed legacy labels are counted separately and are not certified correct.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from placename_translation import COORDINATE_TOLERANCE, load_reviewed
from wwp import load

ROOT = Path(__file__).resolve().parent.parent
CITY_COUNT = 4287


def matches(item: dict, record: dict) -> bool:
    return (item["name"] in {record[k] for k in ("name", "previous", "zh")}
            and abs(item["lat"] - record["lat"]) <= COORDINATE_TOLERANCE
            and abs(item["lon"] - record["lon"]) <= COORDINATE_TOLERANCE)


def verify_coverage(directory: Path, scopes: dict[str, int]) -> list[str]:
    errors = []
    actual = {path.name: len(json.loads(path.read_text(encoding="utf-8")))
              for path in directory.glob("*.json")}
    if actual.keys() != scopes.keys():
        errors.append(f"review files differ: expected {sorted(scopes)}, found {sorted(actual)}")
    for name, expected in scopes.items():
        if isinstance(expected, bool) or not isinstance(expected, int) or expected <= 0:
            raise ValueError(f"invalid review scope count for {name}")
        if name in actual and actual[name] != expected:
            errors.append(f"{name}: expected {expected} reviewed records, found {actual[name]}")
    return errors


def verify_asset(items: list[dict], reviewed: list[dict],
                 baseline: list[dict] | None = None,
                 expected_count: int = CITY_COUNT) -> dict:
    errors = []
    if len(items) != expected_count:
        errors.append(f"city count: expected {expected_count}, found {len(items)}")
    if not reviewed:
        errors.append("review registry is empty")
    for index, item in enumerate(items):
        if not (math.isfinite(item["lat"]) and -90 <= item["lat"] <= 90
                and math.isfinite(item["lon"]) and -180 <= item["lon"] <= 180):
            errors.append(f"record {index}: invalid coordinates")

    matched = set()
    verified = 0
    for record in reviewed:
        indexes = [i for i, item in enumerate(items) if matches(item, record)]
        label = f"{record['name']} ({record['lat']}, {record['lon']})"
        if len(indexes) != 1:
            errors.append(f"{label}: expected one matching city, found {len(indexes)}")
            continue
        index = indexes[0]
        if index in matched:
            errors.append(f"{label}: multiple reviewed records target city {index}")
        matched.add(index)
        if items[index]["name"] != record["zh"]:
            errors.append(f"{label}: expected {record['zh']}, found {items[index]['name']}")
        else:
            verified += 1

    result = dict(city_records=len(items), reviewed_records=len(reviewed),
                  verified_records=verified, unreviewed_records=len(items) - len(matched),
                  errors=errors)
    if baseline is not None:
        if len(baseline) != len(items):
            errors.append(f"baseline count {len(baseline)} differs from asset {len(items)}")
        changed = 0
        for index, (before, after) in enumerate(zip(baseline, items)):
            if any(before[k] != after[k] for k in ("lat", "lon", "flags")):
                errors.append(f"record {index}: coordinates/flags/order changed")
            if before["name"] != after["name"]:
                changed += 1
                approvals = [r for r in reviewed if matches(before, r)]
                if len(approvals) != 1 or after["name"] != approvals[0]["zh"]:
                    errors.append(f"record {index}: unapproved rename "
                                  f"{before['name']} -> {after['name']}")
        result["changed_records"] = changed
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--asset", type=Path, default=ROOT / "assets/placenames/Cities.wwp")
    parser.add_argument("--review-dir", type=Path, default=ROOT / "dict/placenames_reviewed")
    parser.add_argument("--scopes", type=Path,
                        default=ROOT / "dict/placenames_reviewed_scopes.json")
    parser.add_argument("--baseline", type=Path, help="previous Cities WWP to compare")
    parser.add_argument("--report", type=Path, help="write the complete JSON check result")
    args = parser.parse_args()
    result = verify_asset(load(args.asset), load_reviewed(args.review_dir),
                          load(args.baseline) if args.baseline else None)
    scopes = json.loads(args.scopes.read_text(encoding="utf-8"))["files"]
    result["errors"].extend(verify_coverage(args.review_dir, scopes))
    if args.report:
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8", newline="\n")
    for error in result["errors"][:20]:
        print(f"FAIL: {error}")
    status = "FAIL" if result["errors"] else "PASS"
    detail = (f"{result['city_records']} cities, "
              f"{result['verified_records']}/{result['reviewed_records']} reviewed verified, "
              f"{result['unreviewed_records']} legacy unreviewed")
    if "changed_records" in result:
        detail += f", {result['changed_records']} name changes"
    print(f"{status}: {detail}; {len(result['errors'])} errors")
    return 1 if result["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
