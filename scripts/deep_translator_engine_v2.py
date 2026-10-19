# -*- coding: utf-8 -*-
import json
import os
import re

base_dir = r"C:\Users\admin\Desktop\ga-admin-portable-windows-amd64-v0.2.29\GenericAgent\temp\projects\汉化"

# Load existing engine data
import deep_translator_engine as base_eng

GLOSSARY = dict(base_eng.GLOSSARY)
WORD_DICT = dict(base_eng.WORD_DICT)
clean_exact = dict(base_eng.clean_exact)
existing_partial = dict(base_eng.existing_partial)
existing_offsets = dict(base_eng.existing_offsets)
tiered = base_eng.tiered

# 1. Expand GLOSSARY with high-frequency tactical phrases from P1/P2/P3
EXTRA_GLOSSARY = {
    "Deletion may break relationships": "删除操作可能会破坏关联关系",
    "Deletion breaks a relationship": "删除操作破坏了关联关系",
    "Contact the scenario designer": "请联系想定设计者",
    "Creating a standard zone": "正在创建标准区域",
    "Would you like to Update the Waypoint #": "是否要更新航路点 #",
    "/ minute fuel burn rate": "/分钟 燃油消耗率",
    "nm to Bingo fuel": "海里 到达返航油量(Bingo)",
    "nm to Joker fuel": "海里 到达警戒油量(Joker)",
    "Unit is at full stop": "单位已完全停止",
    "No assigned unit can carry": "没有已指派单位能够运载",
    "Cargo Exchange {0} <=> {1}": "货物交换 {0} <=> {1}",
    "Group by Unit Type": "按单位类型分组",
    "[Selected Unit]": "[当前选中单位]",
    "[Hosting Unit]": "[宿主搭载单位]",
    "[Transfer to Unit]": "[转移至目标单位]",
    "[Pickup Nearby / Hosted Units]": "[装载附近 / 已搭载单位]",
    "is now the lead unit for group:": "现已成为该编队的领队/旗舰:",
    "Assign This as Home Base": "将此设施设为母基地",
    "Targeting Vectors": "目标瞄准引导矢量",
    "Error when trying to check if file is signed:": "尝试检查文件数字签名时出错:",
    "Tacview has disconnected from Command.": "Tacview 已断开与 Command 的连接。",
    "This feature is not currently available in Real Time Multiplayer.": "此功能当前在实时多人联机模式下不可用。",
    "Initialization started.": "初始化已开始。",
    "Initialization completed.": "初始化已完成。",
    "Successfully set database hash value.": "已成功设置数据库哈希校验值。",
    "Starting checks for known interfering processes.": "正在检查已知的冲突干扰进程。",
    "cannot be instructed to change course (": "无法受令改变航向 (",
    "cannot participate in attack (": "无法参与攻击行动 (",
    "mine clearing mission '": "扫雷作战任务 '",
    "Unit Remains In Area trigger '": "单位保持在区域内触发器 '",
    "Absolute Control is required to use this feature during a Real Time Multi-Player session. Use the multi-player panel to request Absolute Control of the scenario.": "在实时多人联机回中必须拥有“完全控制权”才能使用此功能。请使用多人联机面板申请对当前想定的完全控制权。",
    "These processes are known to cause instability/performance/UI glitches when running alongside Command! It is recommended to close them (either through their own UI or through Task Manager) before continuing.": "已知这些后台进程在与 Command 同时运行时会导致不稳定、性能下降或界面显示异常！建议在继续之前（通过其自身界面或任务管理器）关闭它们。",
    "WARNING ! A mission depends on the mission you are about to delete, this will break the relationship and could prevent": "警告！另一个任务依赖于您即将删除的任务，删除操作将破坏依赖关系并可能导致任务",
    "to ever start! Do you want to proceed ?": "永远无法启动！您确定要继续吗？",
}
GLOSSARY.update(EXTRA_GLOSSARY)

