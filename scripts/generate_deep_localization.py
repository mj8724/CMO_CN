# -*- coding: utf-8 -*-
import json
import os
import re

base_dir = r"C:\Users\admin\Desktop\ga-admin-portable-windows-amd64-v0.2.29\GenericAgent\temp\projects\汉化"

# Load tiered untranslated candidates
with open(os.path.join(base_dir, "tiered_untranslated.json"), "r", encoding="utf-8") as f:
    tiered = json.load(f)

# Load existing exact and partial from CMOLocalizer.json
with open(os.path.join(base_dir, "CMOLocalizer.json"), "r", encoding="utf-8") as f:
    current_loc = json.load(f)

existing_exact = current_loc.get("exact", {})
existing_partial = current_loc.get("partial", {})
existing_offsets = current_loc.get("offsets", {})

# Keep only valid translated exact items
clean_exact = {}
for k, v in existing_exact.items():
    if any('\u4e00' <= ch <= '\u9fff' for ch in str(v)):
        clean_exact[k] = v

print(f"Base verified exact count: {len(clean_exact)}")

# Master Term Base
TERMS = {
    # 顶层系统与菜单
    "File": "文件",
    "Edit": "编辑",
    "View": "视图",
    "Game": "推演",
    "Sensors": "传感器",
    "Missions + Ref. Points": "任务与参考点",
    "Missions + Ref.Points": "任务与参考点",
    "Missions": "任务",
    "Mission": "任务",
    "Doctrine": "作战条令",
    "Weapons": "武器",
    "UnitOrders": "单位指令",
    "Unit Orders": "单位指令",
    "Editor": "剧本编辑器",
    "Help": "帮助",
    "Scenario": "剧本",
    "Scenarios": "剧本库",
    "Database": "数据库",
    "Settings": "设置",
    "Options": "选项",
    "Tools": "工具",
    "Window": "窗口",
    "Windows": "窗口",

    # 军事行动与战术
    "Rules of Engagement (RoE)": "交战规则 (RoE)",
    "Rules of Engagement": "交战规则",
    "Emission Control (EMCON)": "电磁辐射管制 (EMCON)",
    "Emission Control": "电磁辐射管制",
    "Weapon Release Authority (WRA)": "武器投射权限条令 (WRA)",
    "Weapon Release Authority": "武器投射权限条令",
    "Doctrine / EMCON / WRA": "条令 / 辐射控制 / 武器使用授权",
    "Aggregate Ground Unit (AGU)": "地面聚合部队 (AGU)",
    "Land Warfare": "地面作战",
    "Anti-Air Warfare (AAW)": "防空作战 (AAW)",
    "Anti-Surface Warfare (ASuW)": "反水面作战 (ASuW)",
    "Anti-Submarine Warfare (ASW)": "反潜作战 (ASW)",
    "Mining Operations": "布雷作业",
    "Mine Countermeasures (MCM)": "反水雷/扫雷作业 (MCM)",
    "Mine Countermeasures": "反水雷作业",
    "Search and Rescue (SAR)": "搜救行动 (SAR)",
    "Combat Air Patrol (CAP)": "空中战斗巡逻 (CAP)",
    "Combat Air Patrol": "空中战斗巡逻",
    "Airborne Early Warning (AEW)": "空中预警 (AEW)",
    "Airborne Early Warning": "空中预警",
    "Suppression of Enemy Air Defenses (SEAD)": "防空压制 (SEAD)",
    "Close Air Support (CAS)": "近距离空中支援 (CAS)",
    "Air Interdiction": "空中阻绝",
    "Surface Warfare": "水面作战",
    "Subsurface Warfare": "水下作战",
    "Flight Operations": "飞行作业",
    "Air Operations": "空中作业",
    "Boat Operations": "艇载作业",
    "Cargo Operations": "货物运载作业",
    "Docking Operations": "靠泊作业",
    "Damage Control": "损管与修复状态",
    "Losses & Expenditures": "战损与弹药消耗统计",
    "Losses and Expenditures": "战损与弹药消耗统计",
    "Order of Battle": "战斗序列 (ORBAT)",
    "Formation Editor": "编队编辑器",
    "Mission Editor": "任务编辑器",
    "Event Editor": "事件编辑器",
    "Database Viewer": "数据库查看器",
    "Special Actions": "特殊行动",
    "Time Compression": "时间压缩",
    "God's Eye View": "上帝视界/全知视角",
    "Blind Spots": "雷达盲区/遮挡区",
    "Line-of-sight Tool": "视距/通视分析工具",
    "Line-of-Sight": "视距/通视线",
    "Line of Sight": "视距",

    # 单位与平台分类
    "Aircraft": "战机/航空器",
    "Ship": "水面舰艇",
    "Submarine": "潜艇",
    "Facility": "地面/固定设施",
    "Satellite": "人造卫星",
    "Group": "编组/编队",
    "Contact": "探测接触/目标",
    "Waypoint": "航路点",
    "Reference Point": "参考点",
    "Zone": "区域",
    "Base": "基地",
    "Air Base": "空军基地",
    "Naval Base": "海军基地",
    "Runway": "跑道",
    "Hangar": "机库",
    "Magazine": "弹药库",
    "Mount": "武器挂架/发射架/炮座",
    "Sensor": "传感器",
    "Director": "火控火指仪",
    "Loadout": "挂载配置",
    "Fuel": "燃油",
    "Propulsion": "动力推进系统",
    "Armor": "装甲防护",
    "Crew": "人员编制",
    "Cargo": "货物/装载",
    "Comms": "通信系统",

    # 传感器类型
    "Radar": "雷达",
    "Sonar": "声呐",
    "Active Sonar": "主动声呐",
    "Passive Sonar": "被动声呐",
    "Towed Array": "拖曳线列阵声呐",
    "Dipping Sonar": "吊放声呐",
    "Sonobuoy": "声呐浮标",
    "ESM": "电子支援措施 (ESM)",
    "ECM": "电子对抗干扰 (ECM)",
    "Laser": "激光测距/照射器",
    "Infrared": "红外搜索跟踪 (IRST)",
    "Visual": "目视侦察",
    "Optical": "光学侦察",
    "MAD": "磁异常探测仪 (MAD)",

    # 武器类型
    "Guided Weapon": "制导武器",
    "Unguided Weapon": "非制导武器",
    "Air-to-Air Missile (AAM)": "空空导弹 (AAM)",
    "Air-to-Surface Missile (ASM)": "空面导弹 (ASM)",
    "Surface-to-Air Missile (SAM)": "舰空/地空导弹 (SAM)",
    "Surface-to-Surface Missile (SSM)": "反舰/地地导弹 (SSM)",
    "Anti-Submarine Rocket (ASROC)": "反潜火箭助飞鱼雷",
    "Torpedo": "鱼雷",
    "Depth Charge": "深水炸弹",
    "Mine": "水雷/地雷",
    "Gun": "舰炮/航炮/火炮",
    "Artillery": "火炮系统",
    "Rocket": "无控火箭",
    "Bomb": "航弹",
    "Laser Guided Bomb": "激光制导炸弹",
    "GPS Guided Bomb": "卫星制导炸弹",
    "Decoy": "诱饵",
    "Flare": "红外干扰弹",
    "Chaff": "箔条干扰弹",
    "Drop Tank": "副油箱",
    "Pod": "吊舱",

    # 运动学与物理参数
    "Altitude": "高度",
    "Speed": "航速/速度",
    "Heading": "航向",
    "Course": "航向/航迹向",
    "Bearing": "方位角",
    "Range": "距离/射程",
    "Distance": "距离",
    "Latitude": "纬度",
    "Longitude": "经度",
    "Depth": "深度",
    "Coordinates": "地理坐标",
    "Position": "阵位/位置",
    "Time": "时间",
    "Estimated Time of Arrival": "预计到达时间 (ETA)",
    "Time to Target": "命中/飞抵剩余时间 (TTT)",
    "Time to Impact": "弹着剩余时间",
    "Turn Rate": "转弯速率",
    "Climb Rate": "爬升率",
    "Descent Rate": "下降率",
    "Acceleration": "加速度",
    "Fuel Consumption": "燃油消耗率",
    "Burn Rate": "燃油消耗率",
    "Burn Time": "工作/燃烧时间",
    "Endurance": "滞空/续航时间",
    "Radius": "作战半径",

    # 常见短语与操作动作
    "Add Mission": "添加任务",
    "Edit Mission": "编辑任务",
    "Delete Mission": "删除任务",
    "Clone Mission": "克隆/复制任务",
    "Activate Mission": "激活任务",
    "Deactivate Mission": "停用任务",
    "Assign Units": "指派单位",
    "Unassign Units": "取消指派单位",
    "Select All": "全选",
    "Select None": "全不选",
    "Invert Selection": "反选",
    "Clear Selection": "清除选中",
    "Refresh List": "刷新列表",
    "Close Window": "关闭窗口",
    "Apply Settings": "应用设置",
    "Reset to Default": "恢复默认值",
    "Export to File": "导出到文件",
    "Import from File": "从文件导入",
    "Save Changes": "保存更改",
    "Discard Changes": "放弃更改",
    "Confirm Action": "确认操作",
    "Cancel Action": "取消操作",
    "OK": "确定",
    "Cancel": "取消",
    "Apply": "应用",
    "Close": "关闭",
    "Save": "保存",
    "Load": "加载",
    "Open": "打开",
    "New": "新建",
    "Delete": "删除",
    "Remove": "移除",
    "Add": "添加",
    "Insert": "插入",
    "Copy": "复制",
    "Paste": "粘贴",
    "Cut": "剪切",
    "Rename": "重命名",
    "Search": "搜索",
    "Filter": "筛选",
    "Clear": "清除",
    "Reset": "重置",
    "Refresh": "刷新",
    "Update": "更新",
    "Properties": "属性",
    "Details": "详情",
    "Status": "状态",
    "General": "常规",
    "Advanced": "高级",
    "Summary": "概要",
    "Description": "说明描述",
    "Notes": "战术附注",
    "Name": "名称",
    "Type": "类型",
    "Class": "级别/型号",
    "Category": "分类",
    "Group Name": "编组名称",
    "Side Name": "推演方名称",
    "Unit Name": "单位名称",
    "Target Name": "目标名称",
    "Weapon Name": "武器名称",
    "Sensor Name": "传感器名称",
    "Mount Name": "发射挂架名称",
    "Magazine Name": "弹药库名称",
}

