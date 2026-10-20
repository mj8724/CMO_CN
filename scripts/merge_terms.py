#!/usr/bin/env python3
"""R52：把 LLM 精修过的术语写进 `dict/src/terms.json`，带质量守卫。

为什么守卫要严：术语表是**组合翻译的词汇层**，一个坏词会污染上千条装备名。
`Pod` 译成「舱」而不是「吊舱」，`Bty` 译成「电池」而不是「炮连」，
都会让整个数据库读出怪异中文——这比不翻译更难接受。

守卫规则（任一条命中就拒收，并在报告里列出原因）：
  · 空值 / 与 en 相同 / 不含中文（纯代号允许原样，但要求 en 本身是代号形状）
  · 结构性内容（SQL/路径/格式串/花括号占位）
  · 过长（术语不该超过 12 字）
  · 含中文但夹着英文单词（半译，应由 LLM 重做）
  · 与已有条目冲突且新值更差（长度突增 / 含句读）

用法：
  python scripts/merge_terms.py --merge build/term_out
  python scripts/merge_terms.py --merge build/term_out --dry-run
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
CJK = re.compile(r"[\u4e00-\u9fff]")
LATIN_WORD = re.compile(r"[A-Za-z]{3,}")
SENTENCE = re.compile(r"[。，；！？]")
STRUCTURAL = re.compile(r"\\n|\{%.*?%\}|\{\d+\}|"
                        r"\b(?:UPDATE\s+\w+\s+SET|DELETE\s+FROM|INSERT\s+INTO|SELECT\s+.+?\s+FROM)\b|"
                        r"\.(?:dll|exe|json|html|csv)\b|[A-Za-z]:\\")
CODE_SHAPE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9\-/.\s]*$")


def is_code_token(text: str) -> bool:
    """真代号：含数字或斜杠，或全大写短串（MTU、HE、LDGP）。"""
    t = (text or "").strip()
    if not t or " " in t:
        return False
    if "/" in t or any(c.isdigit() for c in t):
        return True
    return t.isupper() and 2 <= len(t) <= 8


def load(p: Path) -> dict:
    return json.loads(p.read_text(encoding="utf-8"))


def acceptable(en: str, zh: str) -> str | None:
    if not isinstance(zh, str):
        return "值不是字符串"
    zh = zh.strip()
    if not zh:
        return "空"
    if zh == en:
        # 只有「本来就是代号」的才允许原样（MTU、AGM-86C）。
        # 普通英文单词原样返回等于没翻，必须拒收重做。
        if is_code_token(en):
            return None
        return "与原串相同（未翻译）"
    if STRUCTURAL.search(en):
        return "结构性内容"
    if SENTENCE.search(zh):
        return "含句读（是句子不是术语）"
    if len(zh) > 12:
        return f"过长（{len(zh)} 字）"
    if not CJK.search(zh):
        if is_code_token(en) and is_code_token(zh):
            return None          # 代号保留
        return "不含中文且非代号形状"
    # 中文里夹着英文单词 = 半译
    if LATIN_WORD.search(zh):
        return "半译（中文里夹英文词）"
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--merge", required=True)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    srcdir = Path(args.merge)
    files = sorted(srcdir.glob("batch_*.json"))
    if not files:
        print(f"{srcdir} 下没有 batch_*.json")
        return 1

    target = SRC / "terms.json"
    raw = target.read_bytes().decode("utf-8")
    crlf = "\r\n" in raw
    existing = load(target)
    current = {k: v for k, v in existing.items() if not k.startswith("_")}

    accepted: dict[str, str] = {}
    rejected: list[tuple[str, str, str]] = []
    conflicts: list[tuple[str, str, str]] = []
    total = 0
    for f in files:
        data = json.loads(f.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            continue
        for en, zh in data.items():
            if not isinstance(en, str) or en.startswith("_"):
                continue
            total += 1
            reason = acceptable(en, zh)
            if reason:
                rejected.append((en, zh if isinstance(zh, str) else repr(zh), reason))
                continue
            if en in current and current[en] != zh.strip():
                conflicts.append((en, current[en], zh.strip()))
            accepted[en] = zh.strip()

    merged = dict(current)
    merged.update(accepted)
    ordered = {k: merged[k] for k in sorted(merged)}

    print(f"读入 {total} 条：接受 {len(accepted)}，拒收 {len(rejected)}，"
          f"覆盖已有 {len(conflicts)}")
    print(f"terms.json：{len(current)} → {len(ordered)} 条")

    if rejected:
        print("\n=== 拒收样例（前 20）===")
        for en, zh, why in rejected[:20]:
            print(f"  {why:<24} {en!r} -> {zh!r}")
    if conflicts:
        print("\n=== 覆盖已有术语（前 15，请抽查质量）===")
        for en, old, new in conflicts[:15]:
            print(f"  {en!r}: {old!r} → {new!r}")

    if args.dry_run:
        print("\n--dry-run：未写入")
        return 0

    # 保留 `_comment` 之类的元数据键，其余按名称排序
    head = {k: v for k, v in existing.items() if k.startswith("_")}
    out = dict(head)
    out.update(ordered)
    text = json.dumps(out, ensure_ascii=False, indent=2) + "\n"
    if crlf:
        text = text.replace("\n", "\r\n")
    target.write_bytes(text.encode("utf-8"))
    print(f"\n已写入 {target.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
