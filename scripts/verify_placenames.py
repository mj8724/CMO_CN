#!/usr/bin/env python3
"""Check complete reviewed label coverage and optionally compare a baseline.

This gate checks reviewed translations, not merely the presence of Chinese.
The CLI requires every label to be reviewed, including unchanged translations.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from pathlib import Path

from placename_translation import (COORDINATE_TOLERANCE, PLACENAME_SETS,
                                   PlacenameResolver, load_reviewed)
from wwp import load

ROOT = Path(__file__).resolve().parent.parent


def load_metadata_sha256(path: Path, placename_set: str) -> str:
    value = json.loads(path.read_text(encoding="utf-8"))[placename_set]
    if not isinstance(value, str):
        raise ValueError(f"{placename_set}: metadata fingerprint must be a string")
    return value


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
                 expected_count: int = 4287, *, require_complete: bool = False,
                 expected_metadata_sha256: str | None = None) -> dict:
    errors = []
    resolver = PlacenameResolver({}, reviewed)
    if len(items) != expected_count:
        errors.append(f"placename count: expected {expected_count}, found {len(items)}")
    if not reviewed:
        errors.append("review registry is empty")
    for index, item in enumerate(items):
        if not (math.isfinite(item["lat"]) and -90 <= item["lat"] <= 90
                and math.isfinite(item["lon"]) and -180 <= item["lon"] <= 180):
            errors.append(f"record {index}: invalid coordinates")

    if expected_metadata_sha256 is not None:
        metadata = struct.pack("<i", len(items)) + b"".join(
            struct.pack("<ffI", item["lat"], item["lon"], item["flags"]) for item in items)
        if hashlib.sha256(metadata).hexdigest() != expected_metadata_sha256:
            errors.append("coordinates/flags/order differ from canonical metadata baseline")

    by_name = {}
    for index, item in enumerate(items):
        by_name.setdefault(item["name"], []).append(index)
    matched = set()
    verified = 0
    for record in reviewed:
        indexes = [i for alias in {record[k] for k in ("name", "previous", "zh")}
                   for i in by_name.get(alias, ()) if matches(items[i], record)]
        label = f"{record['name']} ({record['lat']}, {record['lon']})"
        if len(indexes) != 1:
            errors.append(f"{label}: expected one matching placename, found {len(indexes)}")
            continue
        index = indexes[0]
        if index in matched:
            errors.append(f"{label}: multiple reviewed records target placename {index}")
        matched.add(index)
        if items[index]["name"] != record["zh"]:
            errors.append(f"{label}: expected {record['zh']}, found {items[index]['name']}")
        else:
            verified += 1

    unreviewed = len(items) - len(matched)
    if require_complete and unreviewed:
        errors.append(f"complete review required: {unreviewed} unreviewed placenames")
    result = dict(placename_records=len(items), reviewed_records=len(reviewed),
                  verified_records=verified, unreviewed_records=unreviewed,
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
                approval = resolver.reviewed_match(before)
                if approval is None or after["name"] != approval["zh"]:
                    errors.append(f"record {index}: unapproved rename "
                                  f"{before['name']} -> {after['name']}")
        result["changed_records"] = changed
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--set", choices=PLACENAME_SETS, default="Cities", dest="placename_set")
    parser.add_argument("--asset", type=Path)
    parser.add_argument("--review-dir", type=Path)
    parser.add_argument("--scopes", type=Path,
                        default=ROOT / "dict/placenames_reviewed_scopes.json")
    parser.add_argument("--baseline", type=Path, help="previous or original WWP to compare")
    parser.add_argument("--metadata-baseline", type=Path,
                        default=ROOT / "dict/placenames_metadata.json",
                        help="fixed metadata fingerprints; custom fixtures must provide their own")
    parser.add_argument("--report", type=Path, help="write the complete JSON check result")
    args = parser.parse_args()
    config = PLACENAME_SETS[args.placename_set]
    asset = args.asset or ROOT / f"assets/placenames/{args.placename_set}.wwp"
    review_dir = args.review_dir or ROOT / "dict" / config["review_dir"]
    result = verify_asset(load(asset), load_reviewed(review_dir),
                          load(args.baseline) if args.baseline else None,
                          expected_count=config["count"], require_complete=True,
                          expected_metadata_sha256=load_metadata_sha256(
                              args.metadata_baseline, args.placename_set))
    scopes = json.loads(args.scopes.read_text(encoding="utf-8"))[config["scope_key"]]
    result["errors"].extend(verify_coverage(review_dir, scopes))
    if args.report:
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8", newline="\n")
    for error in result["errors"][:20]:
        print(f"FAIL: {error}")
    status = "FAIL" if result["errors"] else "PASS"
    detail = (f"{result['placename_records']} {args.placename_set} labels, "
              f"{result['verified_records']}/{result['reviewed_records']} reviewed verified, "
              f"{result['unreviewed_records']} unreviewed")
    if "changed_records" in result:
        detail += f", {result['changed_records']} name changes"
    print(f"{status}: {detail}; {len(result['errors'])} errors")
    return 1 if result["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
