#!/usr/bin/env python3
"""Terminology and naming-consistency gate for dict/CMOLocalizer.json.

Fails when a value contains a banned mistranslation, when a short UI label was
translated into a telegraphic pseudo-sentence that re-embeds the English source,
or when one equipment has several competing Chinese spellings.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict" / "CMOLocalizer.json"
RULES = ROOT / "dict" / "src" / "term_replacements.json"

HAN = re.compile(r"[\u4e00-\u9fff]")
SHORT_KEY = re.compile(r"^[ -~]{2,24}$")
# Vocabulary that only appears when a short label was handed to the prose
# translator; used together with the key re-embedding test below.
FILLER = re.compile(r"如令|各部收悉|各部知悉|遵令|通令|原文为|之部|遵行")
# One spelling per equipment family; the parenthesised English reference form
# ("「怪蛇」5（Python 5）") is the agreed bilingual notation and is allowed.
PYTHON_REF = re.compile(r"(?<![（(])Python\s*-?\s*(\d)")
PYTHON_CN = re.compile(r"(?:蟒蛇|巨蟒|怪蛇)\s*-?\s*(\d)")


def load(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def main() -> int:
    doc = load(DICT)
    rules = load(RULES)
    failures: list[str] = []

    for section in ("exact", "offsets"):
        entries = doc.get(section, {})
        for key, value in entries.items():
            value = str(value)

            for rule in rules.get("banned", []):
                try:
                    pattern = re.compile(rule["pattern"])
                except re.error:
                    continue
                if pattern.search(value):
                    failures.append(f"[banned/{section}] {key!r} -> {value!r} ({rule['reason']})")

            if SHORT_KEY.match(key) and HAN.search(value) and key.lower() in value.lower() \
                    and re.search(r"[。；]", value):
                failures.append(f"[key-embedded/{section}] {key!r} -> {value!r}")

        pythons = {num for num in PYTHON_REF.findall("\n".join(str(v) for v in entries.values()))}
        if pythons:
            failures.append(f"[naming-inconsistent/{section}] bare English Python {sorted(pythons)}")
        mixed = {num for num in PYTHON_CN.findall("\n".join(str(v) for v in entries.values()))}
        if mixed:
            failures.append(f"[naming-inconsistent/{section}] non-standard 蟒蛇/巨蟒 {sorted(mixed)}")

    print(f"dictionary: offsets={len(doc.get('offsets', {}))} exact={len(doc.get('exact', {}))}")
    if failures:
        print(f"FAIL: {len(failures)} terminology violation(s)")
        for f in failures[:60]:
            print("  " + f)
        return 1
    print("PASS: terminology and naming are consistent")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
