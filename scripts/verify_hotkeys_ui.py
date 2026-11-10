#!/usr/bin/env python3
"""快捷键窗口正文（R91）门禁：`dict/src/hotkeys_ui.json` 必须与实机采集的英文原文逐行对齐。

快捷键窗口的 6 段正文（`DarkRichTextBox`）靠「键列 + 制表符/补空格」排两列，运行时不走
通用翻译管道，只吃整段 exact 查表。所以源译文必须满足：

  1. 采集文件（`dict/hotkeys/hotkeys_source.json`）里每段英文都有且只有一条整段译文；
  2. 行数一致；带制表符的行，键列前缀（含制表符与其后的空白补齐）逐字节相同；
     不带制表符的散文行（高手技巧与提示）整行替换，但必须含中文；
  3. 说明文字含中文，且不残留英文虚词（the/and/of/to/... 会被构建期剔除，说明数据脏）；
  4. 不含 `\\r`；不含连续空格（构建期的多空格合并会把列位置挤歪）；
  5. 词典产物里这 6 条的译文与源文件逐字节一致（构建管道没有改动它们）。

任一条不满足 ⇒ 退出码 1。负向用例：删掉一条译文、改一个制表符、把说明改成纯英文都能让它红。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "dict/hotkeys/hotkeys_source.json"
TRANSLATIONS = ROOT / "dict/src/hotkeys_ui.json"
DICTIONARY = ROOT / "dict/CMOLocalizer.json"

FUNCTION_WORDS = ("the", "and", "of", "to", "in", "on", "at", "for", "with", "by",
                  "from", "is", "are", "will", "be")
# 散文行里允许保留的技术标记（不是残留英文句子）
ALLOWED_ASCII = ("Ctrl", "Shift", "Alt", "Num", "PgUp", "PgDn", "Ins", "Del", "F1", "F2",
                 "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "BOL", "RTB",
                 "EMCON", "WRA", "OoB", "Lua", "ATO", "Mouse", "Scroll")


def has_cjk(text: str) -> bool:
    return any("\u4e00" <= ch <= "\u9fff" for ch in text)


def prefix_of(line: str) -> str:
    """键列前缀：最后一个制表符 + 其后的空白补齐；无制表符时为空串。"""
    tab = line.rfind("\t")
    if tab < 0:
        return ""
    i = tab + 1
    while i < len(line) and line[i] in "\t ":
        i += 1
    return line[:i]


def body_of(line: str) -> str:
    return line[len(prefix_of(line)):]


def ascii_words(text: str) -> list[str]:
    out = []
    word = ""
    for ch in text:
        if ch.isascii() and (ch.isalnum() or ch in "'"):
            word += ch
        else:
            if word:
                out.append(word)
            word = ""
    if word:
        out.append(word)
    return out


def check_box(name: str, english: str, chinese: str) -> list[str]:
    problems = []
    if "\r" in english or "\r" in chinese:
        problems.append(f"{name}: 含 \\r（RichTextBox 用 \\n，带 \\r 会整段查表落空）")
    for index, zh_line in enumerate(chinese.split("\n"), start=1):
        # 键列后面的空白补齐属于排版（原样保留），说明文字里的连续空格才是脏数据。
        if "  " in body_of(zh_line):
            problems.append(f"{name}:{index}: 说明里含连续空格 {body_of(zh_line)[:40]!r}")
    en_lines = english.split("\n")
    zh_lines = chinese.split("\n")
    if len(en_lines) != len(zh_lines):
        problems.append(f"{name}: 行数不一致 {len(en_lines)} != {len(zh_lines)}")
        return problems
    for index, (en_line, zh_line) in enumerate(zip(en_lines, zh_lines), start=1):
        prefix = prefix_of(en_line)
        if zh_line[:len(prefix)] != prefix:
            problems.append(f"{name}:{index}: 键列/制表符前缀被改 {prefix!r} -> {zh_line[:len(prefix)]!r}")
        body = body_of(zh_line)
        if not has_cjk(body):
            problems.append(f"{name}:{index}: 说明里没有中文 {body[:40]!r}")
        for word in ascii_words(body):
            if word.lower() in FUNCTION_WORDS:
                problems.append(f"{name}:{index}: 残留英文虚词 {word!r}")
            elif not has_cjk(body) or (len(word) > 12 and word not in ALLOWED_ASCII):
                problems.append(f"{name}:{index}: 疑似保留整段英文 {word!r}")
    return problems


def collect_problems() -> tuple[list[str], dict]:
    problems: list[str] = []
    if not SOURCE.is_file():
        return [f"缺少采集文件 {SOURCE.relative_to(ROOT)}"], {}
    if not TRANSLATIONS.is_file():
        return [f"缺少译文源 {TRANSLATIONS.relative_to(ROOT)}"], {}
    source = json.loads(SOURCE.read_text(encoding="utf-8"))
    translations = json.loads(TRANSLATIONS.read_text(encoding="utf-8"))
    dictionary = json.loads(DICTIONARY.read_text(encoding="utf-8")).get("exact", {})
    if not isinstance(source, dict) or not isinstance(translations, dict):
        return ["采集文件或译文源不是 JSON 对象"], {}

    missing = [name for name, english in source.items() if english not in translations]
    extra = [key for key in translations if key not in source.values()]
    if missing:
        problems.append(f"这些控件缺少整段译文：{', '.join(missing)}")
    if extra:
        problems.append(f"译文源里有 {len(extra)} 条与实机原文对不上（键已过期？）")

    for name, english in source.items():
        if name in missing:
            continue
        chinese = translations[english]
        problems.extend(check_box(name, english, chinese))
        if dictionary.get(english) != chinese:
            problems.append(f"{name}: 词典产物与译文源不一致（先跑 build_dict.py）")

    return problems, {"boxes": len(source), "lines": sum(text.count("\n") + 1 for text in source.values())}


def main() -> int:
    problems, stats = collect_problems()
    if problems:
        print(f"[FAIL] 快捷键窗口正文（{len(problems)} 个问题）")
        for problem in problems[:20]:
            print("  - " + problem)
        return 1
    if stats:
        print(f"[PASS] 快捷键窗口正文 {stats['boxes']} 段 / {stats['lines']} 行与实机原文对齐，词典产物一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
