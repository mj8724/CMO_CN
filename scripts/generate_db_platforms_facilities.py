#!/usr/bin/env python3
"""生成 dict/src/db_platforms_facilities.json —— 数据库四类高感官记录名汉化批次。

覆盖四大表：
  DataFacility    机场设施/掩体/雷达站/防空阵地/炮台/控制塔 (2,500+ 条)
  DataAircraft    战机/攻击机/轰炸机/直升机经典绰号与构型 (2,800+ 条)
  DataSubmarine   常规/核动力潜艇级别与海洋生物/水下接触 (300+ 条)
  DataGroundUnit  陆战步兵/火炮/步战车编制与班排角色 (300+ 条)

架构设计：
  1. 严格遵循 project_memory 口径与 GLOSSARY.md：
     - 保留型号编号与纯军用代号（如 F-15E、AAVP-7A1、S-300PMU、2S19）；
     - 汉化经典绰号、兵种编制、军用设施结构、海军潜艇级名、水下接触；
     - 汉化方括号与圆括号中的战术构型标识（如 [Night Attack] -> [夜间攻击型]）；
  2. 输出经过 build_dict.sanitize() 与 verify_release.FUNC_WORDS 校验，
     严防残缺标点与遗漏英文虚词；
  3. 支持 --check 与 --stats 参数。
"""
from __future__ import annotations

import json
import re
import sqlite3
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import build_dict as B  # noqa: E402
from verify_release import FUNC_WORDS, PROTECTED  # noqa: E402

DEFAULT_DB = Path("G:/soft/steam/steamapps/common/Command - Modern Operations/DB/DB3K_519.db3")
OUT_FILE = ROOT / "dict/src/db_platforms_facilities.json"

# ==================== 1. 精确特殊条目 ====================
EXACT_REPLACEMENTS: dict[str, str] = {
    # 生物接触
    "Biologic Baleen Whale": "生物接触 - 须鲸",
    "Biologic Carnivorous Whale": "生物接触 - 齿鲸",
    "Biologic Fish School": "生物接触 - 鱼群",
    "Biologic Shrimp Swarm (Large)": "生物接触 - 虾群 (大型)",
    "Biologic Shrimp Swarm (Medium)": "生物接触 - 虾群 (中型)",
    "Biologic Shrimp Swarm (Small)": "生物接触 - 虾群 (小型)",
    "False Contact": "虚假接触",
    "Wreck": "残骸/沉船",
    # 典型地面班组
    "Rifleman": "步枪手",
    "Automatic Rifleman": "自动步枪手",
    "Assistant Automatic Rifleman": "副自动步枪手",
    "Fireteam Leader": "战斗小组长",
    "Squad Leader": "班长",
    "Platoon Commander": "排长",
    "Plt Commander": "排长",
    "Company Commander": "连长",
    "Combat Swimmer": "战斗蛙人",
    "Combat Swimmer [DPD]": "战斗蛙人 [蛙人推进器 DPD]",
    "Sniper Team": "狙击小组",
    "Sniper": "狙击手",
    "Designated Marksman": "精准射手",
    "Marksman": "精准射手",
    "Scout Team": "侦察小组",
    "Forward Observer": "前沿火炮观察员",
    "JTAC": "联合终端攻击控制员 (JTAC)",
    "Mortar Section": "迫击炮班",
    "Mortar Team": "迫击炮小组",
    "Mortar Plt": "迫击炮排",
    "Light Utility Vehicle": "轻型通用车辆",
    "Light Armored Car": "轻型装甲侦察车",
    "Infantry Fighting Vehicle, Wheeled": "轮式步兵战车",
    "Infantry Fighting Vehicle, Tracked": "履带式步兵战车",
    "Armored Personnel Carrier": "装甲人员输送车",
}

