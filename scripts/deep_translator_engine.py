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

# Keep only genuinely translated exact items
clean_exact = {}
for k, v in existing_exact.items():
    if any('\u4e00' <= ch <= '\u9fff' for ch in str(v)):
        clean_exact[k] = v

print(f"Base verified exact count: {len(clean_exact)}")

# Comprehensive Military & Aviation & Naval & UI Term Glossary
GLOSSARY = {
    # 基础战术与规则
    "Rules of Engagement (RoE)": "交战规则 (RoE)",
    "Rules of Engagement": "交战规则",
    "Emission Control (EMCON)": "电磁辐射管制 (EMCON)",
    "Emission Control": "电磁辐射管制",
    "Weapon Release Authority (WRA)": "武器投射权限条令 (WRA)",
    "Weapon Release Authority": "武器投射权限条令",
    "Doctrine / EMCON / WRA": "条令 / 辐射控制 / 武器使用授权",
    "Doctrine": "作战条令",
    "Order of Battle": "战斗序列 (ORBAT)",
    "Aggregate Ground Unit (AGU)": "地面聚合部队 (AGU)",
    "Land Warfare": "地面作战",
    "Anti-Air Warfare (AAW)": "防空作战 (AAW)",
    "Anti-Air Warfare": "防空作战",
    "Anti-Surface Warfare (ASuW)": "反水面作战 (ASuW)",
    "Anti-Surface Warfare": "反水面作战",
    "Anti-Submarine Warfare (ASW)": "反潜作战 (ASW)",
    "Anti-Submarine Warfare": "反潜作战",
    "Mining Operations": "布雷作业",
    "Mine Countermeasures (MCM)": "反水雷/扫雷作业 (MCM)",
    "Mine Countermeasures": "反水雷作业",
    "Search and Rescue (SAR)": "搜救行动 (SAR)",
    "Search and Rescue": "搜救行动",
    "Combat Air Patrol (CAP)": "空中战斗巡逻 (CAP)",
    "Combat Air Patrol": "空中战斗巡逻",
    "Airborne Early Warning (AEW)": "空中预警 (AEW)",
    "Airborne Early Warning": "空中预警",
    "Suppression of Enemy Air Defenses (SEAD)": "防空压制 (SEAD)",
    "Suppression of Enemy Air Defenses": "防空压制",
    "Destruction of Enemy Air Defenses (DEAD)": "防空摧毁 (DEAD)",
    "Destruction of Enemy Air Defenses": "防空摧毁",
    "Close Air Support (CAS)": "近距离空中支援 (CAS)",
    "Close Air Support": "近距离空中支援",
    "Air Interdiction": "空中阻绝",
    "Ferry Flight": "转场调机飞行",
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
    "Time Compression": "时间压缩",
    "God's Eye View": "上帝视界/全知视角",
    "Blind Spots": "雷达盲区/遮挡区",
    "Line-of-sight Tool": "视距/通视分析工具",
    "Line-of-Sight": "视距/通视线",
    "Line of Sight": "视距",
    "Pulse Times (lower is better)": "仿真脉冲耗时 (数值越低越好)",

    # 传感器与火控
    "Active Radar Seeker": "主动雷达导引头",
    "Semi-Active Radar Homing": "半主动雷达寻的",
    "Passive Radar Homing": "被动雷达寻的 (反辐射)",
    "Infrared Seeker": "红外导引头",
    "Imaging Infrared (IIR)": "红外成像导引头 (IIR)",
    "Optical Seeker": "光学/电视导引头",
    "Laser Seeker": "激光寻的导引头",
    "Anti-Radiation": "反辐射",
    "Laser Guidance": "激光制导",
    "Command Guidance": "指令制导",
    "Inertial Navigation (INS)": "惯性导航 (INS)",
    "GPS / INS Guidance": "卫星/惯导复合制导",
    "Terrain Contour Matching (TERCOM)": "地形匹配制导 (TERCOM)",
    "Digital Scene Matching (DSMAC)": "数字景象匹配 (DSMAC)",
    "Acoustic Homing": "声自导",
    "Wire Guided": "线导",
    "Wake Homing": "尾流自导",
    "Active Sonar": "主动声呐",
    "Passive Sonar": "被动声呐",
    "Towed Array Sonar": "拖曳线列阵声呐",
    "Towed Array": "拖曳线列阵声呐",
    "Dipping Sonar": "吊放声呐",
    "Sonobuoy": "声呐浮标",
    "Magnetic Anomaly Detector (MAD)": "磁异常探测仪 (MAD)",
    "Magnetic Anomaly Detector": "磁异常探测仪",
    "Electronic Support Measures (ESM)": "电子支援措施 (ESM)",
    "Electronic Support Measures": "电子支援措施",
    "Electronic Countermeasures (ECM)": "电子对抗干扰 (ECM)",
    "Electronic Countermeasures": "电子对抗干扰",
    "Chaff Dispenser": "箔条干扰投放器",
    "Flare Dispenser": "红外干扰弹投放器",
    "Jamming Pod": "自卫干扰吊舱",
    "Offensive Jamming Pod": "攻势防区外干扰吊舱",
    "Early Warning Radar": "远程预警雷达",
    "Surface Search Radar": "对海搜索雷达",
    "Air Search Radar": "对空搜索雷达",
    "Target Acquisition Radar": "目标指示/捕获雷达",
    "Fire Control Radar": "火控雷达",
    "Height-Finding Radar": "测高雷达",
    "Navigation Radar": "导航雷达",
    "Weather Radar": "气象雷达",
    "Synthetic Aperture Radar (SAR)": "合成孔径雷达 (SAR)",
    "Synthetic Aperture Radar": "合成孔径雷达",
    "Side-Looking Airborne Radar (SLAR)": "侧视机载雷达 (SLAR)",
    "Forward Looking Infrared (FLIR)": "前视红外 (FLIR)",
    "Infrared Search and Track (IRST)": "红外搜索跟踪系统 (IRST)",
    "Laser Rangefinder": "激光测距仪",
    "Laser Designator": "激光照射指示器",
    "Optical Director": "光学火控仪",

    # 水下环境与声学
    "Convergence Zone (CZ)": "会聚区 (CZ)",
    "Convergence Zone": "会聚区",
    "Surface Duct": "表面波导/表面声道",
    "Sound Velocity Profile (SVP)": "声速剖面 (SVP)",
    "Thermal Layer": "温跃层/水声跃变层",
    "Layer Depth": "跃层深度",
    "Shadow Zone": "声影区",
    "Bottom Depth": "海图海底水深",
    "Bathymetry": "水深测绘数据",
    "Deep Sound Channel (SOFAR)": "深海声道 (SOFAR)",
    "Ambient Noise": "海洋背景环境噪声",
    "Self Noise": "本舰/本艇自噪声",
    "Flow Noise": "水流噪声",
    "Cavitation": "空泡现象/螺旋桨空泡",
    "Thermocline": "温跃层",

    # 运动学、航路与航段
    "Waypoint List": "航路点列表",
    "Add Waypoint": "添加航路点",
    "Delete Waypoint": "删除航路点",
    "Selected Waypoint": "选中的航路点",
    "Initial Point (IP)": "初始攻击进入点 (IP)",
    "Holding Point": "盘旋等待点",
    "Marshalling Point": "集结点",
    "Ingress Point": "突防进入点",
    "Egress Point": "撤离退出点",
    "Drop Point": "投弹/投放点",
    "Weapon Release Point": "武器投放点",
    "Touchdown Point": "着陆接地点",
    "Takeoff Point": "起飞离地点",
    "Leg Distance": "航段距离",
    "Total Distance": "总航程距离",
    "Leg Time": "航段飞行耗时",
    "Total Time": "总耗时",
    "Hold Time": "盘旋待命时间",
    "Turn Rate": "转弯速率",
    "Climb Rate": "爬升率",
    "Descent Rate": "下降率",
    "Acceleration": "加速度",
    "Cruising Altitude": "巡航高度",
    "Station Altitude": "阵位高度",
    "Attack Altitude": "攻击高度",
    "Transit Altitude": "转场高度",
    "Terrain Following": "贴地飞行/地形跟随",
    "Sea Skimming": "超低空掠海飞行",
    "Military Speed": "军用推力航速",
    "Cruise Speed": "巡航航速",
    "Loiter Speed": "待机经济航速",
    "Flank Speed": "最大极速/加力全速",
    "Full Speed": "全速",
    "Standard Speed": "标准航速",
    "Creep Speed": "慢速静音航速",
    "Reverse Speed": "倒车航速",
    "Max Speed": "最高航速",
    "Min Speed": "最低航速",

    # 燃油与空中加油
    "Bingo Fuel": "返航界限油量 (Bingo Fuel)",
    "Joker Fuel": "戒备警界油量 (Joker Fuel)",
    "Reserve Fuel": "备份余油",
    "Mission Fuel": "任务用油",
    "Fuel Capacity": "最大燃油容量",
    "Internal Fuel": "机内/舱内燃油",
    "External Fuel": "副油箱燃油",
    "Fuel Consumption": "燃油消耗率",
    "Fuel Burn Rate": "燃油消耗速率",
    "Fuel Flow": "燃油流量",
    "Air-to-Air Refueling (AAR)": "空中加油 (AAR)",
    "Air-to-Air Refueling": "空中加油",
    "Aerial Refueling": "空中加油",
    "Boom Refueling": "硬管加油 (飞桁式)",
    "Probe-and-Drogue Refueling": "软管加油 (插头锥套式)",
    "Buddy Refueling": "伙伴加油",
    "Tanker Aircraft": "加油机",
    "Tanker Mission": "加油任务",
    "Tanker Pool": "加油机资源池",
    "Refueling Priority": "空中加油优先级",
    "Refuel Queue": "加油排队队列",
    "Cannot Refuel": "无法进行空中加油",
    "Receiver Aircraft": "受油机",

    # 区域与战术边界
    "No-Nav Zone": "禁航区",
    "Exclusion Zone": "排斥/禁入区",
    "Patrol Zone": "巡逻区",
    "Strike Zone": "打击区",
    "Prosecution Zone": "攻击处置区",
    "Search Area": "搜索区",
    "Activation Zone": "激活触发区",
    "Reference Point": "参考点",
    "Relative Reference Point": "相对参考点 (随舰移动)",
    "Fixed Reference Point": "固定参考点",
    "Bearing & Range": "方位与距离",

    # 武器弹药与挂载
    "Air-to-Air Missile": "空空导弹",
    "Air-to-Surface Missile": "空面导弹",
    "Surface-to-Air Missile": "防空导弹",
    "Anti-Ship Missile": "反舰导弹",
    "Land-Attack Cruise Missile": "对陆攻击巡航导弹",
    "Anti-Radiation Missile": "反辐射导弹",
    "Anti-Submarine Torpedo": "反潜鱼雷",
    "Heavyweight Torpedo": "重型线导鱼雷",
    "Lightweight Torpedo": "轻型反潜鱼雷",
    "Depth Charge": "深水炸弹",
    "Naval Mine": "水雷",
    "Unguided Bomb": "无制导常规航弹",
    "Laser-Guided Bomb": "激光制导炸弹",
    "Precision-Guided Munition": "精确制导弹药",
    "Cluster Bomb": "集束炸弹",
    "Rocket Pod": "无控火箭发射巢",
    "Autocannon": "自动机炮/速射炮",
    "Main Gun": "主舰炮",
    "Secondary Gun": "副炮",
    "Close-In Weapon System (CIWS)": "近程防御武器系统 (CIWS)",
    "Close-In Weapon System": "近程防御武器系统",
    "Surface-to-Air (SAM)": "舰空/地空导弹 (SAM)",
    "Surface-to-Surface (SSM)": "反舰/地地导弹 (SSM)",
    "Air-to-Air (AAM)": "空空导弹 (AAM)",
    "Air-to-Ground (AGM)": "空地导弹 (AGM)",

    # 状态与条令细节
    "Ignore Plotted Course": "脱离预定航线自主交战",
    "Engage Targets of Opportunity": "允许攻击临机发现目标",
    "Winchester (Out of Ammo)": "无弹/弹药耗尽 (Winchester)",
    "Shotgun (State)": "弹药打到备用阈值 (Shotgun)",
    "Return to Base (RTB)": "返航基地 (RTB)",
    "RTB on Winchester": "弹药耗尽时立即返航",
    "RTB on Bingo Fuel": "达到返航油量时立即返航",
    "RTB on Damage": "受损达到阈值时立即返航",
    "Automatic Evasion": "遭遇攻击自动规避",
    "Manual Override": "手动接管/强制覆盖",
    "Investigate Contacts": "脱离航线前出查证目标",
    "Disengage on Threat": "受到威胁时主动脱离",
    "Shoot-Look-Shoot": "打-看-打 (评估毁伤后再射)",
    "Salvo Size": "齐射数量",
    "Weapons Allocation": "武器分配方案",
    "Release Range": "投射射程",
    "Max Range": "最大射程",
    "Min Range": "最小射程",

    # UI 常用指令
    "Assign to Mission": "指派到任务",
    "Unassign from Mission": "从任务中取消指派",
    "Set Formation": "设置编队阵位",
    "Set Course": "设定航线",
    "Set Altitude": "设定高度",
    "Set Speed": "设定航速",
    "Set Throttle": "设定油门档位",
    "Attack Options": "攻击选项",
    "Manual Fire": "手动开火",
    "Automatic Fire": "自动开火",
    "Drop Ordnance": "紧急抛弃外挂弹药",
    "Dump Fuel": "紧急空中放油",
    "Eject / Abandon Unit": "弹射弃机 / 弃舰",
    "Scuttle Unit": "自沉/自毁单位",
    "Refuel from Tanker": "呼叫加油机加油",
    "Replenish at Sea": "海上综合补给 (RAS)",
    "Quick Turnaround": "快速周转出击 (战时快速挂弹)",
    "Maintenance Priority": "地勤维护优先级",
    "Readying Time": "战备出动准备时间",

    # 数据库查看器与编辑器
    "Database Viewer": "数据库查看器",
    "Database Viewer Thread": "数据库浏览线程",
    "Database Relation": "数据库关联关系",
    "Source <-> Target DB relation": "源库与目标库关联关系",
    "TARGET RAMDB :  Referenced table": "内存目标数据库 : 引用数据表",
    "Own : entries in the source database": "本方 : 源数据库中的数据项",
    "Naval Formation Editor": "海军编队编辑器",
    "ValidateAircraftPropulsion": "验证战机动力推进系统",
    "ValidateShipStatsAndFlags": "验证水面舰艇属性与标志位",
    "ValidateShipsPropulsion": "验证舰艇动力推进系统",
    "ValidateShipAircraftFacilities": "验证舰载航空设施配置",
    "ValidateFacilityNames": "验证地面设施名称有效性",
    "Drawing Zone Polygon [ESC to cancel]": "正在绘制区域多边形 [按 ESC 取消]",
    "Attempt Retargeting": "尝试重新寻的锁定目标",
    "AIRBORNE WEAPONS": "空中在航武器",
    "Current Cargo at": "当前货物装载于",
    "Edit Cargo for": "编辑货物装载配置: ",
    "Existing Units": "当前现有单位列表",
    "HighlightTarget": "在地图上高亮标示目标",
    "No Target DB": "未指定目标数据库",
    "does not exist in target database": "在目标数据库中不存在",
    "Component Pointer from target table": "目标数据表中的组件指针",
    "No target aircraft with ID": "未找到指定 ID 的目标战机",
    "Aircraft has no Visibility values": "战机缺少目视能见度/隐身数值",
    "uses fuel entry": "使用燃油配置项",
    "Warning : This mount contains": "警告 : 该武器挂架包含",
    "Mount magazine weapon record": "武器挂架与弹药库关联记录",
    "Satellite on-unit in-service year": "卫星平台服役起始年份",
    "Satellite on-unit out-of-service year": "卫星平台退役终止年份",
    "Diesel fuel qty should be": "柴油燃油数量应为",
    "Oil fuel qty should be": "重油燃油数量应为",
    "Gas fuel qty should be": "燃气燃油数量应为",
    "Electric fuel qty should be": "蓄电池电量应为",
    "AIP fuel qty should be": "AIP绝气推进燃油量应为",
    "Capable vs Air targets but the PoK is 0%": "具备防空打击能力但杀伤概率(PoK)为0%",
    "Minimum Surface range should be 0nm": "水面目标最小射程应为0海里",
    "Minimum Land range should be 0nm": "对陆目标最小射程应为0海里",
    "The weapons minimum launch altitude": "该武器的最低发射高度",
    "The weapons maximum launch altitude": "该武器的最高发射高度",
    "The weapons cruise altitude": "该武器的巡航飞行高度",
    "The weapons maximum target altitude": "该武器允许攻击的目标最大高度",
    "The weapons minimum target altitude": "该武器允许攻击的目标最小高度",
    "Can only rename one unit, group or contact at a time.": "一次只能重命名一个单位、编队或目标接触。",
    "Drop ordnance at max range": "在最大射程处投掷弹药",
    "You have specified an invalid Tacview executable.": "您指定的 Tacview 可执行文件路径无效。",
}

