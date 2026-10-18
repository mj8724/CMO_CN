# 《现代行动：指挥 (Command: Modern Operations)》专家级全汉化工程归档 (v3.0 终极版)

## 一、项目概述
- 游戏根目录：`G:\soft\steam\steamapps\common\Command - Modern Operations`
- 游戏主程序：`Command.exe` (版本 1.06, 64-bit .NET Framework 4.8 桌面应用，受 SmartAssembly 8.x 强名称混淆保护)
- 交付成果：
  1. `C:\Users\admin\Desktop\CMO_Chinese_Setup.exe`：单文件独立图形化一键安装/卸载程序（内嵌全部引擎、字库与 8 大数据库模板，支持命令行静默模式与自动寻路）
  2. `C:\Users\admin\Desktop\CMO_Chinese_Localization_Pack.zip`：绿色免安装汉化包（解压至游戏根目录即可）
  3. `projects/汉化/`：全套 C# 源代码、词典处理脚本及原始备份

---

## 二、架构演进与突破（v1.0 -> v2.0 -> v3.0）

### 1. 核心注入机制：CLR 运行时级无损挂载
- 通过 `Command.exe.config` 配置 `<runtime><assemblyBinding>`，实现主程序启动时强行挂载外置汉化引擎 `CMOLocalizer.dll`。
- 不修改 `Command.exe` 任何二进制字节，不破坏 SmartAssembly 强名称签名，彻底规避游戏反作弊闪退与 Steam 文件校验冲突。
- 采用双轨驱动架构：
  - **静态字符串池解密**：动态解密主程序中 28,833 个字符串槽位。
  - **WinForms / WPF / WebBrowser 视觉树侦听**：深度扫描所有窗体、菜单栏、状态栏、树形视图、表格单元格以及富文本简报网页，挂载 `DocumentCompleted` 与实时 DOM 注入。

### 2. 数据库安全汉化突破（DB3K & CWDB - 彻底攻克）
- **解决核心矛盾**：SQLite 数据库中的实体名称（如 F-16C、AIM-120）是 Lua 脚本与想定事件绑定的物理主键，直接改动库字段会导致历史想定无法加载或闪退。
- **攻克方案（双层解耦）**：
  1. **HTML 模板层彻底汉化**：本地化 `DB/Templates/` 下全部 8 个装备详情渲染模板（`Aircraft.html`, `Facility.html`, `Loadout.html`, `Magazine.html`, `Satellite.html`, `Sensor.html`, `Ship.html`, `Submarine.html`），涵盖全部性能参数、装甲指标、雷达航电参数、航程推重比等数百项技术字段，并备份原始英文模板于 `DB/Templates_Original_Backup`。
  2. **Enum 枚举参数全量本地化**：从数据库 71 张 `Enum%` 枚举字典表中提取 2,122 项战术属性与分类术语（推进系统、航电代次、作战分类、挂载构型、WRA 拦截条令等），翻译后全量并入 `CMOLocalizer.json` 词库（词条扩充至 13,566+ 条）。
  3. **装备型号与武器名称双语无损显示 (v4.0 新增)**：在保持底层 SQLite 数据库主键 100% 官方原版以防想定报错的前提下，在 `CMOLocalizer.dll` 与 8 大 HTML 渲染模板中内嵌动态无损标注引擎，对 F-16C、苏-35S、歼-20A、伯克级、055型、AIM-120D、霹雳-15、战斧等主流战机、军舰、导弹及地面雷达设施动态标注中英双语译名（如 `F-16C Fighting Falcon 【F-16C "战隼"战斗机】`），鼠标悬浮与页面阅读无缝对照。

### 3. 战役列表与关卡想定双语汉化 (v4.0 新增)
- 遍历并汉化全部 11 场官方经典与 DLC 战役定义（`Chains of War -> 战争之链`、`Desert Storm -> 沙漠风暴` 等），并在关卡列表中双语显示任务序号与中文译名。
- 遍历并汉化全部 261 个 `.scen` 文件的关卡标题 (`<ScenTitle>`) 与 139 个地理作战区域 (`<ScenSetting>`)，如 `1. 蓝色黎明 (1. Blue Dawn)`、`台湾海峡 (Taiwan Strait)`，使战役选择界面彻底中文化。
- 所有 `.campaign` 与 `.scen` 原始文件均在同目录下保留 `.orig` 备份，支持 100% 无损还原。

### 4. 战役剧情与作战简报正文全量深度翻译 (v4.0 新增)
- 遍历游戏 `Scenarios/` 目录下全部 323 个 HTML 任务简报文件及 182 个内嵌简报的想定文件，通过多线程军事翻译管线，全量翻译 4,885 个长篇叙事段落与背景剧情。
- 中文字符覆盖量从原始的 2,472 字跃升至 **104,813 字**，将复杂的历史战况背景、最高指挥部战役条令、核武器使用授权限制与电磁管制指令全部中文化。
- 原版 HTML 简报均保留 `.html.orig` 备份，随时可通过一键安装器原样还原。

---

## 三、安装与卸载使用说明

### 1. 一键安装器 (`CMO_Chinese_Setup.exe`)
- 双击运行：程序自动扫描注册表并锁定 Steam 游戏目录，点击“一键安装汉化”即可。
- 静默自动化：
  - 安装：`CMO_Chinese_Setup.exe /install`
  - 卸载：`CMO_Chinese_Setup.exe /uninstall`