# 2. Expand WORD_DICT with comprehensive vocabulary (nouns, verbs, adjectives, grammar connectors)
EXTRA_WORDS = {
    # 语法功能词与连接词（用于平滑句子合成）
    "the": "", "a": "一个", "an": "一个",
    "to": "至/到", "of": "的", "in": "在...内", "for": "用于", "on": "在...上",
    "at": "于", "with": "带有/使用", "from": "来自/从", "by": "通过/按", "as": "作为",
    "into": "进入", "is": "是/为", "are": "是/为", "was": "曾是", "were": "曾是",
    "be": "为", "been": "已被", "has": "拥有/已", "have": "拥有/已", "had": "曾有",
    "do": "执行", "does": "执行", "did": "已执行", "not": "不/未", "no": "无",
    "or": "或", "and": "与", "but": "但", "if": "如果", "when": "当...时",
    "while": "当...期间", "where": "位置", "than": "比", "only": "仅", "also": "也",
    "already": "已经", "now": "现在", "here": "此处", "there": "该处",
    "this": "此", "that": "该", "these": "这些", "those": "那些",
    "it": "它", "you": "您", "your": "您的", "we": "我们", "our": "本方",
    "cannot": "无法", "can't": "无法", "don't": "不要", "doesn't": "未", "didn't": "未",
    "won't": "不会", "isn't": "不是", "aren't": "不是", "wasn't": "未曾",
    "please": "请", "want": "想要", "proceed": "继续", "continue": "继续",
    "try": "尝试", "trying": "正在尝试", "again": "再次", "later": "稍后",
    "before": "在...之前", "after": "在...之后", "during": "在...期间",
    "between": "在...之间", "among": "在...之中", "against": "对抗/针对",
    "without": "无/不带", "within": "在...之内", "through": "通过",
    "over": "超过/在...上方", "under": "低于/在...下方", "above": "高于", "below": "低于",
    "up": "向上", "down": "向下", "out": "外出/耗尽", "off": "关闭/脱离",

    # 补充高频专业词汇
    "desired": "期望", "fixed": "固定", "usage": "使用方式", "burn": "消耗/燃烧",
    "rate": "速率", "mothership": "母舰/搭载母机", "motherships": "母舰/母机",
    "crew": "乘员/机组", "bar": "状态条/栏", "inventory": "库存/清单",
    "exchange": "交换", "ops": "作业", "operations": "作战/作业",
    "hosting": "搭载中", "hosted": "已搭载", "transfer": "转移/调拨",
    "pickup": "装载/拾取", "nearby": "附近", "ammunition": "弹药", "ammo": "弹药",
    "vector": "矢量", "vectors": "矢量", "targeting": "目标指示/瞄准",
    "deletion": "删除", "break": "破坏/中断", "breaks": "破坏",
    "relationship": "关联关系", "relationships": "关联关系",
    "designer": "设计者", "instructed": "受令", "participate": "参与",
    "clearing": "扫雷/清除", "remains": "保持/停留", "signed": "数字签名",
    "older": "较旧", "newer": "较新", "download": "下载", "install": "安装",
    "least": "至少", "assistance": "帮助/支持", "software": "软件",
    "feature": "功能", "currently": "当前", "available": "可用",
    "unavailable": "不可用", "multiplayer": "多人联机", "multi-player": "多人联机",
    "session": "会话", "request": "请求/申请", "absolute": "完全/绝对",
    "initialization": "初始化", "fetching": "正在获取", "recent": "最新",
    "hash": "哈希值", "checks": "检查", "known": "已知", "interfering": "冲突干扰",
    "processes": "进程", "process": "进程", "cause": "导致",
    "instability": "不稳定", "glitches": "异常/故障", "running": "运行中",
    "alongside": "同时/并列", "recommended": "建议", "manager": "管理器",
    "depends": "依赖于", "depend": "依赖", "prevent": "阻止/妨碍",
    "ever": "始终/曾经", "vis": "可见性", "speeds": "速度配置",
    "weapons": "武器", "units": "单位", "objects": "对象",
    "elevation": "高程/仰角", "azimuth": "方位角", "horizon": "地平线/视距",
    "los": "通视线(LOS)", "blind": "盲区", "spot": "点/区", "spots": "盲区",
    "footprint": "覆盖区", "envelope": "包线", "kinematic": "运动学",
    "no-escape": "不可逃逸区(NEZ)", "dlz": "动态发射区(DLZ)",
    "bingo": "返航油量(Bingo)", "joker": "警戒油量(Joker)",
    "winchester": "弹药耗尽(Winchester)", "shotgun": "弹药达阈值(Shotgun)",
    "standoff": "防区外", "stand-off": "防区外", "bvr": "超视距(BVR)", "wvr": "视距内(WVR)",
    "cap": "战斗巡逻(CAP)", "cas": "近距空中支援(CAS)", "sead": "防空压制(SEAD)",
    "dead": "防空摧毁(DEAD)", "asw": "反潜战(ASW)", "asuw": "反水面战(ASuW)",
    "aaw": "防空战(AAW)", "aew": "空中预警(AEW)", "aar": "空中加油(AAR)",
    "sar": "搜救/合成孔径雷达", "isrt": "情报监视侦察(ISR)", "isr": "情报监视侦察",
    "c2": "指挥控制(C2)", "c4isr": "指挥控制通信情报(C4ISR)",
    "emp": "电磁脉冲(EMP)", "abm": "反导(ABM)", "bmd": "弹道导弹防御(BMD)",
    "icbm": "洲际弹道导弹", "slbm": "潜射弹道导弹", "srbm": "近程弹道导弹",
    "mrbm": "中程弹道导弹", "irbm": "中远程弹道导弹", "hgv": "高超音速滑翔飞行器",
    "sam": "地空/舰空导弹(SAM)", "ssm": "反舰/地地导弹(SSM)", "aam": "空空导弹(AAM)",
    "agm": "空地导弹(AGM)", "ashm": "反舰导弹(AShM)", "lacm": "对陆巡航导弹(LACM)",
    "arm": "反辐射导弹(ARM)", "atgm": "反坦克导弹(ATGM)", "manpads": "便携式防空导弹",
    "ciws": "近防系统(CIWS)", "aaa": "高射炮(AAA)", "torp": "鱼雷",
    "sonobuoys": "声呐浮标", "dipping": "吊放式", "towed": "拖曳式", "hull": "舰体/艇体",
    "bow": "舰艏", "stern": "舰艉", "port": "左舷", "starboard": "右舷",
    "keel": "龙骨", "deck": "甲板", "superstructure": "上层建筑",
    "bridge": "舰桥", "cic": "作战信息中心(CIC)", "mast": "桅杆",
    "periscope": "潜望镜", "snorkel": "通气管", "sail": "潜艇围壳/指挥台",
    "ballast": "压载水舱", "propeller": "螺旋桨", "pumpjet": "泵喷推进器",
    "fuselage": "机身", "wing": "机翼", "wings": "机翼", "cockpit": "座舱",
    "canopy": "座舱盖", "radome": "雷达罩", "pylon": "挂架", "hardpoint": "外挂点",
    "bay": "弹舱/舱室", "rotary": "旋转发射架", "launcher": "发射器/发射架",
    "vls": "垂直发射系统(VLS)", "tube": "发射管/鱼雷管", "tubes": "发射管",
    "rail": "滑轨发射架", "turret": "炮塔", "caliber": "口径", "barrel": "炮管",
    "reload": "重新装填", "reloading": "正在装填", "unload": "卸载", "unloading": "正在卸载",
    "ready": "就绪/战备", "readying": "战备准备中", "maintenance": "维护检修",
    "unavailable": "不可用", "reserve": "预备/储备", "transit": "航渡/转场",
    "ferry": "转场调机", "orbit": "盘旋轨道/太空轨道", "holding": "等待/盘旋",
    "ingress": "进入突防", "egress": "撤离退出", "marshalling": "编队集结",
    "split": "拆分/分离", "join": "合并/汇合", "merge": "汇合/格斗交汇",
    "detached": "已独立分离", "attached": "已编入", "escorting": "正在护航",
    "assigned": "已指派", "unassigned": "未指派", "plotted": "已规划/预定",
    "opportunity": "临机目标", "investigate": "前出查证", "prosecute": "攻击处置",
    "classify": "分类识别", "classified": "已分类", "unclassified": "未分类",
    "ambiguity": "模糊区", "uncertainty": "不确定区(AOU)", "datum": "潜艇概位点",
    "fix": "精确定位点", "bearing-only": "纯方位攻击(BOL)", "bol": "纯方位发射(BOL)",
    "loal": "发射后锁定(LOAL)", "lobl": "发射前锁定(LOBL)", "mid-course": "中段制导",
    "terminal": "末段制导", "illumination": "雷达照射", "datalink": "双向数据链",
    "wire": "线导导线", "wake": "尾流", "acoustic": "声学", "magnetic": "磁感",
    "pressure": "水压", "influence": "非触发/感应引信", "contact": "触发/接触",
    "proximity": "近炸引信", "airburst": "空爆", "delayed": "延时引信",
    "penetrator": "钻地战斗部", "fragmentation": "破片杀伤", "he": "高爆(HE)",
    "heat": "破甲(HEAT)", "ap": "穿甲(AP)", "submunitions": "子母弹药",
    "cluster": "集束弹药", "thermobaric": "温压战斗部", "nuclear": "核战斗部",
    "yield": "当量", "kiloton": "千吨当量(kt)", "megaton": "百万吨当量(Mt)",
}
WORD_DICT.update(EXTRA_WORDS)

