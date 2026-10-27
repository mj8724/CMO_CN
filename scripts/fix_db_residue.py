#!/usr/bin/env python3
"""R71：把数据库记录名里**括号外**的英文残留改写成中文（确定性规则，不用 LLM）。

背景：`scan_english_residue --scope db` 的缺口里，`SA-7a 圣杯 [9K32 Strela-2]`
这种「括号内保留外文代号」是刻意写法，不算缺口；真正的残留是括号外的词。

三类确定性改法（可复现、无自由发挥）：
  1. **Type 规则**：`Type 209-1100` → `209-1100型`，`China Type 65` → `65式`
     （仓库既有写法：`209-1100型`、`80型 巡线员`、`65式双联装`）。
  2. **厂名/普通英文词整词替换**：`CURATED` 表 + `terms.json` 已有译法，
     **最长匹配优先**，且**紧邻数字/连字符的词一律不动**。
     后一条是本脚本的关键防线：按词替换会把 `PC2` 变成「巡逻艇2」、
     `AGM-84D` 变成「空地导弹-84D」（项目文档反复记录过这类垃圾串）。
  3. **整串人工译文**（`WHOLE`）：逐条核过的完整译名。

括号内（`[..]`/`(..)`/`（..）`/`【..】`）一律不动——那里是外文绰号与原厂型号。
厂名之外、没有稳定中文译法的代号（`SRS-6 Romb-4A`、`MicroPUFFS` 之类）保持原文，
按 `merge_record_names.py` 的「专名放行」约定不属漏译。

用法：
  python scripts/fix_db_residue.py            # 生成 build/residue_out/batch_*.json + 统计
  python scripts/fix_db_residue.py --dry-run  # 只统计
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "dict" / "src"
BUILD = ROOT / "build"

HAN = re.compile(r"[\u4e00-\u9fff]")
# 括号（含全角）内的内容一律不动
BRACKETS = re.compile(r"\[[^\[\]]*\]|\([^()]*\)|（[^（）]*）|【[^【】]*】")
TYPE = re.compile(r"(?<![A-Za-z])Type\s+([0-9][0-9A-Za-z\-]*)(?![0-9A-Za-z\-]|型|式)")
CHINA_TYPE = re.compile(
    r"(?<![A-Za-z])(?:China|Chinese)\s+Type\s+([0-9][0-9A-Za-z\-]*)(?![0-9A-Za-z\-]|型|式)")
# 残余英文词判定：长度 >= 4 且非全大写（与 scan_english_residue 同口径）
LATIN_WORD = re.compile(r"[A-Za-z]{4,}")
# 整词边界：前后都不能是字母数字（型号连续体整体保护）
BOUND_L = r"(?<![A-Za-z0-9\-/])"
BOUND_R = r"(?![A-Za-z0-9\-/])"
# 相邻重复的中文片段（`警戒雷达` + `Radar`→`雷达` 会拼出「雷达雷达」）
DUP_CJK = re.compile(r"([\u4e00-\u9fff]{2,4})\1+")

# 厂名/品牌/普通英文词的人工核过译法（只收中文文献有稳定译法的）。
# 刻意不收通用词（Radar/Sea/Fire/Long/Twin/Bty…）：它们是句子成分，
# 当名字的词层替换会拼出「警戒雷达雷达」「长 履带」这类垃圾串（已实测）。
# 术语表 terms.json 只服务引擎的组合翻译通道，不能拿来做整词替换。
CURATED = {
    # 普通英文词：这些在记录名里是真漏译
    "Generic": "通用", "Runway": "跑道",
    "Penetrator": "侵彻型", "incendiary": "燃烧", "cluster": "集束",
    "Flamethrower": "火焰喷射器", "Coax": "同轴机枪", "Tier": "层",
    "Skipper": "船长",
    "Enduring": "持久",
    "Update": "升级",
    # 北约绰号（多词先匹配；单用 Long/Track 这类词是禁止的）
    "Long Track": "长轨", "Flat Face": "平面", "Spoon Rest": "匙架",
    "Fire Can": "火罐", "Squeeze Box": "挤压箱", "Long Range": "远程",
    "Side Net": "侧网", "Odd Pair": "奇对", "Long Bow": "长弓",
    "Enduring Shield": "持久之盾", "Hunting Eagle Strike": "猎鹰突击",
    # 已入词典的同一批产品名（保持一致，不另起译法）
    "Sperry": "斯佩里", "Alkan": "阿尔坎", "Mizrak": "米兹拉克",
    "Maverick": "小牛", "Talon": "禽爪", "Hughes": "休斯",
    "Plessey": "普莱西", "Shield": "盾", "Kapsel": "荚舱", "Decca": "德卡",
    # 厂名 / 品牌（中文文献通行音译）
    "Fadak": "法达克", "Vicon": "维康", "Virgo": "维尔戈", "Voronezh": "沃罗涅日",
    "Omera": "奥梅拉", "Teseo": "特塞奥", "Nettuno": "内图诺", "Nettunel": "内图内尔",
    "Roketsan": "罗克桑", "Aselsan": "阿塞尔桑", "Korkut": "科尔库特",
    "Kongsberg": "康斯伯格", "Cockerill": "科克里尔", "Emerson": "埃默森",
    "Ericsson": "爱立信", "Elbit": "埃尔比特", "Elta": "埃尔塔", "Elisra": "埃利斯拉",
    "Sagem": "萨吉姆", "Telephonics": "泰勒福尼克斯", "Raymarine": "雷松",
    "Furuno": "古野", "Plessey": "普莱西", "Aerostar": "航星", "Skeldar": "谢尔达尔",
    "Reccelite": "侦察之光", "MicroPUFFS": "微型烟团", "BlueTracker": "蓝追踪者",
    "BlueWatcher": "蓝卫士", "BlueMaster": "蓝主人", "BlueHunter": "蓝猎手",
    "HeliTOW": "直升机陶", "Protean": "普罗蒂安", "Saphir": "蓝宝石", "Odra": "奥德拉",
    "Veloce": "韦洛切", "Shakti": "夏克提", "Aculeus": "刺针", "InWarner": "内置告警器",
    "Kord": "科尔德", "Fraktsiya": "分队", "Smalta": "斯马尔塔", "Timnex": "蒂姆内克斯",
    "Telelens": "长焦镜头", "Philax": "菲莱克斯", "Deseaver": "德西弗",
    "Elips": "埃利普斯", "Tetral": "泰特拉尔", "Kariwara": "卡里瓦拉",
    "Gardenya": "加尔德尼亚", "Pastel": "帕斯特尔", "Sonata": "索纳塔",
    "Sherloc": "夏洛克", "Barem": "巴雷姆", "Platan": "普拉坦", "Vera": "维拉",
    "Lira": "里拉", "Pinguin": "企鹅", "Karinga": "卡林加", "Kowsar": "科萨尔",
    "Rezonans": "共振", "Altius": "阿尔提乌斯", "Simsek": "闪电",
    "Dhruvastra": "德鲁瓦斯特拉", "Deso": "德索", "Sonac": "索纳克",
    "Falarick": "法拉克", "Gokdeniz": "格克德尼兹", "Ellora": "埃洛拉", "Smash": "粉碎",
    "Oqab": "鹰", "Navab": "纳瓦布", "Zoka": "佐卡", "Aveox": "艾维奥克斯",
    "Banderol": "巴杰罗尔", "Bina": "比纳", "Bulsae": "火鸟", "Cheonyong": "天勇",
    "Furia": "弗里亚", "Garmon": "和声", "Jastrzab": "鹰", "KamAZ": "卡玛斯",
    "Kavosh": "卡沃什", "Leleka": "鹳", "Lionfish": "狮子鱼", "Merop": "蜂虎",
    "MiSAR": "微型合成孔径雷达", "Mikholit": "米霍利特", "NautoScan": "诺泰扫描",
    "Opaz": "奥帕兹", "Orlik": "小鹰", "Piorun": "闪电", "Rychag": "杠杆",
    "Sagol": "萨戈尔", "Tabas": "塔巴斯", "TacNet": "战术网", "Toloue": "曙光",
    "Toufan": "风暴", "Traan": "特兰", "Uprava": "指挥", "Uttam": "乌塔姆",
    "VisionMaster": "视界大师", "Visionmaster": "视界大师", "Vola": "沃拉",
    "Yabhon": "亚布洪", "Consilium": "康西利姆", "Selesmar": "塞莱斯马",
    "Shibaabura": "芝浦", "Nebo": "天空", "Protivnik": "对手", "Sorbtsiya": "对抗",
    "Khibiny": "希比内", "Klyukva": "红莓", "Lyutik": "雏菊", "Lena": "列娜",
    "Furke": "弗尔克", "Podkat": "波德卡特", "Podlet": "波德莱特", "Raguga": "彩虹",
    "Rezets": "切割者", "Sopka": "山丘", "Sosna": "松树", "Novella": "新星",
    "Vinyetka": "维涅特卡", "Otklik": "回波", "Paket": "包", "Prima": "第一",
    "Protazan": "长矛", "Virzh": "维尔日", "Solntsepyok": "烈日",
    "Avtomat": "自动", "Naxos": "纳克索斯", "Kurs": "航向", "Reyder": "袭击者",
    "Pollux": "普洛克斯", "Caiquen": "凯肯", "Daedalus": "代达罗斯",
    "Daedelus": "代达罗斯", "Almas": "钻石", "Saeqeh": "闪电", "Fotros": "福特罗斯",
    "Hamaseh": "哈马塞", "Mohajer": "移民", "Anapa": "阿纳帕", "Kronshtadt": "喀琅施塔得",
    "Favorit": "宠儿", "Trophy": "战利品", "Skynex": "天空拦截者", "Skyborg": "天空博格",
    "Starlink": "星链", "Krab": "蟹", "Ottokar": "奥托卡", "Brda": "布尔达",
    "Sintez": "合成", "Khibiny-M": "希比内-M",
}

# 整串译文：逐条人工核过（含括号，不能靠词替换拼出来）。
WHOLE = {
    "Squeeze Box [Kondensor, IR/TV/Laser]": "挤压箱 [康登索尔，红外/电视/激光]",
    "CBU-57/B incendiary cluster, Thunderbox Mk1 [WC]": "CBU-57/B 燃烧集束炸弹，雷霆箱 Mk1 [集束]",
    "CBU-57/B incendiary cluster, Thunderbox Mk1": "CBU-57/B 燃烧集束炸弹，雷霆箱 Mk1",
    "Stinger MANPADS [Fliegerfaust]": "毒刺 便携式防空导弹 [飞拳]",
    "FIM-92C Stinger MANPADS [Fliegerfaust]": "FIM-92C 毒刺 便携式防空导弹 [飞拳]",
    "SAM Plt/2 (Rapier FSA Blindfire)": "地空导弹第2排 (长剑 FSA 盲射)",
    "TOS-1A Solntsepyok Heavy Flamethrower": "TOS-1A 烈日 重型火焰喷射器",
    "Palm Frond [MR-231-3 Waigatsch]": "棕榈叶 [MR-231-3 瓦伊加奇]",
    "Tall Rack [55Zh6UME Nebo UME, Protivnik-GE Type]": "高架 [55Zh6UME 天空 UME，对手-GE 型]",
    "PL-636.3 Kilo [Varshavyanka]": "PL-636.3 基洛级 [华沙女人]",
    "ESR-220 Kameelperd": "ESR-220 长颈鹿",
    "Kongsberg ST2400 Variable Depth Sonar (VDS)": "康斯伯格 ST2400 可变深度声呐（VDS）",
    "Generic FrankenSAM USV Mount": "通用 弗兰肯防空导弹 无人艇基座",
    "Generic FrankenSAM USV": "通用 弗兰肯防空导弹 无人艇",
    "Condib 120 [xxkg Runway Penetrator]": "康迪布 120 [xxkg 跑道侵彻弹]",
    "NautoScan NX [X-Band]": "诺泰扫描 NX [X波段]",
    "NautoScan NX [S-Band]": "诺泰扫描 NX [S波段]",
    "Sperry VisionMaster FT 250 [I Band]": "斯佩里 视界大师 FT 250 [I 波段]",
    "Sperry VisionMaster FT 250 [S Band]": "斯佩里 视界大师 FT 250 [S 波段]",
    "Sperry Visionmaster FT 250": "斯佩里 视界大师 FT 250",
    "Consilium Selesmar RTM 30 SIM": "康西利姆 塞莱斯马 RTM 30 SIM",
    "Consilium Selesmar RTM 25 XIM": "康西利姆 塞莱斯马 RTM 25 XIM",
    "2x Shibaabura 6NMU Diesels": "2x 芝浦 6NMU 柴油机",
    "Telephonics 143": "泰勒福尼克斯 143",
    "59N6 Protivnik-GE": "59N6 对手-GE",
    "SPS-171/172 Sorbtsiya [L-005]": "SPS-171/172 对抗 [L-005]",
    "OESVR Fraktsiya [IR]": "OESVR 分队 [红外]",
    "OESVR Fraktsiya [TV]": "OESVR 分队 [电视]",
    "OESVR Fraktsiya": "OESVR 分队",
    "CA/BQG-501 MicroPUFFS": "CA/BQG-501 微型烟团",
    "MicroPUFFS": "微型烟团",
    "SurveilSPIRE": "监视塔",
    "DroneDefender": "无人机防御者",
    "BrightNite": "明夜",
    "PhantomStrike 900": "幽灵打击 900",
    "MicroLink Devices Solar Cells, Amprius LI Batteries": "微链接设备 太阳能电池，安普瑞斯 锂电池",
    "Raymarine SL72": "雷松 SL72",
    "Searanger": "海响尾蛇",
    "20mm/128 Searanger": "20mm/128 海响尾蛇",
    "AirMaster C": "空中大师 C",
    "25mm/75 M242 Bushmaster CPWS": "25mm/75 M242 大毒蛇 遥控武器站",
    "Cockerill 3030 [30mm Mk44 Bushmaster II, 7.62mm Coax]": "科克里尔 3030 [30mm Mk44 大毒蛇 II，7.62mm 同轴机枪]",
    "Cockerill 3105 [105mm, 7.62mm Coax]": "科克里尔 3105 [105mm，7.62mm 同轴机枪]",
    "40mm AGS/7.62mm MG Trackfire RWS": "40mm 自动榴弹发射器/7.62mm 机枪 履带火力 遥控武器站",
    "Zanzottera 498i Two-Stroke Boxer Engine": "赞佐特拉 498i 二冲程式水平对置发动机",
    "Turbomachine TJ1000 Turbojet": "涡轮机械 TJ1000 涡喷发动机",
    "Bardehlass [M113] APC": "巴尔德拉斯 [M113] 装甲输送车",
    "Reccelite": "侦察之光 侦察吊舱",
    "RECCELITE": "侦察之光 侦察吊舱",
    "Patroller UAV SAR / MTI Radar Pod": "巡逻者 无人机合成孔径/动目标显示雷达吊舱",
    "Checkmate AESA": "将杀 有源相控阵",
    "Outdragon SIGINT": "外龙 信号情报",
    "Outdragon SIGINT, GBU-49/B Paveway II GPS/LGB [Mk82]":
        "外龙 信号情报，GBU-49/B 宝石路 II GPS/激光制导炸弹 [Mk82]",
    "Hammerhead Command, Control, Signal Processing, and Decision Module":
        "锤头 指挥、控制、信号处理与决策模块",
    "Superhawk COMINT": "超级鹰 通信情报",
    "SuperHawk [Mod TACJAM-A]": "超级鹰 [改进型 TACJAM-A]",
}


def outside_brackets(t: str) -> list[tuple[int, int]]:
    spans, pos = [], 0
    for m in BRACKETS.finditer(t):
        if m.start() > pos:
            spans.append((pos, m.start()))
        pos = m.end()
    if pos < len(t):
        spans.append((pos, len(t)))
    return spans


def rewrite_outside(text: str, fn) -> str:
    out, last = [], 0
    for start, end in outside_brackets(text):
        out.append(text[last:start])
        out.append(fn(text[start:end]))
        last = end
    out.append(text[last:])
    return "".join(out)


def load_current_values() -> dict[str, str]:
    """当前词典的 exact 值——工作清单里缓存的 cur 可能是上一轮快照，会过期。"""
    p = ROOT / "dict" / "CMOLocalizer.json"
    if not p.is_file():
        return {}
    data = json.loads(p.read_text(encoding="utf-8"))
    exact = data.get("exact") or {}
    return {k: v for k, v in exact.items() if isinstance(v, str) and HAN.search(v)}


def build_patterns(table: dict[str, str]) -> re.Pattern:
    """最长匹配优先的正则表；只在整词边界处替换。"""
    keys = sorted(table, key=len, reverse=True)
    alts = "|".join(re.escape(k) for k in keys)
    return re.compile(BOUND_L + "(" + alts + ")" + BOUND_R)


def type_rule(value: str) -> str:
    """`Type NNN` → `NNN型`，`China Type NNN` → `NNN式`。仅括号外。"""
    def sub(seg: str) -> str:
        if "中国" in seg or "China" in seg:
            seg = CHINA_TYPE.sub(lambda m: f"{m.group(1)}式", seg)
        return TYPE.sub(lambda m: f"{m.group(1)}型", seg)
    return rewrite_outside(value, sub)


def make_rewriter(table: dict[str, str]):
    pattern = build_patterns(table)

    def sub(seg: str) -> str:
        # 中国制式的 PLA 写法是「NNN式」（65式、81式），不是「NNN型」
        seg = type_rule(seg)
        # 只替换整词，且紧邻数字/连字符的型号连续体由边界断言排除在 pattern 之外
        out = pattern.sub(lambda m: table[m.group(1)], seg)
        return DUP_CJK.sub(r"\1", out)

    return sub


def residual(value: str) -> list[str]:
    """括号外仍残留的英文词（长度 >= 4 且非全大写）。"""
    return [w for w in LATIN_WORD.findall(rewrite_outside(value, lambda s: s))
            if not w.isupper()]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--outdir", default=str(BUILD / "residue_out"))
    ap.add_argument("--size", type=int, default=200)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    todo = json.loads((BUILD / "db_residue_todo.json").read_text(encoding="utf-8"))
    current = load_current_values()
    sub = make_rewriter(CURATED)

    changed, same, untouched = {}, [], 0
    type_only = 0
    for item in todo:
        en = item["en"]
        cur = current.get(en) or item["cur"]  # 优先用当前词典值，避免用过期快照
        if en in WHOLE:
            new = WHOLE[en]
        elif not cur:
            # 无现成译文时**只**运行 Type 规则：它是仓库已有的写法（`209-1100型`），
            # 不依赖任何名字约定。且只在括号外再无残留实词时才收（否则
            # `Type 73, 127mm HVAR Rockets` 会变成「73型, 127mm HVAR Rockets」半译串）。
            new = type_rule(en)
            if new == en or residual(new):
                untouched += 1
                continue
            type_only += 1
        else:
            new = rewrite_outside(cur, sub)
        if new == cur:
            same.append(en)
            continue
        changed[en] = new

    left = [(en, v, residual(v)) for en, v in changed.items() if residual(v)]
    print(f"待处理 {len(todo)} 条：改写 {len(changed)}（其中纯 Type 规则 {type_only}），"
          f"无变化 {len(same)}，无译文且无 Type 可改 {untouched}")
    print(f"改写后括号外仍留英文词 {len(left)} 条（纯代号/无稳定译名的专名，按约定保留）")
    for en, v, ws in left[:8]:
        print(f"  {ws[:3]} | {v[:70]}")
    print("\n=== 改写样例 ===")
    for en, v in list(changed.items())[:12]:
        print(f"  {en[:60]}\n    -> {v[:70]}")
    if args.dry_run:
        return 0

    outdir = Path(args.outdir)
    outdir.mkdir(parents=True, exist_ok=True)
    for old in outdir.glob("batch_*.json"):
        old.unlink()
    keys = sorted(changed)
    for i in range(0, len(keys), args.size):
        part = {k: changed[k] for k in keys[i:i + args.size]}
        idx = i // args.size + 1
        (outdir / f"batch_{idx:03d}.json").write_text(
            json.dumps(part, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n写入 {outdir}（{len(keys)} 条）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