- 内置自我还原：点击“还原英文原版”可彻底移除注入配置、还原全部 8 个数据库 HTML 模板、还原全部战役简报原始文件，恢复 100% 官方纯净状态。

### 2. 绿色免安装包 (`CMO_Chinese_Localization_Pack.zip`)
- 解压后将全部文件覆盖到游戏根目录即可。

## [2026-10-11] 全量军语深度精修、机翻清洗与实机终审闭环

1. **机翻痕迹彻底清洗与纯正军语替换**：
   - 全量清洗 4,008 个安全 offsets 词条与 exact/partial 词库中生硬的双斜杠备选词与生硬机翻。
   - 重点替换：
     * "接触" / "目标目标接触" -> "目标接触"（如取消虚警、雷达辐射源特征、遗忘目标接触）
     * "载荷/有效载荷" -> "外挂载荷/挂载构型"
     * "突击/攻击" -> "空中突击/对地对海打击"
     * "巡逻/巡视" -> "战斗巡逻/警戒巡逻"
     * "投射物/弹药" -> "弹药基数/武备发射"
     * "装填/重新装填" -> "武器再装填/战备整备"
     * "拦截/拦截机" -> "防空拦截/截击机"
     * "任务想定编辑/飞行计划想定编辑" -> "任务编辑器/飞行计划编辑器"
   - 彻底移除了 14 个高危数据库内部哈希检测字符串，解决数据库哈希校验错误 2，游戏启动及新建想定全程 0 报错。

2. **核心业务窗口实机交互验证**：
   - **主菜单栏**（文件/视图/推演/地图设置与工具/预设视角/单位命令/任务与参考点/目标接触/想定编辑/帮助）100% 纯正军语。
   - **快捷工具栏**（自动交战/手动交战/航线规划/油门/高度/编队编辑/弹药库/航空作业/小艇作业/武备系统/传感器/系统与损管/作战条令/任务编辑）100% 中文自绘图标。
   - **【编辑阵营 (Edit Sides)】窗口**：添加/移除/重命名/条令-交战规则/阵营外交/态势感知等级全中文。
   - **【作战条令与交战规则 (Doctrine / RoE)】窗口**：超视距交战逻辑、绝气推进(AIP)、撤退与重新部署、辐射控制(EMCON)、充电工况等近百项细化条令全中文。
   - **【任务编辑器 (Mission Editor)】窗口**：多任务联合视图/战术总览、空中任务指令(ATO)、起飞时间(Zulu)、目标到达时间(ToT)等全中文。

3. **最终交付物**：
   - `projects\汉化\CMO_Chinese_Setup.exe` (支持一键安装与一键卸载)
   - `projects\汉化\CMO_Chinese_Pack_v3.0.zip` (免安装绿色整合包)
   - 游戏根目录已同步部署就绪。

## [2026-10-11] v3.0 终极全量增强：子菜单全覆盖、战役想定树全汉化与简报引擎升级

1. **主菜单栏 10 个顶级菜单及其全部 397 个子菜单项 100% 汉化与精修**：
   - 全量扫描提取主程序内存与菜单定义共 397 项（覆盖：文件、视图、推演、地图设置与工具、预设视角、单位命令、任务与参考点、目标接触、想定编辑、帮助）。
   - 修复了此前遗漏的带斜杠/括号复合项（如 `Add/Edit Sides` -> `添加/编辑阵营`、`Enable/Disable Autosave` -> `开启/关闭自动存档`、`Show/Hide...` 等共 11 项）。
   - 修复了 2 处因词根匹配产生的叠词现象（如 `切换单位状态面板显示`、`单位条令与交战规则...`）。

2. **战役与想定选择树 (Scenario Browser) 100% 汉化**：
   - 深度梳理了官方 11 部经典战役（《战争之链》、《沙漠风暴》、《北方炼狱》、《赤潮》、《潜艇危机》、《安全核戒备》、《马岛战争》、《克什米尔烽火》、《流沙》、《战火重燃》等）及全部 261 个想定（共 499 个树节点）。
   - 为每个想定定制了纯正中文军事译名（如《蓝色拂晓》、《以眼还眼》、《布比延海战》、《黑金闪电战》、《空海一体战》、《铁腕镇压》等）。
   - 解决了带编号（如 `1. Blue Dawn`、`10. Crackdown`）和带年份（`, 1985`）的动态正则智能匹配。

3. **想定简报与地图探明**：
   - 探明地图全大写国名烘焙在 Stamen 瓦片 PNG 中，矢量地名来自 `WW\Data\Earth\Placenames\*.wwp` 并受限于 WorldWind 的 GDI 字体绑定；
   - 加载想定界面支持动态注入翻译。

## 打包完整性铁律（2026-10-15 用户明确要求）

**打包（`scripts/package.sh`）时必须把全部汉化资产打进去，缺一不可。**

- 用户原话：「打包的时候必须打包齐全」「请确保打包的时候把所有的汉化都带上」。
- 事故背景：v4.1.3 及更早的安装包与绿色包**从未包含** `WW\PlacenameSets\*.wwp`（地图地名集）、
  `Symbols\Menu\*.png`（13 张中文按钮图）、`Launcher\Localization\stringsChinese.resx`，
  于是用户装包后**工具栏按钮与地图仍是英文**；而开发者本机因手工部署过这些文件而显示中文，
  造成「本地全好、用户全英文」的假象。