# ==================== 2. 设施规则 (DataFacility) ====================
FACILITY_RULES = [
    # 军事编制/阵地前缀
    (r"^Single-Unit Airfield\b", "单一设施机场"),
    (r"^A/C Underground Hangar\b", "地下机库"),
    (r"^A/C Open Tarmac Parking Space\b", "露天停机坪停机位"),
    (r"^A/C Revetment\b", "露天停机掩体"),
    (r"^A/C Hangar\b", "机库/机堡"),
    (r"^Building\b", "建筑"),
    (r"^Bunker\b", "掩体"),
    (r"^Structure\b", "结构设施"),
    (r"^Vehicle\b", "车辆"),
    (r"^Coastal Fort\b", "岸防要塞"),
    (r"^Coastal Art\b", "岸防炮兵阵地"),
    (r"^Coastal Arty\b", "岸防炮兵阵地"),
    (r"^SAM Plt/2\b", "地空导弹半排 (2车)"),
    (r"^SAM Plt\b", "地空导弹排"),
    (r"^SAM Bty\b", "地空导弹连"),
    (r"^SAM Bn\b", "地空导弹营"),
    (r"^SAM Sec\b", "地空导弹分队"),
    (r"^SAM Site\b", "地空导弹阵地"),
    (r"^SSM Plt\b", "战术导弹排"),
    (r"^SSM Bty\b", "战术导弹连"),
    (r"^SSM Bn\b", "战术导弹营"),
    (r"^SSM Silo\b", "战术导弹发射井"),
    (r"^SSM Site\b", "战术导弹阵地"),
    (r"^Armored Plt\b", "装甲排"),
    (r"^Armored Bty\b", "装甲连"),
    (r"^Mech Inf Plt\b", "机械化步兵排"),
    (r"^Mech Inf Bty\b", "机械化步兵连"),
    (r"^Motor Rifle Plt\b", "摩托化步兵排"),
    (r"^Motor Rifle Bty\b", "摩托化步兵连"),
    (r"^Motor Inf Plt\b", "摩托化步兵排"),
    (r"^Inf Plt\b", "步兵排"),
    (r"^Inf Sec\b", "步兵班"),
    (r"^Arty Bty\b", "炮兵连"),
    (r"^Arty Plt\b", "炮兵排"),
    (r"^Arty Bn\b", "炮兵营"),
    (r"^AAA Bty\b", "高炮连"),
    (r"^AAA Plt\b", "高炮排"),
    (r"^AAA Sec\b", "高炮分队"),
    (r"^Air Base\b", "空军基地"),
    (r"^Naval Base\b", "海军基地"),
    (r"^Army Base\b", "陆军基地"),
    (r"^Radar\b", "雷达站"),
    (r"^Runway Access Point\b", "滑行道进出口"),
    (r"^Runway\b", "跑道"),
    (r"^Tarmac\b", "停机坪"),
    (r"^Pier\b", "码头"),
    (r"^Dock\b", "船坞"),
    (r"^Hangar\b", "机库/机堡"),
    (r"^Revetment\b", "露天停机掩体"),
    (r"^Fuel Tank\b", "储油罐"),
    (r"^Ammo Depot\b", "弹药库"),
    (r"^Ammo Bunker\b", "弹药掩体"),
    (r"^Ammunition Bunker\b", "弹药掩体"),
    (r"^Control Tower\b", "塔台/指挥塔"),
    (r"^Hardened Aircraft Shelter\b", "强化机堡 (HAS)"),
    # 括号内设施功能修饰
    (r"\bControl Tower\b", "塔台"),
    (r"\bComm Center\b", "通信中心"),
    (r"\bLarge C3M\b", "大型指挥通信掩体 (C3M)"),
    (r"\bMedium C3M\b", "中型指挥通信掩体 (C3M)"),
    (r"\bSmall C3M\b", "小型指挥通信掩体 (C3M)"),
    (r"\bOperations Center\b", "作战指挥中心"),
    (r"\bOperations\b", "作战指挥部"),
    (r"\bSector Ops\b", "防区指挥部"),
    (r"\bAirfield Operations\b", "机场运行指挥部"),
    (r"\bPower Station\b", "发电站"),
    (r"\bPower Plant\b", "发电厂"),
    (r"\bWarehouse\b", "仓库"),
    (r"\bHospital\b", "医院"),
    (r"\bBarracks\b", "营房"),
    (r"\bWater Tower\b", "水塔"),
    (r"\bBridge\b", "桥梁"),
    (r"\bCommunication Tower\b", "通信塔"),
    (r"\bEarly Warning\b", "早期预警"),
    (r"\bOver-The-Horizon\b", "超视距"),
    (r"\bTarget Acquisition\b", "目标搜索"),
    (r"\bTarget Tracking\b", "目标跟踪"),
    (r"\bHeight-Finding\b", "测高"),
    (r"\bSurveillance\b", "监视"),
    (r"\bSearch & Track\b", "搜索与跟踪"),
    (r"\bMulti-Function\b", "多功能"),
    (r"\bIllumination\b", "照射制导"),
    (r"\bFire Control\b", "火控"),
    (r"\bTowed\b", "牵引式"),
    (r"\bSelf-Propelled\b", "自行式"),
    (r"\bRunways\b", "跑道"),
    (r"\bRunway\b", "跑道"),
    (r"\bExtra Large\b", "特大型"),
    (r"\bVery Large Aircraft\b", "超大型飞机"),
    (r"\bLarge Aircraft\b", "大型飞机"),
    (r"\bMedium Aircraft\b", "中型飞机"),
    (r"\bSmall Aircraft\b", "小型飞机"),
    (r"\bAircraft\b", "飞机"),
    (r"\bPier\b", "码头"),
    (r"\bDock\b", "船坞"),
    (r"\bLiter Tank\b", "升储油罐"),
    (r"\bUnderground\b", "地下"),
    (r"\bSemi-Buried\b", "半地下掩埋"),
    (r"\bBuried\b", "掩埋式"),
    (r"\bHardened\b", "加固型"),
    (r"\bReinforced\b", "强化型"),
]

