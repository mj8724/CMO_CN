#!/usr/bin/env python3
"""Apply translated placenames to the game's World Wind .wwp files.

Backs up each file as `<name>.wwp.orig` on first apply (so the whole map
localization is reversible with one command) and asserts the entry count and
coordinates are unchanged afterwards -- only the names may differ.

    python apply_placenames.py            apply country + city names
    python apply_placenames.py --verify   report only, change nothing
    python apply_placenames.py --revert   restore every .wwp from .orig
"""
from __future__ import annotations

import argparse
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
from placename_translation import PlacenameResolver, load_dictionary, load_reviewed, translate_items
from wwp import load, save

HAN = __import__("re").compile(r"[\u4e00-\u9fff]")


def process(sets: Path, rel: str, resolver: PlacenameResolver, verify: bool) -> None:
    path = sets / rel
    if not path.is_file():
        print(f"  {rel}: MISSING")
        return
    items = load(path)
    before = [(i["lat"], i["lon"], i["flags"]) for i in items]
    renamed = translate_items(items, resolver)
    after = [(i["lat"], i["lon"], i["flags"]) for i in items]
    assert before == after, f"{rel}: coordinates changed"
    already = sum(1 for i in items if HAN.search(i["name"]))
    print(f"  {rel}: {len(items)} entries, {renamed} to rename, "
          f"{already} already Chinese, coords verified")
    if verify or renamed == 0:
        return
    bak = Path(str(path) + ".orig")
    if not bak.exists():
        shutil.copy(path, bak)
    save(path, items)
    check = load(path)
    assert len(check) == len(items), f"{rel}: entry count changed"
    assert [(i["lat"], i["lon"], i["flags"]) for i in check] == after, \
        f"{rel}: coordinates changed on write"
    not_chinese = [i["name"] for i in check if not HAN.search(i["name"])]
    print(f"    wrote {rel} (backup at {bak.name}); "
          f"{len(check) - len(not_chinese)}/{len(check)} names Chinese")


def revert(sets: Path) -> None:
    n = 0
    for bak in sorted(sets.rglob("*.wwp.orig")):
        target = Path(str(bak)[:-5])
        shutil.copy(bak, target)
        n += 1
        print(f"  restored {target.relative_to(sets)}")
    print(f"reverted {n} files")


def main() -> int:
    ap = argparse.ArgumentParser()
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--verify", action="store_true")
    mode.add_argument("--revert", action="store_true")
    ap.add_argument("--game-dir", type=Path, default=GAME)
    ap.add_argument("--dictionary", type=Path, default=ROOT / "dict/placenames_cities.json")
    ap.add_argument("--review-dir", type=Path, default=ROOT / "dict/placenames_reviewed")
    args = ap.parse_args()
    sets = args.game_dir / "WW/PlacenameSets"

    if args.revert:
        revert(sets)
        return 0

    table = load_dictionary(ROOT / "dict/placenames_countries.json")
    table.update(load_dictionary(args.dictionary))
    reviewed = load_reviewed(args.review_dir)
    resolver = PlacenameResolver(table, reviewed)
    print(f"translation table: {len(table)} names")
    for rel in ("Synoptic/0000_0000.wwp", "Cities/0000_0000.wwp"):
        process(sets, rel, resolver, args.verify)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
