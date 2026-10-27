#!/usr/bin/env python3
"""R72：数据库列表行尾 Comments 的待译工作量清单 + 词表守卫。

Comments 由游戏拼在 DB 查看器列表行末尾（实机形态
`A-300-600ST Beluga -- 民用/商用机 (商业民航), 1995, 5x, Super Guppy Replacement`），
引擎侧由 `TranslateDBRowCommentsTail` 按逗号分段查 `dict/src/db_comments.json`。

为什么单独成表：Comments 里 `Tank`(油箱) / `Mount`(挂架) / `Group`(编组) 与界面同名词条
含义不同（界面上 Tank=坦克、Group=编队），并进 `exact` 会把界面译坏（实测冲突 47 条）。

用法：
  python scripts/db_comments_work.py                 # 覆盖率 + 待译清单（按行数排序）
  python scripts/db_comments_work.py --top 200       # 只看前 200 个待译段
  python scripts/db_comments_work.py --check         # 词表守卫（门禁用，不查 DB）
"""
from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
TABLE = ROOT / "dict" / "src" / "db_comments.json"
BUILD = ROOT / "build"

HAN = re.compile(r"[\u4e00-\u9fff]")
LATIN = re.compile(r"[A-Za-z]")
DIGIT = re.compile(r"\d")
# 译文里允许保留的英文：型号/编号（含数字）、全大写缩写、单位
UNIT_OK = {"mm", "km", "kg", "nm", "kt", "kts", "lb", "lbs", "ft", "ton", "tons", "x", "k"}
LATIN_WORD = re.compile(r"(?<![A-Za-z])([A-Za-z]{2,})(?![A-Za-z])")
# 数量+单位/编制：结构由引擎规则处理，不进词表（否则永远追不上）
QUANTITY = (
    re.compile(r"^\d{1,3}x\s+(Mounts?|Pylons?)$", re.I),
    re.compile(r"^\d{1,3}x pr (Bty|Plt|Bn|Coy|Sec|Rgt)$", re.I),
    re.compile(r"^\d{1,4}(?:\.\d)?\s+tons?$", re.I),
    re.compile(r"^\d{1,3}(?:\.\d)?k\s+lb$", re.I),
    re.compile(r"^\d{1,4}(?:\.\d)?\s*lb$", re.I),
    # 与引擎 TranslateCommentQuantity 的扩展规则保持一一对应（漏一条就会把
    # 引擎已经处理好的段算成"待译"，清单虚高）
    re.compile(r"^\d{1,3}x\s+(Bty|Btys|Plt|Plts|Bn|Bns|Coy|Coys|Sec|Secs|Regt|Regts)$", re.I),
    re.compile(r"^\d{1,4}\s+(Rnds?|rounds?|Rounds?|rnds?)$", re.I),
    re.compile(r"^\d{1,3}x\s+(Tubes?|Tube|Barrels?|DCs?|Missiles?|Chaff|Salvos?|Sites?|Groups?|Lnchrs?|Launchers?)$", re.I),
)


def load_table() -> dict[str, str]:
    if not TABLE.is_file():
        return {}
    data = json.loads(TABLE.read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if isinstance(v, str) and not k.startswith("_")}


def is_quantity(seg: str) -> bool:
    return any(p.match(seg) for p in QUANTITY)


def is_year_or_count(seg: str) -> bool:
    """引擎 `IsYearOrCount` 的镜像：纯年份/数量段由结构保留，不查表。"""
    if not seg:
        return True
    if not DIGIT.search(seg):
        return False
    if not LATIN.search(seg):
        return True
    return seg[0].isdigit() and len(seg) <= 6


def covered(seg: str, table: dict[str, str]) -> bool:
    return seg in table or is_year_or_count(seg) or is_quantity(seg)


def write_freq(dbs: list[Path], freq: "list[tuple[str, int]]") -> Path:
    """把频次表写到 build/comments_freq.json。

    `db_comments_next.py` 靠它排序；以前这份文件没有生成者（手工留的），
    一旦 build/ 被清理，`--limit` 会静默输出「待译段 0 个」并以 0 退出——
    翻译循环在还有上千待译段时无提示断粮（R72 复查 F3）。
    """
    out = BUILD / "comments_freq.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(freq, ensure_ascii=False), encoding="utf-8")
    return out


