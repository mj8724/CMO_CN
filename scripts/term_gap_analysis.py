#!/usr/bin/env python3
"""R52：术语缺口分析——算出「加哪个词能解锁最多条目」。

为什么需要它：数据库还有三万多条英文名，逐条手写不现实。但这些名字是
「结构 + 词汇」拼出来的，`TryComposeTranslate` 只要**每个词都认识**就能成句。
所以真正的问题是「还缺哪些词」，而不是「还缺哪些名字」。

本脚本逐字复现引擎的匹配规则，对每条缺口报出**具体卡在哪个词上**：

  规则 1  `<名称> (规格)` / `<名称> [规格]` —— name 与 spec 都必须能组合
  规则 3  整串 `TranslateComposedName`
  TranslateComposedName：按空格切词，最长 5 词窗口优先查 wordsMap、再查 exactMap；
                        查不到时，含数字或含斜杠的 token（型号/编号）原样直通，
                        其余一律视为生词 → 整条放弃

输出两件事：
  1. 「只差一个词」的条目按缺的那个词聚类 —— 加这一个词就解锁一批；
  2. 这些待补词本身，按解锁条目数排序，供 LLM 逐条精修。

用法：
  python scripts/term_gap_analysis.py                 # 摘要
  python scripts/term_gap_analysis.py --emit-batches  # 生成待译术语批次
"""

from __future__ import annotations

import argparse
import json
import re
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
BUILD = ROOT / "build"

CJK = re.compile(r"[\u4e00-\u9fff]")
PAREN = re.compile(r"^(?P<name>[^()\[\]]+?)\s*(?P<open>[\(\[])(?P<spec>[^()\[\]]*)(?P<close>[\)\]])$")
NUM_UNIT = re.compile(r"^(?P<num>\d[\d.,]*\s*(?:-\s*\d[\d.,]*)?\s*k?)\s*(?P<unit>[A-Za-z]+)$")
QTY_PY = re.compile(r"^(?P<n>\d+)\s*x\s*(?P<rest>.+)$", re.I)
QTY_TAIL = re.compile(r"^(?P<rest>.+?)\s+x\s*(?P<n>\d+)(?P<tail>\s*[,;].*)?$", re.I)
UNITS_SPEC = re.compile(r"^(?P<n>\d[\d,]*)\s*units?$", re.I)
CAPACITY = re.compile(
    r"^(?P<num>\d[\d.,]*\s*k?)\s*(?P<unit>Liter|Liters|Gallon|Gallons|kg|lbs?)\s+(?P<rest>.+)$", re.I)

UNITS = {
    "ft": "英尺", "kft": "千英尺", "m": "米", "km": "千米", "nm": "海里",
    "kt": "节", "kts": "节", "kg": "千克", "lb": "磅", "lbs": "磅",
    "liter": "升", "liters": "升", "gallon": "加仑", "gallons": "加仑",
    "unit": "个单位", "units": "个单位",
}


def is_designation(tok: str) -> bool:
    if "/" in tok or any(c.isdigit() for c in tok):
        return True
    # 单个大写字母是舰体/机种分类前缀（P 601 / F 27 / A 32A）
    if len(tok) == 1 and tok.isupper():
        return True
    # 型号里的罗马数字序数（Phantom II、Corsair II）
    return tok in ("II", "III", "IV", "VI")


def norm(s: str) -> str:
    return re.sub(r"\s+", " ", (s or "").strip())


def parse_balanced_paren(s: str) -> tuple[str, str, str, str, str] | None:
    first_p = s.find("(")
    first_sq = s.find("[")
    if first_p < 0 and first_sq < 0:
        return None
    open_idx = min(first_p, first_sq) if first_p >= 0 and first_sq >= 0 else (first_p if first_p >= 0 else first_sq)
    o_c = s[open_idx]
    c_c = ")" if o_c == "(" else "]"
    name = s[:open_idx].strip()
    if not name:
        return None
    depth = 1
    close_idx = -1
    for i in range(open_idx + 1, len(s)):
        if s[i] == o_c:
            depth += 1
        elif s[i] == c_c:
            depth -= 1
            if depth == 0:
                close_idx = i
                break
    if close_idx < 0:
        return None
    return name, o_c, s[open_idx + 1:close_idx].strip(), c_c, s[close_idx + 1:].strip()


