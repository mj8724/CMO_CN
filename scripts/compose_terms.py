#!/usr/bin/env python3
"""R44：组合翻译（结构规则 + 术语表）。

为什么要有它：装备/设施名是「名称 + 数量 + 规格/容器」拼出来的，逐条词库永远追不上——
每加一个新装备就要手写一条中文，而且生成器不认识的新词会原样留在中文里，产出
`AvGas (200k Liter 地下 Tank)`、`A/C 地下飞机 Shelter (10x 大型飞机)` 这种半英半中的条目。

这里的规则与引擎 `CMOLocalizer.TryComposeTranslate` **一一对应**：
  · 规则 1  `<名称> (<规格>)`  →  `<名称译>（<规格译>）`
  · 规则 2  `<数字><单位>`      →  `<数字> <中文单位>`
  · 规则 3  整串术语最长匹配（多词短语优先）
任何一段出现生词就整体放弃（返回 None），因为半截翻译比不翻译更糟。

用途：
  · `build_dict.py` 用它修复历史条目里的半译残留；
  · `verify_regressions.py --static` 用它做门禁，防止再生成半译条目；
  · `--check` 独立跑一遍审计，打印仍然半译的条目。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"

# 与引擎 composeUnits 保持一致（单位是结构，不是词汇）
UNITS = {
    "ft": "英尺", "kft": "千英尺", "m": "米", "km": "千米", "nm": "海里",
    "kt": "节", "kts": "节", "kg": "千克", "lb": "磅", "lbs": "磅",
    "liter": "升", "liters": "升", "gallon": "加仑", "gallons": "加仑",
    "unit": "个单位", "units": "个单位",
}

CJK = re.compile(r"[\u4e00-\u9fff]")
PAREN = re.compile(r"^(?P<name>[^()\[\]]+?)\s*\((?P<spec>[^()]*)\)$")
NUMBER_UNIT = re.compile(r"^(?P<num>\d[\d.,]*\s*k?)\s*(?P<unit>[A-Za-z]+)$")
QTY = re.compile(r"^(?P<n>\d+)\s*x\s*(?P<rest>.+)$", re.IGNORECASE)
UNITS_SPEC = re.compile(r"^(?P<n>\d[\d,]*)\s*units?$", re.IGNORECASE)
CAPACITY = re.compile(
    r"^(?P<num>\d[\d.,]*\s*k?)\s*(?P<unit>Liter|Liters|Gallon|Gallons|kg|lbs?)\s+(?P<rest>.+)$",
    re.IGNORECASE,
)


def has_cjk(text: str) -> bool:
    return bool(CJK.search(text or ""))


def load_terms() -> dict[str, str]:
    p = SRC / "terms.json"
    if not p.is_file():
        return {}
    data = json.loads(p.read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_") and isinstance(v, str)}


def _number_with_unit(text: str | None) -> str | None:
    m = NUMBER_UNIT.match((text or "").strip())
    if not m:
        return None
    unit = UNITS.get(m.group("unit").lower())
    if not unit:
        return None
    return f"{m.group('num').strip()} {unit}"


def _composed_name(text: str, terms: dict[str, str], exact: dict[str, str] | None = None) -> str | None:
    """按词边界做最长术语优先匹配；任何生词都放弃（不半截翻译）。"""
    if not text:
        return None
    t = re.sub(r"\s+", " ", text.strip())
    if not t:
        return None
    if t in terms:
        return terms[t]
    if exact and t in exact:
        return exact[t]
    lowered = {k.lower(): v for k, v in terms.items()}
    # 术语表让位于词典：已有的人工译法是唯一事实源
    if exact:
        for k, v in exact.items():
            lowered.setdefault(k.lower(), v)
    words = t.split(" ")
    out: list[str] = []
    i = 0
    while i < len(words):
        best_j, best_zh = -1, None
        for j in range(min(len(words) - 1, i + 4), i - 1, -1):
            cand = " ".join(words[i:j + 1]).lower()
            if cand in lowered:
                best_j, best_zh = j, lowered[cand]
                break
        if best_j < 0:
            return None
        out.append(best_zh)
        i = best_j + 1
    return "".join(out) or None


def _composed_spec(spec: str, terms: dict[str, str], exact: dict[str, str] | None = None) -> str | None:
    if not spec:
        return None
    s = re.sub(r"\s+", " ", spec.strip())
    if not s:
        return None

    m = QTY.match(s)
    if m:
        rest = _composed_spec(m.group("rest"), terms, exact)
        return f"{m.group('n')} 个{rest}" if rest else None

    m = UNITS_SPEC.match(s)
    if m:
        return f"{m.group('n')} 个单位"

    m = CAPACITY.match(s)
    if m:
        num = _number_with_unit(f"{m.group('num').strip()} {m.group('unit')}")
        rest = _composed_name(m.group("rest"), terms, exact)
        if num and rest:
            return f"{num}{rest}"

    num_unit = _number_with_unit(s)
    if num_unit:
        return num_unit

    return _composed_name(s, terms, exact)


def compose(text: str, terms: dict[str, str], exact: dict[str, str] | None = None) -> str | None:
    """把英文显示串组合成中文；不可组合时返回 None。"""
    if not text or not terms or has_cjk(text):
        return None
    s = re.sub(r"\s+", " ", text.strip())
    if not s:
        return None

    m = PAREN.match(s)
    if m:
        name = _composed_name(m.group("name"), terms, exact)
        spec = _composed_spec(m.group("spec"), terms, exact)
        if name and spec:
            return f"{name}（{spec}）"
        return None

    num_unit = _number_with_unit(s)
    if num_unit:
        return num_unit

    return _composed_name(s, terms, exact)


# 中文里允许残留的拉丁词：型号/编号/专有名词。这些不是「该译没译」，而是本来就不译。
RESIDUE_OK = re.compile(
    r"\b("
    r"[A-Z]{1,5}(-\d+[A-Za-z0-9/+]*)*"      # F-15E / SA-2 / KC-130 / A/C
    r"|\d+(\.\d+)?[A-Za-z]{0,3}"            # 707-320 / 1750kg
    r"|[A-Za-z]+[/&]\s?[A-Za-z]+"           # Boom/Drogue
    r")\b"
)


def residue_words(en: str, zh: str, terms: dict[str, str]) -> list[str]:
    """返回中文值里仍然残留的「术语表已知词」——这些是真正的半译缺口。"""
    if not zh or not has_cjk(zh):
        return []
    leftovers = []
    for word in set(re.findall(r"[A-Za-z][A-Za-z\-']+", zh)):
        if RESIDUE_OK.fullmatch(word):
            continue
        if word.lower() in {k.lower() for k in terms}:
            leftovers.append(word)
    return leftovers


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="审计词典里的半译残留")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    terms = load_terms()
    if not terms:
        print("terms.json 缺失或为空", file=sys.stderr)
        return 2

    if not args.check:
        print(f"术语 {len(terms)} 条；compose('AvGas (200k Liter Underground Tank)') = "
              f"{compose('AvGas (200k Liter Underground Tank)', terms)}")
        return 0

    dict_path = ROOT / "dict" / "CMOLocalizer.json"
    doc = json.loads(dict_path.read_text(encoding="utf-8"))
    exact = doc.get("exact") or {}
    bad = {}
    for en, zh in exact.items():
        left = residue_words(en, zh, terms)
        if left:
            bad[en] = (zh, left)

    if args.json:
        print(json.dumps({"count": len(bad),
                          "sample": {k: v for k, v in list(bad.items())[:40]}},
                         ensure_ascii=False, indent=1))
    else:
        print(f"半译残留 {len(bad)} 条 / 共 {len(exact)} 条")
        for en, (zh, left) in list(bad.items())[:25]:
            print(f"  {en}\n      {zh}   ← 残留 {left}")
    return 0 if not bad else 1


if __name__ == "__main__":
    raise SystemExit(main())
