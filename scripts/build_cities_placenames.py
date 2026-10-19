#!/usr/bin/env python3
"""Build a translated Cities WWP offline, using reviewed coordinates first.

    python scripts/build_cities_placenames.py --output build/Cities.wwp
    python scripts/build_cities_placenames.py --source English.wwp --output Cities.wwp

The existing flat dictionary supplies unreviewed names. This command never
rewrites that dictionary or discovers/writes a game installation implicitly.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from placename_translation import PlacenameResolver, load_dictionary, load_reviewed, translate_items
from wwp import load, save

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT / "assets/placenames/Cities.wwp")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dictionary", type=Path, default=ROOT / "dict/placenames_cities.json")
    parser.add_argument("--review-dir", type=Path, default=ROOT / "dict/placenames_reviewed")
    args = parser.parse_args()

    resolver = PlacenameResolver(load_dictionary(args.dictionary), load_reviewed(args.review_dir))
    items = load(args.source)
    renamed = translate_items(items, resolver)
    save(args.output, items)
    print(f"Built {args.output}: {len(items)} cities, {renamed} renamed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