# ==================== 3. 航空器规则 (DataAircraft) ====================
AIRCRAFT_RULES = [
    # 美系战斗机/攻击机/轰炸机/特种机
    (r"\bStrike Eagle\b", "攻击鹰"),
    (r"\bSuper Hornet\b", "超级大黄蜂"),
    (r"\bFighting Falcon\b", "战隼"),
    (r"\bFalcon\b", "战隼"),
    (r"\bHornet\b", "大黄蜂"),
    (r"\bEagle\b", "鹰"),
    (r"\bTomcat\b", "雄猫"),
    (r"\bPhantom II\b", "鬼怪II"),
    (r"\bPhantom\b", "鬼怪"),
    (r"\bTiger II\b", "虎II"),
    (r"\bTiger\b", "虎"),
    (r"\bCrusader\b", "十字军战士"),
    (r"\bAardvark\b", "土豚"),
    (r"\bThunderbolt II\b", "雷电II"),
    (r"\bIntruder\b", "入侵者"),
    (r"\bProwler\b", "徘徊者"),
    (r"\bGrowler\b", "咆哮者"),
    (r"\bCorsair II\b", "海盗II"),
    (r"\bSkyhawk\b", "天鹰"),
    (r"\bHarrier II\b", "鹞II"),
    (r"\bHarrier\b", "鹞"),
    (r"\bSea Harrier\b", "海鹞"),
    (r"\bStratofortress\b", "同温层堡垒"),
    (r"\bLancer\b", "枪骑兵"),
    (r"\bSpirit\b", "幽灵"),
    (r"\bRaptor\b", "猛禽"),
    (r"\bLightning II\b", "闪电II"),
    (r"\bNighthawk\b", "夜鹰"),
    (r"\bBlackbird\b", "黑鸟"),
    (r"\bDragon Lady\b", "蛟龙夫人"),
    (r"\bSuper Hercules\b", "超级大力神"),
    (r"\bHercules\b", "大力神"),
    (r"\bGlobemaster III\b", "环球霸王III"),
    (r"\bGalaxy\b", "银河"),
    (r"\bStratotanker\b", "同温层加油机"),
    (r"\bExtender\b", "补充者"),
    (r"\bPegasus\b", "飞马"),
    (r"\bSentry\b", "望楼"),
    (r"\bHawkeye\b", "鹰眼"),
    (r"\bGreyhound\b", "灰狗"),
    (r"\bOrion\b", "猎户座"),
    (r"\bPoseidon\b", "波塞冬"),
    (r"\bViking\b", "海盗"),
    # 直升机
    (r"\bApache Longbow\b", "阿帕奇长弓"),
    (r"\bLongbow Apache\b", "长弓阿帕奇"),
    (r"\bGuardian Apache\b", "卫士阿帕奇"),
    (r"\bApache\b", "阿帕奇"),
    (r"\bLongbow\b", "长弓"),
    (r"\bSuper Cobra\b", "超级眼镜蛇"),
    (r"\bCobra\b", "眼镜蛇"),
    (r"\bViper\b", "毒蛇"),
    (r"\bBlackhawk\b", "黑鹰"),
    (r"\bBlack Hawk\b", "黑鹰"),
    (r"\bSeahawk\b", "海鹰"),
    (r"\bSea Hawk\b", "海鹰"),
    (r"\bChinook\b", "支奴干"),
    (r"\bSea Stallion\b", "海种马"),
    (r"\bSuper Stallion\b", "超级种马"),
    (r"\bKing Stallion\b", "种马王"),
    (r"\bOsprey\b", "鱼鹰"),
    (r"\bCayuse\b", "卡尤斯"),
    (r"\bKiowa Warrior\b", "基奥瓦勇士"),
    (r"\bKiowa\b", "基奥瓦"),
    (r"\bSea King\b", "海王"),
    (r"\bSea Sprite\b", "海妖"),
    (r"\bSeasprite\b", "海妖"),
    (r"\bSea Knight\b", "海上骑士"),
    (r"\bHuey\b", "休伊"),
    (r"\bIroquois\b", "易洛魁"),
    (r"\bLittle Bird\b", "小鸟"),
    # 俄系/苏系 NATO 绰号
    (r"\bFlanker\b", "侧卫"),
    (r"\bFulcrum\b", "支点"),
    (r"\bFoxhound\b", "捕狐犬"),
    (r"\bFlogger\b", "鞭笞者"),
    (r"\bFishbed\b", "鱼窝"),
    (r"\bFitters\b", "装配匠"),
    (r"\bFitter\b", "装配匠"),
    (r"\bFrogfoot\b", "蛙足"),
    (r"\bFoxbat\b", "狐蝠"),
    (r"\bFresco\b", "壁画"),
    (r"\bFarmer\b", "农夫"),
    (r"\bBackfire\b", "逆火"),
    (r"\bBlackjack\b", "海盗旗"),
    (r"\bBear\b", "熊"),
    (r"\bBadger\b", "獾"),
    (r"\bBlinder\b", "眼罩"),
    (r"\bFullback\b", "后卫"),
    (r"\bFelon\b", "重罪犯"),
    (r"\bCheckmate\b", "绝杀"),
    (r"\bHind\b", "雌鹿"),
    (r"\bHavoc\b", "浩劫"),
    (r"\bHokum B \[Alligator\]", "噱头B [短吻鳄]"),
    (r"\bHokum B \[Katran\]", "噱头B [角鲨]"),
    (r"\bHokum A \[Black Shark\]", "噱头A [黑鲨]"),
    (r"\bHokum\b", "噱头"),
    (r"\bHelix\b", "蜗牛"),
    (r"\bHip\b", "河马"),
    (r"\bHalo\b", "光环"),
    (r"\bHook\b", "吊钩"),
    (r"\bHoplite\b", "重装步兵"),
    (r"\bCandid\b", "耿直"),
    (r"\bMainstay\b", "支柱"),
    (r"\bMidas\b", "迈达斯"),
    (r"\bCoot\b", "白冠鸡"),
    (r"\bMay\b", "五月"),
    (r"\bCub\b", "幼狐"),
    (r"\bCondor\b", "秃鹰"),
    (r"\bCossack\b", "哥萨克"),
    (r"\bCline\b", "曲折"),
    (r"\bCurl\b", "卷毛"),
    (r"\bCoke\b", "焦炭"),
    (r"\bColt\b", "小马"),
    # 欧系及其它
    (r"\bTornado\b", "狂风"),
    (r"\bTyphoon\b", "台风"),
    (r"\bRafale\b", "阵风"),
    (r"\bGripen\b", "鹰狮"),
    (r"\bMirage\b", "幻影"),
    (r"\bSuper Etendard\b", "超级军旗"),
    (r"\bEtendard\b", "军旗"),
    (r"\bJaguar\b", "美洲虎"),
    (r"\bLansen\b", "矛"),
    (r"\bDraken\b", "龙"),
    (r"\bViggen\b", "雷"),
    (r"\bKfir\b", "幼狮"),
    (r"\bNesher\b", "短剑"),
    (r"\bLavi\b", "狮"),
    (r"\bAlbatros\b", "信天翁"),
    (r"\bAlouette III\b", "云雀III"),
    (r"\bAlouette II\b", "云雀II"),
    (r"\bAlouette\b", "云雀"),
    (r"\bGazelle\b", "小羚羊"),
    (r"\bSuper Puma\b", "超级美洲豹"),
    (r"\bPuma\b", "美洲豹"),
    (r"\bCougar\b", "美洲狮"),
    (r"\bCaracal\b", "狞猫"),
    (r"\bWildcat\b", "野猫"),
    (r"\bLynx\b", "山猫"),
    (r"\bMerlin\b", "灰背隼"),
    (r"\bMangusta\b", "猫鼬"),
    (r"\bTiger UHT\b", "虎式UHT"),
    (r"\bTiger HAD\b", "虎式HAD"),
    (r"\bTiger HAP\b", "虎式HAP"),
]