# Add vocabulary mapping
VOCAB = {
    "units": "单位", "unit": "单位",
    "targets": "目标", "target": "目标",
    "weapons": "武器", "weapon": "武器",
    "sensors": "传感器", "sensor": "传感器",
    "aircraft": "战机", "plane": "战机",
    "ships": "舰艇", "ship": "舰艇",
    "submarines": "潜艇", "submarine": "潜艇",
    "facilities": "设施", "facility": "设施",
    "magazines": "弹药库", "magazine": "弹药库",
    "mounts": "挂架/炮座", "mount": "挂架/炮座",
    "missions": "任务", "mission": "任务",
    "contacts": "接触目标", "contact": "接触目标",
    "waypoints": "航路点", "waypoint": "航路点",
    "altitudes": "高度", "altitude": "高度",
    "speeds": "航速", "speed": "航速",
    "distances": "距离", "distance": "距离",
    "ranges": "射程/距离", "range": "射程/距离",
    "angles": "角度", "angle": "角度",
    "profiles": "剖面方案", "profile": "剖面方案",
    "settings": "设置", "setting": "设置",
    "options": "选项", "option": "选项",
    "configs": "配置", "config": "配置",
    "parameters": "参数", "parameter": "参数",
    "modes": "模式", "mode": "模式",
    "types": "类型", "type": "类型",
    "classes": "型号类别", "class": "型号类别",
    "status": "状态", "state": "状态",
    "records": "记录", "record": "记录",
    "entries": "条目", "entry": "条目",
    "items": "项", "item": "项",
    "lists": "列表", "list": "列表",
    "groups": "编组", "group": "编组",
    "zones": "区域", "zone": "区域",
    "areas": "区域", "area": "区域",
    "points": "点", "point": "点",
    "values": "数值", "value": "数值",
    "numbers": "编号/数值", "number": "编号/数值",
    "names": "名称", "name": "名称",
    "codes": "代号/代码", "code": "代号/代码",
    "errors": "错误", "error": "错误",
    "warnings": "警告", "warning": "警告",
    "messages": "消息", "message": "消息",
    "logs": "日志", "log": "日志",
    "events": "事件", "event": "事件",
    "actions": "行动", "action": "行动",
    "orders": "指令", "order": "指令",
    "commands": "命令", "command": "命令",
    "controls": "控制", "control": "控制",
    "rules": "规则", "rule": "规则",
    "results": "结果", "result": "结果",
    "reports": "报告", "report": "报告",
    "views": "视图", "view": "视图",
    "windows": "窗口", "window": "窗口",
    "dialogs": "对话框", "dialog": "对话框",
    "buttons": "按钮", "button": "按钮",
    "menus": "菜单", "menu": "菜单",
    "tools": "工具", "tool": "工具",
    "tables": "表格", "table": "表格",
    "columns": "列", "column": "列",
    "rows": "行", "row": "行",
    "files": "文件", "file": "文件",
    "paths": "路径", "path": "路径",
    "folders": "文件夹", "folder": "文件夹",
    "directories": "目录", "directory": "目录",
    "data": "数据", "info": "信息", "information": "信息",
    "selected": "选中的", "active": "活动/当前", "inactive": "未激活",
    "current": "当前", "default": "默认", "custom": "自定义",
    "automatic": "自动", "manual": "手动", "auto": "自动",
    "enabled": "已启用", "disabled": "已禁用", "enable": "启用", "disable": "禁用",
    "available": "可用", "unavailable": "不可用",
    "valid": "有效", "invalid": "无效",
    "required": "必选/必需", "optional": "可选",
    "primary": "主要/首要", "secondary": "次要",
    "maximum": "最大", "minimum": "最小", "max": "最大", "min": "最小",
    "high": "高", "medium": "中", "low": "低",
    "total": "总计", "average": "平均", "remaining": "剩余",
    "estimated": "预计", "calculated": "计算得出的",
    "internal": "内部", "external": "外部",
    "loaded": "已装载", "empty": "空", "full": "满载",
    "ready": "就绪", "unready": "未就绪", "busy": "忙碌",
    "operational": "战备可用", "damaged": "受损", "destroyed": "已摧毁",
    "friendly": "友方", "hostile": "敌方", "neutral": "中立", "unfriendly": "非友善", "unknown": "未知",
}

