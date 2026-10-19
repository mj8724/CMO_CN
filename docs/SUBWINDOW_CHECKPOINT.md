# 子窗口实机排查与军语精修进度记录 (Phase 3 - 汇总精修阶段)
## 1. 已完成实机排查的子窗口清单
1. **编辑阵营 (Edit Sides) & 新建阵营对话框 (Create a new side)**
   - 发现漏翻与机翻：Add Nature Side, Briefing, Postures, Enablers, Awareness Level:, Fixed Color:, Change Color, Create a new side, 连带责任 (Collective Responsibility -> 集体交战联动/阵营连带态势)。
2. **作战条令 (Doctrine / ROE / EMCON / WRA / Withdraw & Redeploy / Targeting)**
   - 发现漏翻与机翻：
     - 窗口标题格式：`条令 & 交战规则(RoE) 用于推演方:` -> `作战条令与交战规则 (ROE) - 所属阵营:`
     - 标签页：`Withdraw & Redeploy (Ships / Subs / Land)` -> `撤离与重新部署 (舰艇/潜艇/陆基)`
     - 条令项：`A/G Strafing (Gun)` -> `对地机炮扫射`、`AAW WRA Qty` -> `防空武器发射数量授权`、`AAW 制导` -> `防空导弹制导体制`、`AGU Tactical Posture` -> `地面部队战术态势`、`AIP Usage` -> `潜艇不依赖空气推进(AIP)使用策略`、`ASuW WP Missiles` -> `反舰导弹航路点规划`、`Air Ops Tempo` -> `航空出动高强度节奏`
     - 底部按钮：`On all doctrine definitions`、`Reset to Default`、`Reset affected missions (inherit from above Doctrine)`、`Reset Doctrine (use inherited settings)`、`Reset affected units (inherit from above Doctrine)`、`Set to 'Inherit'`
3. **任务编辑器 (Mission Editor) & 新建任务弹窗 (New Mission / Task Pool / Package)**
   - 发现严重机翻与漏翻：
     - `最小 # 的就绪打击一个/c 必需至触发器任务:` (原文 `Minimum # of ready strike a/c required to trigger mission:`) -> `触发打击任务所需的最少就绪战机数：`
     - `Mission Editor for side:` -> `任务规划编辑器 - 所属阵营：`
     - `New Mission / Task Pool / Package` -> `新建作战任务 / 兵力池 / 任务编组`
     - `Parent pool:` -> `上级兵力池：`
     - `Order RTB` -> `下令返航 (RTB)`
     - `Showing Multi-Mission` -> `显示多任务指派单位`
     - `Time On Target (ToT) (Zulu)` / `Time on Target (ToT)` -> `弹着/抵达目标时间 (ToT) (标准时)`
     - `Take-Off Time (Zulu)` -> `指定起飞时间 (标准时)`
     - `Scrub mission if side is human` -> `若该阵营由玩家指挥则取消任务`
     - `Targets destroyed : continue mission` -> `目标摧毁后：继续执行任务`
     - `Un-Mark from escorts` -> `取消护航编队标记`
     - `Use pre-generated flightplans only (no auto-generation by Mission AI)` -> `仅使用预生成飞行计划（禁用任务AI自动规划）`
     - `Tankers (AAR):` -> `空中加油机 (AAR)：`
     - `Summary:` -> `任务综述：`、`Phase :` -> `当前阶段：`
4. **系统设置 (Game Options)**
   - 发现漏翻与机翻：
     - 标签页：`Map Display` -> `海图与态势显示`、`Sounds/Music` -> `音效与背景音乐`、`Tacview` -> `Tacview 三维态势视图`
     - 选项：`Clear all existing trails` -> `清除所有现有航迹`、`Fine-grained navigation, max distance (nm):` -> `精细航路规划最大距离 (海里)：`、`Frequency` -> `采样频率`、`Lifetime (sec)` -> `保留时长 (秒)`、`Icon Zoom` -> `图标缩放模式`、`Reset positions of secondary windows` -> `重置所有子窗口位置`、`Show speed of ground units/contacts in:` -> `地面单位与目标的航速单位：`、`Show units behaviour bark` -> `显示单位战术行为浮动提示`、`Vertical scaling will also apply to rendered units` -> `垂直高度缩放同步应用于三维单位模型`、`please allow a few seconds for world mesh to update` -> `（修改后请等待数秒以更新全球地形网格）`、`Tacview executable path:` -> `Tacview 主程序路径：`、`Unit System` -> `度量衡单位制`、`现代化武器装备数据库查看Auto-Pause` (错位拼接修复)
5. **想定编辑与简介窗口**
   - `Edit HTML` -> `编辑 HTML 源码`