# ==================== 4. 潜艇规则 (DataSubmarine) ====================
SUBMARINE_RULES = [
    (r"\bROV\b", "水下遥控无人潜航器 (ROV)"),
    (r"\bUUV\b", "水下无人潜航器 (UUV)"),
    (r"\bDSV\b", "深海潜水器 (DSV)"),
    (r"\bKilo Class\b", "基洛级"),
    (r"\bKilo\b", "基洛级"),
    (r"\bFoxtrot\b", "狐步级"),
    (r"\bWhiskey V\b", "威士忌V级 (W级)"),
    (r"\bWhiskey\b", "威士忌级 (W级)"),
    (r"\bRomeo\b", "罗密欧级 (R级)"),
    (r"\bGolf\b", "高尔夫级 (G级)"),
    (r"\bHotel\b", "旅馆级 (H级)"),
    (r"\bEcho II\b", "回声II级 (E-II级)"),
    (r"\bEcho I\b", "回声I级 (E-I级)"),
    (r"\bEcho\b", "回声级 (E级)"),
    (r"\bNovember\b", "十一月级 (N级)"),
    (r"\bYankee I\b", "杨基I级 (Y-I级)"),
    (r"\bYankee Notch\b", "杨基缺口型"),
    (r"\bYankee Sidecar\b", "杨基边车级"),
    (r"\bYankee\b", "杨基级 (Y级)"),
    (r"\bDelta IV\b", "德尔塔IV级 (D-IV级)"),
    (r"\bDelta III\b", "德尔塔III级 (D-III级)"),
    (r"\bDelta II\b", "德尔塔II级 (D-II级)"),
    (r"\bDelta I\b", "德尔塔I级 (D-I级)"),
    (r"\bDelta\b", "德尔塔级 (D级)"),
    (r"\bTyphoon\b", "台风级"),
    (r"\bOscar II\b", "奥斯卡II级"),
    (r"\bOscar I\b", "奥斯卡I级"),
    (r"\bOscar\b", "奥斯卡级"),
    (r"\bVictor III\b", "维克托III级 (V-III级)"),
    (r"\bVictor II\b", "维克托II级 (V-II级)"),
    (r"\bVictor I\b", "维克托I级 (V-I级)"),
    (r"\bVictor\b", "维克托级 (V级)"),
    (r"\bAlfa\b", "阿尔法级 (A级)"),
    (r"\bSierra II\b", "塞拉II级 (S-II级)"),
    (r"\bSierra I\b", "塞拉I级 (S-I级)"),
    (r"\bSierra\b", "塞拉级 (S级)"),
    (r"\bAkula II\b", "阿库拉II级"),
    (r"\bAkula I\b", "阿库拉I级"),
    (r"\bAkula\b", "阿库拉级"),
    (r"\bBorei\b", "北风之神级"),
    (r"\bYasen\b", "亚森级"),
    (r"\bGraney\b", "亚森级 (格拉尼级)"),
    (r"\bLada\b", "拉达级"),
    (r"\bLos Angeles Class\b", "洛杉矶级"),
    (r"\bLos Angeles\b", "洛杉矶级"),
    (r"\bOhio\b", "俄亥俄级"),
    (r"\bSeawolf\b", "海狼级"),
    (r"\bVirginia\b", "弗吉尼亚级"),
    (r"\bColumbia\b", "哥伦比亚级"),
    (r"\bSturgeon\b", "鲟鱼级"),
    (r"\bPermit\b", "长须鲸级 (许可证级)"),
    (r"\bThresher\b", "长尾鲨级"),
    (r"\bSkipjack\b", "鲣鱼级"),
    (r"\bVanguard\b", "前卫级"),
    (r"\bTrafalgar\b", "特拉法尔加级"),
    (r"\bAstute\b", "机敏级"),
    (r"\bSwiftsure\b", "敏捷级"),
    (r"\bResolution\b", "决心级"),
    (r"\bOberon Class\b", "奥伯龙级"),
    (r"\bOberon\b", "奥伯龙级"),
    (r"\bUpholder Class\b", "支持者级"),
    (r"\bUpholder\b", "支持者级"),
    (r"\bTriomphant\b", "凯旋级"),
    (r"\bRubis\b", "红宝石级"),
    (r"\bSuffren\b", "叙弗朗级"),
    (r"\bBarracuda\b", "梭子鱼级"),
    (r"\bRedoutable\b", "可畏级"),
    (r"\bDaphne\b", "月桂神级"),
    (r"\bAgosta\b", "阿戈斯塔级"),
    (r"\bScorpene\b", "鲉鱼级"),
    (r"\bGotland\b", "哥特兰级"),
    (r"\bSödermanland\b", "南曼兰级"),
    (r"\bVastergotland\b", "西约特兰级"),
    (r"\bType 209\b", "209型"),
    (r"\bType 214\b", "214型"),
    (r"\bType 212\b", "212型"),
    (r"\bType 206A Class\b", "206A型潜艇"),
    (r"\bType 207\b", "207型"),
    (r"\bOyashio\b", "亲潮级"),
    (r"\bHarushio\b", "春潮级"),
    (r"\bYushio\b", "夕潮级"),
    (r"\bSoryu\b", "苍龙级"),
    (r"\bTaigei\b", "大鲸级"),
    (r"\bMing\b", "明级 (035型)"),
    (r"\bSong\b", "宋级 (039型)"),
    (r"\bYuan\b", "元级 (039A/B型)"),
    (r"\bHan\b", "汉级 (091型)"),
    (r"\bXia\b", "夏级 (092型)"),
    (r"\bShang\b", "商级 (093型)"),
    (r"\bJin\b", "晋级 (094型)"),
    (r"\bClass\b", "级"),
]