def split_camel_case(s):
    # Split CamelCase like "DesiredSpeed" -> ["Desired", "Speed"]
    if not re.match(r'^[A-Z][a-zA-Z0-9]+$', s):
        return None
    parts = re.findall(r'[A-Z]+(?=[A-Z][a-z]|\d|\W|$)|[A-Z]?[a-z]+|\d+', s)
    if len(parts) >= 2:
        return parts
    return None

def deep_translate(text):
    if not text or not isinstance(text, str):
        return None
    raw = text.strip()
    if not raw or len(raw) <= 1:
        return None
    # Skip Tacview Lua code or Registry keys
    if raw.startswith("Tacview.") or raw.startswith("SOFTWARE\\") or ":// " in raw:
        return None

    # 1. Exact & Glossary match
    if raw in GLOSSARY:
        return GLOSSARY[raw]
    if raw in clean_exact:
        return clean_exact[raw]
    for k, v in GLOSSARY.items():
        if k.lower() == raw.lower():
            return v

    # 2. Outer bracket wrapping like "[Selected Unit]" or "(Calculating LOS"
    if (raw.startswith("[") and raw.endswith("]")) or (raw.startswith("(") and raw.endswith(")")):
        inner = raw[1:-1].strip()
        inner_t = deep_translate(inner)
        if inner_t:
            return f"{raw[0]}{inner_t}{raw[-1]}"

    # 3. Unclosed leading or trailing bracket like "(Calculating LOS" or "cannot participate in attack ("
    if raw.startswith("(") and ")" not in raw:
        inner_t = deep_translate(raw[1:].strip())
        if inner_t:
            return f"({inner_t}"
    if raw.endswith("(") and "(" not in raw[:-1]:
        inner_t = deep_translate(raw[:-1].strip())
        if inner_t:
            return f"{inner_t} ("

    # 4. Colon separated
    if ":" in raw:
        parts = raw.split(":", 1)
        p0 = parts[0].strip()
        p1 = parts[1].strip()
        t0 = deep_translate(p0)
        if t0:
            if not p1:
                return f"{t0}:"
            t1 = deep_translate(p1)
            return f"{t0}: {t1 if t1 else p1}"

    # 5. CamelCase UI Labels / Headers (e.g. DesiredSpeed, RadarActive, AssignedUnits)
    camel_parts = split_camel_case(raw)
    if camel_parts:
        trans_parts = []
        hit = 0
        for cp in camel_parts:
            cpl = cp.lower()
            if cpl in WORD_DICT and WORD_DICT[cpl]:
                trans_parts.append(WORD_DICT[cpl].split("/")[0])
                hit += 1
            else:
                trans_parts.append(cp)
        if hit == len(camel_parts):
            return "".join(trans_parts)

    # 6. Main + Bracket like "Disengage (Drop all targets)"
    m = re.match(r"^([^\(\)\[\]]+)[\s]*([\(\[][^\(\)\[\]]+[\)\]])$", raw)
    if m:
        main_p = m.group(1).strip()
        sub_p = m.group(2).strip()
        t_main = deep_translate(main_p)
        t_sub = deep_translate(sub_p[1:-1].strip())
        if t_main and t_sub:
            return f"{t_main} {sub_p[0]}{t_sub}{sub_p[-1]}"
        elif t_main:
            return f"{t_main} {sub_p}"

    # 7. Sentence & Phrase Compositional Translation
    # Tokenize keeping punctuation and placeholders like {0}
    tokens = re.findall(r'\{\d+\}|[A-Za-z0-9\-\']+|\s+|[^\w\s\{\}]', raw)
    word_tokens = [t for t in tokens if re.match(r'^[A-Za-z]+$', t)]
    if not word_tokens:
        return None

    hit_words = 0
    content_hit = 0
    out_tokens = []
    GRAMMAR_SET = {"the", "a", "an", "to", "of", "in", "for", "on", "at", "with", "from", "by", "as", "is", "are", "was", "were", "be", "been", "has", "have", "had", "and", "or", "but"}

    for tok in tokens:
        if re.match(r'^[A-Za-z\-\']+$', tok):
            tl = tok.lower()
            if tl in WORD_DICT:
                w_trans = WORD_DICT[tl]
                if w_trans:
                    # Pick concise primary meaning for multi-word sentences
                    if len(word_tokens) >= 3 and "/" in w_trans:
                        w_trans = w_trans.split("/")[0]
                    out_tokens.append(w_trans)
                hit_words += 1
                if tl not in GRAMMAR_SET:
                    content_hit += 1
            elif tl.endswith("s") and tl[:-1] in WORD_DICT:
                w_trans = WORD_DICT[tl[:-1]].split("/")[0]
                out_tokens.append(w_trans)
                hit_words += 1
                content_hit += 1
            elif tl.endswith("ed") and tl[:-2] in WORD_DICT:
                w_trans = "已" + WORD_DICT[tl[:-2]].split("/")[0]
                out_tokens.append(w_trans)
                hit_words += 1
                content_hit += 1
            elif tl.endswith("ing") and tl[:-3] in WORD_DICT:
                w_trans = "正在" + WORD_DICT[tl[:-3]].split("/")[0]
                out_tokens.append(w_trans)
                hit_words += 1
                content_hit += 1
            else:
                out_tokens.append(tok)
        else:
            out_tokens.append(tok)

    ratio = hit_words / len(word_tokens)
    if content_hit >= 1 and (ratio >= 0.75 or (len(word_tokens) <= 3 and ratio >= 0.65)):
        res = "".join(out_tokens)
        # Clean up spaces between Chinese characters
        res = re.sub(r'(?<=[\u4e00-\u9fff])\s+(?=[\u4e00-\u9fff])', '', res)
        res = re.sub(r' +', ' ', res).strip()
        if any('\u4e00' <= ch <= '\u9fff' for ch in res):
            return res

    return None

# Run across all tiered items
final_exact = dict(clean_exact)
new_cnt = 0

for cat in ["p1", "p2", "p3"]:
    for text in tiered[cat]:
        if text in final_exact:
            continue
        t = deep_translate(text)
        if t and t != text and any('\u4e00' <= ch <= '\u9fff' for ch in t):
            final_exact[text] = t
            new_cnt += 1

print(f"V2 New translations synthesized: {new_cnt}")
print(f"V2 Final exact dictionary size: {len(final_exact)}")

# Update partial dictionary
final_partial = dict(existing_partial)
for k, v in GLOSSARY.items():
    if len(k) >= 4 and k not in final_partial:
        final_partial[k] = v

# Save updated CMOLocalizer.json
out_loc = {
    "version": "2.0.0",
    "updated": "2026-10-10",
    "offsets": existing_offsets,
    "exact": final_exact,
    "partial": final_partial,
    "words": {k: v.split("/")[0] for k, v in WORD_DICT.items() if v}
}

out_path = os.path.join(base_dir, "CMOLocalizer.json")
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(out_loc, f, ensure_ascii=False, indent=2)

print(f"Saved CMOLocalizer.json (Size: {os.path.getsize(out_path)/1024:.2f} KB)")
