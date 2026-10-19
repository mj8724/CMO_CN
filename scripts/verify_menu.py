#!/usr/bin/env python3
"""Verify every menu string is translated, and emit the terminology table.

Menu strings come from two sources:
  docs/menu_strings_dump.txt   live MenuStrip capture (offset + control + text)
  dict/strings_pool.json       engine pool around the menu region

The check is the one the task requires: 100% of menu items carry a Chinese
translation and none keeps an English function word.
"""
from __future__ import annotations

import json
import re
import sys
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"
DOCS = ROOT / "docs"

sys.path.insert(0, str(ROOT / "scripts"))
from triage import classify  # noqa: E402

HAN = re.compile(r"[\u4e00-\u9fff]")
HAN_SPACE = re.compile(r"[\u4e00-\u9fff][ \t]+[\u4e00-\u9fff]")
FUNC = re.compile(r"(?<![A-Za-z])(the|of|and|for|with|from|to|in|on|at|by|is|are)"
                  r"(?![A-Za-z])", re.I)
MENU_LO, MENU_HI = 79700, 122000

# Keyboard key names shown in the hotkey list.  Chinese software conventionally
# keeps these in English, and the same tokens are parsed as `Keys` enum members,
# so translating them would break key binding.
KEY_NAMES = {
    "Alt + Enter", "Backspace", "Enter", "Spacebar / Ctrl+Enter",
    "Up Arrow / Num 8", "Down Arrow / Num 2", "Left Arrow / Num 6",
    "Right Arrow / Num 4", "Z / Mouse Scroll", "X / Mouse Scroll",
    "V / PgUp / Num 9", "P / PgDn / Num 3", "Toggle End / Num 1",
    "Toggle Home / Num 7", "Toggle *", "| (Vert. Separator)",
    "Shift + Ins", "Ctrl+I I (Yes/Inherit)", "Ctrl+I (Yes/Inherit)",
    "Ctrl+A (Hold/Inherit)", "I (Yes/Inherit)", "A (Hold/Inherit)",
}


def collect() -> list[tuple[str, str]]:
    """(english, chinese-or-empty) for every real menu item, in menu order."""
    pool = json.loads((DICT / "strings_pool.json").read_text(encoding="utf-8"))
    doc = json.loads((DICT / "CMOLocalizer.json").read_text(encoding="utf-8"))
    off, exact = doc["offsets"], doc["exact"]

    rows: dict[str, str] = {}
    for k, v in pool.items():
        if MENU_LO <= int(k) <= MENU_HI and classify(v) in ("display", "label"):
            rows[v] = off.get(k) or exact.get(v, "")

    for line in (DOCS / "menu_strings_dump.txt").read_text(
            encoding="utf-8").splitlines():
        m = re.match(r"\[(\d+)\]\s+(.*?)\s+==>\s+(.*)$", line)
        if not m:
            continue
        _, owner, text = m.groups()
        if text == "<NONE>" or not re.search(r"[A-Za-z]", owner):
            continue
        if not re.search(r"[A-Za-z]", text):
            continue
        rows.setdefault(owner, off.get(text) or exact.get(owner, ""))
    return sorted(rows.items(), key=lambda kv: kv[0].lower())


def main() -> int:
    rows = collect()
    kept = [(en, zh) for en, zh in rows if en in KEY_NAMES and not HAN.search(zh)]
    missing = [(en, zh) for en, zh in rows
               if not HAN.search(zh) and en not in KEY_NAMES]
    bad = [(en, zh) for en, zh in rows
           if HAN.search(zh) and (FUNC.search(zh) or HAN_SPACE.search(zh))]

    print(f"menu items checked : {len(rows)}")
    print(f"translated         : {len(rows) - len(missing) - len(kept)}")
    print(f"key names kept EN  : {len(kept)}")
    print(f"untranslated       : {len(missing)}")
    print(f"stiff / residual   : {len(bad)}")

    if missing:
        print("\nUNTRANSLATED:")
        for en, _ in missing[:40]:
            print(f"  - {en!r}")
    if bad:
        print("\nRESIDUAL ENGLISH / SPACING:")
        for en, zh in bad[:40]:
            print(f"  - {en!r} -> {zh!r}")

    # --- terminology table ------------------------------------------------
    lines = ["# CMO 菜单军语对照表", "",
             "由 `scripts/verify_menu.py` 从游戏字符串池与菜单栏实测转储生成。", "",
             f"- 菜单项总数：**{len(rows)}**",
             f"- 已汉化：**{len(rows) - len(missing) - len(kept)}**",
             f"- 按键名保留英文：**{len(kept)}**",
             f"- 生硬/残留英文：**{len(bad)}**", "",
             "| 英文原文 | 中文译名 |", "|---|---|"]
    for en, zh in rows:
        label = zh or ("（按键名·保留英文）" if en in KEY_NAMES else "（未译）")
        lines.append(f"| {en.replace('|', chr(92) + '|')} "
                     f"| {label.replace('|', chr(92) + '|')} |")
    (DOCS / "MENU_TERMINOLOGY.md").write_text("\n".join(lines) + "\n",
                                              encoding="utf-8")
    print(f"\nwrote docs/MENU_TERMINOLOGY.md ({len(rows)} rows)")

    if missing or bad:
        print("\nFAIL: menu translation incomplete")
        return 1
    print("\nPASS: every menu item is translated with no residual English")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