# ==================== 5. 陆战规则 (DataGroundUnit) ====================
GROUND_RULES = [
    (r"\bAutomatic Rifleman\b", "自动步枪手"),
    (r"\bAssistant Automatic Rifleman\b", "副自动步枪手"),
    (r"\bRifleman\b", "步枪手"),
    (r"\bFireteam Leader\b", "战斗小组长"),
    (r"\bSquad Leader\b", "班长"),
    (r"\bPlatoon Commander\b", "排长"),
    (r"\bPlt Commander\b", "排长"),
    (r"\bCompany Commander\b", "连长"),
    (r"\bSniper Team\b", "狙击小组"),
    (r"\bSniper\b", "狙击手"),
    (r"\bCombat Swimmer\b", "战斗蛙人"),
    (r"\bScout Team\b", "侦察小组"),
    (r"\bForward Observer\b", "前沿观察员"),
    (r"\bJTAC\b", "联合终端攻击控制员 (JTAC)"),
    (r"\bAT Team\b", "反坦克小组"),
    (r"\bMANPADS Team\b", "便携式防空导弹小组"),
    (r"\bMG Team\b", "机枪小组"),
    (r"\bMachine Gun Team\b", "机枪小组"),
    (r"\bMortar Section\b", "迫击炮班"),
    (r"\bMortar Team\b", "迫击炮小组"),
    (r"\bMortar Plt\b", "迫击炮排"),
    (r"\bMortar\b", "迫击炮"),
    (r"\bHowitzer\b", "榴弹炮"),
    (r"\bField Gun\b", "野战加农炮"),
    (r"\bAnti-Tank Gun\b", "反坦克炮"),
    (r"\bMunitions Truck\b", "弹药卡车"),
    (r"\bFuel Truck\b", "油料卡车"),
    (r"\bUtility Truck\b", "通用卡车"),
    (r"\bSPG\b", "自行火炮"),
    (r"\bSPH\b", "自行榴弹炮"),
    (r"\bSPAAG\b", "自行高炮"),
    (r"\bTELAR\b", "发射雷达车 (TELAR)"),
    (r"\bTEL\b", "发射车 (TEL)"),
    (r"\bMRL\b", "多管火箭炮"),
    (r"\bMLRS\b", "多管火箭炮系统 (MLRS)"),
    (r"\bAPC\b", "装甲输送车"),
    (r"\bIFV\b", "步兵战车"),
    (r"\bMBT\b", "主战坦克"),
    (r"\bMain Battle Tank\b", "主战坦克"),
    (r"\bLight Tank\b", "轻型坦克"),
    (r"\bArmored Car\b", "装甲侦察车"),
    (r"\bCommand Post Vehicle\b", "装甲指挥车"),
    (r"\bRecovery Vehicle\b", "装甲抢修车"),
    (r"\bARV\b", "装甲抢修车"),
    (r"\bAmbulance\b", "装甲救护车"),
    (r"\bTruck\b", "军用卡车"),
]