print(f"Glossary base entries: {len(GLOSSARY)}")


# Comprehensive Word & Phrase Dictionary for Compositional and Sentence Translation
WORD_DICT = {
    # 动词与操作
    "add": "添加", "added": "已添加", "adding": "正在添加",
    "delete": "删除", "deleted": "已删除", "deleting": "正在删除",
    "remove": "移除", "removed": "已移除", "removing": "正在移除",
    "edit": "编辑", "edited": "已编辑", "editing": "正在编辑",
    "clear": "清除", "cleared": "已清除", "clearing": "正在清除",
    "reset": "重置", "select": "选择", "selected": "已选中",
    "create": "创建", "created": "已创建", "creating": "正在创建",
    "update": "更新", "updated": "已更新", "updating": "正在更新",
    "load": "加载", "loaded": "已加载", "loading": "正在加载",
    "save": "保存", "saved": "已保存", "saving": "正在保存",
    "import": "导入", "imported": "已导入", "export": "导出", "exported": "已导出",
    "copy": "复制", "copied": "已复制", "paste": "粘贴",
    "rename": "重命名", "clone": "克隆", "duplicate": "复制副本",
    "assign": "指派", "assigned": "已指派", "unassign": "取消指派", "unassigned": "未指派",
    "attach": "挂载/附加", "detach": "分离/卸下",
    "enable": "启用", "enabled": "已启用", "disable": "禁用", "disabled": "已禁用",
    "activate": "激活", "activated": "已激活", "deactivate": "停用", "deactivated": "已停用",
    "open": "打开", "close": "关闭", "closed": "已关闭",
    "start": "开始/启动", "stop": "停止", "pause": "暂停", "resume": "继续",
    "apply": "应用", "cancel": "取消", "confirm": "确认", "accept": "接受",
    "ignore": "忽略", "abort": "中止", "retry": "重试", "refresh": "刷新",
    "search": "搜索", "find": "查找", "found": "已找到", "filter": "筛选",
    "sort": "排序", "view": "查看/视图", "show": "显示", "hide": "隐藏",
    "lock": "锁定", "locked": "已锁定", "unlock": "解锁", "unlocked": "已解锁",
    "check": "检查/勾选", "uncheck": "取消勾选", "validate": "验证/校验", "validation": "校验",
    "calculate": "计算", "calculating": "正在计算", "recalculate": "重新计算",
    "connect": "连接", "connected": "已连接", "disconnect": "断开连接",
    "launch": "发射/起飞", "launched": "已发射/已起飞", "firing": "正在开火", "fire": "开火/发射", "fired": "已发射",
    "drop": "投掷/抛弃", "dropped": "已投掷", "jettison": "紧急抛投",
    "engage": "交战/接敌", "engaging": "正在交战", "disengage": "脱离接触",
    "attack": "攻击", "attacking": "正在攻击", "intercept": "拦截", "intercepting": "正在拦截",
    "patrol": "巡逻", "patrolling": "正在巡逻", "strike": "打击",
    "support": "支援", "supported": "受支持", "escort": "护航",
    "refuel": "加油", "refuelling": "正在加油", "refueling": "正在加油",
    "replenish": "补给", "rearm": "重新装填弹药", "repair": "维修/损管",
    "deploy": "部署", "deployed": "已部署", "recover": "回收/着舰",
    "land": "着陆/陆地", "landing": "着陆/降落", "takeoff": "起飞",
    "dock": "停靠/入坞", "undock": "出坞/离泊", "submerge": "下潜", "surface": "上浮/水面",
    "evade": "规避", "evading": "正在规避", "illuminate": "雷达照射",
    "jam": "电磁干扰", "jamming": "正在干扰", "detect": "探测", "detected": "已探测到",
    "track": "跟踪/航迹", "tracking": "正在跟踪", "identify": "识别", "identified": "已识别",
    "change": "更改/切换", "changed": "已更改", "set": "设置/设定", "setting": "设置", "settings": "设置",
    "use": "使用", "used": "已使用", "using": "使用",
    "allow": "允许", "allowed": "被允许", "forbid": "禁止", "forbidden": "被禁止",
    "require": "需要", "required": "必需", "missing": "缺失", "lacks": "缺少",
    "exist": "存在", "exists": "存在", "occurred": "已发生", "failed": "失败",
    "unable": "无法", "cannot": "不能/无法", "must": "必须", "should": "应当",
    "can": "可以", "may": "可能/可以", "will": "将", "would": "将会",

    # 军事平台与实体
    "aircraft": "战机/航空器", "plane": "飞机", "helicopter": "直升机", "uav": "无人机", "ucav": "无人作战飞机",
    "drone": "无人机", "AWACS": "预警机", "tanker": "加油机", "tankers": "加油机",
    "fighter": "战斗机", "bomber": "轰炸机", "attacker": "攻击机", "recon": "侦察机",
    "transport": "运输机/运输船", "satellite": "卫星", "satellites": "卫星",
    "ship": "水面舰艇", "ships": "水面舰艇", "vessel": "舰船", "boat": "艇", "boats": "小艇",
    "carrier": "航空母舰", "cruiser": "巡洋舰", "destroyer": "驱逐舰", "frigate": "护卫舰",
    "corvette": "轻型护卫舰", "amphibious": "两栖舰艇", "auxiliaries": "辅助舰船",
    "submarine": "潜艇", "submarines": "潜艇", "sub": "潜艇", "ssbn": "弹道导弹核潜艇", "ssn": "攻击型核潜艇", "ssk": "常规潜艇",
    "facility": "地面设施", "facilities": "地面设施", "building": "建筑物", "bunker": "掩体/地堡",
    "airbase": "空军基地", "runway": "跑道", "taxiway": "滑行道", "tarmac": "停机坪",
    "hangar": "机库", "pier": "码头", "dockyard": "船坞", "pad": "起降坪",
    "vehicle": "地面车辆", "armor": "装甲车辆", "tank": "坦克", "artillery": "火炮",
    "infantry": "步兵", "platoon": "排级单位", "battery": "导弹连/炮兵连", "site": "阵地",
    "unit": "单位", "units": "单位", "group": "编队/编组", "groups": "编队",
    "formation": "队形/编队", "member": "成员", "lead": "长机/旗舰", "leader": "领队", "wingman": "僚机",
    "side": "推演方/阵营", "sides": "阵营", "coalition": "联盟", "hostile": "敌方", "friendly": "友方",
    "neutral": "中立", "unfriendly": "非友好", "unknown": "未知/不明",
    "contact": "探测目标/接触", "contacts": "探测目标", "target": "目标", "targets": "目标",
    "threat": "威胁", "threats": "威胁", "survivor": "幸存者/落水人员",

    # 武器、传感器与子系统
    "weapon": "武器", "weapons": "武器", "ordnance": "弹药", "munition": "弹药", "munitions": "弹药",
    "missile": "导弹", "missiles": "导弹", "rocket": "火箭弹", "bomb": "航弹/炸弹", "bombs": "炸弹",
    "torpedo": "鱼雷", "torpedoes": "鱼雷", "mine": "水雷", "mines": "水雷",
    "depth": "深度", "charge": "深水炸弹/装药", "decoy": "诱饵", "decoys": "诱饵",
    "chaff": "箔条", "flare": "红外干扰弹", "gun": "火炮/机炮", "cannon": "机炮",
    "warhead": "战斗部/弹头", "warheads": "战斗部", "fuze": "引信", "seeker": "导引头",
    "guidance": "制导", "propulsion": "动力推进", "engine": "发动机/引擎", "engines": "发动机",
    "sensor": "传感器", "sensors": "传感器", "radar": "雷达", "sonar": "声呐",
    "esm": "电子支援(ESM)", "ecm": "电子干扰(ECM)", "irst": "红外搜索跟踪(IRST)",
    "flir": "前视红外(FLIR)", "optical": "光学", "visual": "目视", "laser": "激光",
    "ir": "红外", "infrared": "红外", "mad": "磁异探测(MAD)", "acoustic": "声学",
    "mount": "武器挂架/炮座", "mounts": "武器挂架", "magazine": "弹药库", "magazines": "弹药库",
    "loadout": "挂载方案", "loadouts": "挂载方案", "cargo": "货物/载荷", "fuel": "燃油",
    "battery": "蓄电池/炮兵连", "aip": "绝气推进(AIP)", "nuclear": "核动力/核武器",
    "diesel": "柴油", "gas": "燃气轮机", "steam": "蒸汽轮机", "electric": "电力推进",
    "comm": "通信设备", "comms": "通信链路", "datalink": "数据链", "network": "网络",
    "component": "组件/部件", "components": "组件", "damage": "毁伤/受损", "structural": "结构",
    "fire": "火灾/开火", "flooding": "进水", "destroyed": "已摧毁", "sunk": "已击沉", "damaged": "受损",

    # 运动学、空间与气象环境
    "speed": "航速/速度", "velocity": "速度", "altitude": "高度", "elevation": "海拔/仰角",
    "depth": "深度", "range": "射程/距离", "distance": "距离", "bearing": "方位",
    "heading": "航向", "course": "航线/航向", "waypoint": "航路点", "waypoints": "航路点",
    "flightplan": "飞行计划", "route": "路线/航线", "leg": "航段", "path": "路径",
    "zone": "区域", "zones": "区域", "area": "区域/面积", "point": "点/位置", "points": "点数/参考点",
    "reference": "参考", "polygon": "多边形", "circle": "圆形区", "sector": "扇区",
    "station": "阵位/巡逻位", "base": "基地", "home": "母基地", "destination": "目的地",
    "origin": "起点", "source": "源", "location": "位置", "position": "位置/坐标",
    "latitude": "纬度", "longitude": "经度", "terrain": "地形", "land": "陆地",
    "sea": "海洋/海况", "water": "水域", "subsurface": "水下", "air": "空中/防空",
    "surface": "水面/表面", "space": "太空/空间", "orbital": "轨道",
    "weather": "气象/天气", "temperature": "温度", "wind": "风速/风向", "cloud": "云层",
    "clouds": "云层", "rain": "降雨/降水", "sea-state": "海况等级", "thermocline": "温跃层",
    "layer": "跃层/图层", "duct": "表面波导", "cz": "会聚区", "day": "昼间/天", "night": "夜间",
    "dawn": "黎明", "dusk": "黄昏", "time": "时间", "duration": "持续时间", "delay": "延迟",

    # 速度与高度档位
    "band": "高度层/波段", "bands": "高度层/波段", "altitudeband": "高度层",
    "military": "军用推力/军事", "cruise": "巡航", "loiter": "待机/盘旋",
    "afterburner": "加力燃烧室/全加力", "flank": "最大全速/侧翼", "full": "全速/满载",
    "standard": "标准", "creep": "微速/静音慢速", "stop": "停车/停止",
    "subsonic": "亚音速", "supersonic": "超音速", "hypersonic": "高超音速",
    "high": "高", "medium": "中", "low": "低", "very": "极/非常",
    "minimum": "最小/最低", "min": "最小", "maximum": "最大/最高", "max": "最大",
    "higher": "高于/更高", "lower": "低于/更低", "greater": "大于", "less": "小于", "equal": "等于/相同",

    # 任务、条令与脚本系统
    "mission": "任务", "missions": "任务", "task": "任务", "package": "任务编组包",
    "doctrine": "条令", "roe": "交战规则(RoE)", "emcon": "电磁辐射管制(EMCON)", "wra": "武器使用授权(WRA)",
    "active": "主动/激活", "passive": "被动", "inherent": "固有", "inherit": "继承上级",
    "automatic": "自动", "auto": "自动", "manual": "手动",
    "event": "事件", "events": "事件", "trigger": "触发器", "triggers": "触发器",
    "condition": "条件", "conditions": "条件", "action": "动作", "actions": "动作",
    "lua": "Lua脚本", "script": "脚本", "console": "控制台", "special": "特殊",
    "message": "消息/电文", "log": "日志", "logs": "日志", "report": "报告",
    "score": "得分", "scoring": "计分", "triumph": "完胜", "victory": "胜利", "defeat": "战败",

    # 软件界面、数据库与系统词汇
    "scenario": "想定/剧本", "campaign": "战役", "database": "数据库", "db": "数据库",
    "dbid": "数据库ID", "id": "编号(ID)", "objectid": "对象ID", "componentid": "组件ID",
    "table": "数据表", "record": "记录", "entry": "条目", "entries": "条目",
    "field": "字段", "column": "列", "row": "行", "index": "索引", "value": "数值/值", "values": "数值",
    "key": "键/密钥", "string": "字符串", "text": "文本", "number": "数字/数量", "count": "数量/计数",
    "list": "列表", "tree": "树状视图", "grid": "表格", "tab": "标签页", "panel": "面板",
    "window": "窗口", "dialog": "对话框", "form": "表单/窗口", "forms": "表单",
    "menu": "菜单", "button": "按钮", "toolbar": "工具栏", "statusbar": "状态栏",
    "map": "态势图/地图", "globe": "地球视图", "overlay": "图层叠加", "symbol": "军标符号",
    "file": "文件", "files": "文件", "folder": "文件夹", "directory": "目录", "path": "路径",
    "name": "名称", "title": "标题", "description": "描述", "type": "类型", "subtype": "子类型",
    "class": "级别/类别", "category": "分类", "role": "角色/定位", "status": "状态", "state": "状态",
    "mode": "模式", "option": "选项", "options": "选项", "preference": "首选项", "preferences": "偏好设置",
    "configuration": "配置", "config": "配置", "parameter": "参数", "parameters": "参数",
    "property": "属性", "properties": "属性", "attribute": "特性", "flag": "标志位", "flags": "标志位",
    "modifier": "修正值", "consumption": "消耗量", "capacity": "容量", "size": "尺寸/大小",
    "weight": "重量", "length": "长度", "width": "宽度", "height": "高度", "displacement": "排水量",
    "signature": "特征信号(隐身/反射)", "visibility": "可见度", "accuracy": "精度", "pok": "毁伤概率(PoK)",
    "capability": "能力", "capable": "具备能力", "limitations": "限制条件",
    "error": "错误", "warning": "警告", "info": "信息", "information": "详情信息",
    "valid": "有效", "invalid": "无效", "null": "空值(Null)", "empty": "空", "none": "无",
    "all": "全部", "any": "任意", "no": "无/否", "not": "不/未", "yes": "是",
    "current": "当前", "new": "新建/新", "existing": "现有", "default": "默认", "custom": "自定义",
    "common": "通用", "general": "常规", "advanced": "高级", "basic": "基础",
    "local": "本地/局部", "global": "全局", "server": "服务器", "client": "客户端",
    " thread": "线程", "memory": "内存", "performance": "性能", "pulse": "脉冲",
    "version": "版本", "author": "作者", "date": "日期", "year": "年份",
}