- 强制要求：
  1. `scripts/package.sh` 末尾的「汉化资产覆盖率断言」必须保持通过，缺失即构建失败，不得绕过。
  2. 新增任何汉化资产时必须五处同步：① 收录到 `assets/`；② `build.sh` 内嵌资源；
     ③ 安装器 `Deploy*` 部署 + 卸载 `Restore*` 还原（覆盖前写 `.orig` 备份）；
     ④ 绿色包携带；⑤ 覆盖率断言清单加入该资产。
  3. 发布前必须实测核对：`grep -a <资源名> build/CMO_Chinese_Setup_v<ver>.exe` 确认内嵌；
     解包 `CMO_Chinese_Pack_<ver>.zip` 核对条目数与各资产数量。
- 当前资产清单：`CMOLocalizer.dll`、`CMOLocalizer.json`、`CMO_Updater.exe`、`Command.exe.config` 补丁、
  `DB\Templates` 8 个、`Scenarios\**\*.html` 323(+323 备份)、`WW\PlacenameSets` 2、
  `Symbols\Menu` 28、`Launcher\Localization` 1、`汉化说明.md`（绿色包共 690 条目）。
- 另一类同类问题：**位图管按钮文字、词典管悬停提示**——替换了按钮位图不等于提示语也汉化了，
  提示语缺失只能补词条（例：`Auto Engage Target→自动交战目标`）。

## 待办：凭据泄露收尾（2026-10-15 发现）

- 已止血（commit 90668f8）：`scripts/translate_worker.py` 密钥改环境变量并入 .gitignore；
  删除 `.workflow/recovery/`（含 GitHub 凭证的压缩检查点）；.gitignore 加三条硬规则。
- **未完成 ①**：用户自行吊销/轮换两处凭据——网关 `newapi.liubaitech.cn` 的 `sk-` 令牌、
  GitHub OAuth/会话恢复令牌（用户 2026-10-15 表示稍后处理）。
- **未完成 ②**：历史重写 + 强推（`git filter-repo --replace-text` + `--invert-paths` 删
  `.workflow/recovery` 与 `scripts/translate_worker.py`），需先做 mirror 备份；
  旧提交 `998c563` 起仍含明文密钥，重写后建议联系 GitHub 支持清理缓存视图。
- 预防建议：加提交前密钥扫描（`sk-`/`ghp_`/`gho_`/`AKIA` 等）并接入发布门禁。

## 密钥使用规范（2026-10-15 事故后确立）

**任何密钥/令牌一律不入库。** 统一用 `scripts/credentials.py` 解析，顺序：
① 环境变量（`CMO_LLM_API_KEY` 等，临时/CI 首选）→ ② `%USERPROFILE%\.cmo-cn\secrets.json`（本机长期）
→ ③ 仓库内 `.secrets.local.json`（已在 .gitignore）。格式见 `docs/credentials.example.json`（仅占位符）。

- 代码里**只允许** `get_credential("llm_api_key", env="CMO_LLM_API_KEY")` 这种写法，不得出现明文。
- `.gitignore` 已覆盖：`.env*`、`.secrets.local.json`、`secrets/`、`*.secrets.json`、`*.pem`、`*.key`、
  `.workflow/recovery/`、`.workflow/**/session-*.md`、`scripts/translate_worker.py`。
- 三重拦截：`verify_all.py` 第 10 项 `secrets`（含 `--history` 全历史）、`package.sh` 打包前硬门禁、
  可选提交钩子（`git config core.hooksPath scripts/hooks`）。
- 万一再次泄露：**先吊销/轮换**（公开仓库分钟级被抓），再清理 tip、重写历史强推（配方见本文件「凭据泄露收尾」节）。
- 更优做法：能在 pi/teammate 运行时用现成凭据完成的工作，就不要在仓库里放密钥。

## 发布后运维要点（2026-10-15 实机踩坑）

1. **Steam 校验/更新会冲掉汉化**：实测发现游戏目录被还原为原版（`Scenarios` 含中文 0/323、
   `.orig` 备份 0 个），原因是 Steam 校验游戏文件完整性 / 游戏更新会还原它管理的文件。
   处置：**重跑一次安装器即恢复**（`CMO_Chinese_Setup.exe` 或绿色包覆盖）。
   注：`.orig` 备份被清掉后，卸载仍可还原（Steam 侧本身就是原版），但重装会重新生成备份。
2. **用户侧升级路径**：≤ v4.1.3 没有更新助手，必须**手动装一次 v4.1.4**；从 4.1.4 起
   才有「开机自检 + 每 6 小时检查 + 托盘常驻 + 检查升级/不再提醒」的自动升级。
3. **密钥三重拦截的启用**：`verify_all.py` 第 10 项与 `package.sh` 已默认生效；
   本机再启用提交钩子：`git config core.hooksPath scripts/hooks`。
4. **发布后自检清单**：`verify_all.py` 需 10/10；`grep -a <资源名> build/CMO_Chinese_Setup_v<ver>.exe`
   核对内嵌；解包绿色包核对条目数（v4.1.4 = 690 条目）；Release 四个资产（安装包/绿色包 + 两个 sha256）
   用 HTTP 206 分片下载验证。
5. **v4.1.5 发布实测（2026-10-15）**：安装包/绿色包均已发布并验证——
   `CMO_Chinese_Setup_v4.1.5.exe`（14 601 216 B，sha256 6a97ebc4…）/ `CMO_Chinese_Pack_4.1.5.zip`（7 074 629 B，
   sha256 a10f4592…），绿色包 690 条目（DB 模板 8 / 地名集 2 / 符号图 28 / 启动器 1）。
   发布后`/releases/latest` = v4.1.5，更新助手可发现；本地已用安装器实装一次，
   数据库查看器点击条目 `RenderedHTMLString` = 34 874 B（修复生效），`verify_all.py` 10/10。