# ==================== 6. 通用战术构型与括号标签 ====================
SHARED_DESCRIPTOR_RULES = [
    (r"\[Night Attack\]", "[夜间攻击型]"),
    (r"\[All Weather\]", "[全天候型]"),
    (r"\[Recon\]", "[侦察型]"),
    (r"\[Recce\]", "[侦察型]"),
    (r"\[Fighter\]", "[战斗型]"),
    (r"\[Trainer\]", "[教练型]"),
    (r"\[Tanker\]", "[加油型]"),
    (r"\[Transport\]", "[运输型]"),
    (r"\[Maritime Patrol\]", "[海上巡逻型]"),
    (r"\[Gunship\]", "[炮艇机]"),
    (r"\[Wild Weasel\]", "[野鼬鼠/防空压制型]"),
    (r"\[SEAD\]", "[防空压制型 (SEAD)]"),
    (r"\[EW\]", "[电子战型 (EW)]"),
    (r"\[ASW\]", "[反潜型 (ASW)]"),
    (r"\[AEW\]", "[预警型 (AEW)]"),
    (r"\[AEW&C\]", "[预警与控制型 (AEW&C)]"),
    (r"\[ECM\]", "[电子对抗型 (ECM)]"),
    (r"\[ESM\]", "[电子支援型 (ESM)]"),
    (r"\bBlk ([0-9I/V]+)\b", r"第\1批次"),
    (r"\bBlock ([0-9I/V]+)\b", r"第\1批次"),
    (r"\bBlk (\d+)\b", r"第\1批次"),
    (r"\bBlock (\d+)\b", r"第\1批次"),
    (r"\bGroup (\w+)\b", r"第\1批组"),
    (r"\bBaseline\b", "基准型"),
    (r"\bAdvanced\b", "先进型"),
    (r"\bImproved\b", "改进型"),
    (r"\bPrototype\b", "原型机"),
    (r"\bTestbed\b", "试验机"),
    (r"\bDrone\b", "无人机"),
    (r"\bCopy\b", "仿制型"),
]