print(f"Word dict entries: {len(WORD_DICT)}")


def smart_translate(text):
    if not text or not isinstance(text, str):
        return None
    raw = text.strip()
    if not raw:
        return None
        
    # 1. Direct hit in GLOSSARY or clean_exact
    if raw in GLOSSARY:
        return GLOSSARY[raw]
    if raw in clean_exact:
        return clean_exact[raw]
        
    # 2. Check case-insensitive GLOSSARY
    for k, v in GLOSSARY.items():
        if k.lower() == raw.lower():
            return v

    # 3. Handle prefixes / colons like "Fuel: ", "Target: "
    if ":" in raw:
        parts = raw.split(":", 1)
        prefix_t = smart_translate(parts[0].strip())
        if prefix_t:
            rest = parts[1].strip()
            if not rest:
                return prefix_t + ":" + parts[1]
            rest_t = smart_translate(rest)
            if rest_t:
                return f"{prefix_t}: {rest_t}"
            else:
                return f"{prefix_t}: {rest}"

    # 4. Handle brackets / parentheses like "Disengage (Drop all targets)"
    m = re.match(r"^([^\(\)\[\]]+)[\s]*([\(\[][^\(\)\[\]]+[\)\]])$", raw)
    if m:
        main_part = m.group(1).strip()
        tail_part = m.group(2).strip()
        main_t = smart_translate(main_part)
        inner = tail_part[1:-1].strip()
        inner_t = smart_translate(inner)
        bracket_l = tail_part[0]
        bracket_r = tail_part[-1]
        
        if main_t and inner_t:
            return f"{main_t} {bracket_l}{inner_t}{bracket_r}"
        elif main_t:
            return f"{main_t} {bracket_l}{inner}{bracket_r}"

    # 5. Handle Tab/Grid/View suffixes in UI controls
    for suffix, trans_suffix in [
        ("Tab", "标签页"), ("GridView", "表格"), ("Control", "控件"),
        ("Panel", "面板"), ("Window", "窗口"), ("Dialog", "对话框"),
        ("Editor", "编辑器"), ("Viewer", "查看器"), ("Tool", "工具"),
        ("Manager", "管理器"), ("Settings", "设置"), ("Options", "选项")
    ]:
        if raw.endswith(suffix) and len(raw) > len(suffix) + 2:
            stem = raw[:-len(suffix)]
            stem_t = smart_translate(stem)
            if stem_t:
                return stem_t + trans_suffix

    # 6. Compound phrase translation using WORD_DICT
    tokens = re.split(r'(\s+|[,\-_/])', raw)
    all_translated = True
    composed = []
    has_chinese = False
    
    for tok in tokens:
        if not tok:
            continue
        if re.match(r'^\s+$|^[,\-_/]$', tok):
            composed.append(tok)
            continue
        tok_low = tok.lower()
        if tok_low in WORD_DICT:
            composed.append(WORD_DICT[tok_low])
            has_chinese = True
        elif tok in GLOSSARY:
            composed.append(GLOSSARY[tok])
            has_chinese = True
        elif re.match(r'^\d+(\.\d+)?(kg|kt|nm|m|km|ft|lbs|sec|min|hrs|%)?$', tok, re.I):
            composed.append(tok)
        else:
            all_translated = False
            composed.append(tok)

    if has_chinese and (all_translated or len(raw.split()) <= 3):
        res = "".join(composed)
        # Avoid messy double spaces
        res = re.sub(r' +', ' ', res)
        return res

    # 7. Common sentence pattern matching for logs and validations
    PATTERNS = [
        (r"^No ([A-Za-z]+) with ID\s*(.*)$", lambda m: f"未找到ID为 {m.group(2)} 的{WORD_DICT.get(m.group(1).lower(), m.group(1))}"),
        (r"^([A-Za-z]+) has no ([A-Za-z]+) values!*$", lambda m: f"{WORD_DICT.get(m.group(1).lower(), m.group(1))}缺少{WORD_DICT.get(m.group(2).lower(), m.group(2))}数值！"),
        (r"^Error occurred while ([^:]+): (.*)$", lambda m: f"在执行“{m.group(1)}”时发生错误: {m.group(2)}"),
        (r"^Failed to ([^:]+): (.*)$", lambda m: f"无法{m.group(1)}: {m.group(2)}"),
        (r"^([A-Za-z\s]+) cannot be ([A-Za-z]+)\.*$", lambda m: f"{m.group(1).strip()}不能为{WORD_DICT.get(m.group(2).lower(), m.group(2))}"),
        (r"^([A-Za-z\s]+) must be greater than ([A-Za-z\s0-9]+)\.*$", lambda m: f"{m.group(1).strip()}必须大于{m.group(2).strip()}"),
        (r"^([A-Za-z\s]+) must be less than ([A-Za-z\s0-9]+)\.*$", lambda m: f"{m.group(1).strip()}必须小于{m.group(2).strip()}"),
        (r"^([A-Za-z\s]+) must be equal to ([A-Za-z\s0-9]+)\.*$", lambda m: f"{m.group(1).strip()}必须等于{m.group(2).strip()}"),
    ]
    for pat, repl in PATTERNS:
        pm = re.match(pat, raw, re.I)
        if pm:
            try:
                return repl(pm)
            except Exception:
                pass

    return None

# Now run smart_translate over all untranslated candidates
final_exact = dict(clean_exact)
new_count = 0

for cat in ["p1", "p2", "p3"]:
    for text in tiered[cat]:
        if text in final_exact:
            continue
        trans = smart_translate(text)
        if trans and trans != text and any('\u4e00' <= ch <= '\u9fff' for ch in trans):
            final_exact[text] = trans
            new_count += 1

print(f"Total new translations synthesized: {new_count}")
print(f"Total final exact dictionary size: {len(final_exact)}")

# Also build partial dictionary
final_partial = dict(existing_partial)
for k, v in GLOSSARY.items():
    if len(k) >= 4 and k not in final_partial:
        final_partial[k] = v

print(f"Total final partial dictionary size: {len(final_partial)}")

# Save to CMOLocalizer.json
out_loc = {
    "version": "2.0.0",
    "updated": "2026-10-10",
    "offsets": existing_offsets,
    "exact": final_exact,
    "partial": final_partial,
    "words": WORD_DICT
}

out_path = os.path.join(base_dir, "CMOLocalizer.json")
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(out_loc, f, ensure_ascii=False, indent=2)

print(f"Successfully generated CMOLocalizer.json (Size: {os.path.getsize(out_path) / 1024:.2f} KB)")