## 缺陷台账与发版回归铁律（2026-10-15 用户明确要求）

**用户原话**：「以后把我这些修 bug，记录在案，哪里有问题。以后每次发版本前都需要对这样问题进行测试，
写测试 SOP，避免再次出现问题。」

三份权威文件（缺一不可）：

1. **`docs/BUG_REGISTRY.md`（台账）**：每个用户可感知问题一条 `R<n>`——现象 / 根因 / 改动位置 /
   用户可见验收 / 回归测试点。编号只增不改（失效时追加新条目并在旧条目注明被取代）。
2. **`scripts/verify_regressions.py`（可执行回归）**：台账用例的自动化实现，分 `--static`（源码/资产/词库断言，
   无需游戏）与 `--live`（实机：数据库查看器八类详情渲染 + 类型框原生 token + 两帧截图无频闪 + 地名集）；
   输出 `build/regression_report.json`。**R0 先校验“游戏目录引擎/词库与本次构建同哈希”**——
   否则回归结论无效（已踩坑：发版前被旧安装包覆盖回不含修复的引擎，实测误报“回归失败”）。
3. **`docs/RELEASE_TEST_SOP.md`（发版 SOP）**：环境校验 → `verify_all.py` 10/10 → `verify_regressions.py`
   全绿 → `package.sh`（内含 static 回归硬门禁 + 密钥门禁 + 资产覆盖断言）→ `publish_release.py`
   （要求 24h 内、同版本、live 全通过的回归报告，否则拒绝上传，仅可 `--force` 并留痕）→ 发布后自检（latest/206/摘要/升级链/实装复测）。

**新增 bug 时的强制三件套**：台账条目 + 回归用例（优先自动化）+ CHANGELOG 条目。
只写其中任一项，下次发版就无法把问题真测住。

## 数据库查看器汉化禁区（2026-10-15 实机事故：详情面板空白）

**铁律：凡是「游戏按文本分派」的控件，绝不允许改写其选项文本；宁可显示英文 token。**

- **事故**：用户打开【数据库查看器】后，点击任何装备条目，下方详情面板一片死灰，
  且点击时中文会闪一下变回英文。
- **根因**：v4.1.3「下拉框全量汉化」把汉化铺到通用路径，`InternalDBViewer.CB_ObjectType`
  的选项从 `Aircraft` 变成「飞机 / 航空器 (Aircraft)」。游戏用该控件 `Text` 做表分派
  （`DisplaySelection → DisplayInfoInDetailsPanel(_SelectedObjectID, _SelectedObjectType)`，
  异常栈里可直接看到），文本一变，所有分支落空 → 不生成详情 HTML（`RenderedHTMLString` = 0）。
  专用路径 `LocalizeDBViewerComboBox` 早有该保护，但通用路径先执行，保护形同虚设。
- **修复**：`IsProtectedDBViewerCombo()` 统一在通用 + 专用路径生效；该控件固定显示游戏原生 token。
- **必须遵守**：
  1. 新增任何下拉框/列表汉化逻辑（含通用路径）时，先确认游戏是否按文本分派；
     数据库查看器对象类型框不得汉化。
  2. 验证口径：打开数据库查看器 → 点击条目 → `RenderedHTMLString` 长度必须 > 0
     （`DB_INSPECT` 查询，修复后 3.4~5.9 万字节）。
  3. 该窗口在辅助 UI 线程创建，`Application.OpenForms` 查不到；必须用 `FindDBViewerForm()`
     （`EnumWindows` + `Control.FromHandle` 回退），否则 `DB_*` 诊断命令会全部报「not open」。
  4. 程序化 `DB_SET_FILTER` 只改 `SelectedIndex`，不触发游戏的用户事件处理器；
     验证筛选联动必须真实点击下拉框并提交，否则会误判「筛选失效」。
  5. 不要连续多次 `CLICK_MENU 数据库查看器`：会堆叠多个查看器实例并让引擎命令通道卡死。
- **证据**：`evidence/live/dbviewer/99_live_repro.png`（故障）vs `103_pane_rendered.png` /
  `140_final_state.png`（修复后 #4515 CH-4B Rainbow 详情页全中文）。

## 安装状态/设置/回退的权威定义（2026-10-16，v4.1.6）

**用户事故原话**：「为什么我还原了也显示已经安装新版本了，这不就是我想回退也回退不了了么，
而且之前的最小化托盘设置啥的全都没有了。更新频率也没有了。」→ 由此确立下列硬约束：

1. **状态真相只能是「部署实证」**：游戏目录内的 `CMOChinese_Installed.json`（版本 + 时间 + 来源 +
   `CMOLocalizer.dll`/`CMOLocalizer.json` 的 sha256）。注册表 `Version` 只反映"最后运行的那个安装包"，
   严禁再作为状态来源（历史事故：还原后仍显示「已安装 v4.1.5」，且任何旧包一装就把它改成旧版本号）。
   没有任何清单时走 `InstallManifest.Adopt()` 自愈（版本回退链：注册表 Version → 已部署资产 `_meta.version` → unknown），
   否则存量 4.1.x 用户与绿色包用户会被误报「未安装」。
