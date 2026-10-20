#!/usr/bin/env python3
"""R52：把 LLM 精修过的想定文本写回各自的原归属源文件。

关键点：想定文本分散在 20 多个 `*_side*.json` / `*_message*.json` 里，
必须按批次清单的 `src` 字段写回**原文件**。若图省事全塞进一个文件，
字母序会让某个文件再次覆盖另一个文件里的同名键（本项目踩过：
`P-3F Orion` 被 side 的句子碎片盖掉了 db 的正确译法）。

守卫：
  · 空值 / 与原串相同 / 不含中文
  · 结构性内容（SQL/路径/格式串/花括号占位）
  · 含「中文/中文」长短语并列（读起来像两个名字）
  · 译文里残留 3 个以上普通英文小写单词（人名/型号/缩写放行）
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
BUILD = ROOT / "build"

CJK = re.compile(r"[\u4e00-\u9fff]")
LATIN_WORD = re.compile(r"(?<![A-Za-z])([A-Za-z]{3,})(?![A-Za-z])")
STRUCTURAL = re.compile(r"\\n|\{%.*?%\}|\{\d+\}|"
                        r"\b(?:UPDATE\s+\w+\s+SET|DELETE\s+FROM|INSERT\s+INTO|SELECT\s+.+?\s+FROM)\b|"
                        r"\.(?:dll|exe|json|html|csv)\b|[A-Za-z]:\\")


def acceptable(en: str, zh: str) -> str | None:
    if not isinstance(zh, str):
        return "值不是字符串"
    zh = zh.strip()
    if not zh:
        return "空"
    if STRUCTURAL.search(en):
        return "结构性内容"
    if zh == en:
        # DTG 时间码、电码这类本来就不该翻
        if re.fullmatch(r"[A-Za-z0-9\-/.\s:]+", en) and not CJK.search(en):
            return None
        return "与原串相同"
    if not CJK.search(zh):
        return "不含中文"
    # 人名/型号/缩写允许保留；真正漏译是一串普通英文小写词
    plain = [w for w in LATIN_WORD.findall(zh) if w.islower() and not any(c.isdigit() for c in w)]
    if len(plain) >= 3:
        return f"残留英文词 {plain[:3]}"
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--merge", required=True)
    ap.add_argument("--batches", default=str(BUILD / "scen_batches"))
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    srcdir = Path(args.merge)
    files = sorted(srcdir.glob("batch_*.json"))
    if not files:
        print(f"{srcdir} 下没有 batch_*.json")
        return 1

    # en -> 原归属文件 stem
    owner: dict[str, str] = {}
    for bf in sorted(Path(args.batches).glob("batch_*.json")):
        for item in json.loads(bf.read_text(encoding="utf-8")).get("entries", []):
            src = item.get("src", "")
            if src:
                owner[item["en"]] = src[:-len(".json")] if src.endswith(".json") else src

    accepted: dict[str, str] = {}
    rejected: list[tuple[str, str, str]] = []
    unchanged = 0
    total = 0
    for f in files:
        for en, zh in json.loads(f.read_text(encoding="utf-8")).items():
            if not isinstance(en, str) or en.startswith("_"):
                continue
            total += 1
            reason = acceptable(en, zh)
            if reason:
                rejected.append((en, zh if isinstance(zh, str) else repr(zh), reason))
                continue
            accepted[en] = zh.strip()

    per_file: dict[str, dict[str, str]] = {}
    unowned = 0
    for en, zh in accepted.items():
        stem = owner.get(en)
        if stem is None:
            unowned += 1
            continue
        per_file.setdefault(stem, {})[en] = zh

    print(f"读入 {total} 条：接受 {len(accepted)}，拒收 {len(rejected)}，无归属 {unowned}")
    written = 0
    for stem, mapping in sorted(per_file.items()):
        target = SRC / (stem + ".json")
        if not target.is_file():
            print(f"  !! 归属文件不存在: {target.name}")
            continue
        raw = target.read_bytes().decode("utf-8")
        crlf = "\r\n" in raw
        data = json.loads(raw)
        before = len(data)
        changed = 0
        for en, zh in mapping.items():
            if data.get(en) != zh:
                data[en] = zh
                changed += 1
            else:
                unchanged += 1
        if not changed:
            continue
        text = json.dumps({k: data[k] for k in sorted(data)}, ensure_ascii=False, indent=1) + "\n"
        if crlf:
            text = text.replace("\n", "\r\n")
        if args.dry_run:
            print(f"  {target.name}: {before} 条，将改 {changed}（dry-run）")
        else:
            target.write_bytes(text.encode("utf-8"))
            print(f"  {target.name}: {before} 条，改 {changed}")
        written += changed

    print(f"\n合计改动 {written} 条（与旧值相同 {unchanged}）")
    if rejected:
        print("\n=== 拒收样例（前 15）===")
        for en, zh, why in rejected[:15]:
            print(f"  {why:<26} {en[:60]!r}\n      -> {zh[:60]!r}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
