#!/usr/bin/env python3
"""Declare UTF-8 in already-localized briefing HTML, and gate the contract.

Why this exists
---------------
The game renders 想定说明 / 任务简报 through mshtml (WinForms WebBrowser).  With no
charset declaration in the page, mshtml falls back to the ANSI codepage, decodes
our UTF-8 Chinese as GBK, and the whole panel shows as mojibake.  The English
originals were pure ASCII, so the missing declaration was harmless until the
pages were translated -- 317 of the 327 localized pages shipped without one.

    python scripts/fix_static_html_charset.py            # report only (default)
    python scripts/fix_static_html_charset.py --apply    # add the declaration in place
    python scripts/fix_static_html_charset.py --check     # gate: exit 1 when any page lacks it

Only the declaration is inserted; body text and the `.orig` backups are never
touched, so an uninstall still restores the original English pages.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
from localize_html_files import ensure_charset  # noqa: E402

GAME = Path(os.environ.get("GAME_DIR")
            or "G:/soft/steam/steamapps/common/Command - Modern Operations")
HAN = re.compile(r"[\u4e00-\u9fff]")


def decode(source: bytes) -> tuple[str, str]:
    if source.startswith(b"\xef\xbb\xbf"):
        return source.decode("utf-8-sig"), "utf-8-sig"
    try:
        return source.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        pass
    for encoding in ("gb18030", "cp1252"):
        try:
            return source.decode(encoding), encoding
        except UnicodeDecodeError:
            continue
    return source.decode("utf-8", errors="replace"), "utf-8"


def pages(game: Path) -> list[Path]:
    found = []
    for pattern in ("Scenarios/**/*.htm", "Scenarios/**/*.html"):
        found.extend(game.glob(pattern))
    return sorted(p for p in set(found)
                  if p.is_file() and not p.name.lower().endswith(".orig"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=GAME)
    parser.add_argument("--apply", action="store_true",
                        help="insert the declaration in place")
    parser.add_argument("--check", action="store_true",
                        help="exit non-zero when a localized page lacks a declaration")
    args = parser.parse_args()

    scanned = localized = fixed = 0
    lacking = []
    for path in pages(args.game_dir):
        scanned += 1
        source = path.read_bytes()
        document, encoding = decode(source)
        if not HAN.search(document):
            continue
        localized += 1
        updated, changed = ensure_charset(document, encoding)
        if not changed:
            continue
        lacking.append(path.relative_to(args.game_dir).as_posix())
        if args.apply:
            path.write_bytes(updated.encode(encoding, errors="xmlcharrefreplace"))
            fixed += 1

    print(f"scanned={scanned} localized={localized} missing-declaration={len(lacking)}"
          + (f" fixed={fixed}" if args.apply else ""))
    for name in lacking[:10]:
        print("  no charset:", name)
    if len(lacking) > 10:
        print(f"  ... {len(lacking) - 10} more")
    if args.check and lacking:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
