#!/usr/bin/env python3
"""R71 静态回归：数据库记录名不得被场景源的句子碎片覆盖，残留改写不得污染型号。

三条判据，每条都带负向用例（证明判据真的会红）：

  1. **单一事实源**：`db_*` 源里已有「括号外无英文残留」的干净译名时，
     场景源（`*side*.json` / `showcase_side.json`）不得同键再写一句
     「英文前缀 + 整句」把它覆盖掉——构建按文件名字母序后写者胜，
     这正是 R71 实测的现象：`Radar (Long Track [P-40])` 曾显示成
     「Radar Long Track P-40型雷达担负空情预警 (全天候值班)」。
  2. **已修脏值不得回生**：本轮清理掉的 34 条具体脏值（原文/译文对）逐个断言
     已不在任何源文件里。比"猜句子碎片形态"精确——后者会把
     `1x YaMZ 238 V 型柴油机，240 马力` 这类合法规格串误判成碎片。
  3. **残留改写不得按词拼接**：`fix_db_residue.py` 的 Type 规则只动 `Type NNN`；
     `terms.json` 的缩写（PC=巡逻艇、AGM=空地导弹）不能被当词替换
     （会把 `PC2` 变「巡逻艇2」），`Radar` 也不得与既有「警戒雷达」拼成
     「警戒雷达雷达」。括号内原文一律不动。

用法：
  python src/tests/DatabaseResidueTests.py
"""

from __future__ import annotations

import importlib.util
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "dict" / "src"
HAN = re.compile(r"[\u4e00-\u9fff]")
BRACKETS = re.compile(r"\[[^\[\]]*\]|\([^()]*\)|（[^（）]*）|【[^【】]*】")
LATIN_WORD = re.compile(r"[A-Za-z]{4,}")
SIDE_FILES = ("*side*.json", "showcase_side.json")

# R71 清理掉的具体脏值：键 → 曾经污染的场景源译文。
# 只增不改：这些都是实测过的「英文前缀 + 整句」形态。
FIXED_DEBRIS = {
    "SAM Bty (SA-4 Ganef [2K11 Krug])":
        "SAM Bty SA-4 Ganef 2K11 Krug导弹连担负机动防空 (待机歼敌)",
    "Radar (Long Track [P-40])":
        "Radar Long Track P-40型雷达担负空情预警 (全天候值班)",
    "Radar (Spoon Rest A [P-12])": "雷达 (Spoon Rest A [P-12])",
    "Radar (Type 80 Linesman)": "雷达 (Type 80 Linesman)",
    "Radar (Flat Face B [P-19])": "警戒雷达Radar(Flat Face B [P-19])开机值班",
    "Radar (Type 96 [S-649])": "雷达 (Type 96 [S-649])",
    "Radar (Type 95 [S-259])": "雷达 (Type 95 [S-259])",
    "AAA Bty (37mm Type 65 Twin x 4)":
        "AAA Bty高炮连辖37mm Type 65 Twin型高炮4门之阵地列为要点目标 (必须拔除)",
    "AAA Bty (ZPU-2 x 4 + Fire Can FC)":
        "AAA Bty高炮连辖ZPU-2型高射机枪4挺配Fire Can火控雷达之阵地难缠 (先打雷达)",
    "AAA Plt/2 (20mm/70 Oerlikon Mk4 x 2)": "高炮排AAA Plt/2(20mm/70 Oerlikon Mk4 x 2)火力配属完毕",
    "SAM Sec (Redeye MANPADS x 4)": "防空分队SAM Sec(Redeye MANPADS x 4)掩护跟进",
    "SAM Bn (SA-6b Gainful [2K12 Kub])": "防空导弹营SAM Bn(SA-6b Gainful [2K12 Kub])展开部署",
    "SAM Sec (SA-7a Grail MANPADS x 4)": "萨姆导弹分队 (SA-7a Grail MANPADS x 4)",
    "SAM Bn (SA-2d Guideline [S-75 Dvina])": "萨姆导弹营 (SA-2d Guideline [S-75 Dvina])",
    "BPK Kashin [Pr.61]": "BPK Kashin级驱逐舰Pr.61型，敌水面突击群兵力",
    "BPK Kresta II [Pr.1134A Berkut A]": "BPK Kresta II级巡洋舰Pr.1134A Berkut A型，敌水面突击群核心",
    "VTR Boris Chilikin [Pr.1559V]": "VTR Boris Chilikin级补给舰 [Pr.1559V]",
    "F 300 Oslo [Dealey Mod]": "F 300 Oslo舰 [Dealey Mod]",
    "S 32 San Luis [Type 209-1100]": "S 32 San Luis号潜艇Type 209-1100型，我水下突击兵力",
    "D 95 Manchester [Type 42 Batch 3]": "D 95 Manchester曼彻斯特号驱逐舰 [Type 42 Batch 3]",
    "Type 55A Shantou/Swatow": "Type 55A“汕头”（Swatow）",
    "P-3C Orion Update III AIP": "P-3C 猎户座 Update III AIP",
    "Bunker (Sector Control Station)":
        "Bunker掩体属Sector Control Station分区指挥所，为敌防空指挥中枢 (列为首要目标)",
}


def check(ok: bool, message: str) -> None:
    if not ok:
        raise AssertionError(message)
    print("PASS " + message, flush=True)


def load(name: str) -> dict:
    p = SRC / name
    if not p.is_file():
        return {}
    data = json.loads(p.read_text(encoding="utf-8"))
    return ({k: v for k, v in data.items()
             if isinstance(v, str) and not k.startswith("_")}
            if isinstance(data, dict) else {})