def collect_comments(dbs: list[Path]) -> "list[tuple[str, int]]":
    import collections
    counts: collections.Counter[str] = collections.Counter()
    for db in dbs:
        con = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
        try:
            tables = [r[0] for r in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table'")]
            for t in tables:
                if not t.startswith("Data"):
                    continue
                try:
                    cols = [r[1] for r in con.execute(f'PRAGMA table_info("{t}")')]
                except sqlite3.Error:
                    continue
                if "Comments" not in cols:
                    continue
                try:
                    for (v,) in con.execute(
                            f'SELECT Comments FROM "{t}" WHERE Comments IS NOT NULL'):
                        if v and v != "-":
                            counts[v] += 1
                except sqlite3.Error:
                    continue
        finally:
            con.close()
    return counts.most_common()


def check_table(table: dict[str, str]) -> list[str]:
    """词表守卫：任一条不满足就报错（门禁调用）。"""
    problems = []
    for k, v in table.items():
        if not v.strip():
            problems.append(f"{k!r}: 空译文")
            continue
        if not HAN.search(v):
            problems.append(f"{k!r}: 译文不含中文（{v!r}）")
        if v.strip() == k.strip():
            problems.append(f"{k!r}: 译文与原文相同")
        # 纯代号/年份不得进表：结构由引擎规则处理，进表会挡住规则
        if is_year_or_count(k) or is_quantity(k):
            problems.append(f"{k!r}: 纯年份/数量段应由结构规则处理，不该进词表")
        # 译文里仍留超过 8 字符的普通英文词（型号/缩写放行）
        for w in LATIN_WORD.findall(v):
            if w.lower() in UNIT_OK or w.isupper() or any(c.isdigit() for c in w):
                continue
            if len(w) >= 4 and w[:1].islower():
                problems.append(f"{k!r}: 译文残留英文词 {w!r}（{v!r}）")
                break
    return problems


def check_normalized_conflicts(table: dict[str, str]) -> list[str]:
    """归一化后同一个键不得有两种译文（`Pacer CRAG` / `Pacer Crag` 这类）。

    实测出现过：同一个英文段大小写写法不同、译文却不一致，玩家看到两种说法。
    """
    import collections
    groups: dict[str, dict[str, str]] = collections.defaultdict(dict)
    for k, v in table.items():
        # 只抹掉空白/下划线与**首尾**标点：`-(Armoured Personel Carrier)` 的前导 `-`
        # 是有意义的形态标记，抹掉就会把两条不同的段误判成同一段的两种译法。
        # 只抹掉**内部**空白/下划线与首尾标点：`Poseidon C-3` 与 `Poseidon C3` 应判同段。
        # 连字符与斜杠**不能**抹：`Victor I/II`（I、II 批次）与 `Victor III`（III 级）是
        # 不同的批次，抹掉 `/` 会把两者撞成同一个键、误报冲突（实测踩过）。
        key = re.sub(r"[\s_]+", "", k.strip().strip("-—–·,.;:")).lower()
        groups[key][k] = v
    out = []
    for norm, variants in groups.items():
        if len(set(variants.values())) > 1:
            out.append(f"{norm}: " + " | ".join(f"{k}={v}" for k, v in variants.items()))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=80)
    ap.add_argument("--check", action="store_true",
                    help="词表守卫；同时用固定阈值断言覆盖率不退化")
    ap.add_argument("--min-covered", type=float, default=0.44,
                    help="--check 时要求的最低完全覆盖率（默认 0.44）")
    ap.add_argument("--json", default=str(BUILD / "db_comments_work.json"))
    ap.add_argument("--dbs", default="DB3K_519,CWDB_519",
                    help="逗号分隔的 DB 文件名（不含 .db3）")
    args = ap.parse_args()

    table = load_table()
    print(f"词表 {TABLE.name}: {len(table)} 条")

    problems = check_table(table) + check_normalized_conflicts(table)
    if problems:
        print(f"\n守卫失败（{len(problems)} 条）：")
        for p in problems[:20]:
            print("  - " + p)
        return 1
    print("守卫：PASS（译文含中文、无纯数量键、无残留英文词）")

    dbs = [GAME / "DB" / (n.strip() + ".db3") for n in args.dbs.split(",")]
    dbs = [d for d in dbs if d.is_file()]
    if not dbs:
        print("找不到 DB 文件；用 --dbs 指定", file=sys.stderr)
        return 1
    freq = collect_comments(dbs)
    freq_path = write_freq(dbs, freq)
    print(f"频次表: {freq_path}")
    total = sum(n for _, n in freq)

    done = partial = 0
    backlog: dict[str, int] = {}
    for value, n in freq:
        segs = [s.strip() for s in value.split(", ") if s.strip()]
        hit = sum(1 for s in segs if covered(s, table))
        if segs and hit == len(segs):
            done += n
            continue
        if hit:
            partial += n
        for s in segs:
            if not covered(s, table):
                backlog[s] = backlog.get(s, 0) + n

    print(f"\nComments 行覆盖（{'/'.join(d.name for d in dbs)}，共 {total} 行 / {len(freq)} 个唯一值）")
    print(f"  完全覆盖 {done:>6} ({done/total:5.1%})")
    print(f"  部分覆盖 {partial:>6} ({partial/total:5.1%})")
    print(f"  未动     {total-done-partial:>6} ({(total-done-partial)/total:5.1%})")

    if args.check:
        # 覆盖率守卫：词表被误删/误改会让覆盖跌破阈值，必须判红，
        # 否则「表还在但内容被清空」这种退化会静默通过。
        ratio = done / total if total else 0.0
        ok = ratio >= args.min_covered
        print(f"覆盖率守卫：{ratio:.1%} >= {args.min_covered:.0%} -> "
              + ("PASS" if ok else "FAIL"))
        return 0 if ok else 1

    ranked = sorted(backlog.items(), key=lambda kv: -kv[1])
    print(f"\n待译段 {len(ranked)} 个（按出现行数排序，前 {args.top}）：")
    for s, n in ranked[:args.top]:
        print(f"  {n:>5}  {s[:90]}")

    payload = {
        "dbs": [d.name for d in dbs],
        "totals": {"rows": total, "unique": len(freq),
                   "covered": done, "partial": partial,
                   "untouched": total - done - partial},
        "backlog": [{"seg": s, "rows": n} for s, n in ranked],
    }
    out = Path(args.json)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n清单: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