# Translation helper function for phrase
def translate_phrase(text):
    text_clean = text.strip()
    if not text_clean:
        return text

    # Direct match in clean_exact
    if text_clean in clean_exact:
        return clean_exact[text_clean]
    
    # Direct match in TERMS
    if text_clean in TERMS:
        return TERMS[text_clean]

    # Handle colon format: "Key: Value" or "Key:"
    if ":" in text_clean:
        parts = text_clean.split(":", 1)
        left = parts[0].strip()
        right = parts[1].strip() if len(parts) > 1 else ""
        left_trans = TERMS.get(left, clean_exact.get(left))
        if not left_trans:
            left_words = left.split()
            if len(left_words) <= 3 and all(w.lower() in VOCAB for w in left_words):
                left_trans = "".join(VOCAB[w.lower()] for w in left_words)
        if left_trans:
            if not right:
                return left_trans + ":"
            right_trans = TERMS.get(right, clean_exact.get(right, right))
            return f"{left_trans}: {right_trans}"

    # Handle parentheses: "Name (Detail)"
    m = re.match(r"^([^\(\)]+)\s*\((.+)\)$", text_clean)
    if m:
        main_part = m.group(1).strip()
        sub_part = m.group(2).strip()
        main_trans = TERMS.get(main_part, clean_exact.get(main_part))
        if not main_trans and len(main_part.split()) <= 3 and all(w.lower() in VOCAB for w in main_part.split()):
            main_trans = "".join(VOCAB[w.lower()] for w in main_part.split())
        sub_trans = TERMS.get(sub_part, clean_exact.get(sub_part))
        if not sub_trans and len(sub_part.split()) <= 3 and all(w.lower() in VOCAB for w in sub_part.split()):
            sub_trans = "".join(VOCAB[w.lower()] for w in sub_part.split())
        
        if main_trans or sub_trans:
            mt = main_trans if main_trans else main_part
            st = sub_trans if sub_trans else sub_part
            return f"{mt} ({st})"

    # Handle slash: "PartA / PartB"
    if "/" in text_clean:
        parts = [p.strip() for p in text_clean.split("/")]
        trans_parts = []
        any_success = False
        for p in parts:
            tp = TERMS.get(p, clean_exact.get(p))
            if not tp and len(p.split()) <= 2 and all(w.lower() in VOCAB for w in p.split()):
                tp = "".join(VOCAB[w.lower()] for w in p.split())
            if tp:
                trans_parts.append(tp)
                any_success = True
            else:
                trans_parts.append(p)
        if any_success:
            return " / ".join(trans_parts)

    # Verb patterns
    words = text_clean.split()
    if len(words) >= 2:
        v0 = words[0].lower()
        rest = " ".join(words[1:])
        rest_trans = TERMS.get(rest, clean_exact.get(rest))
        if not rest_trans and len(words[1:]) <= 3 and all(w.lower() in VOCAB for w in words[1:]):
            rest_trans = "".join(VOCAB[w.lower()] for w in words[1:])

        if rest_trans:
            if v0 in ["select", "choose"]: return f"选择 {rest_trans}"
            if v0 in ["edit", "modify"]: return f"编辑 {rest_trans}"
            if v0 in ["add", "create", "new"]: return f"添加 {rest_trans}"
            if v0 in ["delete", "remove"]: return f"删除 {rest_trans}"
            if v0 in ["clear"]: return f"清除 {rest_trans}"
            if v0 in ["show", "display"]: return f"显示 {rest_trans}"
            if v0 in ["hide"]: return f"隐藏 {rest_trans}"
            if v0 in ["enable"]: return f"启用 {rest_trans}"
            if v0 in ["disable"]: return f"禁用 {rest_trans}"
            if v0 in ["reset"]: return f"重置 {rest_trans}"
            if v0 in ["refresh", "reload"]: return f"刷新 {rest_trans}"
            if v0 in ["assign"]: return f"指派 {rest_trans}"
            if v0 in ["unassign"]: return f"取消指派 {rest_trans}"
            if v0 in ["copy"]: return f"复制 {rest_trans}"
            if v0 in ["paste"]: return f"粘贴 {rest_trans}"
            if v0 in ["export"]: return f"导出 {rest_trans}"
            if v0 in ["import"]: return f"导入 {rest_trans}"
            if v0 in ["save"]: return f"保存 {rest_trans}"
            if v0 in ["load"]: return f"加载 {rest_trans}"
            if v0 in ["view"]: return f"查看 {rest_trans}"
            if v0 in ["open"]: return f"打开 {rest_trans}"
            if v0 in ["close"]: return f"关闭 {rest_trans}"

    # Suffix patterns
    if len(words) >= 2:
        vn = words[-1].lower()
        front = " ".join(words[:-1])
        front_trans = TERMS.get(front, clean_exact.get(front))
        if not front_trans and len(words[:-1]) <= 3 and all(w.lower() in VOCAB for w in words[:-1]):
            front_trans = "".join(VOCAB[w.lower()] for w in words[:-1])

        if front_trans:
            if vn in ["settings", "setting", "options"]: return f"{front_trans} 设置"
            if vn in ["list"]: return f"{front_trans} 列表"
            if vn in ["type"]: return f"{front_trans} 类型"
            if vn in ["class"]: return f"{front_trans} 级别/型号"
            if vn in ["mode"]: return f"{front_trans} 模式"
            if vn in ["status", "state"]: return f"{front_trans} 状态"
            if vn in ["name"]: return f"{front_trans} 名称"
            if vn in ["details"]: return f"{front_trans} 详情"
            if vn in ["properties"]: return f"{front_trans} 属性"
            if vn in ["count", "qty"]: return f"{front_trans} 数量"
            if vn in ["editor"]: return f"{front_trans} 编辑器"
            if vn in ["viewer"]: return f"{front_trans} 查看器"
            if vn in ["tool"]: return f"{front_trans} 工具"
            if vn in ["window"]: return f"{front_trans} 窗口"
            if vn in ["dialog"]: return f"{front_trans} 对话框"
            if vn in ["button"]: return f"{front_trans} 按钮"
            if vn in ["tab"]: return f"{front_trans} 标签页"

    # Multi-word full vocab matching
    if len(words) <= 4 and all(w.lower() in VOCAB for w in words):
        return "".join(VOCAB[w.lower()] for w in words)

    return None

