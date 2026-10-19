#!/usr/bin/env python3
"""Build a translated Cities or Synoptic WWP offline from approved reviews.

    python scripts/build_cities_placenames.py --output build/Cities.wwp
    python scripts/build_cities_placenames.py --source English.wwp --output Cities.wwp
    python scripts/build_cities_placenames.py --set Synoptic --require-complete --output Synoptic.wwp

Complete reviewed coverage is required by default. Custom subsets may use
--allow-partial; unknown points retain their names unless --dictionary is given.
This command never discovers or writes a game installation implicitly.
"""
from __future__ import annotations

import argparse
from pathlib import Path

from placename_translation import (PLACENAME_SETS, PlacenameResolver, load_dictionary,
                                   load_reviewed, translate_items)
from verify_placenames import verify_asset
from wwp import load, save

ROOT = Path(__file__).resolve().parent.parent


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--set", choices=PLACENAME_SETS, default="Cities", dest="placename_set")
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dictionary", type=Path,
                        help="explicit custom name mapping for points outside the review registry")
    parser.add_argument("--review-dir", type=Path)
    coverage = parser.add_mutually_exclusive_group()
    coverage.add_argument("--require-complete", dest="allow_partial", action="store_false",
                          help="require complete approved review coverage (default)")
    coverage.add_argument("--allow-partial", action="store_true",
                          help="allow custom subsets without certifying complete review coverage")
    parser.set_defaults(allow_partial=False)
    args = parser.parse_args()

    config = PLACENAME_SETS[args.placename_set]
    source = args.source or ROOT / f"assets/placenames/{args.placename_set}.wwp"
    mapping = load_dictionary(args.dictionary) if args.dictionary else {}
    review_dir = args.review_dir or ROOT / "dict" / config["review_dir"]
    reviewed = load_reviewed(review_dir)
    resolver = PlacenameResolver(mapping, reviewed)
    baseline = load(source)
    items = [dict(item) for item in baseline]
    renamed = translate_items(items, resolver)
    if not args.allow_partial:
        result = verify_asset(items, reviewed, baseline, expected_count=config["count"],
                              require_complete=True)
        if result["errors"]:
            raise ValueError("\n".join(result["errors"]))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    save(args.output, items)
    print(f"Built {args.output}: {len(items)} {args.placename_set} labels, {renamed} renamed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
