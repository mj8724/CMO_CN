#!/usr/bin/env python3
"""数据库汉化覆盖审计：逐文本面统计缺口并按「是否值得翻译」分类。

覆盖四个面（与 DB 查看器实际渲染的字段对应）：
  record_name     各 Data* 表的 Name 列（列表/详情里显示的装备名）
  record_comment  各 Data* 表的 Comments 列（参考注释/年代与型号清单）
  enum            Enum* 表的 Description/Comment（下拉与详情里的枚举显示值）
  prose           DB/Descriptions/**/*.txt 的正文段落（详情页大段说明）

判定与分类：
  translated      在 exact 词典、或四个 DB 词库源里已有中文译文
  no_letters      不含字母（纯数字/符号）—— 按口径 13 不译
  numeric_unit    数值+单位（如 "2 nm" / "3 rnds"）—— 按口径 13 不译
  designator      纯代号（每段都是代号/缩写/数字，无可译英文词）—— 保留英文
  prose           含可译英文词 —— 真缺口

用法：
  python scripts/db_coverage_audit.py [--json build/db_coverage_audit.json] [--samples 3]
"""
from __future__ import annotations

import json
import re
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
DEFAULT_DB = GAME / "DB/DB3K_519.db3"
DESC = GAME / "DB/Descriptions"

# 需要审计的记录名表（DB 查看器的对象类型）
NAME_TABLES = ["DataAircraft", "DataShip", "DataSubmarine", "DataFacility", "DataGroundUnit",
               "DataSatellite", "DataSensor", "DataWeapon", "DataMount", "DataLoadout",
               "DataMagazine", "DataWarhead", "DataPropulsion", "DataComm", "DataCommType"]

HAN = re.compile(r"[\u4e00-\u9fff]")
LETTER = re.compile(r"[A-Za-z]")
BLANK = re.compile(r"\r?\n\s*\r?\n")
MARKER = re.compile(r"^(OVERVIEW|DETAIL|DETAILS|SPECIFICATION|SPECIFICATIONS|NOTE|NOTES|SOURCE|SOURCES)\s*:\s*", re.I)
NUMERIC_UNIT = re.compile(
    r"^[\d.,/\-\s]*(nm|m|km|ft|kt|kts|kg|lb|rnds?|rds?|sec|s|min|h|deg|%|m2|km/h|mph|"
    r"ghz|mhz|khz|hz|pps|dbi|w|kw|mm|cm|in)?[\d.,/\-\s]*$", re.I)
# 括号/斜杠/空格切分后的「词」
TOKEN_SPLIT = re.compile(r"[\s/()\[\],;:+=+&'\"]+")
# 各种型号/代号形态：F-15E、SS-N-27、533mm、AN/SPY-1D、MK46、9A34M2、MIM-23C
DESIGNATOR = re.compile(r"^(?:[A-Za-z]{0,4}[-/]?)*\d+[A-Za-z0-9\-/.]*$|^[A-Z0-9][A-Z0-9\-/.]{1,}$")


def load_dict() -> tuple[dict, dict[str, dict]]:
    d = json.loads((ROOT / "dict/CMOLocalizer.json").read_text(encoding="utf-8"))
    exact = d.get("exact") or {}
    src: dict[str, dict] = {}
    for name in ("db_record_names", "db_enums_extra", "db_enum_displays", "db_descriptions"):
        p = ROOT / f"dict/src/{name}.json"
        if p.exists():
            src[name] = json.loads(p.read_text(encoding="utf-8"))
    return exact, src


def make_checker(exact: dict, src: dict[str, dict]):
    def translated(s: str) -> bool:
        zh = exact.get(s)
        if isinstance(zh, str) and HAN.search(zh):
            return True
        for v in src.values():
            zh = v.get(s)
            if isinstance(zh, str) and HAN.search(zh):
                return True
        return False

    def has_word(s: str) -> bool:
        """是否含「可译英文词」：纯字母、长度 >= 3、非全大写代号。"""
        for tok in TOKEN_SPLIT.split(s):
            tok = tok.strip(".-")
            if len(tok) >= 3 and tok.isalpha() and not tok.isupper():
                return True
        return False

    def classify(s: str) -> str:
        if not LETTER.search(s):
            return "no_letters"
        if NUMERIC_UNIT.match(s):
            return "numeric_unit"
        if not has_word(s):
            return "designator"
        return "prose"

    return translated, classify


def audit_values(values, translated, classify) -> dict:
    out = {"total": 0, "translated": 0, "no_letters": 0, "numeric_unit": 0,
           "designator": 0, "prose": 0, "samples_prose": [], "samples_designator": []}
    for s in values:
        out["total"] += 1
        if translated(s):
            out["translated"] += 1
            continue
        k = classify(s)
        out[k] += 1
        if k == "prose" and len(out["samples_prose"]) < 40:
            out["samples_prose"].append(s)
        if k == "designator" and len(out["samples_designator"]) < 12:
            out["samples_designator"].append(s)
    return out


