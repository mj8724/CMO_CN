#!/usr/bin/env python3
"""Apply translated placenames to the game's World Wind .wwp files.

Backs up each file as `<name>.wwp.orig` on first apply (so the whole map
localization is reversible with one command) and asserts the entry count and
coordinates are unchanged afterwards -- only the names may differ.

    python apply_placenames.py            apply country + city names
    python apply_placenames.py --verify   report only, change nothing
    python apply_placenames.py --revert   restore every .wwp from .orig
    python apply_placenames.py --refresh-tiles  redraw existing label tiles even without renames
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
from placename_translation import PlacenameResolver, load_dictionary, load_reviewed, translate_items
from wwp import load, save

HAN = __import__("re").compile(r"[\u4e00-\u9fff]")


def process(sets: Path, rel: str, resolver: PlacenameResolver, verify: bool) -> int:
    path = sets / rel
    if not path.is_file():
        print(f"  {rel}: MISSING")
        return 0
    items = load(path)
    before = [(i["lat"], i["lon"], i["flags"]) for i in items]
    renamed = translate_items(items, resolver)
    after = [(i["lat"], i["lon"], i["flags"]) for i in items]
    assert before == after, f"{rel}: coordinates changed"
    already = sum(1 for i in items if HAN.search(i["name"]))
    print(f"  {rel}: {len(items)} entries, {renamed} to rename, "
          f"{already} already Chinese, coords verified")
    if verify or renamed == 0:
        return renamed
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
    return renamed


def refresh_tiles(game: Path) -> None:
    cache = game / "WW/Cache/Stamen Terrain - Labels"
    if not cache.is_dir() or not any(cache.rglob("*.png")):
        print("label cache: absent or empty, nothing to refresh")
        return
    powershell = Path(os.environ.get("SystemRoot", "C:/Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    subprocess.run([str(powershell), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                    str(ROOT / "scripts/refresh_placename_tiles.ps1"),
                    "-GameDir", str(game.resolve())], check=True)


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
    ap.add_argument("--refresh-tiles", action="store_true",
                    help="redraw existing label tiles even if WWP names are already current")
    ap.add_argument("--game-dir", type=Path, default=GAME)
    ap.add_argument("--dictionary", type=Path, default=ROOT / "dict/placenames_cities.json")
    ap.add_argument("--review-dir", type=Path, default=ROOT / "dict/placenames_reviewed")
    args = ap.parse_args()
    sets = args.game_dir / "WW/PlacenameSets"

    if args.revert:
        revert(sets)
        refresh_tiles(args.game_dir)
        return 0

    table = load_dictionary(ROOT / "dict/placenames_countries.json")
    table.update(load_dictionary(args.dictionary))
    reviewed = load_reviewed(args.review_dir)
    resolver = PlacenameResolver(table, reviewed)
    print(f"translation table: {len(table)} names")
    renamed = sum(process(sets, rel, resolver, args.verify)
                  for rel in ("Synoptic/0000_0000.wwp", "Cities/0000_0000.wwp"))
    if args.verify:
        print("label cache: not verified (--verify never renders or writes tiles)")
    elif renamed or args.refresh_tiles:
        refresh_tiles(args.game_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
