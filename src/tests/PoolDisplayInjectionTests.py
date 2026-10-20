#!/usr/bin/env python3
"""Check the tracked pool-injection source against the pool and dictionary builder."""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))

from build_dict import build  # noqa: E402
from inject_filter import injection_safe  # noqa: E402


def main() -> int:
    pool = json.loads((ROOT / "dict/strings_pool.json").read_text(encoding="utf-8"))
    source = json.loads((ROOT / "dict/src/pool_display_inject.json").read_text(encoding="utf-8"))
    current = json.loads((ROOT / "dict/CMOLocalizer.json").read_text(encoding="utf-8"))
    valid = set(pool.values())
    rejected = {text: injection_safe(text, valid)[1]
                for text in source if not injection_safe(text, valid)[0]}
    rebuilt = build()
    offsets = current["offsets"]
    expected = rebuilt["offsets"]
    drift = [key for key in offsets.keys() | expected.keys()
             if offsets.get(key) != expected.get(key)]
    disagreement = [text for key, text in pool.items()
                    if key in offsets and current["exact"].get(text) != offsets[key]]
    ok = bool(source) and not rejected and not drift and not disagreement
    print(f"{'PASS' if ok else 'FAIL'} pool source={len(source)} "
          f"unsafe={len(rejected)} offset-drift={len(drift)} "
          f"exact-disagreement={len(disagreement)}")
    if not ok:
        print(f"unsafe={list(rejected.items())[:5]} offsets={drift[:5]} "
              f"exact={disagreement[:5]}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