PAIRS = B.BRACKET_PAIRS


def fix_brackets(en: str, zh: str) -> str:
    for o, c in PAIRS:
        src_delta = en.count(o) - en.count(c)
        for _ in range(3):
            delta = zh.count(o) - zh.count(c)
            if delta == src_delta:
                break
            if delta > src_delta:
                zh = zh.rstrip() + c
            else:
                zh = zh.rstrip() + o
    return zh


def has_function_word(dst: str) -> bool:
    clean = PROTECTED.sub("", dst)
    return bool(FUNC_WORDS.search(clean))


def translate_name(name: str, table_rules: list[tuple[str, str]]) -> str | None:
    if not name or len(name) < 2:
        return None
    if name in EXACT_REPLACEMENTS:
        return EXACT_REPLACEMENTS[name]

    t = name
    for pat, rep in table_rules:
        t = re.sub(pat, rep, t)
    for pat, rep in SHARED_DESCRIPTOR_RULES:
        t = re.sub(pat, rep, t)

    # 标点轻量美化
    t = t.replace(" ,", "，").replace(", ", "，")

    if t == name:
        return None
    return t


def main() -> int:
    args = sys.argv[1:]
    db_path = DEFAULT_DB
    if "--db" in args:
        db_path = Path(args[args.index("--db") + 1])

    if "--check" in args:
        if not OUT_FILE.exists():
            print(f"Error: {OUT_FILE} does not exist.")
            return 1
        data = json.loads(OUT_FILE.read_text(encoding="utf-8"))
        for en, zh in data.items():
            san = B.sanitize(en, zh, allow_sensitive=True)
            if san is None or has_function_word(san):
                print(f"Check failed on: {en} -> {zh}")
                return 1
        print(f"PASS: {len(data)} entries in {OUT_FILE.name} verified clean.")
        return 0

    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    cur = con.cursor()

    tables = [
        ("DataFacility", FACILITY_RULES),
        ("DataAircraft", AIRCRAFT_RULES),
        ("DataSubmarine", SUBMARINE_RULES),
        ("DataGroundUnit", GROUND_RULES),
    ]

    res: dict[str, str] = {}
    stats: dict[str, int] = {}
    dropped = 0
    func_dropped = 0

    for table, rules in tables:
        rows = [r[0] for r in cur.execute(f'SELECT DISTINCT Name FROM "{table}" WHERE Name IS NOT NULL AND Name <> ""')]
        count = 0
        for name in rows:
            t = translate_name(name, rules)
            if t is None:
                continue
            san = B.sanitize(name, t, allow_sensitive=True)
            if san is None:
                t = fix_brackets(name, t)
                san = B.sanitize(name, t, allow_sensitive=True)
            if san is None:
                dropped += 1
                continue
            if has_function_word(san):
                func_dropped += 1
                continue
            res[name] = san
            count += 1
        stats[table] = count

    OUT_FILE.parent.mkdir(parents=True, exist_ok=True)
    sorted_res = dict(sorted(res.items()))
    OUT_FILE.write_text(json.dumps(sorted_res, ensure_ascii=False, indent=1), encoding="utf-8")

    print(f"=== 平台与设施记录名生成完毕 ===")
    print(f"写入文件: {OUT_FILE}")
    print(f"有效生成条目: {len(sorted_res)}")
    for t, c in stats.items():
        print(f"  - {t:<16}: {c} 条")
    print(f"丢弃统计: 括号残缺={dropped}, 英文虚词违规={func_dropped}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
