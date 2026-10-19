#!/usr/bin/env python3
"""Apply translated placenames to the game's World Wind .wwp files.

Backs up each file as `<name>.wwp.orig` on first apply (so the whole map
localization is reversible with one command) and asserts the entry count and
coordinates are unchanged afterwards -- only the names may differ.
By default only coordinate-matched approved reviews can rename points.
Unknown points retain their names; custom dictionaries require explicit flags.

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
from placename_translation import (PLACENAME_SETS, PlacenameResolver, load_dictionary,
                                   load_reviewed, translate_items)
from wwp import load, save


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
    reviewed = sum(1 for item in items if resolver.reviewed_match(item) is not None)
    print(f"  {rel}: {len(items)} entries, {renamed} to rename, "
          f"{reviewed} reviewed matches after translation, coords verified")
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
    print(f"    wrote {rel} (backup at {bak.name}); "
          f"{reviewed}/{len(check)} names match reviewed labels")
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
    ap.add_argument("--dictionary", type=Path, help="explicit custom Cities name mapping")
    ap.add_argument("--synoptic-dictionary", type=Path,
                    help="explicit custom Synoptic name mapping")
    ap.add_argument("--review-dir", type=Path, default=ROOT / "dict/placenames_reviewed")
    ap.add_argument("--synoptic-review-dir", type=Path,
                    default=ROOT / "dict/placenames_synoptic_reviewed")
    args = ap.parse_args()
    sets = args.game_dir / "WW/PlacenameSets"

    if args.revert:
        revert(sets)
        refresh_tiles(args.game_dir)
        return 0

    resolvers = {}
    for name in PLACENAME_SETS:
        dictionary = args.dictionary if name == "Cities" else args.synoptic_dictionary
        mapping = load_dictionary(dictionary) if dictionary else {}
        review_dir = args.review_dir if name == "Cities" else args.synoptic_review_dir
        resolvers[name] = PlacenameResolver(mapping, load_reviewed(review_dir))
    renamed = sum(process(sets, f"{name}/0000_0000.wwp", resolver, args.verify)
                  for name, resolver in resolvers.items())
    if args.verify:
        print("label cache: not verified (--verify never renders or writes tiles)")
    elif renamed or args.refresh_tiles:
        refresh_tiles(args.game_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