def main() -> int:
    args = sys.argv[1:]
    db_path = DEFAULT_DB
    out_json = ROOT / "build/db_coverage_audit.json"
    if "--db" in args:
        db_path = Path(args[args.index("--db") + 1])
    if "--json" in args:
        out_json = Path(args[args.index("--json") + 1])

    exact, src = load_dict()
    translated, classify = make_checker(exact, src)
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    cur = con.cursor()
    tables = [r[0] for r in cur.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]

    report: dict[str, dict] = {"db": db_path.name, "surfaces": {}}

    # 1) 记录名 + Comments
    for t in NAME_TABLES:
        try:
            cols = [r[1] for r in cur.execute(f'PRAGMA table_info("{t}")')]
        except sqlite3.Error:
            continue
        for col, surface in (("Name", "record_name"), ("Comments", "record_comment")):
            if col not in cols:
                continue
            vals = [r[0] for r in cur.execute(
                f'SELECT DISTINCT "{col}" FROM "{t}" WHERE "{col}" IS NOT NULL AND "{col}"<>\'\'')]
            key = f"{surface}:{t}"
            report["surfaces"][key] = audit_values(vals, translated, classify)

    # 2) 枚举
    for t in [x for x in tables if x.startswith("Enum")]:
        cols = [r[1] for r in cur.execute(f'PRAGMA table_info("{t}")')]
        col = "Description" if "Description" in cols else ("Comment" if "Comment" in cols else None)
        if not col:
            continue
        vals = [r[0] for r in cur.execute(
            f'SELECT DISTINCT "{col}" FROM "{t}" WHERE "{col}" IS NOT NULL AND "{col}"<>\'\'')]
        report["surfaces"][f"enum:{t}"] = audit_values(vals, translated, classify)

    # 3) 正文（DB/Descriptions）
    prose = {"total": 0, "translated": 0, "no_letters": 0, "numeric_unit": 0,
             "designator": 0, "prose": 0, "samples_prose": [], "samples_designator": [],
             "by_category": {}}
    for sub in sorted(p for p in DESC.iterdir() if p.is_dir()):
        for f in sub.glob("*.txt"):
            cat = (re.match(r"([A-Za-z]+)_", f.name) or [None, sub.name])[1].capitalize()
            st = prose["by_category"].setdefault(cat, {"total": 0, "translated": 0, "prose": 0})
            text = f.read_bytes().decode("utf-8", "replace")
            for block in BLANK.split(text):
                for idx, raw in enumerate(block.splitlines()):
                    line = raw.strip()
                    if len(line) < 4 or HAN.search(line) or not LETTER.search(line):
                        continue
                    if idx == 0:
                        m = MARKER.match(line)
                        if m:
                            line = line[m.end():].strip()
                    if len(line) < 4:
                        continue
                    key = re.sub(r"\s+", " ", line)
                    st["total"] += 1
                    prose["total"] += 1
                    if translated(key):
                        st["translated"] += 1
                        prose["translated"] += 1
                        continue
                    k = classify(key)
                    prose[k] += 1
                    if k == "prose":
                        st["prose"] += 1
                        if len(prose["samples_prose"]) < 60:
                            prose["samples_prose"].append(f"{f.name}: {key[:120]}")
    report["surfaces"]["prose:Descriptions"] = prose

    # 汇总排名
    rows = []
    for key, v in report["surfaces"].items():
        gap = v["total"] - v["translated"]
        rows.append((v["prose"], gap, key, v))
    rows.sort(reverse=True)
    print(f"=== DB 覆盖审计（{db_path.name}）===")
    print(f"{'面':<44}{'总数':>7}{'已译':>7}{'缺口':>7}{'可译':>7}{'代号':>6}{'数单位':>7}")
    for _, gap, key, v in rows:
        if v["total"] == 0:
            continue
        print(f"{key:<44}{v['total']:>7}{v['translated']:>7}{gap:>7}"
              f"{v['prose']:>7}{v['designator']:>6}{v['numeric_unit'] + v['no_letters']:>7}")
    tot = {"total": 0, "translated": 0, "prose": 0, "designator": 0, "skip": 0}
    for v in report["surfaces"].values():
        tot["total"] += v["total"]
        tot["translated"] += v["translated"]
        tot["prose"] += v["prose"]
        tot["designator"] += v["designator"]
        tot["skip"] += v["no_letters"] + v["numeric_unit"]
    print(f"{'合计':<44}{tot['total']:>7}{tot['translated']:>7}"
          f"{tot['total'] - tot['translated']:>7}{tot['prose']:>7}{tot['designator']:>6}{tot['skip']:>7}")
    report["totals"] = tot
    out_json.parent.mkdir(parents=True, exist_ok=True)
    out_json.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n审计明细: {out_json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