# Translate sentence patterns
def translate_sentence(text):
    t = text.strip()
    # Confirmation dialogs
    m = re.match(r"^Are you sure you want to (.+)\?$", t, re.I)
    if m:
        action = m.group(1).strip()
        ta = translate_phrase(action) or action
        return f"您确定要 {ta} 吗？"

    m = re.match(r"^Do you want to (.+)\?$", t, re.I)
    if m:
        action = m.group(1).strip()
        ta = translate_phrase(action) or action
        return f"您是否希望 {ta}？"

    # Cannot / Unable to
    m = re.match(r"^Cannot (.+) because (.+)$", t, re.I)
    if m:
        action = m.group(1).strip()
        reason = m.group(2).strip()
        ta = translate_phrase(action) or action
        tr = translate_phrase(reason) or reason
        return f"无法 {ta}，因为 {tr}"

    m = re.match(r"^Cannot (.+)\. (.+)$", t, re.I)
    if m:
        action = m.group(1).strip()
        reason = m.group(2).strip()
        ta = translate_phrase(action) or action
        tr = translate_phrase(reason) or reason
        return f"无法 {ta}。{tr}"

    m = re.match(r"^Unable to find (.+) with ID:\s*(.+)$", t, re.I)
    if m:
        obj = m.group(1).strip()
        oid = m.group(2).strip()
        to = translate_phrase(obj) or obj
        return f"未找到 ID 为 {oid} 的 {to}"

    m = re.match(r"^Warning\s*:\s*(.+)$", t, re.I)
    if m:
        body = m.group(1).strip()
        tb = translate_phrase(body) or body
        return f"警告 : {tb}"

    m = re.match(r"^Error\s*:\s*(.+)$", t, re.I)
    if m:
        body = m.group(1).strip()
        tb = translate_phrase(body) or body
        return f"错误 : {tb}"

    return None

# Test candidate pools
candidates = {}
candidates.update(tiered["p1"])
candidates.update(tiered["p2"])
candidates.update(tiered["p3"])

print(f"Total untranslated candidates pool: {len(candidates)}")

translated_count = 0
new_translations = {}

for text, count in candidates.items():
    if text in clean_exact:
        continue
    # Try sentence
    res = translate_sentence(text)
    if not res:
        # Try phrase
        res = translate_phrase(text)
    
    if res and res != text:
        new_translations[text] = res
        translated_count += 1

print(f"Successfully generated new translations: {translated_count}")

# Sample results
print("\n--- Samples of new translations ---")
for k in list(new_translations.keys())[:30]:
    print(f"  {repr(k)} -> {repr(new_translations[k])}")
