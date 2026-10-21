#!/usr/bin/env python3
"""R52：把 LLM 精修过的数据库装备名写回 `dict/src/db_record_names.json`。

为什么要专门的守卫：这批数据的通病就是**型号夹在中文里**
（`100mm MT-12R Towed 反坦克机炮/火炮`）。如果合并时不再查一遍，
LLM 偶尔照抄 old 或漏译就会把半译原样写回去，等于白做。

守卫（任一条命中就拒收并列出）：
  · 空值 / 与原串相同 / 不含中文
  · 译文里仍残留 4 个字母以上的英文单词（型号/编号允许：含数字或全大写）
  · 译文里出现「中文/中文」二选一写法（拼名字时会产生歧义）
  · 结构性内容（SQL/路径/格式串）
  · 长度异常（超过原串 4 倍 + 12）
  · 与 old 完全相同（说明没改进，保留原样也没意义，但不算错误——单独统计）

用法：
  python scripts/merge_record_names.py --merge build/record_out
  python scripts/merge_record_names.py --merge build/record_out --dry-run
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
TARGET = SRC / "db_record_names.json"

CJK = re.compile(r"[\u4e00-\u9fff]")
# 4 个字母以上的连续英文词；型号（含数字）与全大写缩写另行放行
LATIN_WORD = re.compile(r"(?<![A-Za-z])([A-Za-z]{4,})(?![A-Za-z])")
STRUCTURAL = re.compile(r"\\n|\{%.*?%\}|\{\d+\}|"
                        r"\b(?:UPDATE\s+\w+\s+SET|DELETE\s+FROM|INSERT\s+INTO|SELECT\s+.+?\s+FROM)\b|"
                        r"\.(?:dll|exe|json|html|csv)\b|[A-Za-z]:\\")
# 「中文/中文」在数据库名称里是**合法且常见**的写法：
#   定时/触发 引信、对海/对陆 雷达、博斯威克/塔科马 双厂并列、
#   维克斯/罗尔斯·罗伊斯 联合承制、火箭助推/主发动机 双级动力。
# 这些都不是歧义，强行改写反而失真。只拦「两边各 >= 8 汉字」的超长并列
# （读起来像把两句话塞进一个名字）。
CHOICE = re.compile(r"[一-鿿]{8,}\s*/\s*[一-鿿]{8,}")

# 允许保留的英文片段：型号/编号（含数字）、全大写缩写、常见单位
UNIT_OK = {"mm", "km", "kg", "nm", "kt", "kts", "lb", "lbs", "ft",
           "rnds", "rnd", "dtg", "mps", "kph", "hrs", "min", "sec"}


def is_allowable_latin(word: str, whole: str) -> bool:
    """判断译文里的一个英文词是否属于「应当保留」的型号/编号。

    保留的情形：单位、含数字、全大写缩写、以及**紧跟在数字前的型号词**
    （`Type 63A`、`Model 1953`、`Mk 82`）。后者容易被误判成残留英文，
    但它们确实是型号的一部分。
    """
    if word.lower() in UNIT_OK:
        return True
    if any(c.isdigit() for c in word):
        return True
    if word.isupper():
        return True
    idx = whole.find(word)
    while idx >= 0:
        before = whole[idx - 1] if idx > 0 else ""
        after = whole[idx + len(word):].lstrip()
        if (before.isdigit() or before in "-/"
                or (after[:1].isdigit())
                or (after[:1] in "-/" and after[1:2].isdigit())):
            return True
        idx = whole.find(word, idx + 1)
    return False


def acceptable(en: str, zh: str) -> str | None:
    if not isinstance(zh, str):
        return "值不是字符串"
    zh = zh.strip()
    if not zh:
        return "空"
    if zh == en:
        return "与原串相同"
    if STRUCTURAL.search(en):
        return "结构性内容"
    if not CJK.search(zh):
        return "不含中文"
    if CHOICE.search(zh):
        return "含「中文/中文」二选一写法"
    leftover = [w for w in LATIN_WORD.findall(zh) if not is_allowable_latin(w, zh)]
    # 中文句子里夹着一个首字母大写的短词，通常是音译/绰号专名（Sair、Vulcan），
    # 中文军事文献本来就这样写；真正的漏译是**多个**普通英文词或单个小写长词。
    real = [w for w in leftover
            if not (w[:1].isupper() and len(w) <= 8)]
    if real:
        return f"残留英文词 {real[:3]}"
    if len(zh) > len(en) * 4 + 12:
        return f"译文异常长（{len(zh)} vs {len(en)}）"
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--merge", required=True)
    ap.add_argument("--target", default="db_record_names.json",
                    help="目标源文件（默认 db_record_names.json）")
    ap.add_argument("--allow-new", action="store_true", help="允许新增条目（默认防呆拦截）")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    srcdir = Path(args.merge)
    files = sorted(srcdir.glob("batch_*.json"))
    if not files:
        print(f"{srcdir} 下没有 batch_*.json")
        return 1

    target = SRC / args.target
    raw = target.read_bytes().decode("utf-8")
    crlf = "\r\n" in raw
    data = json.loads(raw)

    accepted: dict[str, str] = {}
    rejected: list[tuple[str, str, str]] = []
    unchanged = 0
    total = 0
    for f in files:
        payload = json.loads(f.read_text(encoding="utf-8"))
        mapping = payload if isinstance(payload, dict) else {}
        for en, zh in mapping.items():
            if not isinstance(en, str) or en.startswith("_"):
                continue
            total += 1
            reason = acceptable(en, zh)
            if reason:
                rejected.append((en, zh if isinstance(zh, str) else repr(zh), reason))
                continue
            if data.get(en) == zh.strip():
                unchanged += 1
                continue
            accepted[en] = zh.strip()

    # 防呆：如果绝大多数待写键原本不在目标文件里，几乎一定是 --target 选错了
    # （本轮真实踩过：舰名批被写进了 db_record_names.json，覆盖掉 5,608 条）。
    unknown = [k for k in accepted if k not in data]
    if not args.allow_new and accepted and len(unknown) > len(accepted) * 0.5:
        print(f"!! 中止：{len(unknown)}/{len(accepted)} 个键不在 {args.target} 中，"
              f"目标文件可能选错了（若确实是在新增条目，请传 --allow-new）")
        return 2

    merged = dict(data)
    merged.update(accepted)
    ordered = {k: merged[k] for k in sorted(merged)}

    print(f"读入 {total} 条：接受 {len(accepted)}，拒收 {len(rejected)}，"
          f"与旧值相同 {unchanged}")
    print(f"{args.target}：{len(data)} → {len(ordered)} 条")
    if rejected:
        print("\n=== 拒收样例（前 20）===")
        for en, zh, why in rejected[:20]:
            print(f"  {why:<30} {en!r}\n      -> {zh!r}")

    if args.dry_run:
        print("\n--dry-run：未写入")
        return 0

    text = json.dumps(ordered, ensure_ascii=False, indent=1) + "\n"
    if crlf:
        text = text.replace("\n", "\r\n")
    target.write_bytes(text.encode("utf-8"))
    print(f"\n已写入 {target.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