2. **设置与安装态必须解耦**：设置存 `%APPDATA%\CMOChinese\settings.json`（**Roaming**，
   避免被 `%LOCALAPPDATA%` 清理逻辑波及），还原/卸载一律保留，只有显式 `--purge-settings` /
   勾选「同时清除我的全部设置」才清。严禁再对设置做 `DeleteSubKeyTree` 式的连坐删除。
3. **回退单位是整个安装包**（`%LOCALAPPDATA%\CMOChinese\packages\`，保留最近 `snapshot_keep` 个），
   不做资产级快照——真实足迹含 Scenarios 323 文件/218MB、Symbols/Menu 158 文件，
   快照引擎资产会得到"旧引擎 + 新资产"混合态。回退后由新版代码补写清单，
   并校验部署 dll == 缓存包内嵌 dll，不一致就拒绝登记。
4. **降级提示必须门控**：仅当 `远端 < 本地` **且本地已缓存该版本包**时才提示「可回退」，
   否则静默（否则跑未发布内测版本的用户每次检查都会被劝着降级）。
5. **版本号五处强制一致**：`VERSION` / 安装器 `CurrentVersion` / 安装器窗口标题 /
   助手 `UpdaterVersion` / 词典 `_meta.version`，由 `verify_all.py` 的 version 门禁把关。
   历史事故：词典戳停在 4.1.4 而版本已是 4.1.5，导致安装包名与包内资产自称版本不符。
6. **共享逻辑只写一份**：设置/清单/缓存/回退/门控都在 `src/shared/CMOShared.cs`，
   编译进安装器与助手两个 EXE；`build/CMOSharedTest.exe` 是不进包的单测宿主
   （`verify_regressions.py --static` 的 R18 要求它 ALL PASS）。
7. **新增回归分层**：`--static`（源码/资产/单测，打包硬门禁）、`--installer`（临时合成游戏目录里
   跑安装/还原/回退/自愈，**不碰真实游戏目录也不需要游戏运行**）、`--live`（需游戏，国际惯例
   发版前必跑）。任何在真实游戏目录/注册表里做的自动化测试都必须自带备份与恢复。

## 注入式汉化的三条硬约束（2026-10-16 输入闪退事故后确立）

1. **输入控件的正文永远不许改写**。游戏 DarkUI 的自定义输入框（`DarkTextBox`）不继承 `TextBoxBase`，
   只看 `TextBoxBase` 会漏判；判定要按「类型名含 TextBox/EditBox、以 Edit 结尾」这类特征兜底。
   实证事故：引擎把用户输入的 `m` 按词典 `m → 米` 改写 → 游戏写回 `m` → 无限拉锯 →
   日志 `UI Controls Translated count … (m -> 米)` 每 2.5 秒涨 50 → `0xc00000fd` 栈溢出闪退。
2. **任何注入进游戏进程的回调都不许让异常逃逸**。异常从 `WndProc`/WPF dispatcher 这类原生→托管回调逃出时，
   CLR 直接 `FailFast`（进程瞬间消失，无弹窗、无堆栈），用户看到的就是「闪退」；
   必须内部消化 + 记录，并常备崩溃取证（`CMOLocalizer_Crash.log`）以便事后定位。
   同理，回写前要有拉锯熔断（同一目标值重复写入即放弃该控件），熔断/封顶要留堆栈证据。
3. **深度闸门只能挂在事件回写路径上**。遍历器（WPF 视觉树、控件树、想定树）的递归深度等于树深度，
   把闸门套在遍历器上会静默漏译深层控件（本轮初版即如此，已回退）；
   闸门的意义是切断「我们的回写→游戏回调→我们再回写」的循环，不是限制正常遍历。

## 战役内剧本名属必译面（2026-10-16 用户实测）

- 战役里显示的剧本名来自 `Scenarios/**/*.campaign` 的 `<Name>` 节点，与 `.scen` 里的 `<ScenTitle>` 是
  **两套数据**；只覆盖 `.scen` 会让「战役里有些剧本名还是英文」。门禁必须同时断言两者的词典覆盖
  （`verify_all.py` 的 `scenario meta` 现为 titles + settings + campaign names 三重覆盖）。
- `Local Video: *.wmv|*.mp4` 是游戏用于找视频文件的字符串，**永远不要翻译**。
- 游戏字符串池（`dict/strings_pool.json`）里的串必须写进 `offsets`（引擎启动时注入池中），
  只写 `exact` 无效——`Resume from save` 就是这样长期显示英文的。
- 词典词条必须幂等：`Steam Workshop → 创意工坊 (Steam Workshop)` 被二次翻译即成叠译
  「创意工坊 (创意工坊 (Steam Workshop))」；凡是可能被重复施加的映射，值里不能再出现源串。

## 发版必须先过统一入库检查（2026-10-16 用户要求）

- 清单：`docs/RELEASE_MANIFEST.md`（要发什么 / 必须先入库什么 / 禁止带上什么 / 发布后自检 / 回滚路径）。
- 一条命令：`python scripts/release_check.py`（`--pre` 为打包前预检）。判定逻辑单点：
  `publish_release.py` 复用 `release_check.check_regression_report`，不要在别处再写一份报告校验。
- 打包门禁（`package.sh` → `verify_regressions.py --static --no-report`）**不得**覆盖
  `build/regression_report.json`；发布前最后一次回归必须是带 `--live` 的那次。

## 词典必须能从源重建（2026-10-17，RV-20261016 落地）

**铁律：`dict/CMOLocalizer.json` 是产物，必须能从 `dict/src/**` + `scripts/build_dict.py` 字节级重建。**
重跑一次构建就悄悄回退已修好的显示串，比构建失败危险得多（旧的「完整」词典会照旧发布）。

1. **写入必须显式 LF**：Windows 文本模式默认把 `\n` 写成 `\r\n`，一次重建就是 **+49 427 字节**的
   纯换行 churn，违反「词典保持 LF、最小 diff、禁止无序格式化」硬约束。落盘必须 `newline="\n"`
   （`scripts/build_dict.py:406-427`），并保持 `indent=1` / `ensure_ascii=False` / 单末尾换行不变。
2. **同一事实只能有一个源**：`legacy_fixes.json` 不得覆盖 `campaign_ui.json` 的正确值
   （历史事故：`legacy_fixes` 按字母序后加载，把工坊页签盖回非幂等的「创意工坊 (Steam Workshop)」，
   等于静默撤销 R20 的叠译修复）；长标签放 `dict/src/labels.json`（`allow_sensitive` 通道，
   绝不被 `is_script_or_schema` 误判丢弃）；人工核验过的偏移注入放 `dict/src/force_offsets.json`，
   **且它有权覆盖 `identifiers` 过滤**（否则 offset 7916 被静默跳过、工坊页签退回英文）。
3. **验收口径**：重跑 `build_dict.py` 必须与已提交词典**字节一致**
   （当前 11 007 317 B / sha256 `2765a8c4…` / CRLF=0 / offsets 3 523 / exact 46 089）。
4. **门禁 R24 把守**：`verify_regressions.py` 的 `R24-static-dict-reproducible` 钉住上述五个前提；
   谁改坏了这条链，`--static`（打包硬门禁）就会红。

## 门禁必须能被证伪（2026-10-17，RV-20261016 落地）

- **每条 gate 都要有负向用例**：一个「永远 PASS」的门禁比没有门禁更糟，它会让人以为已经检查过了。
  实证：简报门禁 `translated >= len(files) - image_only` 在目标为空时是 `0>=0`，永远绿——
  现在空集直接判 FAIL（`scripts/verify_all.py:114-121`）。新增/修改任何 gate 时，必须同时给出
  「什么样的输入会让它 FAIL」的实测证据（空集、零命中、故意破坏后跑一次）。
- **用对外部工具方言不确定的写法时，必须用真实历史验证它真能命中**：历史密钥扫描原用
  `git log -G<regex>`，git 自己的正则方言不认 `\b`/`{20,}`，对「确实存在过凭据改动」的仓库**零命中**
  （而 `git log -S"sk-"` 能命中）——这条 gate 等于不存在，却长期挂着「已启用」的名义。
  已改为按 blob 去重 + `cat-file --batch` 全量扫描并打印覆盖率
  （1 011 对象 → 扫 560 个 / 79MB / 命中 0）。**规则：验证不了它会命中的扫描，就不算扫描。**
- 同理，门禁只能**只读**：脚本可解析性用 `ast.parse`，不用 `compileall`（后者会写 `.pyc`，
  与 R22「不得有无源孤儿缓存」直接冲突）。

## 部署标记必须是元数据标识符，不能用字符串字面量（2026-10-17）

- `R0-static-env` 靠「部署到游戏目录的 `CMOLocalizer.dll` 里含哪些符号」判断它是否包含本轮修复
  （不能用整文件哈希：csc 每次编译都换 MVID/时间戳，哈希必变）。标记必须取**元数据标识符**
  ——方法名/类型名（如 `IsProtectedDBViewerCombo` / `IsTextInputControl` / `InstallCrashGuard`），
  因为它们在 DLL 的元数据表里，ASCII 直接搜得到。
- **不能用字符串字面量**：`#US`（用户字符串堆）里存的是 UTF-16，用 ASCII 搜二进制必然搜不到。
  本轮初版拿 `SCEN_CTRL_TREE` 这类字面量当标记，结果被门禁自己抓出**假红**（引擎明明是对的）。
- 推论：给部署资产加「修复是否在场」的判定时，先在真实二进制上验证 `标记.encode() in blob` 为真，
  再把它写进 `DEPLOY_MARKERS`（`scripts/verify_regressions.py:57-84`）。

## 测试编排：安装器用例会关掉游戏（2026-10-17）

- **`--static --installer` 与 `--live` 必须分两次运行**：安装器用例会调 `CMOShared.KillProcesses()`
  （对 `Command`/`CMO_Updater` 先 CloseMainWindow 再 Kill）——这是安装器的**正确行为**，
  但它与游戏同时跑会让 live 用例在 `L0-game` 假失败（代码没问题，却报「游戏未启动/回归失败」）。
- 判定：`--static`（源码/资产/词库/单测）与 `--installer`（合成游戏目录，不碰真实目录也不需要游戏）
  可以一起跑；`--live`（需要游戏运行）必须单独一次。
  已写入 `docs/RELEASE_TEST_SOP.md:37-38` 与 `docs/RELEASE_MANIFEST.md:47`，
  `release_check.py` 的回归报告校验按此执行。
- 另一条同类坑：R0 会比对「游戏目录的引擎/词库与本次构建是否同哈希」，
  被旧安装包覆盖的环境会得到**假失败**——先跑 `scripts/deploy.sh` 或重装一次再复测。

## 先量再改：perf 类 finding 必须先做微基准（2026-10-17，RV-20261016 落地）

- **评审的性能预期是静态估算，不能当施工图。** 本轮三项「优化」的实测结果：`O-04`
  （安装包程序集/版本查询记忆化）收益在噪声内 **≈ 0**（这些进程存活很短，14MB 读基本命中系统页缓存，
  主导成本是启动与解包）；`O-05`（词库解析单次化）**实测否决并已回退**（估算「省 10-15s」；
  实际解析 10.5MB JSON 只需 读盘 4ms + 解析 51ms，9 次 ≈ 0.5s，**高估约两个数量级**，
  要拿收益得先把 3 个 `verify_*` 改成可导入模块）；`O-01`（页面替换预过滤）问题真实且严重，
  但收益是 **≈5×**（0.67s→0.135s / 2.08s→0.39s），**不是评审估的 1-2 个数量级**。
- **流程规则**：任何 perf 类 finding，先写 10 行以内的微基准量出前后差异，再决定是否动代码；
  收益在噪声内的改动，要么不做，要么明确写成「消除重复工作、无用户可感知加速」。
- 实测数据留档：`evidence/team-review-20261016/outputs/review/landed-results.md`。

## 忽略目录不等于安全（2026-10-17）

- **`.gitignore` 只保护版本库，不保护工作区/产物。** 实证：
  `scripts/__pycache__/translate_worker.cpython-312.pyc` 含明文 `sk-` 令牌，
  因 `__pycache__/` 被忽略而对「只看 `git ls-files`」的密钥门禁完全不可见——它没被提交，
  但打包 / 压缩 / 共享工作区的任何一步都会把它带走。
- **凭据扫描必须覆盖「危险忽略项」**：`__pycache__/*.pyc`、`*.pyc/*.pyo`、`.env*`、
  `*.pem/*.key/*.pfx/*.p12`、`*.log`、`credentials*.json`/`*secret*.json`
  （`scripts/verify_no_secrets.py:38-45`）；扫描面为三层（已跟踪 + 未跟踪 + 危险忽略项），
  跳过项必须打印原因，不得静默 skip。
- 推论：**「没进 git」≠「没出去」**。清理泄漏时先假设它已经离开本机（先吊销/轮换），再清理 tip 与历史。
## 实机测试的真实边界（2026-10-17，RV-20261016 扫描后确立）

三条会让「自动化以为测了、其实没测到」的坑，下次做窗口扫描前先看这里：

1. **用命令行参数带 `.scen` 启动不会真的载入单位**：状态栏恒为「活跃单位数(AU): 0」，
   于是「单位命令」系菜单项（攻击选项/传感器/武器/弹药库/损伤/条令…）被点击后**静默无操作**——
   `CLICK_MENU` 返回 `ok` 但没有新窗体，WinForms 与 WPF 两侧都没有新句柄。
   要覆盖这批窗口，必须先真实点中地图上的单位（`视图 → 选择下一个单位` 与「快速跳转」都不行）。
2. **游戏界面分三套渲染层，扫描要分别覆盖**：
   - WinForms（`Application.OpenForms`）→ 用引擎的 `CTRL_TREE`；
   - WPF → 用 `WPF_DUMP`（大厅 `StartGameMenuWindow` 属这一层，`CTRL_TREE` 完全看不到）；
   - 大厅的 WebView2 面板（如启动时的 Professional Edition 介绍）→ 两者都够不到，审计日志也从不记录，
     属当前机制的盲区。
3. **富文本控件（`DarkRichTextBox`）是引擎的翻译盲区**：`ctrl.Text` 有值但引擎不改写其内容
   （改 Text 会丢 Tab 排版，并触碰 R19 的输入保护不变量）。快捷键窗口整窗英文就属于这一类；
   读它们要用 `CTRL_TREE`（能读到 Text），不要用审计日志（它只记引擎尝试翻译过的串）。

配套工具：`evidence/team-review-20261016/sweep.py`（WinForms 窗口扫描）、`sweep_units.py`（单位命令系，
当前被第 1 条挡住）、产物在 `evidence/team-review-20261016/sweep/`。
4. **窗口内 HTML（WebView2）元素没有 Win32 几何信息，无法用坐标点中**（这是“进不了想定 / 点不动
   阵营选择按钮”的真因）：`CTRL_TREE` 在 `ChooseSide` 这类窗体上只能看到窗体本身、看不到子控件，
   `WPF_DUMP` 也看不到，因为那些按钮是 HTML。靠截图目测像素不可靠（截图会缩放）。
   **正解**：让引擎在进程内操作该 WebView2（`CoreWebView2.ExecuteScriptAsync` 导出 DOM / 点击按钮），
   不依赖前台与坐标。附带事实：`GuiDriver focus` 返回 `Focus result: False`、
   合成按键落不进游戏输入框（所以我改用 WM_CHAR）；但鼠标点击对可枚举窗口是有效的，
   不要把“前台被拒”当成所有点不动的统一解释（我先前这么写过，已纠正）。
5. **live 套件收尾会关掉全部窗口，之后游戏会自行正常退出**（无崩溃日志、事件日志无错误）：
   所以每一段交互前都要先 `restart_game`，不要在 live 跑完后接着做对话式探查。
6. **`2 selectable sides` 属未定位盲区**：不在 WinForms 控件树、不在 WPF 控件树、也不在简报文本层
   （三条路都试过），按“同一目标不超三次”的纪律停止追查，别再重复试。
7. **想定进不去就会让一大批窗口「假死」**：`CLICK_MENU` 对单位命令系菜单项返回 `ok` 但什么都不发生，
   根因是活跃单位数为 0（没进想定）。可靠的进入方式是 `CLICK_BUTTON 进入想定`
   （命中 `ChooseSide.Button1`），**不要**靠鼠标点像素——那个按钮没有可枚举的 Win32 几何信息，
   截图目测坐标点不中。
8. **本轮新增探针命令**（产品代码，已随引擎部署）：
   - `CTRL_TREE [窗体过滤]`：导出控件树（`d/t/n/v/b/text`），找控件名与文本用；
   - `WEBVIEW_TEXT <窗体过滤>`：读该窗体 WebView2 的 `document.body.innerText`；
   - `WEBVIEW_JS <窗体过滤> <js>`：在 UI 线程上 `ExecuteScriptAsync` 并返回结果。
   注意：**WebView2 的控件属性与脚本调用必须在 UI 线程上碰**，在后台线程读 `CoreWebView2`
   会抛 `TargetInvocationException`（已实测，命令已按此实现）。
9. **界面文本可能有多种宿主，定位顺序建议**：先 `CTRL_TREE` 找控件名（多数标签/按钮在这里，
   例如 `DarkLabel lblSideNumber` 承载 `2 selectable sides`）→ 再看 `WPF_DUMP` →
   最后才是 `WEBVIEW_TEXT/WEBVIEW_JS`。**先定位宿主再改规则**，否则规则会加在错的层（我踩过）。
10. **熔断计数只能统计「真正的写入」**：`AllowWrite` 的语义是「同一目标值被重复写入超限 = 与游戏拉锯」，
    所以调用点必须只在**确实要写**时触发。反面教材（2026-10-17 实测）：`TranslateComboItems` 在入口无条件调用，
    导致**一次都没写入**的组合框被反复访问后永久静音——游戏日志里累计 269~311 次
    `WRITE LOOP BLOCKED ComboBoxItems`（CB_ScenOrder 28 次…），表现就是下拉项大面积英文。
    正确做法：先算出待改项（空则不调用、不计数），指纹用「本次待写入的目标值」，
    这样写成功即不再计数、只有游戏回滚（真拉锯）才累加。
11. **查界面英文必须同时查「控件文本」与「下拉项」**：只采控件当前 `.text` 会漏掉「下拉打开后才看得见的英文」，
    必须用 `DUMP_COMBOS` 导每个 ComboBox 的全部 Items；变化判定要按「(窗体,控件名) 身份 + 条目内容」，
    不能按窗体名（窗口可能在已有窗体里打开/重开）。另外，**下拉项能否译出来还受写拉锯熔断影响**
    （见第 10 条），所以「条目是英文」不一定等于「缺词条」，先看熔断日志再判断。
12. **多义词不要加全局 exact 条目**：`All`/`Normal`/`Major`（别处是军衔「少校」）这类词加进词典会在其它界面
    误译；专有名词（OSM 地图样式名 `Bright`/`Positron` 等、版权行）也一样保留英文。
13. **「数值 + 单位」形态不需要翻译**（2026-10-18 用户明确裁决）：如 `40000 ft`、`545 节`这类**数字加单位**的值
    保留原样即可，**不计入缺口**、不要为它补词条；判定这类英文不是缺陷。
    （注意：界面**标签**与**单位词所在的整句**仍照旧翻；被排除的只是「纯数值+单位」这种值。）
14. **数据库与记录名汉化架构（R33 确立）**：
    - DB 内容分三层：① 描述正文（`dict/src/db_descriptions.json`，16.3k条，排除引文后 100% 汉化）；② 枚举值（`db_enums.json` + `db_enums_extra.json`，769条新增）；③ 记录名（`db_record_names.json`，9.9k条武器/挂载/挂具名称，纯代号保留英文）。
    - 运行时复合串规则：WRA 复合、自主等级、座舱世代示例、航程剖面变体、数量前缀 (`\d+x `) 必须在引擎层通过结构拆分分别翻译，不得依赖整串枚举。
    - 门禁守卫：`verify_regressions` 中的 `R33-static-db-records` 硬断言三表规模与代表串。

## 版本双号与游戏构建号（2026-10-17 用户明确要求，R31 事故后确立）

1. **发布物必须同时写清两个号**：① 本补丁版本（`VERSION`）；② **适配的游戏内部版本**（构建号，仓库根 `GAME_BUILD`）。
   落点四处：安装器标题/副标题、更新助手提示、发版说明正文（`publish_release.py` 自动在首行写「适配游戏内部版本：X」）、
   `README.md`（绿色包里即 `汉化说明.md`）。`verify_all.gate_version` 校验单一来源与三处引用一致。
2. **游戏更新（构建号变化）= 我们升次版本**：Steam 推送新构建后，次版本号 +1、修订位归零（例：4.1.8 → 4.2.0）并重新适配；
   具体数字由用户确认后写入，不要自行拍定。
3. **构建号是硬事实，只能从游戏里取**：标题串 `Command: Modern Operations v1.10 - Build <n>` 是唯一可靠来源。
   引擎已有哨兵（`NoteGameBuild`）：启动时把实际构建号与安装清单里的 `game_build` 比对，不一致即写日志告警。
   引擎代码**不得写死**构建号（旧代码写死 `1900.20` 正是本次事故的一部分）。
4. **游戏更新后的处置顺序**：① 先确认构建号；② 判断注入点/偏移是否需要重做（引擎日志会打
   `CoreDecryption: …`，失败必报；只读探针 `POOL_DUMP` 可在几分钟内复核全部偏移）；③ 补新版新增文本；
   ④ 全门禁 → 打包 → 发布（打包/发布仍需用户授权）。