class Composer:
    """逐字复现引擎的 TryComposeTranslate / TranslateComposedName。"""

    def __init__(self, words: dict[str, str], exact: dict[str, str]):
        self.words = words
        self.exact = exact

    def lookup(self, text: str) -> str | None:
        return self.words.get(text) or self.exact.get(text)

    def name(self, text: str, blockers: list[str]) -> str | None:
        t = norm(text)
        if not t:
            return None
        direct = self.lookup(t)
        if direct:
            return direct
        words = t.split(" ")
        out = []
        i = 0
        while i < len(words):
            best_j, best_zh = -1, None
            for j in range(min(len(words) - 1, i + 4), i - 1, -1):
                cand = " ".join(words[i:j + 1])
                zh = self.lookup(cand)
                if zh:
                    best_j, best_zh = j, zh
                    break
            if best_j < 0:
                if words[i].isdigit() and i + 2 < len(words) and words[i + 1] in ("x", "X"):
                    out.append(words[i] + " × ")
                    i += 2
                    continue
                if words[i] in ("x", "X") and i + 1 < len(words) and words[i + 1].isdigit():
                    out.append(" × " + words[i + 1])
                    i += 2
                    continue
                if words[i] in ("+", "-", "--"):
                    out.append(" " + words[i] + " ")
                    i += 1
                    continue
                if not words[i]:
                    i += 1
                    continue
                # 剥掉前导/尾随标点（含 [P-12] / Mk. / ASW,）
                tok = words[i]
                start = 0
                while start < len(tok) and tok[start] in ",;:()[]\"'":
                    start += 1
                end = len(tok)
                while end > start:
                    ch = tok[end - 1]
                    if ch in ",;:()[]\"'":
                        end -= 1
                    elif ch == '.' and end >= start + 2 and not tok[end - 2].isdigit():
                        end -= 1
                    else:
                        break
                pfx = tok[:start]
                core = tok[start:end]
                sfx = tok[end:]
                zh2 = self.lookup(core) if core else None
                if zh2:
                    out.append(pfx + zh2 + sfx)
                    i += 1
                    continue
                if not core:
                    out.append(tok)
                    i += 1
                    continue
                zh = self.lookup(core) if core != words[i] else None
                if zh:
                    out.append(zh + words[i][len(core):])
                    i += 1
                    continue
                if not is_designation(core):
                    blockers.append(core)
                    return None
                out.append(core + words[i][len(core):])
                i += 1
                continue
            out.append(best_zh)
            i = best_j + 1
        return "".join(out)

    def spec(self, text: str, blockers: list[str]) -> str | None:
        s = norm(text)
        if not s:
            return None
        m = QTY_TAIL.match(s)
        if m:
            rest = self.spec(m.group("rest"), blockers)
            tail = (m.group("tail") or "").strip()
            tail_zh = self.spec(tail[1:], blockers) if tail else None
            if rest and (tail_zh or not tail):
                return rest + " × " + m.group("n") + ("，" + tail_zh if tail_zh else "")
            return None
        m = QTY_PY.match(s)
        if m:
            rest = self.spec(m.group("rest"), blockers)
            return (m.group("n") + " 个" + rest) if rest else None
        m = UNITS_SPEC.match(s)
        if m:
            return m.group("n") + " 个单位"
        m = CAPACITY.match(s)
        if m:
            num, rest = m.group("num").strip(), m.group("rest")
            unit = UNITS.get(m.group("unit").lower())
            rest_zh = self.name(rest, blockers)
            if unit and rest_zh:
                return f"{num} {unit}{rest_zh}"
        m = NUM_UNIT.match(s)
        if m and m.group("unit").lower() in UNITS:
            return m.group("num").strip() + " " + UNITS[m.group("unit").lower()]

        # 嵌套二级括号（只有能解析出平衡括号才递归，避免死循环）
        if ("(" in s or "[" in s) and parse_balanced_paren(s):
            inner = self.compose(s, blockers)
            if inner:
                return inner

        # 规格内逗号列表
        if "," in s:
            parts = [cl.strip() for cl in s.split(",") if cl.strip()]
            part_zh = [self.spec(cl, blockers) for cl in parts]
            if all(part_zh):
                return "，".join(part_zh)

        return self.name(s, blockers)

    def compose(self, raw: str, blockers: list[str]) -> str | None:
        s = norm(raw)
        if not s or CJK.search(s):
            return None
        bp = parse_balanced_paren(s)
        if bp:
            name, o_c, spec, c_c, tail = bp
            n = self.name(name, blockers)
            sp = self.spec(spec, blockers)
            t_zh = self.name(tail, blockers) if tail else ""
            if n and sp and (t_zh or not tail):
                b_c = "[" if o_c == "[" else "（"
                e_c = "]" if c_c == "]" else "）"
                return f"{n}{b_c}{sp}{e_c}" + (f" {t_zh}" if t_zh else "")
            return None
        m = NUM_UNIT.match(s)
        if m and m.group("unit").lower() in UNITS:
            return m.group("num").strip() + " " + UNITS[m.group("unit").lower()]

        # 嵌套二级括号（只有能解析出平衡括号才递归，避免死循环）
        if ("(" in s or "[" in s) and parse_balanced_paren(s):
            inner = self.compose(s, blockers)
            if inner:
                return inner

        # 规格内逗号列表
        if "," in s:
            parts = [cl.strip() for cl in s.split(",") if cl.strip()]
            part_zh = [self.spec(cl, blockers) for cl in parts]
            if all(part_zh):
                return "，".join(part_zh)

        return self.name(s, blockers)


