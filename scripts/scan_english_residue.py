#!/usr/bin/env python3
"""R52：英文残留扫描——用游戏**数据本身**当语料，检查引擎真实产出。

和 `verify_regressions` 的 `L-ui-residuals` 的区别：
那个用例只检查「已入词典却仍显英文」，没进词典的字符串它永远看不见。
所以「数据库里整屏英文」「军队名没翻译」可以在所有门禁全绿的情况下长期存在。

这里把 DB 各表的 Name、枚举 Description、想定内嵌文本直接喂给引擎的
`TranslateString`，判据换成「翻完还剩多少拉丁字母」，从而得到**真实缺口**。

用法：
  python scripts/scan_english_residue.py                 # 扫描 + 摘要 + 写缺口清单
  python scripts/scan_english_residue.py --summary-only  # 只打印摘要
  python scripts/scan_english_residue.py --scope db      # 只扫数据库
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path(os.environ.get("GAME_DIR", "G:/soft/steam/steamapps/common/Command - Modern Operations"))
BUILD = ROOT / "build"
CSC = Path("C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")

LATIN_WORD = re.compile(r"[A-Za-z]{2,}")
# 纯型号/编号：全大写或含数字的 token 视为装备代号，不算"未翻译的英文"
CODE_LIKE = re.compile(r"^(?=.*\d)[A-Za-z0-9]|^[A-Z0-9][A-Z0-9\-/.]*$")


def is_meaningful_word(text: str) -> bool:
    """文本里是否还有"真的英文单词"（而不是 AGM-86C 这种装备代号）。

    判据：长度 >= 4 且不是全大写。全大写短串基本都是缩写/代号，界面保留原样是惯例。
    """
    for tok in LATIN_WORD.findall(text or ""):
        if len(tok) >= 4 and not tok.isupper():
            return True
    return False


def collect_db(games: list[Path]) -> list[tuple[str, str, str]]:
    """DB 里的用户可见文本：Data* 表的 Name + 枚举表的 Description。"""
    out: list[tuple[str, str, str]] = []
    for db in games:
        con = sqlite3.connect(db.as_uri() + "?mode=ro", uri=True)
        try:
            tables = [r[0] for r in con.execute(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
            for t in tables:
                if t.startswith("Data"):
                    col = "Name"
                elif t.startswith("Enum"):
                    col = "Description"
                else:
                    continue
                try:
                    rows = con.execute(
                        f'SELECT "{col}" FROM "{t}" WHERE "{col}" IS NOT NULL AND "{col}" <> \'\''
                    ).fetchall()
                except sqlite3.Error:
                    continue
                tag = f"{db.stem}"
                for (v,) in rows:
                    if isinstance(v, str) and v.strip():
                        out.append(("db", f"{tag}.{t}", v))
        finally:
            con.close()
    return out


def collect_scenario() -> list[tuple[str, str, str]]:
    """想定内嵌文本：side/message 源批次里的英文原文（值才是译文）。"""
    out = []
    for p in sorted((ROOT / "dict" / "src").glob("*.json")):
        if not re.search(r"_(side|message)", p.stem):
            continue
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        if not isinstance(data, dict):
            continue
        for k, v in data.items():
            if k.startswith("_") or not isinstance(v, str):
                continue
            out.append(("scenario", p.stem, k))
    return out


def collect_ui() -> list[tuple[str, str, str]]:
    """界面术语源里的英文原文，用来确认这些已覆盖（回归基线，不是新缺口）。"""
    out = []
    for name in ("menu_terms", "db_viewer_html_terms", "terms", "ui_cleanup_terms"):
        p = ROOT / "dict" / "src" / f"{name}.json"
        if not p.is_file():
            continue
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            continue
        if isinstance(data, dict):
            for k in data:
                if not k.startswith("_"):
                    out.append(("ui", name, k))
    return out


def build_scanner() -> Path:
    dll = BUILD / "CMOLocalizer.dll"
    if not dll.is_file():
        raise SystemExit("缺少 build/CMOLocalizer.dll，先跑 bash scripts/build.sh --engine-only")
    exe = BUILD / "ResidueScan.exe"
    refs = [f"-reference:{dll}"]
    proc = subprocess.run(
        [str(CSC), "-nologo", "-target:exe", "-platform:anycpu", f"-out:{exe}", *refs,
         str(ROOT / "src" / "tests" / "ResidueScan.cs")],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    if proc.returncode:
        raise SystemExit("编译 ResidueScan 失败:\n" + (proc.stdout or "") + (proc.stderr or ""))
    return exe


def run_scan(rows: list[tuple[str, str, str]]) -> list[dict]:
    exe = build_scanner()
    BUILD.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="cmo-residue-") as td:
        inp, outp = Path(td) / "in.tsv", Path(td) / "out.tsv"
        seen = set()
        with inp.open("w", encoding="utf-8", newline="\n") as f:
            for cat, table, text in rows:
                clean = text.replace("\t", " ").replace("\r", " ").replace("\n", " ")
                key = (cat, table, clean)
                if key in seen:
                    continue
                seen.add(key)
                f.write(f"{cat}\t{table}\t{clean}\n")
        proc = subprocess.run([str(exe), str(ROOT), str(BUILD / "CMOLocalizer.dll"),
                               str(inp), str(outp)],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        if proc.returncode:
            raise SystemExit("ResidueScan 运行失败:\n" + (proc.stdout or "") + (proc.stderr or ""))
        print("  " + (proc.stdout or "").strip())
        results = []
        with outp.open(encoding="utf-8") as f:
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) < 5:
                    continue
                cat, table, src, dst, verdict = parts[:5]
                results.append({"category": cat, "table": table, "src": src,
                                "dst": dst, "verdict": verdict})
        return results


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--scope", choices=["all", "db", "scenario", "ui"], default="all")
    ap.add_argument("--summary-only", action="store_true")
    args = ap.parse_args()

    rows: list[tuple[str, str, str]] = []
    if args.scope in ("all", "db"):
        games = sorted(GAME.glob("DB/*.db3"))
        print(f"数据库: {', '.join(g.name for g in games)}")
        rows += collect_db(games)
    if args.scope in ("all", "scenario"):
        rows += collect_scenario()
    if args.scope in ("all", "ui"):
        rows += collect_ui()

    # 91 个数据库版本里同一装备名会重复上百次；词典是共用的，
    # 所以必须按**文本**全局去重，否则统计出来的缺口是假的。
    uniq: dict[str, tuple[str, str, str]] = {}
    for cat, table, text in rows:
        if text not in uniq:
            uniq[text] = (cat, table, text)
    rows = list(uniq.values())

    print(f"语料 {len(rows)} 条（已按文本去重），开始扫描…")
    results = run_scan(rows)

    # 分类：纯代号（可保留）/ 真英文（缺口）
    gaps = []
    stats: dict[str, dict[str, int]] = {}
    for r in results:
        bucket = stats.setdefault(r["category"], {"ok": 0, "partial": 0, "none": 0, "gap": 0})
        bucket[r["verdict"]] = bucket.get(r["verdict"], 0) + 1
        if r["verdict"] == "ok":
            continue
        if not is_meaningful_word(r["src"]) and r["verdict"] == "none":
            continue  # 纯装备代号，界面保留原样是惯例
        if r["verdict"] == "partial" and not is_meaningful_word(r["dst"]):
            r["verdict"] = "ok"        # 剩下的拉丁字母是型号，不算半译
            continue
        bucket["gap"] += 1
        gaps.append(r)

    print("\n=== 按来源统计 ===")
    print(f"{'来源':<12}{'已中文':>9}{'半译':>8}{'未翻':>8}{'真缺口':>9}")
    for cat, b in sorted(stats.items()):
        print(f"{cat:<12}{b.get('ok', 0):>9}{b.get('partial', 0):>8}"
              f"{b.get('none', 0):>8}{b.get('gap', 0):>9}")

    by_table: dict[str, int] = {}
    for g in gaps:
        by_table[g["table"]] = by_table.get(g["table"], 0) + 1
    print("\n=== 缺口分布（Top 20）===")
    for t, n in sorted(by_table.items(), key=lambda kv: -kv[1])[:20]:
        print(f"  {n:>6}  {t}")

    if args.summary_only:
        return 0

    out = BUILD / "residue_gaps.json"
    BUILD.mkdir(exist_ok=True)
    out.write_text(json.dumps(gaps, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n缺口清单: {out}（{len(gaps)} 条）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
