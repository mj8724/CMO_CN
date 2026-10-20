#!/usr/bin/env python3
"""R52：缺口翻译的批次流水线（工作清单 → LLM 批次 → 守卫式合并）。

为什么要分批而不是一次性批量替换：装备/部队名的译法需要逐条判断
（型号保留、绰号意译、人名地名音译、编制缩写的固定译法），
任何"按词替换"都会产出 `舰载机 KA-6D Intruder 升空加油` 这种垃圾串——
这正是本轮要清理的历史债。

用法：
  python scripts/build_translation_batches.py --source corrupt --size 120
  python scripts/build_translation_batches.py --source db --tables DataShip,DataSensor
  python scripts/build_translation_batches.py --merge build/xlate_out

合并守卫（任一条不满足就整条丢弃并计入报告）：
  · zh 必须非空、与 en 不同、且含中文；
  · zh 里不得原样嵌着 en（防止再把句子碎片当名字译文写进来）；
  · en 不得是结构性内容（SQL/路径/格式串/花括号占位）；
  · zh 长度不得超过 en 的 4 倍 + 12（防串行错位）。
"""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
BUILD = ROOT / "build"
CJK = re.compile(r"[\u4e00-\u9fff]")
# 句读：出现在「残余文本」里，说明值是句子而不是名字
SENTENCE_PUNCT = re.compile(r"[。，；、！？…—]")


def sentence_debris(key: str, value: str) -> bool:
    """值是不是「键 + 一句话」的句子碎片。

    `F-16` -> `F-16 战隼` **不算**：残余只是两个字的绰号，是正确译法。
    `KA-6D Intruder` -> `舰载机 KA-6D Intruder 升空加油` 算：残余带动作和标点。
    """
    if not key or len(key) < 6 or key not in value:
        return False
    residual = value.replace(key, "")
    if SENTENCE_PUNCT.search(residual):
        return True
    return len(residual) > 12
# 结构性内容绝不能进译文（池注入铁律同样适用）
# 结构性内容绝不能进译文（池注入铁律同样适用）。
# SQL 关键词必须**大小写敏感**：`P-3C Orion Update II`、`INTEL UPDATE ONE` 是正常装备/报文名，
# 一旦用 re.I 就会被当成 SQL 误杀（实测踩过）。
STRUCTURAL = re.compile(r"\\n|\{%.*?%\}|\{\d+\}|"
                        r"\b(?:UPDATE\s+\w+\s+SET|DELETE\s+FROM|INSERT\s+INTO|SELECT\s+.+?\s+FROM)\b|"
                        r"\.(?:dll|exe|json|html|csv)\b|[A-Za-z]:\\")


def load_json(p: Path):
    return json.loads(p.read_text(encoding="utf-8-sig"))


def corrupt_entries() -> list[dict]:
    """历史上的错位 exact 条目：键是单位/舰名，值却是整句译文。

    判据与审计一致：值含中文、且键原样出现在值里（键只是句子的前缀）。
    这类条目的正确译文应当只是**名字本身**，不是句子。
    """
    out = []
    for p in sorted(SRC.glob("*_side*.json")) + sorted(SRC.glob("*message*.json")):
        try:
            data = load_json(p)
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        if not isinstance(data, dict):
            continue
        for k, v in data.items():
            if k.startswith("_") or not isinstance(v, str):
                continue
            # 审计要**宽**：宁可多列，交给逐条 LLM 判断是否有问题。
            if CJK.search(v) and len(k) >= 6 and k in v:
                out.append({"id": f"{p.stem}::{k}", "en": k, "bad": v, "src": p.name})
    return out


def db_gaps(tables: list[str] | None) -> list[dict]:
    gaps = json.loads((BUILD / "residue_gaps.json").read_text(encoding="utf-8"))
    out, seen = [], set()
    for g in gaps:
        if g["category"] != "db":
            continue
        table = g["table"].split(".", 1)[1] if "." in g["table"] else g["table"]
        if tables and table not in tables:
            continue
        key = (table, g["src"])
        if key in seen:
            continue
        seen.add(key)
        out.append({"id": f"{table}::{g['src']}", "en": g["src"],
                    "bad": g["dst"], "src": table})
    return out


def chunk(items: list[dict], size: int) -> list[list[dict]]:
    return [items[i:i + size] for i in range(0, len(items), size)]