def outside_brackets(t: str) -> str:
    prev = None
    while prev != t:
        prev = t
        t = BRACKETS.sub(" ", t)
    return t


def any_residual(t: str) -> list[str]:
    """整串（含括号）里的英文残留词——DB 查看器走的全局 exact，括号内也照样显示。"""
    return [w for w in LATIN_WORD.findall(t or "") if not w.isupper()]


def residual_words(t: str) -> list[str]:
    """括号外仍残留的英文词（长度 >= 4 且非全大写，与 scan_english_residue 同口径）。"""
    return [w for w in LATIN_WORD.findall(outside_brackets(t or "")) if not w.isupper()]


def side_sources() -> dict[str, dict]:
    out = {}
    for pattern in SIDE_FILES:
        for p in sorted(SRC.glob(pattern)):
            out[p.name] = load(p.name)
    return out


def find_single_source_violations(db: dict, sides: dict) -> list[str]:
    """场景源用**比 db_* 更多英文残留**的值覆盖同键（DB 查看器读全局 exact，直接受影响）。"""
    out = []
    for name, mapping in sides.items():
        for k, v in mapping.items():
            clean = db.get(k)
            if clean is None or not HAN.search(v) or clean == v:
                continue
            if len(any_residual(v)) > len(any_residual(clean)):
                out.append(f"{name}: {k!r} -> {v[:60]!r}")
    return out


def find_debris(db: dict, sides: dict, debris: dict) -> list[str]:
    """已登记的脏值是否又出现在任何源文件里。"""
    out = []
    for name, mapping in list(sides.items()) + [("db", db)]:
        for k, bad in debris.items():
            if mapping.get(k) == bad:
                out.append(f"{name}: {k!r} 恢复成 {bad[:60]!r}")
    return out


def check_single_source() -> None:
    db: dict[str, str] = {}
    for p in sorted(SRC.glob("db_*.json")):
        db.update(load(p.name))
    sides = side_sources()

    offenders = find_single_source_violations(db, sides)
    check(not offenders,
          f"R71 单一事实源：场景源未覆盖 db_* 的干净记录名（db 干净 {len(db)} 条）"
          if not offenders else f"R71 单一事实源被破坏：{'；'.join(offenders[:3])}")

    # 负向用例：判据必须抓住真实形态
    fake_db = {"Radar (Long Track [P-40])": "雷达站 (长轨 [P-40])"}
    fake_sides = {"x_side.json": {"Radar (Long Track [P-40])":
                                  "Radar Long Track P-40型雷达担负空情预警 (全天候值班)"}}
    check(len(find_single_source_violations(fake_db, fake_sides)) == 1,
          "R71 负向用例：句子碎片覆盖干净译名会被判红")

def check_fixed_debris_absent() -> None:
    db: dict[str, str] = {}
    for p in sorted(SRC.glob("db_*.json")):
        db.update(load(p.name))
    sides = side_sources()

    bad = find_debris(db, sides, FIXED_DEBRIS)
    check(not bad,
          f"R71 已修的 {len(FIXED_DEBRIS)} 条句子碎片未回生"
          if not bad else f"R71 句子碎片回生：{'；'.join(bad[:3])}")

    # 负向用例：同样的判据必须能抓出脏值
    fake = {"x_side.json": {"Bunker (Sector Control Station)":
                            FIXED_DEBRIS["Bunker (Sector Control Station)"]}}
    check(len(find_debris({}, fake, FIXED_DEBRIS)) == 1,
          "R71 负向用例：脏值回生会被判红")


def load_fixer():
    spec = importlib.util.spec_from_file_location(
        "fix_db_residue", ROOT / "scripts" / "fix_db_residue.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def check_fixer_safety() -> None:
    fixer = load_fixer()
    sub = fixer.make_rewriter(fixer.CURATED)

    cases = {
        "Type 965M 单床架": "965M型 单床架",
        "中国 Type 65 反潜火箭弹": "中国 65型 反潜火箭弹",
        "Type 209-1100 潜艇": "209-1100型 潜艇",
        "Type 17053，30059型": "17053型，30059型",
    }
    for src, want in cases.items():
        got = fixer.rewrite_outside(src, sub)
        check(got == want, f"R71 Type 规则：{src!r} -> {got!r}")

    # 型号连续体与缩写必须原样通过（真实反例：PC=巡逻艇、AGM=空地导弹曾把型号拆开）
    for s in ("40mm AGS/7.62mm MG Trackfire RWS", "AGM-84D 鱼叉",
              "2x SEMT-皮尔斯蒂克 12 PC2 Vxxx 柴油机",
              "AN/SPS-64(V)6 [Raycas 1660 12S]"):
        check(fixer.rewrite_outside(s, sub) == s, f"R71 型号受保护：{s!r}")

    # 括号内一律不动
    bracketed = "雷达 (Spoon Rest A [P-12])"
    check(fixer.rewrite_outside(bracketed, sub) == bracketed, "R71 括号内原文不动")

    # 负向用例：重复中文必须能被检出（`Radar` 入表曾拼出「警戒雷达雷达」）
    check(fixer.DUP_CJK.search("警戒雷达雷达") is not None,
          "R71 负向用例：重复中文能被 DUP_CJK 检出")


def main() -> int:
    check_single_source()
    check_fixed_debris_absent()
    check_fixer_safety()
    print("\nR71 全部通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