def load_inputs() -> tuple[dict, dict, list[dict]]:
    words = {k: v for k, v in json.loads((SRC / "terms.json").read_text(encoding="utf-8")).items()
             if not k.startswith("_")}
    ex = json.loads((ROOT / "dict" / "CMOLocalizer.json").read_text(encoding="utf-8-sig"))["exact"]
    gaps = json.loads((BUILD / "residue_gaps.json").read_text(encoding="utf-8"))
    return words, ex, gaps


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--emit-batches", action="store_true")
    ap.add_argument("--size", type=int, default=100)
    ap.add_argument("--max-terms", type=int, default=4000)
    args = ap.parse_args()

    words, exact, gaps = load_inputs()
    comp = Composer(words, exact)

    single: dict[str, set[str]] = defaultdict(set)   # 词 -> 只差它就能成句的条目
    multi = Counter()
    still_ok = 0
    total = 0
    for g in gaps:
        if g["category"] != "db":
            continue
        total += 1
        blockers: list[str] = []
        comp.compose(g["src"], blockers)
        uniq = {b for b in blockers if not is_designation(b)}
        if not uniq:
            still_ok += 1
            continue
        multi[min(len(uniq), 6)] += 1
        if len(uniq) == 1:
            single[next(iter(uniq))].add(g["src"])

    print(f"DB 缺口 {total} 条")
    print(f"  已能被组合覆盖: {still_ok}")
    print("  仍未覆盖，按剩余生词数分布:")
    for k in sorted(multi):
        print(f"    {k}{'+' if k >= 6 else ''} 个生词 : {multi[k]:>6} 条")

    ranked = sorted(single.items(), key=lambda kv: -len(kv[1]))
    unlock = sum(len(v) for _, v in ranked)
    print(f"\n只差 1 个词就能成句的条目: {unlock} 条，涉及 {len(ranked)} 个待补词")
    print("\n=== 解锁收益 Top 40 ===")
    for w, entries in ranked[:40]:
        print(f"  {len(entries):>5} 条  {w!r}")

    if not args.emit_batches:
        return 0

    # 生成待补术语的工作清单，连同例句一起交给 LLM 判断词性/上下文
    terms = [(w, sorted(single[w])) for w, _ in ranked[: args.max_terms]]
    outdir = BUILD / "term_batches"
    outdir.mkdir(parents=True, exist_ok=True)
    for old in outdir.glob("batch_*.json"):
        old.unlink()
    groups = [terms[i:i + args.size] for i in range(0, len(terms), args.size)]
    for i, group in enumerate(groups, 1):
        payload = {
            "batch": i,
            "instructions": [
                "把每个 en 译成简体中文军事术语（单词/短语粒度，不是整句）。",
                "返回 JSON 对象 {en: zh}，只输出 JSON。",
                "en 可能是缩写，按军事惯例展开或保留：Plt=排、Bty=炮连、Coy=连、Bn=营、"
                "Rgt=团、Bde=旅、Div=师、Sqn=中队、Sec=班、MG=机枪、Arty=炮兵、Inf=步兵、"
                "Mech=机械化、Rnds=发、Pods=吊舱、Mod=改进型、Blk=批次、LGB=激光制导炸弹。",
                "装备绰号按通行译法：Sidewinder=响尾蛇、Sparrow=麻雀、Harpoon=鱼叉、"
                "Hellfire=地狱火、Paveway=宝石路、Falcon=战隼、Intruder=入侵者、"
                "Hornet=大黄蜂、Phantom=鬼怪、Tomcat=雄猫、Eagle=鹰。",
                "用途词：Datalink=数据链、Rangefinder=测距仪、Designator=指示器、"
                "Launcher=发射器、Turret=炮塔、Casemate=炮座、Towed=牵引式、"
                "Self-propelled=自行式、Silo=发射井、Warhead=战斗部、Propulsion=推进装置。",
                "examples 是该词所在的实际装备名，用来判断语境；不要翻译 examples。",
                "若 en 是纯型号/代号/人名，原样返回（如 AGM-86C、Kilo）。",
            ],
            "terms": [{"en": w, "examples": entries[:4]} for w, entries in group],
        }
        (outdir / f"batch_{i:03d}.json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n生成 {len(groups)} 个术语批次 → {outdir}（共 {len(terms)} 个词）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