def cmd_batches(args) -> int:
    if args.source == "corrupt":
        items = corrupt_entries()
    else:
        items = db_gaps(args.tables)
    if not items:
        print("没有待翻译条目")
        return 1
    outdir = Path(args.outdir) if args.outdir else BUILD / "xlate_batches"
    outdir.mkdir(parents=True, exist_ok=True)
    for old in outdir.glob("batch_*.json"):
        old.unlink()
    groups = chunk(items, args.size)
    for i, group in enumerate(groups, 1):
        payload = {
            "batch": i,
            "instructions": [
                "把每个 en 译成简体中文军事术语。",
                "返回 JSON 对象 {en: zh}，只输出 JSON，不要解释。",
                "装备型号/编号/BLUF 代号保留原样（B-66B、AN/TPS-43、SS-N-2、150mm）。",
                "绰号按解放军/军事文献通行译法意译：Sidewinder=响尾蛇、Sparrow=麻雀、"
                "Harpoon=鱼叉、Hellfire=地狱火、Paveway=宝石路、Falcon=战隼、"
                "Intruder=入侵者、Hornet=大黄蜂、Seasprite=海妖、Orion=猎户座。",
                "舰名/人名/地名按新华社通行音译（Melbourne=墨尔本、Kentucky=肯塔基）。",
                "编制缩写用固定译法：Plt=排、Bty=炮连、Coy=连、Bn=营、Rgt=团、"
                "Bde=旅、Div=师、Sqn=中队、Sec=班、MG=机枪、Arty=炮兵、Inf=步兵、"
                "Mech=机械化、Rnds=发、Pods=吊舱、Mod=改进型、Blk=批次。",
                "【重要】看不出是英文单词的结构串（SQL、路径、格式串、纯代号堆叠）"
                "请把该条**省略不返回**，不要硬编。",
                "bad 字段是历史上错误的旧译文，仅供判断语境，**不要照抄**。",
            ],
            "entries": group,
        }
        (outdir / f"batch_{i:03d}.json").write_text(
            json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"生成 {len(groups)} 个批次，共 {len(items)} 条 → {outdir}")
    print(f"每个批次 ≤{args.size} 条")
    return 0


def acceptable(en: str, zh: str) -> str | None:
    """返回拒绝原因；None 表示接受。"""
    if not zh or not isinstance(zh, str):
        return "空"
    zh = zh.strip()
    if not zh:
        return "空"
    if zh == en:
        return "与原串相同"
    if not CJK.search(zh):
        return "不含中文"
    if sentence_debris(en, zh):
        return "译文是「原名 + 句子」的碎片"
    if STRUCTURAL.search(en):
        return "结构性内容"
    if len(zh) > len(en) * 4 + 12:
        return f"译文异常长（{len(zh)} vs {len(en)}）"
    return None


def cmd_merge(args) -> int:
    srcdir = Path(args.merge)
    files = sorted(srcdir.glob("batch_*.json"))
    if not files:
        print(f"{srcdir} 下没有 batch_*.json")
        return 1

    accepted: dict[str, str] = {}
    rejected: list[tuple[str, str, str]] = []
    total = 0
    for f in files:
        data = json.loads(f.read_text(encoding="utf-8"))
        mapping = data if isinstance(data, dict) else {}
        for en, zh in mapping.items():
            if not isinstance(zh, str) or en.startswith("_"):
                continue
            total += 1
            reason = acceptable(en, zh)
            if reason:
                rejected.append((en, zh, reason))
            else:
                accepted[en] = zh.strip()

    # 译文必须写回**它原来的批次文件**。若图省事全塞进一个池，
    # 字母序会让 side 再次覆盖 db 里已有的正确译法。
    # 归属直接读批次清单里的 src 字段（`corrupt_entries()` 在数据已清洗后会返回空，
    # 不能再拿它当事实源）。
    owner: dict[str, str] = {}
    batchdir = Path(args.batches) if args.batches else BUILD / "xlate_batches"
    for bf in sorted(batchdir.glob("batch_*.json")):
        data = json.loads(bf.read_text(encoding="utf-8"))
        for item in data.get("entries", []):
            src = item.get("src", "")
            if not src:
                continue
            stem = src[:-len(".json")] if src.endswith(".json") else src
            owner[item["en"]] = "db_gap_fill" if stem.startswith("Data") else stem

    per_file: dict[str, dict[str, str]] = {}
    unowned = 0
    for en, zh in accepted.items():
        stem = owner.get(en)
        if stem is None:
            unowned += 1
            continue
        per_file.setdefault(stem, {})[en] = zh

    written = 0
    for stem, mapping in sorted(per_file.items()):
        target = SRC / (stem + ".json")
        exists = target.is_file()
        existing = {}
        if exists:
            existing = {k: v for k, v in load_json(target).items() if not k.startswith("_")}
        before = len(existing)
        existing.update(mapping)
        crlf = exists and b"\r\n" in target.read_bytes()
        text = json.dumps({k: existing[k] for k in sorted(existing)},
                          ensure_ascii=False, indent=1) + "\n"
        if crlf:
            text = text.replace("\n", "\r\n")
        target.write_bytes(text.encode("utf-8"))
        written += len(mapping)
        print(f"  {target.name}: {before} -> {len(existing)} (+{len(mapping)})")

    print(f"\n读入 {total} 条，接受 {len(accepted)}，拒绝 {len(rejected)}，"
          f"写回 {written} 条，无归属 {unowned} 条")
    if rejected:
        print("\n=== 拒绝样例（前 15 条）===")
        for en, zh, why in rejected[:15]:
            print(f"  {why:<30} {en!r} -> {zh!r}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", choices=["corrupt", "db"], default="corrupt")
    ap.add_argument("--tables", type=lambda s: [x for x in s.split(",") if x])
    ap.add_argument("--size", type=int, default=120)
    ap.add_argument("--outdir")
    ap.add_argument("--merge", help="合并已翻译好的批次目录")
    ap.add_argument("--batches", help="批次清单目录（默认 build/xlate_batches）")
    ap.add_argument("--into", default="db_gap_fill.json", help="合并进哪个源批次文件")
    args = ap.parse_args()
    if args.merge:
        return cmd_merge(args)
    return cmd_batches(args)


if __name__ == "__main__":
    raise SystemExit(main())
