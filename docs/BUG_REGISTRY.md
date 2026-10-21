# 汉化问题台账（BUG REGISTRY）

> 用途：**每一个用户反馈过的问题都要在这里留案**——现象、根因、改动位置、用户可见验收、回归测试点。
> 发版前必须按 `docs/RELEASE_TEST_SOP.md` 对台账里所有 `回归` 项逐条测试；
> 能自动化的用例由 `scripts/verify_regressions.py` 执行（`static` 子集进 `package.sh` 硬门禁，`--live` 子集发版前必跑）。
>
> 编号规则：`R<序号>`，**只增不改**；修复后若用例失效或需调整，追加新条目并在旧条目注明「已被 Rx 取代」。
> 新增问题必须同时：① 写台账条目；② 落一条可执行的回归用例（优先写进 verify_regressions.py）；
> ③ 在 `docs/CHANGELOG.md` 对应版本段落说明。

| 编号 | 版本 | 用户可感知现象 | 回归用例（自动化） | 状态 |
|---|---|---|---|---|
| R1 | 4.1.5 | 数据库查看器详情面板空白 + 中文闪成英文 | `static` CB_ObjectType 保护 + `live` 八类详情渲染/token/无频闪 | 已修 |
| R2 | 4.1.4 | 装了汉化地图仍然是英文 | `static` 地名集资产 + `live` 中文标签 | 已修 |
| R3 | 4.1.4 | 任务编辑器载荷名括号内英文 | `static` 词库/术语断言（verify_terminology） | 已修 |
| R4 | 4.1.4 | 右侧单位状态面板中英频闪 | `live` 两帧截图 md5 一致 | 已修 |
| R5 | 4.1.3 | 工具栏按钮文字仍是英文 | `static` 中文按钮图覆盖引擎期望文件名 | 已修 |
| R6 | 4.0.0 | 加载想定报 `Data Source cannot be empty` | `static` offsets 无 SQL 片段（verify_release） | 已修 |
| R7 | 4.0.0 | 快捷键显示 `Shift + 在...内` | `static` 词库机翻残留（verify_terminology） | 已修 |
| R8 | 4.0.0 | 数据库查看器点击条目详情空白（首例） | `static` 禁止改写 ComboBox/ListControl/TextBoxBase 的 .Text | 已修（R1 为其复发） |
| R9 | 4.1.3 | 教程/简报正文仍英文、WebView2 注入崩溃 | `static` 简报/教程覆盖门禁 | 已修 |
| R10 | 4.1.4 | 消息日志多行教程消息漏译 | 手动抽样（日志窗口） | 已修 |
| R11 | 4.1.2 | 数据库筛选下拉框汉化后筛选失效 | `live` 国家/子类型真实交互联动 | 已修 |
| R12 | 4.1.4 | 工具栏「自动交战/手动交战」提示词未汉化 | `static` 词条存在性 | 已修 |

---

## R1 数据库查看器详情面板空白 + 点击后中文闪成英文（v4.1.5）

- **现象**：打开【数据库查看器】→ 列表有数据、筛选可用，但点击任何装备条目，下方详情面板（WebView2）一片死灰；
  点击时中文会闪一下变回英文。
- **根因**：v4.1.3「下拉框全量汉化」把汉化铺到**通用路径**
  （`TranslateControlTree` → `LocalizeComboBoxSafe` → `TranslateComboItems` → `TranslateDBComboItem`），
  而 `InternalDBViewer.CB_ObjectType` 是**游戏按控件文本分派**的控件：
  异常栈 `DarkListView.OnMouseDown → DisplaySelection → DisplayInfoInDetailsPanel(_SelectedObjectID, _SelectedObjectType)`。
  选项被改写成「飞机 / 航空器 (Aircraft)」后所有分支落空 → 不生成详情 HTML（`RenderedHTMLString` = 0）；
  巡检每轮重译同一控件造成中英闪烁。专用路径原有的保护被通用路径绕过（通用路径先执行）。
- **修复位置**：`src/engine/CMOLocalizer.cs`
  - `IsProtectedDBViewerCombo()`：通用路径 `LocalizeComboBoxSafe` 与专用路径 `LocalizeDBViewerComboBox` 统一生效；
  - `FindDBViewerForm()` / `IsDBViewerForm()`：`EnumWindows` + `Control.FromHandle` 回退（该窗体在辅助 UI 线程创建，
    `Application.OpenForms` 查不到），替换 6 处重复查找，恢复 `DB_*` 诊断命令。
- **用户可见验收**：数据库查看器点击条目 → 详情页显示中文「概述说明 / 常规数据 / 装备大类 / 机体尺寸 …」并带配图。
- **回归测试点**（`verify_regressions.py`）：
  - `static`：源码中 `IsProtectedDBViewerCombo` 必须在两条路径都被调用（防止有人删掉保护）；
  - `live`：八类对象（Aircraft/Ship/Submarine/Weapon/Facility/Satellite/Ground Unit/Sensor）逐个切换后点击条目，
    `DB_INSPECT.html_len` 必须 > 阈值（Aircraft ≥ 20000，其余 ≥ 5000）；
  - `live`：`CB_ObjectType` 文本必须是游戏原生 token（`Aircraft`/`Ship`/…），出现中文即回归失败；
  - `live`：同一稳定态两帧截图 md5 必须一致（无中英频闪）。
- **证据**：`evidence/live/dbviewer/99_live_repro.png`（故障）vs `103_pane_rendered.png`、`140_final_state.png`（修复后）；
  发布后实机：`evidence/live/dbviewer/200_release_v415_installer_live.png`（html_len 34 874）。

## R2 装了汉化地图仍然英文（v4.1.4）

- **现象**：用户装包后地图上国名/海域/城市名仍为英文。
- **根因**：安装器/绿色包从未分发 `WW/PlacenameSets/{Synoptic,Cities}/0000_0000.wwp`
  （World Wind 地名集是地图标签数据源），引擎再汉化也无效。
- **修复位置**：`src/installer/CMO_Chinese_Setup.cs`（`DeployPlacenames` / `RestorePlacenames`，带 `.orig` 备份）、
  `scripts/package.sh`（绿色包携带 + 资产覆盖断言）、`assets/placenames/*.wwp`。
- **用户可见验收**：装包后地图国名/海名/城市名为中文；卸载可还原。
- **回归测试点**：`static` 两个 `.wwp` 存在且含汉字、安装器内嵌引用存在；`live` 地图标签为中文（人工/截图）。

## R3 任务编辑器「可用」列表括号内载荷名英文（v4.1.4）

- **现象**：行文本「苏法/风暴 #1 (Air Superiority BVR) (拉马特戴维)」整串查表落空、含中文被跳过 → 括号里永远英文。
- **根因**：复合串无整串词条；`ContainsChinese` 早退逻辑把混排串整体放过。
- **修复位置**：`src/engine/CMOLocalizer.cs`（`TranslateString` 早退前按 `()`/`[]` 拆出纯英文片段单独翻译再拼回）、
  词库补 `[UNAVAILABLE]`/`A/A:`/`Light 202`/`Radar`。
- **回归测试点**：`static` `verify_terminology.py`；手动抽样任务编辑器窗口。

## R4 单位状态面板中英频闪（v4.1.4）

- **现象**：右侧面板 航速/深度/阵营 等文本在中文与英文之间反复跳。
- **根因**：仅靠 UI 轮询补翻译（600ms/1500ms）+ WPF 数据绑定持续回推英文 → 中间窗口期被画到屏幕上。
- **修复位置**：`src/engine/CMOLocalizer.cs`（记录实时 WPF 元素 + `CompositionTarget.Rendering` 每帧绘制前重译，
  `SetCurrentValue` 保留绑定；每 8 帧全量补扫）。
- **回归测试点**：`live` 同一稳定态两帧截图 md5 一致（历史上做过 30 帧/60s 采样 0 跳变）。

## R5 工具栏按钮文字仍是英文（v4.1.3）

- **现象**：顶部 13 个战术按钮仍是英文（文字画在位图里）。
- **根因**：`Symbols/Menu/*.png` 中文按钮图未随包分发。
- **修复位置**：`assets/symbols_menu/`、安装器 `DeploySymbolsAndLauncher`、`scripts/package.sh` 资产断言。
- **回归测试点**：`static` 引擎期望的按钮图文件名（当前 13 张）在 `assets/symbols_menu/` 与绿色包中**零缺失**；
  `live` 引擎日志出现「Successfully pre-loaded N localized button images」。

## R6 加载想定报 `Data Source cannot be empty`（v4.0.0）

- **现象**：点「加载选定项」弹 SQLite 错误。
- **根因**：词库 `offsets`（引擎字符串池注入）里混入 `Data Source=` 等 SQL 拼接片段，被翻译后连接串失效。
- **修复位置**：`dict/CMOLocalizer.json` 清洗 + 硬性不变式「只有 display 类字符串可进 offsets」；
  `scripts/verify_release.py` 增加断言。
- **回归测试点**：`static` `verify_release.py`（offsets 不得含 SQL/连接串片段）。

## R7 快捷键显示 `Shift + 在...内`（v4.0.0）

- **现象**：`Shift + Insert` 被单词字典把 `Ins` 当 `In` 翻译。
- **根因**：单词级机翻误伤键名。
- **修复位置**：`dict/src/legacy_garbage_cleaned.json`、`scripts/verify_terminology.py` 门禁。
- **回归测试点**：`static` 术语门禁扫描「在...内」等机翻怪句。

## R8 数据库查看器点击条目详情空白（首例，v4.0.0）

- **现象**：与 R1 相同（第一代）。
- **根因**：`TranslateControlText` 无差别 `ctrl.Text = 中文`，把 `ComboBox.Text` 改成不在选项里的中文 →
  WinForms 把 `SelectedIndex` 重置为 -1 → 游戏 `switch (CB_ObjectType.Text)` 全部落空。
- **修复位置**：`TranslateControlText` 明确跳过 `ComboBox` / `ListControl` / `UpDownBase`。
- **回归测试点**：`static` 源码断言（三类型跳过分支必须存在）；`live` 同 R1。

## R9 教程/简报正文仍英文、WebView2 单引号注入崩溃（v4.1.3）

- **现象**：教程消息与想定简报部分段落英文；修复过程中曾因 JS 注入用单引号导致 WebView2 渲染崩溃。
- **根因**：内嵌 `<ScenDescription>`/教程正文未进词库；注入脚本未做引号转义。
- **修复位置**：`scripts/localize_html_files.py`、词库补录、`TranslateWebView2` 注入脚本转义。
- **回归测试点**：`static` `verify_all.py` 的 briefings 门禁（中文文件数/CJK 字数不下降）+ 教程覆盖门禁。

## R10 消息日志多行教程消息漏译（v4.1.4）

- **现象**：日志里多行教程消息只有首行中文。
- **根因**：整段查表落空；撇号形态（`'` vs `’`）不一致。
- **修复位置**：`src/engine/CMOLocalizer.cs`（逐行翻译 + 撇号容错）。
- **回归测试点**：手动抽样消息日志窗口（历史报告 `docs/TUTORIAL_REGRESSION_V413.md`）。

## R11 数据库筛选下拉框汉化后筛选失效（v4.1.2）

- **现象**：国家/阵营、子类型下拉框汉化后，选值不再过滤列表。
- **根因**：直接改写绑定下拉框的显示值/`Text` 破坏游戏过滤键。
- **修复位置**：`TranslateComboDisplayColumn`（只译显示列，保留 ID 键）。
- **回归测试点**：`live` 真实交互（打开下拉框 → 提交）后列表内容必须变化
  （例：国家=中国 → 出现 CH-4B/CH-5/CH-6 Rainbow 等中国平台）。
  注意：**程序化设置 `SelectedIndex` 不触发游戏的用户事件处理器**，用它验证会误判「筛选失效」。

## R12 工具栏提示词未汉化（v4.1.4）

- **现象**：`自动交战`/`手动交战` 按钮悬停提示仍是英文。
- **根因**：ToolTip 词条未入库。
- **回归测试点**：`static` 词条存在性（`dict/CMOLocalizer.json` exact 含对应英文源串）。

## R13 还原后仍显示「已安装 v4.1.5」，无法回退旧版本（v4.1.6）

- **现象**：用户点「一键还原原版英文」后（或想回退旧版时），安装器/助手仍显示「已安装 v4.1.5」；
  想装回旧版本也回退不了。
- **根因**：
  1. 「已安装什么版本」只存在 `HKCU\Software\CMOChinese\Version` 这一个字符串里，且**任何安装包**
     安装后都会把它写成**该安装包自己的** `CurrentVersion`（`CMO_Chinese_Setup.cs` `WriteRegistrySettings`），
     与真实部署的资产无关；
  2. 更新助手只查 `releases/latest` 且比较用 `>`，永远不会提示「有更旧的版本」；
  3. 安装器只有「一键安装 / 一键还原英文」，没有回退入口。
- **修复位置**：游戏目录内新增 `CMOChinese_Installed.json` 安装清单（版本 + 时间 + 来源 + 引擎文件 sha256）
  作为唯一状态真相；`CmoSettings.RegistryInstalledVersion()` 与清单 `Adopt()` 自愈只作迁移期回退；
  `VersionStore` 缓存安装包 + `Rollback.Run()` 提供回退；安装器「回退到上一版本 vX」按钮与
  助手托盘「回退到上一版本…」共用同一实现。
- **用户可见验收**：还原后立刻显示「原版英文环境（未安装汉化补丁）」；装任何版本包后显示**该包**的版本与时间；
  有本地缓存时可直接回退并显示回退后的版本。
- **回归测试点**：`installer` 模式 `R13-install-manifest`（清单版本与磁盘哈希一致）、
  `R13-uninstall-manifest`（还原后清单消失）、`R13-list-versions`；`R15-rollback`（回退后清单=目标版本
  且部署 dll == 缓存包内嵌 dll）、`R15-rollback-requires-cache`（无缓存必须拒绝且清单不变）。

## R14 还原后更新设置全部丢失（v4.1.6）

- **现象**：用户反馈「之前的最小化托盘设置啥的全都没有了，更新频率也没有了」——还原一次后，检查频率、
  提醒方式、镜像、开机自启偏好全部回到默认。
- **根因**：设置与安装态共用同一个注册表键，而 `CleanupUpdater()`（还原路径）执行的是
  `Registry.CurrentUser.DeleteSubKeyTree(@"Software\CMOChinese")`，一次删掉整个键。
- **修复位置**：设置正文迁到 `%APPDATA%\CMOChinese\settings.json`（`CmoSettings`，含旧注册表一次性迁移、
  原子写、损坏文件备份为 `.bad`）；还原只清安装态（清单 + Run 键 + 助手 exe + 注册表 `Version`），
  设置一律保留；要清除必须显式勾选「还原时同时清除我的全部设置」或 `--purge-settings`。
- **用户可见验收**：设 3 小时检查 + 弹窗提醒 → 还原 → 重装 → 仍然是 3 小时 + 弹窗提醒。
- **回归测试点**：`installer` 模式 `R14-uninstall-keeps-settings`、`R14-purge-explicit`；
  `static` `R18-static-selftest`（设置原子写往返/损坏回退/注册表迁移）。
  另：`scripts/test_installer_ui.py` 会实机改频率/提醒方式并断言 `settings.json` 变化。

## R15 回退会得到「旧引擎 + 新资产」混合态（v4.1.6，设计期拦截）

- **现象（设计评审发现）**：若按"快照 dll+json"实现回退，回退后 Scenarios(323 文件/218MB)、Symbols/Menu
  (158 文件)、地名集、DB 模板仍是新版本的 → 得到一个版本号对不上、资产互不匹配的混合安装。
- **根因**：安装足迹远大于引擎资产；且旧版安装器不写新清单。
- **修复位置**：回退单位改为**整个安装包**（`%LOCALAPPDATA%\CMOChinese\packages\`，保留最近 `snapshot_keep`
  个），由新版代码执行 `缓存包 --install --silent` 后**补写清单**，并校验部署的 `CMOLocalizer.dll`
  sha256 == 该包内嵌的 dll sha256，不一致则拒绝登记清单（避免混合态被记成"正常"）。
- **用户可见验收**：回退后安装器显示回退后的版本；文件与缓存包一致；再升级可正常回到新版本。
- **回归测试点**：`installer` 模式 `R15-rollback`（含 dll 一致性）、`R15-rollback-requires-cache`。

## R16 「可回退」提示会骚扰装了新版本的用户（v4.1.6，评审 P0 拦截）

- **现象（评审发现）**：把比较从 `>` 改成"不相等就提示"后，如果本地跑的是**比线上更新**的版本
  （未发布/内测），每次检查更新都会提示用户"回退到线上那个更旧的版本"，属于反向骚扰。
- **根因**：缺少"本地是否真的有那个版本的安装包"这一前置条件。
- **修复位置**：`ReleaseGate.ShouldOfferRollback(local, remote, hasCachedRemote)`：仅当
  `remote < local` **且** 本地已缓存该版本安装包时才提示回退；否则静默（只写日志）。
  助手 `--check` 在"本地更高但无缓存"时给出明确说明而不是升级/回退流程。
- **用户可见验收**：本地等于线上 → 静默；本地更高且无缓存 → 不提示回退（说明原因）；
  本地更高且已缓存 → 提示「发现可回退版本 vX」。
- **回归测试点**：`static` `R16-static-gate`（共享层门控真值表 + 空输入）、
  实机 `scripts/test_updater_live.py` 的四条 `--check` 路径（有更新/已最新/可回退/门控）。

## R17 存量 4.1.x 用户与绿色包用户被误报「未安装」（v4.1.6）

- **现象（评审发现）**：状态改成只读清单后，用旧版本装过的用户（没有清单文件）会看到
  「原版英文环境（未安装汉化补丁）」；只解压绿色包的用户同样没有清单。
- **根因**：清单是新增文件，历史安装不会凭空产生它。
- **修复位置**：`InstallManifest.Adopt(game, fallbackVersion)` 自愈：引擎文件存在但清单缺失时，
  按当前文件重新生成清单（`source="adopted"`），版本回退链 = 调用方给的注册表 `Version`
  → 已部署资产的 `_meta.version` → `unknown`；安装器打开、助手启动/检查时自动执行，
  并可用 `--adopt "<game>"` 手动触发。绿色包说明（README → 包内 `汉化说明.md`）已写明。
- **用户可见验收**：旧版用户打开安装器显示「已安装完整中文汉化补丁（已登记当前状态，v4.1.5）」
  而不是「未安装」；执行一次 `--adopt` 后 `--list-versions` 显示该版本且 `source=adopted`。
- **回归测试点**：`installer` 模式 `R17-adopt-selfheal`；`static` `R17-static-pack-readme`。

## R18 状态机缺少可执行单测（v4.1.6）

- **现象（评审发现）**：设置/清单/包缓存/回退这些新逻辑如果没有单测，后续改动很容易悄悄破坏，
  而实机用例（需游戏）太重，发版前不一定跑得起来。
- **修复位置**：`src/shared/CMOSharedTest.cs`（测试宿主，`build.sh` 编译为 `build/CMOSharedTest.exe`，
  不进入安装包）：设置首次写/覆盖写/损坏备份/注册表迁移、清单往返 + Modified + Clear、Adopt 需要完整引擎文件、
  包缓存 Prune 保留最近 N + Previous、安装包版本戳解析（文件名优先 + 无版本号时回退内容戳）、
  门控真值表；全部在系统临时目录内运行（不碰用户设置与真实游戏目录）。
- **回归测试点**：`static` `R18-static-selftest`（要求 ALL PASS，退出码 0）。

## R19 创建任务/搜索剧本时输入一个字符即闪退（v4.1.7）

- **现象**：用户实测——创建任务时在任务名框输入 `m` 就闪退；「搜索剧本」（加载想定）搜索框输入 `m` 也闪退。
  表现为进程瞬间消失（无报错弹窗）。
- **根因（有实证）**：
  1) 事件日志（Application 1000/1001）：`Command.exe` 于 2026-10-14 20:16/20:18 以 `System.StackOverflowException`
     崩在 `System.Windows.Forms.ni.dll`；于 2026-10-15 12:30/12:38/12:54/12:55 以
     `Environment.FailFast` 终止（12:54 那条 faulting module = `PresentationFramework.ni.dll`）——两者都是
     「异常/递归从注入进程的托管回调里逃逸」的签名。
  2) 本机复现 + 引擎日志铁证：在「加载想定」搜索框输入 `m`，`CMOLocalizer.log` 出现
     `UI Controls Translated count: 3700 (Sample: m -> 米)`，每 2.5 秒 +50，一路涨到 5900，
     紧接着 15:11:12 进程以 `0xc00000fd`（栈溢出）消失。
  3) 代码实证：`TranslateControlTextInner` 只对 `TextBoxBase` 做「聚焦即跳过」；游戏 DarkUI 的自定义输入框
     （搜索想定框、任务名框）**不继承 TextBoxBase**，落进通用分支被当普通标签翻译；词典里有 `m → 米`
     （单位换算）这类短词条，于是引擎把用户正在输入的 `m` 改写成「米」，游戏再写回 `m`，形成无终止拉锯。
- **修复位置**：`src/engine/CMOLocalizer.cs`
  - `IsTextInputControl()` + 在 `TranslateControlTextInner` 中提前返回：输入控件正文一律不改写（只翻水印）；
  - 组合框聚焦或下拉展开时不改 `Items`；聚焦中的 WPF `RichTextBox` 不动其文档；
  - `AllowWrite()` 回写拉锯熔断：同一控件被反复写同一个目标值超过 6 次即永久放弃该控件并打印堆栈；
  - `EnterMutation()` 深度闸门（上限 8）+ 堆栈留证，只挂在游戏事件回写路径；
  - `InstallCrashGuard()`：`AppDomain.UnhandledException` / `Application.ThreadException` / WPF 钩子，
    完整堆栈写入游戏目录 `CMOLocalizer_Crash.log`，仅当堆栈含本引擎帧时才吞掉异常。
- **用户可见验收**：在任务名框/搜索想定框输入 `m`、中文都不再闪退；输入内容保持原样；若仍有异常，
  游戏目录出现 `CMOLocalizer_Crash.log` 供定位。
- **回归测试点**：`static` 的 `R19-static-input-guard`（断言输入控件早退、崩溃取证、熔断、深度闸门均在位）；
  `static` 的 `R0-static-env` 把 `IsTextInputControl`/`InstallCrashGuard` 纳入部署标记，防止旧引擎假绿；
  `live` 的 `L0/R1-*` 保证加固没有让翻译回退。

## R20 战役里部分剧本名与「系列战役」窗口标签仍是英文（v4.1.7）

- **现象**：用户实测——新版里战役内有些剧本名又变回英文（「沉默的部队」整场如此）；
  「系列战役」窗口有 `Select one of the campaigns…`、`Resume from save`、`Delete selected save` 三个英文标签，
  工坊页签还叠译成「创意工坊 (创意工坊 (Steam Workshop))」。
- **根因**：
  1) 战役内剧本名来自 `Scenarios/**/*.campaign` 的 `<Name>`，共 154 条去重值中 **25 条从未入词典**
     （`The Silent Service` 16 条全缺、`Desert Storm` 的 SCUD Hunting、`Northern Inferno` 的
     The Grey Ghost from the East Coast，另 6 条是 `Local Video: *.wmv/mp4` 文件名，**不得翻译**）。
  2) `Resume from save` 属游戏字符串池（offset 752572），引擎启动时把池内英文注入，只补 `exact` 不够，
     必须同时补 `offsets`。
  3) `Steam Workshop` 词条非幂等（`创意工坊 (Steam Workshop)`），被二次翻译即叠成
     「创意工坊 (创意工坊 (Steam Workshop))」。
  4) 想定浏览器标题是复合串 `[NOT LICENSED] <标题>, <年份>`，整串查表必然落空。
- **修复位置**：`dict/CMOLocalizer.json`（19 条剧本名 + 3 条窗口标签 + 工坊幂等与清理项 + 2 条 offsets 注入）、
  新增 `dict/src/campaign_ui.json`（精选批次，保证重建不丢）、`src/engine/CMOLocalizer.cs`（复合标题拆分规则）、
  `scripts/verify_all.py`（scenario meta 门禁纳入 `<Name>`）。
- **用户可见验收**：系列战役窗口三个标签为中文；战役内剧本名全中文；工坊页签显示「创意工坊」；
  想定浏览器标题显示「[未授权] 航母杀手, 1985」。
- **回归测试点**：`static` 的 `R20-static-campaign-names`（战役剧本名全覆盖断言 + 工坊叠译自愈断言）；
  `verify_all.py` 的 `scenario meta` 门禁（`campaign names N/N`）；实机抽查「加载想定 → 沉默的部队 → 航母杀手」。

## R21 缺少「发版打包清单」与统一入库检查（v4.1.7）

- **现象**：要发版时没有一份权威清单说明「要发什么、必须先入库什么、禁止带上什么」；
  门禁散落在 `package.sh`/`publish_release.py`/`verify_*` 里，靠记忆执行，容易漏项。
- **根因**：发版知识与判定逻辑没有单一入口，且打包门禁曾覆盖 live 回归报告导致发布校验误报（见 R18 相关的
  `--no-report` 修复）。
- **修复位置**：`docs/RELEASE_MANIFEST.md`（清单）+ `scripts/release_check.py`（统一入库检查）+
  `scripts/publish_release.py`（复用 `release_check.check_regression_report`）+ `docs/RELEASE_TEST_SOP.md`（接入顺序）。
- **用户可见验收**：发布前一条命令 `python scripts/release_check.py` 即可看到逐项 PASS/FAIL。
- **回归测试点**：`static` 的 `R21-static-manifest`（清单与脚本存在、互相引用、SOP 引用二者）。

## R22 明文凭据藏在被忽略的字节码缓存里（v4.1.8，未发布）

- **现象（仓库/发布安全，游戏界面无变化）**：工作区里躺着一个含明文 `sk-` 令牌的字节码缓存
  `scripts/__pycache__/translate_worker.cpython-312.pyc`（它连 `.py` 源文件都不存在）；
  而当时唯一的密钥门禁只看 `git ls-files`，对它**完全不可见**。该文件未被提交，
  但任何「打包 / 压缩 / 共享整个工作区」的动作（绿色包、归档、临时镜像）都会把令牌带出去。
- **根因（有实证）**：
  1) `__pycache__/` 在 `.gitignore` 里（2026-10-15 凭据处置 commit `90668f8` 的刻意决定），
     `git ls-files` 永远列不到它 —— 评审 `DG-10`：`verify_no_secrets.py` 的 `tracked_files()` 只看
     `git ls-files`，使含明文 `sk-` 令牌的 `.pyc` 对门禁完全不可见；属评审 `COR-006` / `RC-4`「门禁不检查自身」。
  2) 该 `.pyc` 出自评审 `COR-004` 的 `scripts/batch_translate_chunks.py:11`：它 import 的模块源码不存在
     （只在 `.pyc` 里），删除前后都必然 `ImportError`；评审 `R-04` 已明确否决「把 `translate_worker.py`
     纳入源码管理或从 `.pyc` 恢复」——那会让明文令牌直接入库。
- **修复位置**：
  - 删除 `scripts/__pycache__/translate_worker.cpython-312.pyc`（不改变任何现存链路）；
  - `scripts/verify_no_secrets.py:9-16,38-42,54-70`：扫描范围由「只看 `git ls-files`」扩为**三层**——
    已跟踪 + 未跟踪（`--others --exclude-standard`）+ **危险忽略项**（`__pycache__/*.pyc`、`*.pyc/*.pyo`、
    `.env*`、`*.pem/*.key/*.pfx/*.p12`、`*.log`、`credentials*.json`/`*secret*.json`）；
    跳过项（>5MB、读取失败）全部打印原因，不再静默 skip；输出增加三层计数与覆盖率；
  - `scripts/verify_regressions.py:238-257`：新增 static 用例 `R22-static-bytecode-secrets`
    （所有 `__pycache__/*.pyc` 不得含明文令牌，且不得存在「无 `.py` 源」的孤儿缓存）。
- **用户可见验收**：门禁输出可见三层文件计数与被跳过的文件及原因；合成用例（未跟踪文件放 `sk-` 假令牌、
  被忽略的 `build/*.log` 放 `ghp_` 假令牌）必须 FAIL 并报出 `文件:行`；清理后 PASS（实测：已跟踪 332 +
  危险忽略项 20 = 352 个文件，跳过 2 个 >5MB 词典并已列出）。**⚠ 已泄漏的 `sk-` 令牌必须在服务商侧
  吊销/轮换，本机无法代劳**（收尾清单见 `docs/project_memory.md`「凭据泄露收尾」）。
- **回归测试点**：`static` 的 `R22-static-bytecode-secrets`；负向合成用例实测见 commit `fb743a5`；
  `verify_all.py` 第 10 项与 `package.sh` 打包前硬门禁与该门禁同源。

## R23 门禁自身失效：两条「永远绿」的硬门禁（v4.1.8，未发布）

- **现象**：两条硬门禁「坏了也没人知道」：① 简报门禁在目标为空集时判定是 `0>=0`，永远 PASS；
  ② 历史密钥扫描对「确实存在过凭据改动」的仓库**零命中**——历史门禁等于不存在。
- **根因（有实证）** —— 评审 `O-09` / `COR-006` / `RC-4`「门禁不检查自身」：
  1) `scripts/verify_all.py:114` 的判定 `translated >= len(files) - image_only` 在 `files` 为空时恒成立
     （`0>=0`），且「全图片型」也能空过；`O-09` 要求的「空集判 FAIL + 显式白名单」此前未落地。
  2) `scripts/verify_no_secrets.py` 的 `scan_history()` 原用 `git log --all -G<pattern>`，受 git 自有
     正则方言限制，不认 `\b`/`{20,}` 等写法：实测对**确实存在过凭据改动**的仓库零命中，
     而 `git log -S"sk-"` 能命中（commit `849e602` 实测记录）——这条门禁从未真正生效过。
- **修复位置**：
  - `scripts/verify_all.py:95-124`（`gate_briefings`）：目标为空直接 FAIL 并说明原因；图片型占比 >10%
    判 FAIL 并在输出里列出需人工确认的文件名；正向结果文案与数字**逐字保持不变**（304/323，19 image-only）。
  - `scripts/verify_no_secrets.py:110-170`（`scan_history`）：改为**按 blob 去重**扫描——
    `rev-list --objects --all` → `--batch-check` 按大小预筛（剔除非 blob 与 >5MB）→ 分块 `cat-file --batch`
    读取 → 对每个 blob 跑与工作区**完全同一套** `PATTERNS`（`scripts/verify_no_secrets.py:24-33`）；
    输出覆盖率（blob 去重数 / 实际扫描数 / 总字节 / 跳过超大数 / 预算上限告警）；
    命中时只打印前缀与长度、不打印凭据值。
- **用户可见验收（负向验证）**：把简报门禁目标指向**空目录必须 FAIL**（旧实现为假绿），真实目录 PASS
  且数字不变；历史扫描输出覆盖率：**按 blob 去重 1 011 个对象 → 扫描 560 个（79MB）→ 命中 0**
  （跳过 17 个 >5MB；仓库历史干净，无历史阻塞）。即：这两条门禁现在都能被**证伪**。
- **回归测试点**：简报门禁的负向实测（空目录必须 FAIL）见 commit `849e602`；历史密钥扫描的覆盖率输出
  （blob 去重数 / 扫描数 / 总字节 / 跳过数）见同一次提交的 `--history` 运行。
  注：仓库里那 580 行不可解析的 `scripts/translate_shard2.py` 属另一条同源缺陷，见 **R26**，
  现已 `git mv` 入 `scripts/archive/`（评审 `O-08` / `COR-003`）。

## R24 词典无法从源重建（v4.1.8，未发布）

- **现象**：任何人直接重跑 `python scripts/build_dict.py`，得到的词典与仓库里已提交的词典**不一致**——
  一次「无害的重建」就会静默回退已经修好的显示串，并制造 4.9 万行的纯格式 diff。
- **根因（有实证，四个子缺陷）**：评审 `O-07` 只覆盖原子写；这四条是本轮落地时才实测发现的，
  属评审 `RC-2`「缺单一所有者 / 单一事实源」：
  1) **LF → CRLF**：Windows 文本模式把 LF 全写成 CRLF，产出比已提交词典 **+49 427 字节**的纯换行 churn，
     违反「词典保持 LF、最小 diff、禁止无序格式化」硬约束。
  2) **非幂等值覆盖正确值**：`dict/src/legacy_fixes.json:43` 的
     `"Steam Workshop": "创意工坊 (Steam Workshop)"` 是非幂等映射，而它按字母序在 `campaign_ui.json`
     **之后**加载并覆盖了正确值 → 一次重建就把 **R20 的工坊叠译修复**静默回退。
  3) **长标签被误判丢弃**：`Select one of the campaigns available…from a save.` 被 `is_script_or_schema`
     误判为脚本/schema 而丢弃；它属于「只在 UI 层翻译、绝不注入池」的那一类，应放 `dict/src/labels.json`
     （`allow_sensitive` 通道）。
  4) **人工核验过的偏移被静默跳过**：offset `7916`（Steam Workshop 页签所在的字符串池条目）被
     `identifiers` 过滤静默跳过——即使写进 `force_offsets.json` 也被丢弃，重建后工坊页签退回英文。
- **修复位置**：`scripts/build_dict.py:406-427`（原子写：写 `*.tmp` → `json.loads` 自校验 + 顶层分区断言 →
  `os.replace`；失败/中断清理 tmp，产物永远「完整或不存在」；序列化参数
  `indent=1`/`ensure_ascii=False`/单末尾换行刻意不变，写入显式 `newline="\n"` 强制 LF）、
  `scripts/build_dict.py:314-329`（`force_offsets` 是人工实机核验过的注入白名单，现有权**覆盖
  `identifiers` 过滤**，仍受 `blacklist` 约束）、`dict/src/legacy_fixes.json:43`（改为幂等的「创意工坊」）、
  `dict/src/labels.json`（收长标签）、`dict/src/force_offsets.json`（含 `7916`）。
- **用户可见验收**：重跑 `build_dict.py` 与已提交词典**字节完全一致**
  （11 007 317 字节 / sha256 `2765a8c4…` / CRLF=0 / offsets 3 523 / exact 46 089）；
  重建不再回退任何已修显示串、不再造成格式 churn；游戏内工坊页签仍显示「创意工坊」（不叠译）。
  复测记录：`evidence/team-review-20261016/outputs/review/landed-results.md`。
- **回归测试点**：`static` 的 `R24-static-dict-reproducible`（`scripts/verify_regressions.py:285-312`）
  钉住五个前提：词典纯 LF、`legacy_fixes` 工坊值幂等、`force_offsets` 含 7916、长标签在 `labels.json`
  源批次、写入器强制 LF。

## R25 安装器解压缺包含性校验（越界写游戏目录外）（v4.1.8，未发布）

- **现象（安全边界）**：安装器两处解压——`Symbols\Menu` 菜单图 → `menuDir`、`Scenarios` HTML →
  `gamePath`——直接 `Path.Combine(root, entry.FullName)` 落盘，一个**构造过的 zip** 可以用 `..\..\`
  或绝对路径往游戏目录之外写文件。
- **根因（有实证）**：评审 `O-10` / `SEC-001` / `DG-11`，属根因组 `RC-6`「『可信输入』未编码为断言」。
  两个放大因素：① `.orig` 备份会为目标路径**再生成第二个越界文件**；
  ② 两处解压的外层都是空 `catch { }`（`src/installer/CMO_Chinese_Setup.cs:980` 与 `:1112`），失败无日志。
  安装器是**提权运行**的 ⇒ 恶意条目 = 提权任意写。
- **修复位置**：`src/shared/CMOShared.cs:45-67` 新增 `CmoPaths.TryResolveInside(root, entryName, out dest)`
  （拒绝空/绝对路径/盘符与 ADS 前缀，`GetFullPath` 后必须以规范化根目录+分隔符开头以防 `..` 逃逸，
  异常一律返回 false）；安装器两处改用该函数——`src/installer/CMO_Chinese_Setup.cs:961`（`Symbols\Menu`）
  与 `:1087`（`Scenarios` HTML），越界条目写 `CmoLog.Warn("跳过越界 zip 条目: …")` 后 `continue`
  （不再静默，也不再落盘）。
- **用户可见验收**：构造过的压缩包（`..\..\`、混合 `a/../../`、绝对路径、空条目）的越界条目被拒
  且日志留痕，游戏目录之外零新文件；正常安装不受影响——真实游戏目录 `--install` rc=0、
  `Symbols\Menu` 下 **237 个文件**正常落地（证明合法条目没有被误拦）。
- **回归测试点**：`static` 的 `R18-static-selftest`（`build/CMOSharedTest.exe` 必须 ALL PASS，共 33 项，
  含新增 6 项包含性断言：`zip-inside-normal` / `zip-inside-nested-ok` / `zip-escape-dotdot` /
  `zip-escape-mixed` / `zip-escape-absolute` / `zip-escape-empty`，见 `src/shared/CMOSharedTest.cs:279-297`）；
  `--installer` 子集全绿（`--static --installer` 25/25）；真实安装核对 `Symbols\Menu` 条目数 237。

## R26 仓库里躺着不可解析且从不产出的脚本，门禁从不检查（v4.1.8，未发布）

- **现象**：`scripts/translate_shard2.py`（580 行）在仓库里长期存在，但它在结构上根本无法解析；
  同时它「读入 → 不产出」，也从未被任何代码 import，却被 `scripts/migrate.sh` 的拷贝清单以
  `[ -f ]` 守卫方式引用 —— 属于「看起来还在用、其实早就坏了」的隐身代码。
- **根因（有实证）**：① 语法层面：第 82 行的 `}` 已闭合字典字面量，其后却是裸键值项，末行 580 又是
  孤立 `}`；`ast.parse` 直接 `SyntaxError: unmatched '}'`（line 580），84 行起另有
  `illegal target for annotation`。② 流程层面：全文只有 `open(IN)` 读入，**没有任何 `json.dump`/写出**，
  `OUT = …` 定义后再未被使用。③ 门禁层面：没有任何用例检查「仓库里的脚本能否被解析/能否运行」，
  所以坏脚本可以无限期留在仓库里（评审 COR-003 / O-08，根因组 RC-4「门禁不检查自身」）。
- **修复位置**：`scripts/translate_shard2.py` → `scripts/archive/translate_shard2.py`（`git mv`；
  新增 `scripts/archive/README.md` 说明「只读历史、不参与任何流程」）；`scripts/migrate.sh` 删除该条
  拷贝项并写明原因（不再留下指向不存在文件的引用）；`scripts/verify_regressions.py` 新增用例
  `R26-static-scripts-parse`。
- **用户可见验收**：开发者层面无可见变化（该脚本从未被流程执行）；收益是「不可执行的死代码不再
  伪装成流水线的一部分」，仓库里所有 `scripts/*.py` 都能被解析。
- **回归测试点**：`R26-static-scripts-parse`——用 `ast.parse` 只读解析 `scripts/*.py`（非递归，
  因此排除 `archive/`），断言全部可解析并报出失败文件与行；**刻意不用 `compileall`**：
  它会写出 `.pyc`，与 R22「不得有无源孤儿缓存」直接冲突。实测解析 51 个脚本全部通过。

## R27 天气编辑器/快捷键/控制台等窗口仍有用户看得见的英文（v4.1.8，未发布）

- **现象**：天气编辑器（气象水文）里 `Calm`、`Hurricane` 与页签 `Level 0` 是英文；
  快捷键窗口的分区标题 `Own Units` 是英文；Lua 脚本控制台的 `Insert`、`RUN` 按钮是英文。
  同一窗口里的其它标签（`满天厚云`、`强风暴`、`清除`、`目标接触`、`功能键`…）却都是中文，
  形成「同一屏中英混杂」。
- **根因（有实证）**：这 6 条字符串**根本不在词典里**（`dict/CMOLocalizer.json` 的 exact
  查无此项），所以引擎没有译文可用；与 R20 那种「词典有译文但没生效」不同。
  发现方式：实机逐个打开 30 个窗口/对话框，用 `CTRL_TREE` 导出控件文本并做开窗前后差分
  （`evidence/team-review-20261016/sweep/window_texts.json`），20 条英文候选里 6 条属真缺口，
  其余为设计上保留英文的项（型号 `PHS-36`、量纲 `0.19 Mach`、版权、`自动暂停 (Auto-Pause)`
  这类「中文 + 英文注记」）。
- **修复位置**：`dict/src/ui_cleanup_terms.json` 追加 6 条（`Calm`→无风、`Hurricane`→飓风、
  `Level 0`→层级 0、`Own Units`→己方单位、`Insert`→插入、`RUN`→运行）。严格按 R24 的
  单一事实源规则改**源批次**（不是直接改产物），CRLF 与末尾无换行的文件风格保持原样，
  源文件 diff 仅 +7/−1 行；`build_dict.py` 重建后 `exact` 46089→46095，**只新增这 6 条**、
  无删除、无改值、offsets 完全一致。
- **用户可见验收**：实机打开三个窗口实测——`Calm/Hurricane/Level 0`/`Own Units`/`Insert/RUN`
  全部显示为中文，且原英文不再出现（进程存活、无崩溃日志）。
- **回归测试点**：`L-ui-residuals`（实机抽样 5 个窗口：断言「已入词典却仍显英文」为 0 条，
  并逐条断言上述 6 项的当前状态是「中文在、原英文不在」）。
- **已知盲区（本次未修，如实记录；每一条都附了证据与复现方式）**：
  1. **快捷键窗口的正文**在 `DarkRichTextBox`（富文本）控件里，引擎不翻译其内容，因此整窗的
     快捷键说明仍是英文——即使词典里已有对应译文（`Air Tasking Order (ATO)` 就在词典里却没生效）。
     修它要改富文本内容，会丢格式（这些列表靠 Tab 排版）并触碰 R19 刚加固的输入保护不变量，
     风险高于收益。**证据**：`CTRL_TREE` 导出 `Hotkeys` 窗体时，8 个 `DarkRichTextBox` 全部是英文。
  2. **启动时的「Professional Edition」介绍面板**（`I'm playing for fun` / `I'm a pro user` +
     整段英文说明）**引擎完全够不到**：既不在 WPF 控件树里（`WPF_DUMP` 无命中），
     也从未出现在审计日志里（说明引擎从未把它当成可翻译文本），属大厅 WebView2 未挂注入的那一类。
     用户可勾选面板上的「启动时不显示」关掉它。**复现**：重启游戏后截图即见。
  3. **单位命令系窗口（攻击选项/传感器/武器/弹药库/损伤与系统状态/条令等）本轮未能覆盖**：
     自动化会话里状态栏恒为「活跃单位数(AU): 0」。已试过五种绕法均无效——命令行带 `.scen` 启动、
     `视图 → 选择下一个单位`、先开始推演再选、`快速跳转`、以及用游戏自己的「加载想定」对话框
     （`SCEN_SELECT` + `SCEN_LOAD` 返回 ok）——那批菜单项点击后始终静默无操作
     （`click=ok` 但 WinForms 与 WPF 两侧都没有新句柄）。
     更深一层的根因（已逐步定位，含我自己的误判纠正）：这批对话框打不开的直接原因是
     **没有选中任何人任何单位**（状态栏「活跃单位数(AU)」恒为 0），而 AU 上不去是因为**进不了想定**：
     加载想定后会出现「阵营选择与简报」面板（WinForms 窗体 `ChooseSide`），需要点它上面那个
     「进入想定」按钮。实测：`CTRL_TREE` 只能看到 `ChooseSide` 窗体本身、**没有任何子控件**，
     `WPF_DUMP` 也看不到它——因为按钮是 **WebView2 里的 HTML 元素**，没有任何 Win32 几何信息可供枚举；
     我唯一的办法是从截图目测像素坐标，而截图会经过缩放显示，像素估计偏了就点不中
     （实测点击后该面板完全无变化）。
     （附带事实，不是本项的主因：`GuiDriver focus <hwnd> max` 返回 `Focus result: False`，
     且合成按键落不进游戏输入框——这也是我后来改用 WM_CHAR 投递字符的原因；但鼠标点击
     对其余可枚举窗口是有效的，所以前台限制不是这批对话框打不开的主因。先前把主因写成
     “前台被拒”是不准确的，已纠正。）
     **可行的修法（下一步就能做）**：给引擎加一条探针命令，直接在进程内操作那个 WebView2
     （`CoreWebView2.ExecuteScriptAsync`：导出 DOM 文本 / 点击 HTML 按钮）——引擎本来就在游戏进程里，
     这条路不依赖窗口前台与像素坐标。它同时能解掉盲区 4（那个计数标签就在同一层 HTML 里）。
  4. **阵营选择面板的计数标签** `N selectable side(s)`（实测为 `2 selectable sides`）仍是英文。
     已排查三条路：①放进数据库详情 WebView2 处理器的模式替换——错层（该面板不走那条通道，无效果）；
     ②放进 `TranslateBriefingText` 的模式替换——同样无效果（该标签不在简报文本里）；
     ③用 `CTRL_TREE`/`WPF_DUMP` 找它的宿主控件——两侧都看不到（`ChooseSide` 窗体在两种 dump 里
     都只有窗体本身、没有子控件）。结论：该标签渲染在引擎当前三条通道（WinForms 控件 / WPF 控件 /
     简报与 HTML 文本）都到不了的一层，**未定位到宿主**，按“同一目标不超三次”的纪律停止追查，
     留作已知限制。**复现**：重启 → 加载想定 → 选 Shamal → 加载后截图，该面板正文全中文、
     只有这个计数标签是英文。

## R28 单位命令系窗口里的英文残留（v4.1.8，未发布；本轮已定位并部分待修）

- **现象（实测，2026-10-17）**：进入想定后逐个打开单位命令系窗口，发现
  ① 窗口标题的动态前缀仍是英文：`Sensors for: Taif (King Fahd Air Base)`、
  `Damage Status for Taif (King Fahd Air Base)`（而同族的「弹药库状态 - …」已是中文）；
  ② **「条令 / 交战规则」窗口的选项值大面积英文**：`(Parent Side)`、
  `Inherited, Always Continue Guidance`、`Inherited, Always Except Tankers Refuelling Tankers`、
  `Inherited, Automatic for search and localization`、`Inherited, Pessimistic` 等
  24 条候选（同窗口里 `AAW 制导` 等已汉化，中英混杂）。武器/编队操作/网络通信/指派至任务 四个窗口干净。
- **根因方向（待继续定位）**：标题类是「动态前缀 + 单位名」的复合串，词库是精确匹配、盖不住前缀；
  条令窗口的选项值多半在 **DataGridView / 表格型下拉**里，而引擎现有翻译通道只覆盖
  WinForms 控件文本、组合框 `Items`、WPF 控件与 HTML 文本——**表格单元格内容是否被覆盖尚未验证**。
- **本轮已解决的同类前置问题（关键）**：这批窗口此前**完全打不开**，因为自动会话里
  「活跃单位数(AU)」恒为 0——进不了想定。真正原因不是窗口前台被拒，而是
  「阵营选择与简报」面板（`ChooseSide`）的「进入想定」按钮**没有任何可枚举的 Win32 几何信息可点**。
  修法：给引擎加探针命令后用 `CLICK_BUTTON 进入想定` 直接点该按钮（`ChooseSide.Button1`，按名字命中），
  想定才真正载入、单位出现，随后 6 个单位命令窗口中 5 个可正常打开。
- **回归测试点**：`scripts/verify_regressions.py` 的 `--live` 已有 `L-ui-residuals`（多窗口抽样、
  断言「已入词典却仍显英文」为 0 条）；本条目的新窗口可在下一轮把它扩进来，
  但**必须先修完**，否则会红（这正是该用例的作用）。
- **附带**：`2 selectable sides`（R27 盲区 4）本轮已**定位到宿主**：`ChooseSide` 窗体上的
  `DarkLabel lblSideNumber`——它是普通 WinForms 控件，所以先前把规则放进 HTML/简报层当然无效；
  正确修法是「控件文本翻译路径」上加一条动态计数规则（`^(\d+) selectable sides?$`）。
- **本轮已修（实机验证通过，2026-10-17）**：
  ① `2 selectable sides` → 「可选择 N 个阵营」（`TranslateControlTextInner` 的非 Form 控件动态规则，
  宿主是 `DarkLabel lblSideNumber`）；② 标题前缀 `Sensors for: ` → 「传感器：」、
  `Damage Status for ` → 「损伤与系统状态 - 」（与既有的 `Air Ops - `/`Docking Ops - ` 同一范式）；
  ③ 条令窗口的 `Inherited From Doctrine:` → 「继承自条令：」、`(Parent Side)` → 「（上级阵营）」。
  实机验证：ChooseSide 面板显示「可选择 2 个阵营」且旧英文消失；休眠窗口标题为
  「传感器： 吉达 (阿卜杜勒·阿齐兹国王机场)」；损伤窗口标题为「损伤与系统状态 - …」；
  条令窗口出现「继承自条令：」与「（上级阵营）」。
- **仍未修（有意，含安全约束）**：条令窗口 `Combo_StateSelection` 的**选项值**（`Inherited, Pessimistic`、
  `Inherited, Always Continue Guidance` 等 24 条）仍在英文。原因：它们是**下拉框的当前值/项**，
  而 R8 的教训是「按文本分派的下拉框禁止改写 `.Text`」（改写会破坏 `SelectedIndex` 与游戏逻辑）。
  可行的下一步（需功能验证，不是纯文本改动）：先确认游戏是否按 `SelectedIndex` 取值——
  若是，则可安全翻译 `Items`（下拉列表）并保持 `.Text` 语义；若否，则只能放弃或改用显示层方案。
- **本轮已修（2026-10-17 第二轮，实机验证通过）**：
  ① 术语补齐：`dict/src/chunk03.json` 追加 8 条条令选项值（`Inherited, Pessimistic` → 「继承自上级，悲观估计」、
  `Inherited, Always Continue Guidance` → 「…始终持续制导」、`…Except Tankers Refuelling Tankers` →
  「…始终开火（加油机为加油机加油除外）」、`…Automatic for search and localization` → 「…自动（用于搜索与定位）」、
  `…Pick Nearest` → 「…就近选取」、`…Prioritize Mission Specific Targets` → 「…优先任务特定目标」、
  `…Shortest Route` → 「…最短航线」、`…For Weapon Targets, WRA Quantity …` → 「…对武器目标：WRA 数量由任意制导武器满足」），
  严格按 R24 改**源批次**、不直接改产物；重跑 `build_dict.py` 后 `exact` 46095→46103，**只新增这 8 条**、无删除无改值、offsets 一致、纯 LF。
  沿用词典既有惯例 `继承自上级，<状态>`（词典里早有 `Inherited, Free/Hold/Tight/Various` 的译文，
  说明「翻译条令下拉值」在本项目**已是既有行为**，不是新风险）。
  ② **同时修掉一个我们自己的缺陷（假熔断）**：`TranslateComboItems` 在入口**无条件**调用 `AllowWrite`，
  而 `AllowWrite` 按「同一 target 被重复调用超限」计数 —— 于是**一次都没写入**的组合框只要被反复访问就会被
  **永久静音**。游戏日志里有 **269~311 次** `WRITE LOOP BLOCKED ComboBoxItems`（`CB_ScenOrder` 28 次、
  `Combo_AlertLevel`、`EMCON` 系列…）。修法：先算出「真正要改的项」，计划为空直接返回（不计数）；
  指纹改为「本次待写入的翻译」，写成功则下次不再计数，只有游戏把条目改回去的**真拉锯**才会累加触发熔断。
  实机验证：`Combo_StateSelection` **12/12 去重值全中文、纯英文 0 条**；熔断事件 **311 → 311（零新增）**。

## R29 界面英文残留的系统扫描（含下拉项）与修复（v4.1.8，未发布）

- **做法（本轮确立的可复用方法）**：① 逐个开菜单/对话框；② 同时采 **控件文本**（`CTRL_TREE`）与
  **每个 ComboBox 的全部 Items**（`DUMP_COMBOS`，含 `bound`/`sel`）——只查控件当前文本会漏掉
  「下拉打开后才看得见的英文」；③ 变化判定按「组合框身份 + 条目内容」而不是窗体名（按窗体名会漏掉
  在已有窗体里打开的窗口）；④ 关键窗口截图目视复核（`evidence/team-review-20261016/shots/`）。
- **本轮已修（实机验证）**：`Frequency`→频率、`Stylized`→样式化、`Directional Stylized`→方向化样式、
  `Show on cursor`→在光标处显示、`Select...`→选择…、四色警戒等级（蓝/黄/橙/红）、
  `Minor`/`Servere`→轻微/较严重、四条「重置受影响单位/任务（继承上方 EMCON/WRA）」、
  动态规则 `Every N seconds`→每 N 秒、`Weapons for <单位>`→「武器：<单位>」；
  并把 `Green` 的旧值「绿色 (Green)」改成「绿」，与其它三色统一（实机：AlertLevel 项 = 橙/红/绿/蓝/黄）；
  **`Tacview` 页签叠译**——词条值由「三维态势联动 (Tacview)」改为幂等的「三维态势联动」，并补两条自愈项
  （`三维态势联动 (Tacview)`、叠译后的双层形态 →「三维态势联动」）。实机同词典 A/B（`evidence/team-review-20261016/shots/r29_tacview_tab.png`）：
  旧词典（46116 exact）该页签显示「三维态势联动 (三维态势联动 (Tacview))」，新词典（46118 exact）显示「三维态势联动」。
- **有意不修（附理由）**：`All`/`Normal`/`Major` 属多义词（`Major` 在别处是军衔「少校」），
  全局 exact 条目会在其它界面误译；`CB_OSMstyle` 的一串地图样式名（`Bright`/`Dark Matter`/`Fiord Color`/
  `Toner`/`Positron`/`OSM OpenMapTiles`）是 OSM 样式专有名，与既有 `Sentinel-2 卫星影像` 同类保留；
  `(c) Matrix Games 2013-2026` 是版权行；`AutoSideID`/`AutoSideAndUnitID` 疑似内部枚举标识。
- **已澄清并修复（本轮实测纠正上一轮判断）**：「想定特性与设置」是 **WPF 窗口**
  （`WPF_DUMP` 标题 `Scenario Features + Realism Settings`；`CTRL_TREE` 看不到）。上一轮把截图里那 4 条英文
  （`Allow instant loading for landing planner`、`Point To Point Unit Communication`、`Realistic Order Chain`、
  `Variable burnout speed on boost-coast weapons`）判断为「WPF 虚拟化离屏项」——**实测证伪**：
  该窗口 400×467、17 条特性同屏全部可见（视觉树里既无 `ScrollViewer` 也无 `ListBox`/`ItemsControl`，
  不存在需要滚动才 realize 的项；`shots/r29_realism_features.png` 里 4 条英文赫然在列），
  而它们的英文源串**根本不在词典里**（同窗可见的 13 条中文项各自都有 exact 词条）
  → 结论：真残留 = 缺词条，与 realize/滚动无关。
  修法：补 4 条词条（着陆规划器即时装载 / 点对点单位通信 / 真实指挥链 / 助推-滑翔武器的燃尽速度可变，
  术语跟随既有 exact：`Realistic Order Chain Enabled`→「已启用真实指挥链」、`助推-滑翔`（6 条 vs `助推-滑行` 1 条）），
  实机复验 17/17 全中文、0 残留（`shots/r29_realism_features_fixed.png`）。
- **R29-3 页签扫描（本轮完成）**：`推演选项` 实测 **7 个页签**（通用/地图显示设置/消息日志/声音与推演音效/推演速度/
  三维态势联动/悬停信息——比原记录多一个「悬停信息」）。新增只读探针 `SELECT_TAB <窗体过滤> <索引或页签名>`
  （UI 线程切页签，不点确定/应用），逐页签采控件文本 + 下拉项 + 截图（`shots/r29_options_tab*.png`）：
  **无用户可见英文**——纯英文项只有控件内部名（`ToolStrip1`/`MenuStrip1`/`WorldWindow1` 等，不渲染）、数字值，
  以及「有意保留」的 OSM 样式名与单位缩写（`KPH`/`MPH`）；悬停信息页签的 WPF 内容用 `WPF_DUMP` 查也是 0 条英文。
  两项待确认项用**下拉项**（不是控件 text）钉住：`CB_SonobuoyVisibility` = 正常显示/**虚影**/不显示、
  `Combo_SlugTrailFreq` = **每 5 秒**/每 15 秒/每 60 秒；已加入 live `L-ui-residuals` 断言。
  另注：`CB_OSMstyle` 里 `Basic`→「基础」而其 7 个样式名保持英文（专有名），属文档化的混排。
- **同类残余风险（未修，记录在案）**：凡是「值 = 中文 (英文字典键)」的词条（全词典 1370 条），一旦该文本再次走
  `TranslateString` 的括注通道（`TranslateBracketSegments`）就会被叠译——本轮 A/B 已确认 TabPage 文本会走这条路。
  实机捕获里还有两条同形可见文本：`节 (Knots)`、`自动暂停 (Auto-Pause)`（都是下拉项，当前只翻译一次故不叠；
  若将来引擎重扫下拉项，它们会像 Tacview 页签一样叠）。修法同上（值去掉括号英名 + 自愈项）。
- **回归测试点**：`live` 的 `L-ui-residuals`（打开「推演选项」后必须出现「三维态势联动」，且不得出现
  「三维态势联动 (Tacview)」与双层形态；同一用例再用 `WPF_DUMP` 断言「想定特性与设置」≥10 条特性全中文、
  4 条源串不再出现）；`static` 的 `R24-static-dict-reproducible`（重建字节一致，自愈项与幂等值不会在重建后丢失）。
- **附带产出**：新增只读探针 `DUMP_LISTS`（导出 ListBox / CheckedListBox / ListView 的条目）。
- **探针/用例自身缺陷（R29-3 发现并修复）**：① `DUMP_LISTS` 一直**不可达**——它被写在
  `if (cmd == "WPF_DUMP" || cmd == "DUMP_COMBOS" || cmd == "XLATE_BATCH")` 块里，调用只会得到
  `Unknown command`（此前「那个对话框不是 WinForms 列表（所以没命中）」的结论实为命令没执行）。
  现已加入外层条件，并改为**逐控件兜异常 + 单独登记非 WinForms 列表控件的基类**：实测
  「区域与参考点数管理器」的 4 个 `DarkListView` 都是 `DarkScrollView` 自绘控件
  （`kind=非 WinForms 列表（基类 DarkScrollView）`）→ 它们没有可读的 WinForms `Items`，
  **以后看到 `items: []` 必须结合 `kind` 判读**，别再当成「窗口里没有列表」。
  ② `CTRL_TREE <窗体过滤>` 的过滤词**永远为空**：调度传的是大写首 token（`HandleCtrlTree(cmd)`），
  `Substring` 取不到第二个 token → 带过滤的调用静默退化成全量导出（本轮实测：
  `CTRL_TREE InternalDBViewer` 修复后 23 节点/单窗体，修复前 40 节点/2 窗体）。现改为传原始命令。
  ③ live `R4-live-flicker` 原来比**窗口截图 md5**：GuiDriver 是屏幕抓图（`CopyFromScreen`），
  游戏暗色窗口半透明，窗口背后的**动态地图**会被一起抓进去 → 地图一动画两帧必不同，属用例假红
  （2026-10-17 打包候选实测：两帧 UI 文本逐字相同、差异全在窗口背后的地图上）。
  现改为比**文本状态**（查看器控件文本 + 下拉项 + 详情页 HTML 长度/前 200 字；截图仍保留供人复核）。


## R30 发布标题重复破折号（v4.1.8 发布时发现）

- **现象**：GitHub Release 标题显示成 `v4.1.8 — — 引擎性能与门禁加固`（两个破折号）。
  `latest` 指向、4 个资产、安装包哈希全部正常，只是标题难看；历史 v4.1.5 / v4.1.7 也带着同样的双破折号。
- **根因**：`scripts/publish_release.py:93` 从 CHANGELOG 取小标题后无脑拼 `"v{版本} — {小标题}"`，
  而 CHANGELOG 的小标题本身已含破折号（`## v4.1.8 — 引擎性能与门禁加固`）→ `— —`。
  v4.1.4 及更早的 CHANGELOG 小标题不带破折号，所以那时没有暴露。
- **修复位置**：`scripts/publish_release.py`（取小标题后先剥掉开头的 `—`/`–`/`-` 再拼接）；
  已用同一段逻辑对当前 CHANGELOG 复算：`v4.1.8 — 引擎性能与门禁加固`。
  本次已通过 Release API 把 v4.1.8 的标题更正为正常形态（历史 v4.1.5/v4.1.7 标题未改，属已发布元数据）。
- **回归测试点**：无自动化断言（发布脚本本身的文案逻辑）；下次发版用 `release_check.py` 后目视一次 Release 标题。

## R31 游戏更新到 Build 1900.23 后汉化大面积失效 + 频闪（v4.1.8 发布当日发现）

- **触发事件（实证，非猜测）**：2026-10-17 20:46 Steam 更新 AppID 1076160——
  `starting commit … : 2971 updated, 0 moved, 0 deleted files`、
  `finished update, 2 mounted depots (BuildID 25865381)`，**两个 depot 的清单号同时变化**
  （`1076161: 1281736160605491036 → 6122376107217215217`、`1076162: 8276838379014883079 → 2749776043223916049`）；
  上一次更新是 2026-10-10（BuildID 25673213）。→ 这是**发布方的新构建**，不是 Steam 回写我们改过的文件。
  引擎日志同时抓到新标题 `Command: Modern Operations v1.10 - Build 1900.23`（我们适配并记录的是 **1900.20**）。
- **用户可见现象（用户 7 张截图，21:00 前后，游戏 20:49:51 启动）**：
  ① 状态栏整条英文（`Time: … - Zulu: … - Local: … - Camera: … - AU Count: …`）；
  ② 主标题半截混排「现代行动：指挥：Modern Operations v1.10 - Build 1900.23」；
  ③ 条令/EMCON：`Inherited, Always Fire`、`Inherited, Ignore`、`Inherited, Bingo`、`Inherited, 5%`、`Inherited, 100%`；
  ④ 「快速周转」值 `2 Sorties (Maximum)`；⑤ 编队燃料 `AvGas (200k Liter Underground Tank)`；
  ⑥ 「航空作业」飞机列表行 `8x F-16I Falcon [Sufa] (Ramat David)`；⑦ 消息日志整段英文
  （`AIM-120C-7 AMRAAM P3I.3 #925's Endgame Report HIT`、`new weapon contact (MISSILE #381)`…）；
  ⑧ EMCON 页签「警告ALERT：」行的值文本可见性异常（被遮挡，待定位）；⑨ 条令/EMC 窗口多处中英频闪。
- **根因（四条，均已定位到证据）**：
  1. **偏移池注入整条通道失效（最主要）**：`src/engine/CMOLocalizer.cs` 的 `TryInjectCoreDecryption()` 靠反射找
     `AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU` 的 `gTbYFnJa7EN` / `OVoYFvH0GRf` / `vWjYFIwuiK7` / `ad6YFOZX46s` 字段，
     再把 3523 条中文写进游戏的解密缓冲。新构建里该类/字段已变化 ⇒ 本次会话日志**既无**
     `Set gTbYFnJa7EN anti-debug flag to 100.` **也无** `Successfully pre-populated 3525 translated strings…`
     （此前每次启动都有，今日 17:29/17:35/18:52/19:04/19:06/19:22 各一次）⇒ 显示层（状态栏/HUD/标尺/视高/显卡等）全部回到英文。
  2. **构建号专属串失配**：词典只有整串 `Command: Modern Operations v1.10 - Build 1900.20`，新版是 `…Build 1900.23`，
     于是只被词级路径替换成半截「现代行动：指挥：…」（引擎日志可见）。
  3. **新版新增/变更文本不在词典**（与 R27/R29-2 同类：真缺词条）：`Inherited, Always Fire`、`Inherited, Ignore`、
     `Inherited, Bingo`（含 `Inherited, Bingo 60%`）、`Inherited, 5%`、`Inherited, 100%`、`2 Sorties (Maximum)`
     （词典只有带前导空格的 ` Sorties (Maximum)`）、`AvGas (…Liter Underground Tank)` 等 exact 查无。
  4. **频闪 = 重译与游戏回写拉锯**：本次会话两条熔断——`WRITE LOOP BLOCKED WPF TextBlock after 7 identical writes`（20:51:05）、
     `WRITE LOOP BLOCKED ComboBoxItems 'Combo_StateSelection' after 7 identical writes`（21:10:06）；
     新构建下 WPF/组合框的文本行为变化是诱因（与 R4 同类）。
- **已核验并排除：打包/部署没有缺失（用户怀疑项）**：`build/CMO_Chinese_Pack_4.1.8.zip` 690 条目 → 实装 **688 条逐字节一致、0 条内容不同**；
  其余 2 条为 `Command.exe.config.patch`（模板，已应用到 `Command.exe.config`，含 `CMOLocalizer` 注入配置）与 `汉化说明.md`（实装为 `README_汉化说明.txt`）。
  实装 `CMOLocalizer.dll` / `CMOLocalizer.json` 与仓库同哈希（`4dd2b763…` / `142e52c1…`），运行中的引擎自报
  `3523 offsets, 46122 exact texts`（= 仓库词典）。
- **影响面**：所有已装汉化的用户在游戏更新到 1900.23 后都会遇到；`--live` 门禁与既有回归用例**不会自动发现**
  （它们只断言文本，不校验游戏构建号）。
- **待办修法（按顺序，需用户授权后再动）**：① 重新定位新构建的混淆类/字段并重采偏移（`scripts/bisect_offsets.py` / `identifiers.py` 流程）；
  ② 补词典（新版标题、条令值、快速周转、燃料名等）；③ 消息日志这类动态拼接文本改用规则翻译（R28 `2 selectable sides` 先例）；
  ④ 按熔断日志定位频闪；⑤ 门禁全跑 + 版本提升 + 打包 + 发布。
- **回归测试点（修好后落）**：`static` 增加「游戏构建号哨兵」——记录已适配的 build/标题串，构建号一变直接红；
  `live` 增加状态栏/标题断言（`时间:`、`当地:`、`内部版本`）。
- **修复与验证（2026-10-17 当日完成）**：
  ① **注入恢复**：`TryFindPool` 名字优先 + 结构特征兜底（静态 `List<string>` + `List<int>` + `byte[]`，buffer 取最长），
     每个失败分支都写日志；实测日志出现 `pool class … / pre-populated 3525 条 / out of range 0`。
  ② **偏移核验**：新增只读探针 `POOL_DUMP` 导出本构建全表（3655 对），与词典断言比对 **3522/3523 命中、重采 0**；
     剔除 1 条源码字面量偏移（280066），`scripts/triage.py` 新增 `CODE_LITERAL_FRAGMENT` 硬阻断。
  ③ **新文本**：`Inherited,<状态>` / `<n> Sorties (Maximum)` / `<燃油> (<n> Liter <罐型>)` 三条动态规则（放在整串查表之后）、
     5 条新词条、**消息日志 9 类模板**、`ALERT:` 标签收窄（修用户报的「字被挡住了」）。
  ④ **版本与构建号**：标题规则（不再写死 1900.20）+ 仓库根 `GAME_BUILD` + 安装清单 `game_build` + 引擎构建号哨兵；
     安装器/更新助手/发版说明/README 同时写清两个号（用户 2026-10-17 要求）。
  ⑤ **频闪**：确认是「注入失效 ⇒ 显示层由游戏按英文池重建、我们反复重译」的下游症状，随 ① 消失，
     未新增/放宽熔断参数（保持「真拉锯才熔断」语义）；条令/航空作业窗口反复采样无新增熔断、文本稳定。
- **回归测试点**：`live` 的 `L-build-adaptation`（构建号一致 + 注入生效 + 池内关键偏移中文 + 日志模板）与
  `L-doctrine-combo`（条令项全中文且无新增写拉锯熔断）；`version` 门禁（`verify_all` / `release_check` 共用）
  含「`GAME_BUILD` ↔ `ReleaseConstants.GameBuild` ↔ 安装器引用 ↔ README」四项一致性。
  实测（2026-10-17）：verify_all **10/10**、`--static --installer` **25/25**、`--live` **16/16**（新用例：注入 ok、
  构建号哨兵 ok、池内中文 12/12、日志模板 3/3）；故意把 `GAME_BUILD` 声明成 1900.20 时 `version` 门禁立即 FAIL（已还原）。
- **状态**：已修并验证（2026-10-17）；用户截图存于 `…/Tencent Files/3622024135/nt_qq/nt_data/Pic/2026-10/Ori/`。
  **已随 v4.2.0 打包并正式发布（2026-10-18，经用户授权）**：Release `https://github.com/mj8724/CMO_CN/releases/tag/v4.2.0`
  （tag → `1e01ce3`，4 个资产：Setup 14,735,360 B sha256 `c34c9010…` / Pack 7,158,031 B sha256 `46b2c724…` + 各自 `.sha256`）。
  发布后自检 4/4：latest=v4.2.0 且非 draft/pre、下载包 sha256 与本地构建及同目录 `.sha256` 三处一致、
  真实实装 rc=0（清单 version=4.2.0 + game_build=1900.23，dll/json 与仓库逐字节一致）、
  升级链弹窗显示「发现新版本 v4.2.0」且带「适配游戏内部版本：1900.23」。

---

## R32 v4.2.0 实装后仍有残留英文 + 频闪复发（v4.2.0，2026-10-18 用户实测，**仅登记未修**）

- **触发**：用户 2026-10-18 报「又在频闪」，并给两张截图：09:10 单位状态面板、09:12 任务编辑器条令窗口；
  此时游戏目录已实装 **v4.2.0**（清单 `version=4.2.0`/`game_build=1900.23`，安装于 08:27，游戏 09:06 启动）。
  用户明确要求：**先记录，不要修**。
- **现象 A（单位状态面板，红框）**：① 经验等级 `Veteran`；② 机型类别 `Airborne Early Warning (AEW)`；
  ③ 挂载方案名 `Conformal Airborne Early Warning (CAEW)`（同一行其余为中文：「挂载方案: … 已启用快速出动周转。0 / 2 架次」）；
  ④ 航向行 `地速(Gnd) 220 节（自动: Loiter）` 的 `Loiter`；⑤ 面板底部「乘员海拔 / 乘员高度」行字符被裁切、重叠。
- **现象 B（任务编辑器 · 条令/武器使用授权窗口，红框）**：① 任务标识名 `SupportMission`；
  ② 状态值半截英文 `主动 (Active)`；③ `作战阶段:` 的值原文是 `Waiting for trigger`，**左侧标签 `作战阶段:` 压在值上面**，
  前两字符被覆盖（截图里读作 `itting for trigger`）；④ 值列多处 `Inherited` 前缀本身未译
  （`快速周转出动: Inherited, 战斗机与反潜战机`、`航空出动强度/节奏: Inherited, 最大出动强度 (Su…)`，以及武器状态/水下战/燃料等格）——
  即 R31 只修了 `Inherited, <值>` 里 `<值>` 的翻译，**`Inherited` 这个词本身在这几格里仍是英文**。
- **现象 C（频闪）**：用户口述「又在频闪」，未指明具体控件；静态截图无法体现。
  → **说明**：本节为首次登记时的表述；用户随后补充了复现条件（须在游戏运行中动态选择条目），
  已于下方「现象 C【频闪】已实测复现」小节重写并附实测证据。
- **已核验（只读证据，本次运行 2026-10-18 09:06 起）**：
  1. **注入通道正常**：`CoreDecryption: pool class AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU; strings=yjAvaHkPKAG buffer=P30vaKD9Eao (2044466 B…)`
     与 `Successfully pre-populated 3525 translated strings … (offsets out of range: 0)`；无 `offset injection disabled`。
  2. **构建号一致**：`GameBuild: detected game internal build 1900.23` == 仓库 `GAME_BUILD`，无告警。
  3. **本次运行 `WRITE LOOP BLOCKED` = 0 条**（`CMOLocalizer.log` 历史累计 407 条均属更早会话）
     ⇒ **当前频闪不是 R31 的写回熔断机制**，与 R31 结论（“注入失效 ⇒ 互搏”）不是同一回事。
  4. 引擎自记「未译英文清单」`CMOLocalizer_LiveAudit.log`（本次运行 56456 行）确认这些串**被判为英文但没译出**：
     `Veteran` 69 次、`CAEW` 38、`Loiter` 24、`Conformal Airborne Early Warning` 4、`Airborne Early Warning (AEW)` 3
     ⇒ 性质是「查表未覆盖 / 形态未识别 / 控件值被重建」，**不是注入失效**。
- **待验证假设（供今后动手时用；本轮全部未验证、未修改任何代码/词典）**：
  1. `Inherited` 单前缀未译：疑该格控件按宽度**截断**（只显示前缀）或该格取值是拼接串、整串不在表中；
     判别方式：`CTRL_TREE` / `WPF_DUMP` 取控件真实 `.Text` + `XLATE` 实测。
  2. `itting for trigger`：`作战阶段:` 的值发生**标签覆盖**（与 R31 的 `ALERT:` 遮挡同类，疑控件宽度/绘制顺序）；
     判别方式：`CTRL_TREE` 看几何与文本。
  3. `SupportMission`：疑为任务**内部标识名**（枚举名，可能应有意保留）；需确认游戏是否把它当可译文本再决定。
  4. 频闪：写回熔断为 0 ⇒ 假设为「游戏每次选择/刷新重设控件文本 ↔ 引擎重译」交替（因不是 7 次相同写入故不触发熔断）；
     采样方式同 R4（同屏两帧截图 md5 对比）。
- **现象 D（编队状态面板，09:16，红框）**：① `Airfield (98 units)` / `Airfield`（机场/基地与数量未译）；
  ② `160000 fuel units remaining (AviationFuel)`（油量/单位/油料类型名未译；上方 6 行「航空燃油 (200k 升 地下油罐)」已是中文）；
  ③ EMCON 警告行 `(Manual edit disabled)` 英文；④ `状态: Unassigned` 英文。
- **现象 E（任务编辑器 · 对地打击/空中截击页签，09:19，红框）**：
  ① 下拉选项值 `Active radar from Initial Point (IP) to Winchester`（英文选项）；② 按钮 `CREATE or UPDATE flightplans`；
  ③ `状态: 主动 (Active)` 半截英文；④ 同一个 `作战阶段:` 值被标签覆盖（`itting for trigger`，与现象 B③ 相同）；
  ⑤ 雷达使用/最小打击半径两行的下拉框里「中文选项 + 英文选项」叠在同一格内（截图可见文字重影）。
- **现象 C【频闪】已实测复现（2026-10-18 09:13–09:19，引擎只读探针 `WPF_TEXTLOG`）**：
  游戏处于**推演运行中**（教程想定「空空作战教程 4」，pid 44020）时，右侧 WPF 面板的同一批 ContentControl 被**反复重写**：
  5.5 分钟内（400 条环形缓冲，09:13:18→09:18:42）`Veteran ==> 老手` **93 次**、`Kills 0 ==> 战果击杀: 0` **93 次**、
  `Fire: ==> 起火:` **93 次**、航速/地速行 **58 次**、`Israel` 17 次、`Loiter ==> 盘旋待机` **59 次**。
  即：**游戏约每 1.5–3.5 秒把英文原串写回去，引擎随即再译成中文——这就是肉眼中的中英频闪**；
  而同一会话 `WRITE LOOP BLOCKED` 仍为 **0 条** ⇒ 熔断守卫（“同一控件 7 次相同写入”）**抓不到这种交替写入**
  （每次写入内容与上一次不同），这正是 R31 修完注入后仍能频闪的原因。
  旁证：用户截图里显示为英文的 `Veteran` / `Loiter`，在本日志里都有大量 `==> 老手` / `==> 盘旋待机` 译入记录
  ⇒ 它们**不是缺词条**（词典有），而是**写回被游戏覆盖**。
- **叠译实测（与 R29 已知残余同源）**：航速行 `航速: … 地速(Gnd) 192 节 (自动: Loiter)` 被译成
  `航速: … 地速(  地速(Gnd) ) 192 节 (自动: Loiter)`——即 `地速` 被叠加进已含中文的值里，
  5.5 分钟内同一形态出现 **92 次**；行内 `(自动: Loiter)` 的 `Loiter` 单独仍走原文（同帧日志里另有 `Loiter ==> 盘旋待机`）
  ⇒ 同一行内不同片段走了不同通道，疑似“整串查表未命中 → 回退到括注/词级通道”所致（待验证）。
- **复现条件（用户说明，已实测一致）**：**必须在游戏运行（推演）中动态选择条目/切换单位**；
  游戏未运行、或只看静态截图，面板不刷新 ⇒ 观察不到。本文所有频闪证据均来自“游戏运行 + 反复切换选择”下的采样。
- **影响面**：已装 v4.2.0 的用户可见；现有 `--live` 用例（`L-doctrine-combo` / `L-ui-residuals`）断言的是抽样项的取值必须中文，
  **未覆盖**这些残英文形态、「标签覆盖」形态，也**无法发现写回交替**（它们只在静止状态读一遍文本）。
- **状态**：**仅登记，未修**（用户 2026-10-18 明确「先记录，不要做」）。本轮只做了**只读观测**：
  `DUMP_OPEN_FORMS` / `WPF_DUMP` / `WPF_TEXTLOG` / `CTRL_TREE` + `CMOLocalizer.log` / `CMOLocalizer_LiveAudit.log` 采样，
  未改任何代码、词典、配置，未重建、未打包、未重发。
  按台账规则应在（经授权的）修复时补：① 回归用例
  （`static` 词条存在性；`live` 单位状态/编队状态/条令窗口文本断言 + 覆盖形态几何断言 + **写回交替检测**——
  可复用 `WPF_TEXTLOG`：同一源串在未来 N 秒内的重写次数必须为 0/低值）；② `CHANGELOG` 对应版本段；
  ③ 视归属决定是「真缺词条」（补 `dict/src/*.json` + `build_dict.py`）还是「写回时机/控件宽度」（引擎侧）。
- **证据（均存 `evidence/R32/`，不入库）**：`unit_status_residual.png`（09:10 单位状态）、`doctrine_editor_residual.png`（09:12 条令）、
  `group_status_residual.png`（09:16 编队状态）、`mission_editor_dropdown_residual.png`（09:19 任务编辑器下拉）、
  `wpf_textlog_20261018_0920.json`（`WPF_TEXTLOG` 400 条原始记录，含 93/92/59 次重写与叠译实例）；
  现场日志：游戏目录 `CMOLocalizer.log`、`CMOLocalizer_LiveAudit.log`。
- **现象 F（悬停介绍/帮助文本整段英文，09:2x）**：把鼠标放在「作战阶段」上弹出的帮助框是整段英文：
  `The current phase of the mission in the Operation Planner` / `Waiting for trigger:`（含 `The mission has not yet started, the multi-mission units assigned to the mission, which aren't mobilized won't execute the mission.`）
  / `Satisfied:` / `Triggered:` 四段。查证：词典 `exact` **全部无**、`LiveAudit` 0 命中、`WPF_TEXTLOG` 0 命中
  ⇒ 属「悬停帮助/提示文本通道未覆盖」（不是写回覆盖，也不是已译被覆盖）。
- **现象 G（任务编辑器指派列表行的括号内英文，09:22）**：行形如 `Radar AN/FPS-89 (HP) 哈尔德 (Radar (AN/FPS-89 HP))`，
  红框处均为**括号内的英文片段**（`Radar` / `HP` / `AEW` / `CAEW`），其中 `Radar`（中文名「雷达」）**不是缩写**，
  与我方口径「有意保留英文只限 OSM 样式名/KPH/MPH/控件内部名/纯图片页」不一致 ⇒ 应译未译。
  查证：`Radar`（孤立词）不在 `exact`；`Airfield` 也不在（但 `Veteran`/`Loiter`/`Unassigned` **在**）。
- **【逐串归类（2026-10-18 实测，供修复时直接分派）】**：

| 串（用户截图中看到的英文） | 词典 `exact` | `LiveAudit`（未译） | `WPF_TEXTLOG`（已译） | 归类 |
|---|---|---|---|---|
| `Veteran`（截图里英文） | 有（`老手`） | 69 | ✓ 反复译入 | **已译但被游戏写回覆盖**（现象 C）|
| `Loiter` | 有（`盘旋待机`） | 24 | ✓ 59 次 | 同上 |
| `Fire:` / `Kills 0` | 有 | 0 | ✓ 93 次 | 同上 |
| `Unassigned`（状态行） | **有** | 0 | **✗** | **词典有但通道没送达**（孤例：需查该值走的是哪条路径）|
| `Airfield`（编队状态） | **无** | 26 | ✗ | **真缺词条** |
| `Active radar from Initial Point (IP) to Winchester` | **无** | 3 | ✗ | 真缺词条（下拉选项）|
| `Waiting for trigger:` / `Satisfied:` / `Triggered:` / `The current phase of the mission…` | **无** | 0 | ✗ | 真缺词条（悬停帮助长句）|
| `Radar`（列表行括号内） | **无** | — | ✗ | 真缺词条（非缩写）|

  结论：“有中文名却没译”的用户疑问，实际上是**三类不同原因**：① 已译但被写回覆盖；② 词典有但通道未送达；③ 词典真没有——
  不能用同一条“补词条”去处理，必须按上表分派。
- **证据（追加）**：`hover_tooltip_english.png`（悬停帮助整段英文）、`assign_list_paren_english.png`（指派列表括号内英文）。
- **现象 H（空中出动计划 ATO 工具栏按钮，09:25）**：`Create Flight` / `Delete Flight` 两个按钮英文（同一排其余按钮
  「复制飞行编队 / 编辑飞行计划 / 更改机型与挂载方案 / 更改起飞时间 / 修改到达目标时间 / 清除时间」均为中文）。
- **现象 I（对话框正文英文，09:26）**：弹窗标题「缺少任务或编队」是中文，**正文却是英文**
  `select a Mission and create at least one flight`（按钮「确定」中文）——同一弹窗内中英混杂。
- **现象 J（可用列表设施行，09:27）**：`A/C Hardened Aircraft Shelter (1x Large Aircraft)`、
  `A/C Weather Shelter (1x Medium Aircraft)` 等行完整英文（相邻行却是中文：「航空汽油 (200k 升 地下油罐)」「弹药库 (水面)」）。
- **归类表追加（均实测于 2026-10-18）**：

| 串 | 词典 `exact` | `LiveAudit`（引擎看到但未译） | 归类 |
|---|---|---|---|
| `Create Flight` | 无 | 3 | 真缺词条（按钮文本）|
| `Delete Flight` | 无 | 3 | 真缺词条（按钮文本）|
| `select a Mission and create at least one flight` | 无 | 1 | 真缺词条（对话框正文）|
| `A/C Hardened Aircraft Shelter` | 无 | 13 | 真缺词条（设施名）|
| `A/C Weather Shelter` | 无 | 5 | 真缺词条（设施名）|
| `(1x Large Aircraft)` | 无 | 6 | 真缺词条（部件，需与设施名拼接后整串或分段处理）|

  注：本次三张截图的英文全部属**第③类（词典真缺）**，且 `LiveAudit` 均有命中
  ⇒ **通道能碰到它们**，不涉写回覆盖，补词条即可望生效（与本轮写回覆盖类问题分开处理）。
- **证据（追加 2）**：`ato_buttons_english.png`（ATO 按钮）、`dialog_body_english.png`（弹窗正文）、
  `facility_rows_english.png`（设施行）。
- **现象 K（“会失效原来的中文”，09:34）**：用户反馈——原本已显示中文的文本会**退回英文**（中文失效）；
  本次截图红框为 `Airborne Early Warning (AEW)`（同一面板的 `Veteran`、`Gulfstream G550 AEW [Nahshon-Eitam, CAEW]`、
  `所属阵营: Israel`、行内 `(自动: Cruise)` 等也呈英文）。查证：`Airborne Early Warning (AEW)` **词典 exact 有**、
  但本次运行 `WPF_TEXTLOG` **译入 0 次**、`LiveAudit` 3 命中 ⇒ 属归类表第②类（词典有但通道没送达），
  与 `Unassigned` 同类；而 `Veteran`/`AEW`/`Cruise` 均在词典中且有大量译入记录 ⇒ 同屏内两类问题共存。
- **现象 L（频闪持续，第二段采样 09:32:40–09:39:07）**：`WPF_TEXTLOG` 又一窗口 400 条，同一批控件继续被重写：
  `Israel` **77 次**、`Veteran` **67 次**、`Kills 0` **67 次**、`Fire:` **64 次**、`航速: 350.0 节 (马赫 0.61) (Loiter)` 11 次
  （该变体里 `Loiter` 仍为英文）。⇒ 两个独立时间窗（09:13–09:18 / 09:32–09:39）均复现，与用户「又在频闪」一致；
  同一会话 `WRITE LOOP BLOCKED` 仍为 0。
- **归类表追加（第②类实例）**：

| 串 | 词典 `exact` | `LiveAudit` | `WPF_TEXTLOG` 译入 | 归类 |
|---|---|---|---|---|
| `Airborne Early Warning (AEW)` | **有** | 3 | **0** | 词典有但通道没送达（同 `Unassigned`）|
| `Unassigned` | 有 | 0 | 0 | 同上 |
| `Airborne Early Warning`（不带括号）| 无 | 7 | 0 | 真缺词条（变体形态）|
| `Gulfstream G550 AEW`（前缀）| 无 | 35 | 0 | 真缺词条（型号名可选用保留，需人工定口径）|
| `Cruise` / `AEW` / `Veteran` | 有 | 0–93 命中不等 | 有译入 | 已译但被写回覆盖（第①类）|
- **证据（追加 3）**：`unit_status_flicker_0934.png`（09:34 用户截图，红框 `Airborne Early Warning (AEW)`）。
- **现象 M（用户定位「是这里在频闪」，09:44）**：频闪位置 = **「目标状态」面板**（标题「目标状态」/「拉马特戴维」，MiG-25P 截图）。
  该面板可见文本：`MiG-25P Foxbat A` / `Fighter` / 按钮「目标报告...」/ `WRA: Aircraft - Increased manoeuvrabili`（截断）/
  `所属阵营: Syria` / `航向: 50.8 度` / `航速: 545 节 (马赫 0.95)` / `飞行高度: 40000 ft`（同一截图中「目标报告...」「所属阵营」「航向」「航速」标签为中文）。
  逐串查证（2026-10-18 09:35–09:44 采样）：

| 串 | 词典 `exact` | `LiveAudit` | `WPF_TEXTLOG` 译入 | 初步归类 |
|---|---|---|---|---|
| `Fighter` | 有 | **132** | 0 | ②词典有但通道没送达（与 `Unassigned` 同类）|
| `Syria` | 有 | 0 | 0 | ②（同屏「所属阵营」标签已中文化，值未译）|
| `WRA` | 有 | **159** | 0 | ② |
| `40000 ft`（整串） | 无 | 4 | 0 | **不算缺陷**：用户 2026-10-18 裁决「数值+单位不需要翻译」（project_memory 口径 13）|
| `ft`（孤立单位词） | 有 | 851 | 0 | 同上：属数值+单位形态，不需为它补词条 |
| `MiG-25P …`（型号名） | 无 | 10 | 0 | ③（型号名口径待人工定：保留或译）|

- **观测盲区（修复前必须先解决）**：该面板**对现有只读探针不可见**——
  `WPF_DUMP` 只列出 2 个 WPF 窗口（主窗口「现代行动：指挥」、弹窗「想定特性与拟真度选项」），**没有「目标状态」节点**；
  `CTRL_TREE`（全量）两帧差异 **0**、且无任何含 `Fighter`/`Syria`/`WRA`/`ft` 的控件；
  `DUMP_OPEN_FORMS` 只有 MainForm / NewMission / MissionEditor / DoctrineForm / AirTaskingOrder。
  ⇒ 该面板文本既不走 WPF ContentControl 通道，也不在 WinForms 控件树里（疑自绘 DarkForm），
  因此**无法用 `WPF_TEXTLOG`/`CTRL_TREE` 量化它的频闪**；要观测/修复它，得先新增探针或改用抓屏逐帧比对（R4 的老办法）。
  注：这与前面已能量化的写回交替（现象 C/L，单位状态/编队状态面板，`WPF_TEXTLOG` 可证）是**不同位置**。
- **证据（追加 4）**：`target_status_flicker.png`（09:44 用户截图，标注频闪位置为「目标状态」面板）。

---

## R33 数据库查看器与航空作业大量残留英文（DB 正文/枚举/记录名/复合字段全面补齐，v4.2.1）

- **触发与现象**：用户 2026-10-18 反馈数据库查看器（常规数据、详情正文）与航空作业窗口（挂载方案、物资）存在大量残留英文，具体包括：
  ① 数据库详情页复合字段：`Unmanned Aerial Vehicle (UAV) (WRA: Aircraft - Unspecified)`、`0 (Autonomy Level: ChangeableMission)`、`Undefined (Use Below) (ex: )`；
  ② 挂载航程与飞行剖面：`2200 nm Recon radius. Cruise at optimum altitude and speed. No form-up time. 10% reserves.`；
  ③ 挂载方案名与武器物资名：`Comms Monitor and Jammer`、`1x Generic Comms Jammer Pod [Advanced]`；
  ④ 空中出动计划 (ATO) 按钮（`Create Flight` / `Delete Flight`）、缺少任务弹窗正文（`select a Mission and create at least one flight`）、悬停介绍长段落（`Waiting for trigger...`）、机堡机棚设施名等。
- **根因分析**：
  1. **描述正文缺口与管道静默丢弃**：DB/Descriptions（62k 行）中存在 5.76% 缺口（1,252 唯一待译行），且 `build_dict.py:sanitize()` 的括号平衡检查会静默删除 64 条全角括号微失衡的有效译文（导致无论译多少次都进不了词典）；
  2. **记录名与扩展枚举长期未纳管**：数据库记录名（DataLoadout 8.8k、DataWeapon 4.5k、DataMount 3k）与扩展枚举（Enum* 2.1k）此前从未系统批量汉化，缺乏专门词库源文件；
  3. **游戏引擎动态拼接复合串**：WRA 后缀、自主等级参数、座舱世代示例、航程句号变体（`nm Recon radius.`）、`1x ` 数量前缀在运行时动态拼接，导致整串精确查表必然落空；
  4. **之前修复模式缺陷**：此前采取“按用户截图逐条修补”模式，未建立全量盘点和硬门禁，导致修一条漏百条。
- **修复与验证**：
  1. **正文清零与管道修复**：排除参考文献/鸣谢等口径保留项后，对剩余 96 条正文全部翻译入库；用 `db_desc_bracket_fix.py` 镜像修复 64 条被丢弃的译文，使 `dict/src/db_descriptions.json` 达 16,328 条且 100% 进入最终词库；
  2. **扩展枚举与记录名全量入库**：生成 `dict/src/db_enums_extra.json`（769 条）与 `dict/src/db_record_names.json`（9,904 条），覆盖全部常见战术装备/挂载构型；
  3. **引擎动态规则与复合解析**：在 `CMOLocalizer.cs` 落地 4 条复合串与数量前缀解析规则，并在 `TranslateFlightProfile` 补齐 Recon radius 等句型；
  4. **UI 补全**：`dict/src/ui_cleanup_terms.json` 扩充 29 条 UI 按钮与悬停长句；
  5. **门禁加固**：新增 `R33-static-db-records` 硬门禁，`gate_db_templates` 断言描述完整性；C# 独立单测 `build/test_db_translation.exe` 实测 6 个目标串全部 100% 翻译成功；`exact` 词典规模从 46,127 突破至 57,051。
- **状态**：已修并验证（2026-10-18）。

---

## R34 中文地名瓦片加载停滞：瓦片服务占用游戏共用线程池（v4.2.2，外部 PR#1 合并）

- **来源**：外部贡献 PR#1（@penguin-madagascar，`fix/chinese-map-tile-starvation`），2026-10-18 经独立复现与实机验证后完全合并（squash `371d98c`）。
- **现象**：中文地名标签加载停滞——地图瓦片下载繁忙时本地标签请求长时间不返回（表现为标签不出现）。该缺陷同时会**反向拖慢游戏自身**（占用宿主的线程池）。
- **根因（旧 `StartLocalTileServer` / `HandleTileClient` 三处）**：
  1. `ThreadPool.QueueUserWorkItem(_ => HandleTileClient(...))` 把标签请求排进**游戏共用的 CLR 线程池**——⑨ pool worker 被地图下载占满时本地请求根本不执行（实测：池占满后请求 1.5s 超时、拿不到瓦片）；
  2. 请求行用 `StreamReader.ReadLine()` 读取且**没有总截止时间**——客户端每 250ms 发 1 个字节即可无限刷新单次读超时，一个半开连接**永久占住一个池 worker**（slowloris 式占用）；
  3. **连接数无任何上限**——96 个空闲连接可占掉游戏 96 个线程池 worker（实测进程线程数与连接数同步增长），直接损害宿主游戏。
- **修复**：`src/engine/CMOLocalizer.cs` 地图服务段（归一化行尾后 178 行）——1 个专用接受线程 + 4 个专用工作线程 + 最多 64 项 `BlockingCollection` 队列，**不再触碰共享池**；首行 1500ms 总预算（从接受时刻起算、含排队时间）、首行上限 1024 字符、按剩余预算设置读超时；响应写超时 1500ms；严格校验 `GET /MapTiles/Labels/z/x/y.png HTTP/1.x`（z≤22、x/y 越界拒绝，非法请求 400）；请求/拒绝/错误共采样前 16 条诊断日志。
- **用户可见验收**：清空标签缓存后游戏自身请求全部被正常服务且瓦片非空，地图标签中文正常显示、缩放正常。
- **回归测试点**：`static` `R34-static-tile-contract`（并发契约：脱离共享池/有界队列/专用线程/总预算/拒绝非法请求/诊断上限）；`R34-static-tile-harness`（独立编译后真跑 7 模式：starvation / protocol / idle / batch / multi-idle / trickle / pressure，宿主 `src/tests/MapTileServerTests.cs` 反射启动真实引擎，不加载游戏）；`static` `R0` 部署标记新增 `ReadTileRequestLine` / `tileRequestLogLock`（防旧包把无修复引擎盖回去）。
- **实测证据（2026-10-18）**：
  - 独立复现（同一测试宿主分别对旧/新引擎各跑 7 模式）：旧实现 `starvation` 请求超时、`trickle` 连接一直不关、`pressure` 96 个空闲连接不受限；新实现 7/7 通过（`POOL max=16 entered=16 available=0` 仍返回有效 PNG；`TRICKLE ... closed ms=1513`；`PRESSURE clients=96 closedEarly=28 closedTotal=96 threadsBefore=13 threadsDuring=13`）。
  - 实机（Steam BuildID 25865381 / 游戏 Build 1900.23）：移走中文标签缓存后启动游戏，引擎日志 16 条 `LocalTile z=3 ... rendered=1..17 bytes=... ms=0..17`，**`rejected` 计数为 0**（新的严格请求行校验完全接受游戏真实请求），游戏新抓取 40 张 z=3 瓦片，地球标签中文正常。
- **已知取舍**：过载时允许拒绝（队列满或预算耗尽即关闭连接），不保证任意负载下全部请求成功；游戏会在后续加载中重新请求。
- **状态**：已合并并验证（v4.2.2）。

---

## R35 游戏运行中安装报「安装包内未找到 CMOLocalizer.dll 资源文件」（v4.2.2）

- **现象**：2026-10-18 21:02–21:03 用户连续四次安装最新安装包，全部报
  `[错误] 安装失败: 安装包内未找到 CMOLocalizer.dll 资源文件！`。
- **影响面**：只在「游戏正在运行」且「安装包所在目录没有 `CMOLocalizer.dll`」（例如在下载目录直接双击）时出现；
  开发者/回归从 `build/` 运行安装器时那里躺着 DLL，走的是磁盘复制分支，所以长期没暴露。
- **根因（三步，2026-10-18 实测复现）**：
  1. 运行中的游戏已把 `CMOLocalizer.dll` 作为映像加载，Windows 拒绝任何替换/覆盖/重命名——
     实测 `MoveFileEx(REPLACE_EXISTING)` 返回 **Win32 5 拒绝访问**（独占句柄同理）；
  2. `InstallerCore.ExtractEmbeddedResource` 用 `catch { }` 把所有异常吞掉后返回 `false`，
     调用方无从区分，只能报「资源缺失」——**把「写不进去」误报成「资源不存在」**，用户完全无从下手；
  3. 安装器（以及此前的 `scripts/deploy.sh`）都是原地覆盖写，没走「临时文件 + 原子替换」；
  4. 日志只写到界面日志框（`txtLog`）不落盘，异常又被吞掉——用户侧根本没有可分析的证据
     （用户 2026-10-18 反问「你是不是没打报啊」正是这个）；
  5. **另一台电脑的对照**：那台机器没开游戏也报同一句错。首要原因是**游戏目录不可写**：
     Steam 默认装在 `C:\Program Files` 下，未提权的普通权限建不了文件（实测：受限目录下
     安装器探测写探针报「访问被拒绝」），而旧实现把它同样吞成「资源缺失」。
- **修复**：
  1. 写入统一改为「写同目录临时文件 → `MoveFileEx` 原子替换」，失败时清理临时文件；
  2. `ExtractEmbeddedResource` 新增 `out error`，区分「资源缺失」与「写入失败」，
     文案带上目标路径、真实原因与处置建议（「若游戏正在运行，先关闭游戏后重试」）；
  3. GUI 安装失败且检测到 `Command.exe` 在运行时，弹窗**征得同意**后关闭游戏并自动重试（绝不静默杀进程）；
  4. 新增 R35 回归用例锁住资源释放分支与错误文案。
  5. 新增**目录可写性预检**：不可写时直接给出「游戏目录不可写 + 具体原因 + 需以管理员身份运行」
     的指引（不再让用户看到「资源缺失」）；GUI 弹窗可直接**以管理员身份重启安装器**重试。
  6. 日志同时**落盘**到 `%LOCALAPPDATA%\CMOChinese\installer.log`（含版本、游戏目录、静默失败原因），
     报障时可直接把这个文件发来。
  7. 回归补两例：R36 安装包**内嵌载荷齐全且与构建产物逐字节一致**（直接封死「忘记打包」）；
     R37 目录不可写时文案必须指向权限、不得误报资源缺失。
- **用户可见验收**：安装失败时看到的是「写入 <路径> 失败: …」而不是「资源文件未找到」；
  在 GUI 里同意后，安装器可自动关闭游戏完成安装。
- **回归测试点**：`R35-resource-release`（从「只有 exe」的目录安装 → 内嵌资源释放，DLL/JSON 与仓库逐字节一致、无 `.cmo_new` 残留）；
  `R35-locked-error`（独占锁住目标 DLL → 返回非 0 且文案含「写入/失败」、不含「未找到」）；
  `R36-installer-payload`（安装包内嵌载荷齐全，引擎 DLL/词典 JSON/更新助手/地名集与构建产物逐字节一致）；
  `R37-dir-not-writable`（icacls 拒写当前用户 → 文案含「不可写/管理员」、不含「未找到」）。
- **状态**：已修并验证（2026-10-18）。

---

## R38 自绘面板残留英文：池级注入覆盖不足（v4.2.2）

- **来源**：用户 2026-10-18 截图「目标状态」面板：`WRA: Aircraft - High manouverability fighter (…)`、
  `Multirole (Fighter/Attack)` 等**库值/枚举值**一直是英文（同一面板的标签却是中文）。
- **根因**：这类面板是**自绘面板**，不经我们的控件 / WPF / WebView 通道，直接从游戏**字符串池**取串绘制；
  而池级注入表（`dict.offsets`）只覆盖 3,522 条精选偏移。实测「池里存在且已有译文」的显示串有 8,000+ 条
  从未注入，`Aircraft - High manouverability fighter (F-14, …)` 正在其中（offset 1279828）。
- **修复**：
  1. 新增 `dict/src/pool_display_inject.json`（6,187 条，`build/gen_pool_display_inject.py` 生成）与
     `dict/src/db_enum_displays.json`（150 条数据库枚举显示值），加入 `build_dict.py` 的 `offset_safe_sources`；
     `offsets` 3,522 → **8,735**；
  2. **安全护栏（本轮最关键的教训）**：池注入是在游戏内存里改串，注入到 SQL 片段/格式串/控件名会当场改坏游戏。
     新增 `scripts/inject_filter.py` 作为单一事实源：
     - `injection_safe()`（严格）：机器生成的注入源必须过形状判定（多词短语、无花括号/反斜杠/占位符、
       非 SQL 片段、非短 CamelCase 标识符、非派发 token、非 IDs/blacklist）；
     - `harmful_shape()`（窄口径）：只用它剪**基座词典**里明显危险的形状，绝不能顺手剪掉
       `Options` / `General` / `Unit System` / `Acoustic (1-10 kHz)` 这类既有界面标签
       （2026-10-19 实测：用宽口径剪枝会让快捷键窗口退回英文）；
     - 人工白名单 `force_offsets.json` 的注入不受剪枝影响（如 `7916` → 创意工坊）。
- **回归**：`R38-static-pool-inject` —— ① 自绘面板串必须已注入；② `LongName ASC` / `Description ASC` /
  `FULLNAME ASC` / `{0} seconds` / `Get-AuthenticodeSignature "` / `e.Message: {0}` / `Aircraft` / `Ground Unit`
  等危险串必须**未**注入；③ 合法标签（`Acoustic (1-10 kHz)`）不得被误杀；④ 生成器与字符串池不漂移。
- **实测证据**：实机 `POOL_DUMP` —— offset 1279828 在游戏池里已是「飞机 - 高机动性战斗机 (F-14, F-15, F-16, MiG-29, Su-27)」，
  1185394 已是「多用途 (战斗机与攻击机)」；`Aircraft`（单词 token）保持英文；`DB_NAVIGATE_ID 1` 详情页 45,189 字正常。
- **附带加固（实机用例）**：R1 系列曾因「查看器异步初始化 + CPE 宣传窗遮挡」假红。已加
  `_close_cpe_banner()`（`CLOSE_FORM CPEBannerForm`）、功能性就绪探针（`DB_NAVIGATE_ID` 能取到详情才算就绪，
  不再用 `DarkListView.Items.Count`——虚拟列表恒为 0）与一次必要性重试。
- **状态**：已修并验证（2026-10-18/19）。

---

## R39 条令下拉框英文前缀：`Inherited, ` + 已汉化值（v4.2.2）

- **来源**：用户截图「作战任务设置」下拉值 `Inherited, 始终开火`（同屏其它项都是「继承自上级, …」）。
- **根因**：游戏把英文前缀 `Inherited, ` 与**已经翻译过**的值（来自池/数据库）拼成一句 → 整串含中文；
  引擎的 `Inherited, …` 规则位于 `!ContainsChinese(raw)` 守卫之内，于是整串被跳过、英文前缀留在界面上
  （`TranslateComboItemText` 顶部也有同样的「含中文即返回」早退）。
- **修复**：新增 `FixMixedEnglishPrefix()`（只替换已知英文前缀，尾部原样保留，不重译），并在
  `TranslateString`（早退之前）、`TranslateComboItemText`、WPF `TextBlock`/`ContentControl` 值变更钩子里调用。
- **回归**：`R39-static-mixed-prefix` —— 源码契约 + 用**真引擎 DLL** 跑 `build/test_db_translation.cs`
  的 11 个用例（含 `Inherited, 始终开火 → 继承自上级，始终开火`）。
- **实测**：实机 `XLATE Inherited, 始终开火` → `继承自上级，始终开火`；`Inherited, 战斗机与反潜战(ASW)` 同理。
- **状态**：已修并验证（2026-10-19）。

---

## R40 数据库查看器类型无法选择、筛选失效且装备列表空白（v4.2.2）

- **来源**：用户 2026-10-19 截图反馈「数据库查看器又炸了，类型无法选择 aircraft，筛选条件也用不了了，装备列表全部空白」。
- **根因**：
  1. **SQL 投影列与条件片段被误注入字符串池**：R38 为覆盖自绘面板引入了池级注入表 `pool_display_inject.json`，其中误收录了 `, Hypothetical`（offset 1431010）、`, Deprecated`（offset 1429924）、`Name LIKE '%`（offset 152724）等底层 SQL 片段，分别被注入为 `, 假设型号`、`, 已废弃`、`名称 LIKE '%`；
  2. 游戏打开数据库查看器（`InternalDBViewer`）加载平台时执行 SQL 查询：`SELECT ..., Deprecated, Hypothetical FROM DataAircraft WHERE ...`，因池中被篡改变成 `SELECT ..., 已废弃, 假设型号 FROM ...`，SQLite 底层抛出 `no such column: 假设型号`，窗体初始化与加载流程在填充 `CB_SubType` 后抛出未捕获异常中断；
  3. 后续的 `CB_Hypothetical`、`CB_Country` 和 `ListBox1` 被跳过，全部未初始化（项数恒为 0）；且切换任何装备类型都会因 SQL 语法错误而无法加载列表；
  4. 此外，`LocalizeComboBoxSafe` 与 `LocalizeListBoxSafe` 调用了 `EnterMutation` 递归守卫，但在 `finally` 块中缺少 `ExitMutation()`，导致多控件轮询时守卫深度打满（`MUTATION DEPTH CAP(8)`）。
- **修复**：
  1. `scripts/inject_filter.py` 增加铁律：严格拦截以逗号开头的列投影（`^\s*,`）、含 SQL 条件（`\b(LIKE|NOT LIKE)\b`）、含别名（`\bAS\s+[A-Z]\w+`）、比较符结尾等代码与 SQL 片段；
  2. `build/gen_pool_display_inject.py` 统一切换为调用 `inject_filter.injection_safe` 单一事实源，杜绝过滤规则漂移，重新生成词典；
  3. `src/engine/CMOLocalizer.cs` 在 `LocalizeComboBoxSafe` 与 `LocalizeListBoxSafe` 补齐 `finally { ExitMutation(); }`。
- **用户可见验收**：
  打开数据库查看器，类型为 Aircraft 时，国家/阵营下拉框正常显示 190 个国家，假想下拉框正常显示 3 项，装备列表正常列出 712 款战机，点击条目详情 WebView2 正常生成 2.5~4.5 万字节的中文解析内容。
- **回归测试点**：
  - `static`：`R38-static-pool-inject` 硬断言 `, Hypothetical`、`, Deprecated`、`Name LIKE '%`、`Description LIKE '%` 绝不进入 `offsets` 注入表；
  - `live`：`verify_regressions.py --live` L0 与 R1 用例（八类对象全部切换、点击行生成详情 HTML、无频闪、真机 16/16 全绿）。
- **状态**：已修并验证（2026-10-19）。

---

## R41 地图完全没有文字：瓦片图层标识符被池级注入（v4.2.2）

- **来源**：用户 2026-10-19 反馈「怎么地图都不显示文字了」——地图底图正常，但国名/城市名/海域名一个都不显示。
- **根因**：R38 扩大池级注入时，把**地图瓦片图层的引擎标识符**当成显示串注入了游戏内存：
  `Stamen Terrain - Labels ` / `Stamen Terrain - Lines ` / `Stamen Lines` / `Stamen Labels` /
  `Stamen Terrain ` / `Stamen Terrain` / `Unsupported layer for tile loading: `。
  这些串同时是**图层查找与缓存目录的键**（缓存路径由 `/WW/Cache/{0}/{1}/{2}/{3}.png` 拼出），
  被翻译后标签图层解析失败：既找不到缓存目录、也不再请求瓦片 → 整张地图没有任何地名。
- **可观测特征（很强的定位口径）**：`CMOLocalizer.log` 里从此只有
  `LocalTile rejected GET /MapTiles/Labels/g`（非法探测请求，把 16 条诊断配额全占掉），
  再没有 `LocalTile z=… x=… y=… candidates=… rendered=…` 的渲染行；
  池里能直接看到 `1789768: 'Stamen 标注'` 这类被译掉的层名。
- **修复**：`dict/src/identifiers.json` 追加上述 7 条（该文件的语义正是「可以显示、但**绝不**做池级注入」），
  重建词典后 `offsets` 8756 条、这些串全部只留在 `exact`。
- **用户可见验收**：载入想定后地图显示「中国 / 越南 / 南海 / 阿拉伯海 / 孟加拉湾 / 马尔代夫 / 印度洋…」等中文地名；
  日志重新出现真实瓦片请求与渲染行。
- **回归测试点**：新增 `static` 用例 `R41-static-tile-layer-ids` ——
  ① 7 条图层标识符必须在 `identifiers.json` 里；② 必须**不在** `dict/CMOLocalizer.json` 的 `offsets` 中。
- **状态**：已修并验证（2026-10-19）。

---

## R42 「Inherited, 中文」中英混排：修正逻辑被「含中文就跳过」守卫挡在门外（v4.2.2）

- **来源**：用户 2026-10-19 截图反馈——任务编辑器「辐射控制 / 武器使用授权」表里大量单元格显示
  `Inherited, 持续制导至命中`、`Inherited, 自动在 150 英尺悬停`、`Inherited, 装载设置` 这类
  英文前缀 + 中文尾的混排，且**比上一轮 R39 更多**。
- **根因（两层）**：
  1. **接线断了**：R39 把 `FixMixedEnglishPrefix` 接进了 `TranslateString` 与
     `TranslateComboItemText`，但组合框/列表框的**条目循环**在调用翻译之前就先
     `if (ContainsChinese(raw)) continue;`。而混排串恰恰「整串含中文」，于是永远到不了修正逻辑
     —— 函数是好的、门禁是绿的、界面还是英文。门禁只测函数、没测接线，所以拦不住。
  2. **越修越多**：池级注入不断把更多**值**变成中文（R38 扩充后尤甚），游戏把值拼到英文前缀后面，
     混排串的数量就随词库增长而增长。这就是「又出现、而且更严重」的机制——不是代码退化，
     是修复覆盖率跟不上注入覆盖率的增长。
- **反证过的错解**：曾在池里强制注入前缀 `Inherited, `（偏移 599936）→ `继承自上级，`。
  实测反而更差：原本能整串命中的 `Inherited, Tight` 被改写成 `继承自上级，Tight`（即
  「中文前缀 + 英文尾」，因为值 `Tight` 自己没有池注入）。**前缀是显示片段、不是翻译单元**，
  正确解在控制层，不在池层。该注入已撤销。
- **修复**：
  - 新增 `TryFixMixedBeforeChineseGuard(s)`：整串含中文时先尝试修英文前缀，返回 `null` 才交回
    旧的「含中文就跳过」逻辑；`TranslateComboItems` 与 `LocalizeListBoxSafe` 改为该顺序。
  - `FixMixedEnglishPrefix` 泛化为「词边界 + 可选逗号/空白」，同时覆盖
    `Inherited, X`（实测主形状）、`Inherited,X`、`Inherited X`，并且不误伤 `InheritedFrom…`。
- **用户可见验收**：条令窗口与任务编辑器的下拉项不再出现英文 `Inherited` 前缀；
  实测 `继承自上级，悲观估计 / 限制开火 / 始终开火 / 持续制导至命中` 齐全。
- **回归测试点**：
  - `static`：`R42-static-mixed-prefix-wiring` —— ①源码顺序断言（两个条目循环里修正逻辑必须先于
    `ContainsChinese(...) continue`）②编译并真跑 `src/tests/MixedPrefixTests.cs`（12 个断言）。
  - `live`：`L-mixed-prefix` —— 扫全进程所有下拉项，断言**不存在**「英文 Inherited 前缀 + 中文尾」
    （实测 202 个下拉框 / 653 条目 / 0 条违规）。比逐窗口截图可靠，因为任务编辑器与条令窗口
    共用同一条条目翻译通道。
- **状态**：已修并验证（2026-10-19）。

---

## R43 单位状态面板中英频闪（第二次回归）：熔断器把「正常刷新」当成拉锯并永久拉黑（v4.2.2）

- **来源**：用户 2026-10-19 第二次报同一症状，并补充关键现象——**推演运行时才闪，暂停时不闪**。
- **实测证据**：`CMOLocalizer.log` 里 27 条 `WRITE LOOP BLOCKED WPF TextBlock`，堆栈直指
  `Command.UnitStatus_WPF.RefreshPanel ← Command.RightColumnWPF.RefreshPanels ← MainForm.BZIfsCBS3b
  ← System.Windows.Forms.Timer.OnTick`。即：**游戏自己的刷新定时器**每个脉冲把同一段英文写回面板，
  我们每帧改回中文，`AllowWrite` 按「同一目标值重复 6 次」计数，数到就**永久**拉黑该控件。
  于是先中英频闪，熔断生效后面板永久停在英文（就是用户截图里 `状态: Unassigned`、`Air Base`）。
- **第二个盲区**：`TranslateWpfWindows()` 只枚举**顶层** `HwndWrapper` 窗口，而单位状态侧边栏是
  `ElementHost` 承载的 WPF 子树，永远枚举不到；每 8 帧的全量补扫因此补不到它，只靠 600ms 的
  WinForms 遍历兜底——运行时游戏按脉冲重建元素，等不到下一次慢扫。
- **修复**：
  1. 熔断器由「重复计数即永久拉黑」改为**令牌桶**：20 次/秒以内的写回一律放行（定时刷新不受伤），
     超速只进 1.5s 冷却且**到期自动恢复**，绝不会把控件永久留在英文。
  2. 新增 `TranslateWpfElementHostsNow()`，每帧从 WinForms 控件树反查 `ElementHost.Child` 并补扫，
     把英文窗口期压到一帧以内。
  3. `WPF_DUMP` 增加 ElementHost 子树导出——原来这块是观测盲区，看不到就没法证明修好了。
- **用户可见验收**：推演运行中连续采样 14 帧，同一元素在中英之间翻转 **0** 次；本次进程
  `WRITE LOOP BLOCKED` **0** 条（修复前同类进程 27 条）。
- **回归测试点**：`live` 用例 `L-panel-flicker`（加载想定 → 选中单位 → **开始推演** → 采样 14 帧
  断言零翻转 + 不新增熔断）。判据必须让推演真的跑起来，暂停状态测不出来。

## R44 半译条目（`AvGas (200k Liter 地下 Tank)`）与「换个装备就不会译」：缺的是组合规则（v4.2.2）

- **来源**：用户同一次反馈中的第二半——「还有没汉化的地方」，并要求「举一反三，别下次换个装备就不知道写什么中文」。
- **实测分类**（`XLATE` 探针）：
  - `Status: Unassigned` 有整句译法但界面半译；`151 ft` **没有规则**，整串查表必然落空；
  - `AvGas (200k Liter Underground Tank)` 的**词典值本身**就是 `AvGas (200k Liter 地下 Tank)`
    ——生成器不认识 `Tank/Liter/AvGas` 这些词，把生词原样留在中文里；
  - 全库统计：**872 条**中文值里残留拉丁词，其中「短显示串」355 条（其余是长引用/URL，属正当保留）。
- **根因**：装备/设施名天生是「名称 + 数量 + 规格/容器」的**可组合结构**，而实现只做整串查表，
  于是每出现一个新的容量/数量组合就要手写一条；生成器遇到不认识的词就半译。
  历史上（R8）曾试过「按词猜译」并因产出中英夹生串而整块删除，所以这次必须换一种做法。
- **修复（结构交给代码、词汇交给数据）**：
  - 新增术语表 `dict/src/terms.json`（127 条），经 `build_dict.py` 写进产物 `words` 段
    （引擎里这个段本来就有槽位，只是从来没用过）。
  - 引擎新增 `TryComposeTranslate`：①`<名称> (<规格>)` ②`<数字> <单位>` ③整串术语最长匹配。
    **任何一个词不在术语表/词典里就整体放弃**——宁可留英文，也绝不产出新的夹生串。
  - 词典侧新增 `scripts/compose_terms.py`（与引擎规则一一对应），`build_dict.py` 用它自动修复
    含生词残留的条目：本次修复 **69 条**（`AvGas (…)`、`A/C Underground Aircraft Shelter (…)` 等）。
  - 术语表**让位于词典**：已有译法（`Hardened Aircraft Shelter → 加固机堡`）优先，绝不另造第二种写法。
  - 修复过程中发现并修掉一个真实接线错误：组合规则最初被插到 `TranslateLoadDocTag` 尾部，
    是**死代码**（`XLATE` 全部不变才暴露出来）——已挪到 `DoTranslate` 收尾处。
- **用户可见验收**：`XLATE` 实测（均为词典里**没有**的组合）：
  `151 ft → 151 英尺`、`Airfield (73 units) → 机场（73 个单位）`、
  `AvGas (780k Liter Underground Tank) → 航空汽油（780k 升地下油罐）`、
  `Hardened Aircraft Shelter (6x Medium Aircraft) → 加固机堡（6 个中型飞机）`；
  未知词（`Domain Kill`）原样保留，不产生夹生串。
- **回归测试点**：`static` 用例 `R44-static-compose-rules`（结构规则成句 / 生词一律放弃 /
  术语已进产物 / 不存在「本可组合修复却仍半译」的条目）。

---

## R45 频闪的第三层：译文自我嵌套（每刷一次多裹一层）+ 控件级「含中文就跳过」挡住混合串（v4.2.2）

- **来源**：用户第三次报同一症状（截图圈的是 `跑道 (2600m)` 处），并明确「还是在频闪」。
  R43 修的是熔断误杀与每秒补扫，面板确实不再跳英文了，但**另一个通道还在动**。
- **决定性证据**：`WPF_TEXTLOG` 抓到写回日志
  ```
  'ACTIVE'         -> '主动 (Active)'
  '主动 (Active)'  -> '主动 (主动 (Active))'      ← 每被刷一次多裹一层
  'PASSIVE'        -> '被动 (Passive)'
  '被动 (Passive)' -> '被动 (被动 (Passive))'
  ```
  游戏每个脉冲把界面文本刷回并重新触发翻译，而 `TranslateBracketSegments` 会把
  `主动 (Active)` 里的 `(Active)` 当成"英文载荷"再翻一遍——翻译**不幂等**，
  于是文本一次比一次长。肉眼看到的就是字在闪/在变。
- **第二个漏点**：`TranslateString` 对「整串含中文」直接早退。而池注入与上游汉化只覆盖**标签**，
  值仍是英文，于是 `状态: Unassigned`、`所属阵营: Israel` 这类最常见的混合形状
  永远进不了翻译。**同一道守卫在控件级还有三份副本**（每帧补扫、TextBlock 回调、
  ContentControl 回调），所以即使 `XLATE` 能翻，控件上也照样不动——这次是
  `TextBlock_UnitStatus` 实测仍然显示 `状态: Unassigned` 才暴露出来。
- **修复**：
  1. 括号片段：译文里若已包含原文（`主动` + `(Active)`），判定为「中文 + 英文提示」，
     原样保留，不再嵌套。
  2. 新增 `TranslateMixedValueTail`：`中文标签: 英文值` 的右值单独翻译后重组
     （只处理窄形状：左半含中文、右半纯英文且可整串查到译法）。
  3. 控件级三处守卫统一改为 `HasAsciiLetter(...)`：只要串里还有 ASCII 字母就交给翻译，
     幂等性由 `TranslateString` 保证，不再由各处重复判断「含中文就跳过」。
  4. 翻译入口加**通用水位闸门**：候选译文若「比原文长且原文整体出现在译文里」，拒绝。
- **用户可见验收**（推演运行中、教程想定、18 帧采样）：
  `状态：未分配任务`、`所属基地：无`、`所属阵营：以色列`、`未选择单位` 全部正确；
  中英翻转 0 处、`WPF_TEXTLOG` 叠加迹象 0 条、`WRITE LOOP BLOCKED` 0 条。
- **回归测试点**：`static` 用例 `R45-static-translate-idempotent`
  （①幂等 `T(x)==T(T(x))` ②连续三次翻译**括号层数收敛** ③中文标签带英文值必须整条汉化）。
  注意判据不是"结果不许比原文长"——`Active → 主动 (Active)` 是本项目刻意的
  「中文（英文提示）」写法，只加一次就稳定；真正要禁的是每刷一次多裹一层。

---

## R46 修好了却"没生效"：`CompositionTarget.Rendering` 在静止面板上永不触发（v4.2.2）

- **来源**：用户第四次报同一处闪烁（圈的是 `雷达 (AN/FPS-100)` 那行），并指出「老手不闪了，但雷达还闪」。
- **这一轮最贵的教训**：不是"某处翻译写错了"，而是**修好的代码根本没被执行**。
  - R43/R44 用 `CompositionTarget.Rendering` 做"每帧补扫"。这个事件**只在 WPF 真正绘制时**触发；
    单位状态侧边栏是静止的，于是整个会话里它一次都没跑过。
  - 实测证据：探针计数 `renderTicks = 0`（而 `elementHostScans` 在改成 Timer 驱动后立刻到 4578）。
  - 结论：R43/R44 里那部分"每帧补扫"等于**从未安装**，这解释了为什么用户连续多次反馈"还在闪"。
- **第二个致命细节**：`SetTextKeepBinding` / `SetContentKeepBinding` **不检查控件当前值**。
  高频补扫（40ms）下反复把同一个中文值写回同一个控件，WPF 每次都要走绑定/重绘流程，
  静止面板于是被持续重画——这本身就是可见的"闪"。
- **修复**：
  1. 补扫改成 **Timer 驱动**（40ms，重入保护），并在引擎初始化时立即启动
     （原来挂在 `TranslateWpfWindows()` 里惰性安装，实测部分会话根本没触发）。
  2. 两个回写函数加**幂等短路**：目标值等于控件当前值就直接返回，不写、不重绘。
  3. 新增 `LayoutUpdated` 钩子：游戏**重建**面板元素时，新元素会在布局阶段同步触发，
     此时改写仍在一帧内完成，英文没有上屏机会（依赖属性钩子对"新建即带初值"的元素不触发）。
  4. 新增进程内诊断：`FLICKER_PROBE`/`FLICKER_RESULT`（按树内位置路径采样，能跨元素重建跟踪同一处文本）、
     `TRACE_START`/`TRACE_STOP`/`TRACE_DUMP`（事件级记录每一次 SET 与我们的 WIRE，含 `renderTicks`
     与 `elementHostScans` 计数）。
- **用户可见验收**：屏幕像素采样 300 帧 / 18 秒，**变化 0 次**；门禁内 `补扫次数 32298→32680`
  （节拍照常）且 `像素 40 帧变化 0 次`。
- **回归测试点**：`live` 用例 `L-repaint-tick` —— 先断言 `elementHostScans` 在增长（证明节拍活着），
  再用**屏幕像素**判定是否肉眼可见地闪。判据必须包含"代码是否真的在跑"这一层，
  否则"修好了但没执行"这类问题永远测不出来。
- **配套教训**：诊断探针本身也会骗人。第一版 `FLICKER_PROBE` 在 UI 线程里 `Thread.Sleep`，
  把消息循环卡住 ⇒ 游戏定时器不跑 ⇒ 报告"零变化"；第二版用对象哈希做身份 ⇒ 元素被重建时
  被当成新元素 ⇒ 还是报告"零变化"。改成"后台节拍 + 树内位置路径"后才看到真相。

---

## R47 打开任务编辑器后界面明显卡顿：补扫节拍被窗体数放大（v4.2.2）

- **来源**：用户 2026-10-20 反馈「一打开任务编辑器就很卡」。这是 R43/R46 引入高频补扫后的副作用。
- **实测定位过程（先说结论，再说方法）**：
  - 命令响应正常（`DUMP_OPEN_FORMS` 稳定 0.6s），说明卡的不是引擎的命令通道；
  - 进程 CPU 160%；**关闭任务编辑器后仍是 162%**，暂停推演后升到 191%；
  - **决定性对照**：把汉化引擎整个停用（换回纯英文 `Command.exe.config`）重启，CPU 仍是 **113.8%**，
    与汉化模式 121% 基本相同 ⇒ **这台机器上游戏进程本身就高 CPU，卡顿主因不是汉化层**；
  - 但顺着这条线仍查出一个**真实的汉化层缺陷**（见下），必须修。
- **真实缺陷**：`StartWpfRepaintTick` 的 40ms 定时器对**每个窗体**各投递一次补扫，
  而补扫本身又是"遍历全部窗体"。打开任务编辑器后窗体数量增加，投递数被放大到
  **67 次/秒**（设计值 25 次/秒），每次都要走完整棵 WPF 视觉树。
- **第一版修复踩的坑**：改用 `Timer.Change` 动态改周期做退避，结果把节拍整个停掉
  （实测补扫 0 次）。教训：定时器的自限流不要靠改周期，靠"到点才做"更稳。
- **最终修复**：
  1. 只投递**一次**（选首个可用窗体作锚点），不再按窗体数放大；
  2. 固定 40ms 周期 + **自限流**：距上次真正补扫不足当前周期就直接返回；
  3. **自适应退避**：连续 5 拍没有任何文本被改写，周期退到 200ms；一旦有改写立刻回到 40ms；
  4. `busy` 标志一律在 `finally` 释放，绝不跨线程持有。
- **验收**：编辑器打开/关闭两种状态下补扫频率都稳定在 **约 222ms 一次**（空闲退避生效），
  且不随窗体数量变化；`L-repaint-tick` 仍然 PASS（补扫在跑 + 像素 40 帧 0 变化）。
- **教训（值得记住）**：用户报"卡"时，不要默认是自己刚改的代码。
  先用「停用汉化引擎」做对照，能直接把"游戏本身"和"汉化层"分开；
  这次对照只花了一次重启，却把结论从"我的补扫太快"纠正成"游戏本身高 CPU + 补扫确实有放大问题"。

---

## R49 性能探针：怎么判断"卡顿"是不是我们自己造成的（v4.2.2）

用户要求「卡顿需要一直测试」。为这件事专门加了 `scripts/perf_probe.py`，
因为**卡顿没法做成一次性门禁**（判据不稳定会频繁假红），只能做成可反复运行的观测工具。

### 三条测量原则（每一条都是踩过坑才定下来的）

1. **响应延迟必须取最小值，不能取中位数。**
   引擎测试命令走 1500ms 轮询（`HookWorker` 的 `interval`），中位数几乎恒定在 1415ms，
   完全看不出 UI 忙不忙；最小值消除了轮询相位差，才反映真实处理耗时。

2. **绝对数值没有意义，必须做 A/B 对照。**
   这台机器上 Command 进程本身长期 110~160% CPU——**纯英文模式（完全停用汉化引擎）
   实测也是 112.7%**。所以"CPU 高"不能证明是汉化的问题。

3. **必须确认翻译通道是否在忙。**
   如果 8 秒内 `TRACE_DUMP` 没有任何 `SET`/`WRITE` 事件，说明汉化层根本没在动，
   卡顿从原理上就不可能是它造成的。这条排除项比 CPU 数字更有说服力。

### 判据（只留可复现的）

- 补扫频率必须约 5 次/秒（200ms 空闲退避周期）；> 12/s 判失败——这直接反映我们往 UI 线程丢了多少活。
- 静止界面下翻译通道必须**零事件**；有事件说明汉化层在反复改写控件（R43/R45/R46 那类问题）。

**刻意不用"响应延迟比值"**：实测同一台机器同一状态下该比值在 **0.45x ~ 2.98x** 之间剧烈波动
（轮询相位差主导测量），拿它当门禁必然假红。它只作为观测值打印。

### 当前基线（2026-10-20，连跑三轮完全一致）

```
补扫频率(编辑器打开): 4.9 次/秒
翻译通道事件(8s): 无
PASS
```

### 使用方法

```bash
python scripts/perf_probe.py          # 游戏已在想定里
```

---

## R50 `publish_release.py --verify-only` 不是只读：参数从未实现，且退出码被吞（v4.2.2 发版时暴露）

- **发现方式**：v4.2.2 发布后按 `docs/RELEASE_MANIFEST.md` §5「发布后自检」跑
  `python scripts/publish_release.py --verify-only`，它却**重新上传了全部资产**
  （日志里出现 `Deleting existing asset … / Deleted / Uploaded`）。
- **根因（三个叠加）**：
  1. `--verify-only` **从未被实现**：脚本里没有任何 `verify-only` 字样，只解析 `--force`。
  2. **不校验收到的参数**：任何未知参数都被静默忽略，于是照样走完整发布流程。
  3. 脚本末尾是裸 `main()`，**没有 `sys.exit(main())`** ⇒ 即使走到 `return 1/2` 的错误分支，
     进程仍以 0 退出。在 CI 或 `cmd1 && cmd2` 链里这类失败会被当成成功。
- **危害**：把"只读自检"做成写操作。本次因本地 `build/` 产物与已发布资产是同一份构建，
  重传后哈希未变、资产完好；但若本地产物已被重新构建过，`--verify-only` 会**覆盖已发布资产**，
  而 `RELEASE_MANIFEST` §4 明确要求「已发布资产只增不改」。
- **修复**：
  1. 真正实现 `verify_release()`：只发 GET 请求，核对资产齐全、`digest` 与本地构建哈希一致、
     `latest` 指向该 tag；**校验通过后立即 return，绝不进入上传分支**。
  2. 显式白名单参数（`--force` / `--verify-only`），未知参数直接 `return 2` 并提示可用参数。
  3. 末尾改为 `sys.exit(main())`，错误码不再被吞。
- **验收**：跑 `--verify-only` 前后比对资产 ID 与 `updatedAt`，**完全未变**（确认纯只读）；
  拼错参数 `--verifyonly` 返回退出码 2；正常校验返回 0。
- **教训**：以 `--xxx-only` 命名的开关必须**先断言它真的只读**（跑前后对比远端状态），
  不能只看帮助文本或参数名。同类风险：任何"检查类"脚本都应显式拒绝未知参数，
  否则拼错一个字母就从"检查"变成"执行"。

## R52 部队/装备名未翻译 + 历史译文污染（电报体句子、句子碎片）

**现象**（用户报告）：军队名称没翻译，例如数据库查看器里出现
`People's Liberation Army Air Force`；数据库整屏英文；部分条目显示成
`PLAN Changsha [Type 052D DDG]辖Z-9C ASW如令查收。` 这种中英混杂的垃圾串。

**根因**（三个独立缺陷叠加）

1. **军种/国家枚举缺失**：`EnumOperatorService` / `EnumOperatorCountry` 跨 91 个
   DB 版本的并集 279 条里，31 条没进词典（`People's Liberation Army Air Force`、
   `PLAGF`、`PAP`、`PAFMM`、`PLAASF / PLASSF`、`Second Artillery Corps` 等）。
   这类字符串只在数据库弹窗里出现，`L-ui-residuals` 只查「已入词典却仍显英文」，
   所以永远不会被发现。

2. **构建以旧词典为底座**：`build_dict.build()` 是 `exact = dict(base["exact"])`
   的增量构建。从源批次里删掉的条目不会消失，历史垃圾会永久存活。

3. **历史批量生成器把短标签拼成电报体句子**：源批次里存在
   `'PLAN Changsha [Type 052D DDG]: Z-9C ASW' -> 'PLAN Changsha[...]辖Z-9C ASW如令查收。'`、
   `'Enemy Forces:' -> '敌情通报Enemy Forces如令查收。'` 这类条目。
   其中 **165 条的键是真实 DB 记录名**（`KA-6D Intruder`、`SH-2F Seasprite`、
   `Sang-O`、`Nampo`…），命中时界面就会显示整句。
   另有 1,746 条「值 = 键 + 一句译文」的句子碎片，键根本不在游戏字符串池里
   （实测仅 2 条在池内），所以它们**永远只会污染 `exact` 查找**。

**改动位置**

- 引擎 `TryComposeTranslate` 规则 1：支持 `[规格]` 方括号形态（实测比圆括号多
  3 倍：10,362 条 vs 3,252 条）。
- 引擎 `TranslateComposedName`：型号/编号 token（含数字或斜杠，如 `B-66B`、
  `AN/TPS-43`）不再否决整条名字，原样直通；并剥掉词尾标点再查术语表。
  纯字母缩写（`HQ`、`ASW`）仍必须进术语表，避免产出 `HQ连`。
- `scripts/build_dict.py`：新增污染自愈，按形状清掉长时间标签被拼成电报体句子的
  条目（真正的散文段落带句末标点，保留）。
- 数据：补 33 条军种/国家枚举；删除 1,746 条句子碎片与 93 条电报体污染；
  1,690 条经逐条 LLM 精修后按原归属写回。
- 新增 `scripts/scan_english_residue.py` + `src/tests/ResidueScan.cs`：
  用游戏数据本身当语料、用引擎真实产出判定缺口。
- 新增 `scripts/build_translation_batches.py`：缺口→批次→合并，合并守卫拒收
  句子碎片、结构性内容和长度异常译文。

**用户可见验收**

- 数据库查看器里军种显示中文：`People's Liberation Army Air Force` → 中国人民解放军空军。
- 上面列的垃圾串消失（`KA-6D Intruder` → `KA-6D 入侵者加油机`）。
- `verify_terminology.py` PASS；`verify_regressions --static` 26/26；
  `--live` 19/19（含 L-repaint-tick 补扫 37191→37617 增长）。

**回归测试点**

- `R52-static-residue-scan`：扫描器可运行并给出非零语料。
- `R7-static-terms`：`verify_terminology.py` 的 key-embedded 规则会抓这类污染
  （本轮它正是抓到 `'Nampo' -> '舰名Nampo如令查收。'` 的那道闸）。
- 后续门禁待补：把「已入词典却仍显英文」的循环论证换成
  `scan_english_residue` 的缺口统计。

### R52 续：引擎平衡括号解析 + 规格内逗号列表 + 系统防锁 + 术语表突破三千条（2026-10-21 上午）

**现象**：
1. 数据库大量带中置规格的鱼雷/子母弹/火炮（`Mk48 [650lb/295kg HE] Torpedo`、`CBU-52/B (217 x BLU-61/B) 1.2kg Frag`）及内嵌二级括号的雷达/防空营（`Radar (Spoon Rest A [P-12])`、`SAM Section (SH-04 Galosh [ABM-1B] x 8)`）因原规则 1 正则 `[^()\[\]]*$` 强制括号在末尾且排斥嵌套括号而全盘回退英文。
2. 规格内逗号分隔的属性列表（`Pier [Extra Large, 200-500m]`、`Radar [Air Search, 3D, Long-Range]`）因无法逐项解构而整串失败。
3. `Mk.`、`F.`、`A.` 等带缩写点的 token 因原标点剥除不含点号而被误当成生词。
4. Windows 下高频运行 `build_dict.py` 时因游戏或系统索引器持有文件瞬时句柄导致 `os.replace` 报 `WinError 5` 拒绝访问。

**修复**：
1. 引擎改用 `TryParseBalancedParen` 平衡字符计数扫描（depth++ / depth--），天然支持中置括号与任意深度的嵌套配对。
2. `TranslateComposedSpec` 增加规格内逗号列表逐项解构与全角逗号拼接，并支持二级嵌套递归组合。
3. `TranslateNumberWithUnit` 正则扩展支持 `200-500m` 范围数字单位。
4. `SplitPunct` 增加缩写末尾点剥除（排除数字中间点以保护 `12.7mm`）。
5. `build_dict.py` 写入流程增加 6 次退避重试，超时降级为截断写保底。
6. `terms.json` 扩充至 3,005 条（23.6 倍），覆盖常见武器绰号、雷达代号、发动机与军制词汇。
7. 想定 24 个批次全部完成精修并按原归属写回。

**量化效果**：
- 数据库真缺口：34,203 → 16,504（净降 17,699 条，-51.7%）
- 想定真缺口：3,909 → 289 条（净降 3,620 条，-92.6%）
- 全库总缺口：38,128 → 16,822 条（净降 21,306 条，-55.9%）
- 验收：`verify_all` 11/11 全绿、`verify_regressions --static` 26/26 全绿、`--live` 19/19 全绿。

## R53 原版地图文字被稀疏 WWP 重绘覆盖（全量补回进行中，未闭环）

**来源**：用户要求调查原版与汉化地图缺失，并补回整个游戏地图的全部中文名称。

**根因**：原版 Labels PNG 包含两个 WWP 以外的城市、省州、水体等标签；
汉化运行时与安装器从透明画布只重绘 Cities 4,287 条和 Synoptic 321 条，
并在低缩放隐藏全部城市、在碰撞时直接跳过，导致标签消失。

**已实施的独立增量**：新增全球来源清单与中文审核门禁，缺译、未审、来源/快照/显示未确认均阻断二进制输出；
译名按源记录指纹绑定，不允许复用 ID 后沿用旧审定中文。
共享渲染模块新增严格中文目录读取、payload SHA256、显式缩放和优先级。
尚未接入运行时或安装器，也没有改变已安装游戏。

**验收证据**：`scripts/test_global_map_catalog.py` 包括缺译、非中文、未审、源记录变更和旧产物保留负例；
`scripts/test_map_label_renderer.ps1` 包括目录互读、损坏、低缩放补充城市与范围限制。
实际旧 WWP 29 张样本的新旧输出 RGBA 一致，证明基础支持没有改变旧输入像素；不证明全量补回。

**剩余阻碍**：公开 Stamen 样式与官方 PNG 存在缩放/变体差异，历史 OSM 湖泊与公园源未取得；
全球候选尚有大量未译与未审实体，现有碰撞算法也没有完成原版逐标签可见性验收。
详见 `docs/GLOBAL_MAP_LABELS.md`，不得将 R53 标为已修复或宣称地图无遗漏。

**教训**：原版来源必须建立独立分母；目录有中文、PNG 成功生成、抽样像素一致，均不能替代全部原版文字保留的证明。

### R53 续：新增原图证据与人工译名工作流（仍未闭环）

继续使用可获得的资料，新增确认加拿大十省和七峰缺失，人工确认下限为 111 个名称实体。
七峰原版还显示海拔及单位；历史 OSM node 版本与名称/坐标/原值吻合，当前数据库部分值已变更。
名称全译并不代表高度、符号、多行和跨瓦片布局已保留。

新增 `--reviews` 从独立审核源覆盖候选，并绑定原记录；加拿大十省中文按外交部来源审核，
只批准文字，不解除原版来源、锚点或显示验收阻断。
`required_display_text` 负例证明缺高度、14344 冒充 4344、负号/小数前缀均不能通过。
公开峰源的高度明确标为待原图核对，不能自动当成冻结原值。

相关 Python 测试 24 项、共享渲染断言 48 项通过。静态总回归 22/26，
其余为当前克隆缺按钮图、未构建安装包及生成词典工作副本换行等环境/现有产物前置条件，未修改这些无关项。
完整性验收和游戏接入尚未完成，R53 继续保持未闭环。


### R53 续：现有资料全量核查的冻结结果（仍未闭环）

逐图新增确认 Smithers，累计下限为 112 个名称实体；原图 z6 和公开样式 z10 分开保留，不填补中间缩放。
7,328 张原版缓存逐张 SHA/alpha 比较中，1,569 张原图非空而当前中文全透明，属于瓦片内容丢失数量，不能当作名称数。
448 张非空原图 OCR 为零词；93 张透明原图被 OCR 分配 135 词，相关 114 段另存拒绝清单，原始输入保持不变。
3,351 条自动名称/地理观测没有空图误报，但仍需视觉复核，不进入逐图确认实体或审核目录。

26,070 个已知缺中文城市完成现有 GeoNames、NE 和已知 Wikidata 路径补查，131 批查询无未查批次。
严格主体/英文名/双坐标距离检查后仅纳入 8,245 条新中文候选，新增自动批准为零；错误主体及同名近邻冲突保留。
最终冻结来源 104,768 条 = 已选源记录 47,576 条 + 可见性未决 57,192 条，无遗漏导入源 ID。
分段目录 47,589 行中仅 2,213 行文字已审核，45,376 行仍缺译或待审；来源/显示问题 15 类。
真实门禁退出 1、ready=false、未生成全球二进制；运行时、安装器、游戏均未启用该不完整目录。
本地代码补丁与完整候选/证据另行保存，R53 继续未闭环，不能宣称全游戏地图已补回。


### R53 续：实际中文瓦片包与首批逐区恢复（仍未闭环）

**来源与修复**：用户明确要求实际修复。新增CMOTIL01有限区域瓦片包、离线构建与双入口接入，精确命中直接返回已核验PNG，避免再被低缩放/碰撞规则抹掉。首次Labels完整备份采用staging后发布.orig，原有.orig保持不变，逐文件原子替换；实际字节校验可修复旧透明/损坏/删除缓存，不只信完成标记。安装器未覆盖的旧渲染路径也改为原子写，保留既有刷新计数和时间戳契约。

**首批真实数据**：7区域15瓦片、30名称实例、13原海拔实例、23符号；12张旧全透明，3张双方非空。原图内容逐项独立核验，两个手工山峰符号锚点在视觉审查后校正，最终审批绑定区域指纹、字体及实际RGBA像素。真实data已编译，未写已安装游戏，也不代表全球完成。

**验收**：瓦片包55断言、刷新63检查、旧缓存4测试及既有地图48断言通过；离线构建/来源相关67测试通过。对真实首批包另做110项C#双入口/缓存/备份检查，7区缓存拼图逐RGBA等于批准图。首次独立代码审查发现的原子fallback写入、字体/栅格审批失效及Windows预览路径别名问题均有负例和修复复核。

**剩余**：仅明确列出的区域实际恢复；1,569全透明损失与1,846双方非空集合尚未清零，全球历史源分母/全部中文/实机完整验收仍未满足。R53保持未闭环。不得以PNG非空或有限包生效推断全地图名称已补齐。

### R53 续：核验瓦片进入正常交付链（仍未闭环）

安装器在重建缓存之前部署并验证同目录/patch_files/MAP_TILES内嵌瓦片包，使用同目录staging后原子替换；坏候选不能覆盖好包。build强制嵌入正式asset、绿色包强制复制和覆盖断言、deploy原子复制并逐字节核验；安装清单仅在存在时纳入pack hash。卸载先删除pack，删除失败上抛，避免还原后复活中文包。

独立代码审查Approve；实际C#5三资源fixture46项、脚本契约5项、旧pack55项、刷新63项及引擎构建通过。没有Bash，未执行build/deploy/package整链；没有实机安装/卸载、注册表或进程操作。此项完成正常交付代码接入，不解除全球资料和显示完整性条件。

### R53 续：中国及邻国、加拿大中部实数据补回（仍未闭环）

已加入独立源与像素审核的加拿大中部15格、中国及邻国16格。当前合计9区域46瓦片、103名称实例、13原海拔、63符号；41张原非空、5张源透明结构格，不能把46张都称为空白缺失恢复。基准1569集合已覆盖21张，双方非空1846集合覆盖13张，另10张是本次新取得原图的范围。加拿大旧错位国家标签清除；中国东列扩展到日本海/日本旧Synoptic位置，避免包外重复。Fort Smith、Jasper错误外部元数据已消歧，不照搬错误行政区/GeoNames ID。

全部实际资源、运行时、安装器与模拟缓存329检查通过，9区重拼RGBA等于独立批准atlas，原.orig保持逐图SHA或原本不存在状态。只测试隔离的资源部署helper，不调用注册表、进程或安装主流程。北欧白海另发现包外旧标签重复风险，暂缓该草案区域，没有将未闭合内容写入生效资产。R53仍未闭环。

### R53 续：海域闭合子集与最终有限包核验（仍未闭环）

海域新增5源区及2原图透明清理区，保留楚科奇海两个原锚点，清除两格错位旧北冰洋；同缩放8条现有WWP点均在覆盖内。白海所在欧洲区会与包外旧白海/巴伦支海重复，扩区又引入更多未闭合标签，本次明确暂缓整区，没有静默抹掉或自动审批。

当前16区域128网格、113名称实例、13原海拔、65符号。128 = 基准全透明损失覆盖30 + 双方非空覆盖18 + 基准源透明66 + 新取原图14；55源非空 = 30 + 18 + 新取非空7，另73源透明不算恢复缺失。原1569尚余1539，双方非空1846尚余1828，全图目标仍未完成。

独立区域源/视觉审核均通过；实际MAP_TILES资源helper、runtime、installer和模拟缓存903检查通过，16区重拼RGBA等于批准图，所有预存.orig逐SHA不变或维持原本不存在状态。未写游戏、未运行发布脚本/主安装流程、未发布。R53保持未闭环。


### R53 续：跨区同实例去重与失败标记修复（仍未闭环）

有限包外同一名称的旧WWP点会造成重复，按zoom、中文名、float32经纬度逐位及level精确关联后由CMOTIL02排除；不是按同名删除。原WWP不改，非legacy目录保留。缓存刷新复用完整备份/原子替换，并按旧渲染器双边包含条件刷新已有边界邻格；(0,0)实际旧PNG四格重复均有负例。区域上限256格、总量16384格，仍保持有限覆盖与全球未完成门禁。

相邻矩形共享同一原字串时，shared_sources只对原像素记账，引用同zoom另一已审实例；原文/类别/资料/完整世界bbox精确绑定且owner确有中文输出。未知owner、循环、错框、未审和无输出均拒绝，不增加重复文字。独立审查中发现安装器先写完成marker再失败的旧测试契约错误，已用01/02锁文件负例复现，并改为prepare后重绘、全部成功才complete，计数不重复。

独立重编译pack109项、renderer48、刷新63、交付资源46、脚本契约5通过；builder50及来源14+10+12共86项通过。Python02真实包由C# reader互读通过。原正式128瓦片无新规则重建仍逐字节一致，独立Task10/11审查批准。全部为工作区隔离模拟，不写已安装游戏、注册表或进程。此项修正跨区机制，不代表新增所有地区数据或全图完成；R53未闭环。


### R53 续：各洲独立审核批次合入400瓦片（仍未闭环）

实际asset扩展到23区400网格、648名称实例、13原海拔、284符号；225源非空、175透明结构/清理。美洲两批、欧非三批、亚太z4和新西兰等逐项源/译名/符号/像素独审通过；旧Ocean和透明清理区只在新覆盖完整承接后替换，旧北冰洋上三行保留，不丢失既有批准范围。跨区同原文字仅唯一owner绘制，共享引用联合编译；125条canonical规则精确关联，拒绝美国温哥华误候选。Seville/Naples错取省名已据官方城市证据修正。

400 = 全透明损失覆盖82 + 双方非空覆盖131 + 原基准透明162 + 新取得原图25；225非空 = 82 + 131 + 新取非空12。原1569尚余1487，双方非空1846尚余1715；不能把400网格全部算作修复空白损失，更不能宣称全图完成。

正式资产SHA e2f0df95951f4e325ea76ad80186ffaed0459f7582cc11504ba3d2e42af10ae3。独立完整重建逐字节一致，0源alpha未归账；实际C#5内嵌MAP_TILES资源helper/runtime/installer/缓存/全4608WWP合计34325检查通过。23区双入口重拼RGBA全等批准图，2实际已有包外缓存各只移除1个精确关联旧点并保留其他文字，原.orig逐SHA不变。完成标记失败/重试、缺图/损坏修复、地形哨兵均通过；报告绑定engine/installer/host/manifest/asset SHA。安装器包外重复调用保留2次既有强制重绘计数，并非0写。

工作继续使用10并发名额；亚洲统一大区与西非/朝鲜半岛等高zoom仍在逐图恢复。未写游戏、未发布/推送/改版本，R53保持未闭环。


### R53 续：原版未命名高度及独立无字点（仍未闭环）

美国z8原图有5307m/9342m两处只有高度及三角点，西非z8另有只有城市圆点、没有原文字的实例。原peak必需中文名/符号必需具名feature的约束无法如实表达，不能伪造“无名峰”或虚构点名称。新增peak elevation_only:true严格绑定原数字+m，仅显示同数米一次；拒绝具名源、改数、拆高度、非布尔/非峰标记，不据此排除具名WWP。symbols unlabeled:true须独立来源/说明且无feature_id，仅恢复原点、不增加feature/标签数。全部标记仍纳入原图/区域/像素审批，绝不自动批准。

两组共12项RED观察后最小实现，55builder及14global/10import/13placename共92检查通过；独立代码复核5项含全部非法subcase及旧审批失效通过，未发现阻断。此前正式400瓦片使用新代码重建逐字节不变（e2f0df...），原具名文字/点路径保持。原数据中高度单位错误按历史官方OSM版本与原图照录，不用当前数据库修正原图数字。此代码支持新增真实草案表达，不代表这些草案已批准或全球完成。


### R53 续：高zoom与亚洲统一包扩至798瓦片（仍未闭环）

新增美洲80格169字、西非96格132字与64格71字、欧非/亚太z3共20格69字，及亚洲133格223字和东非21格63字的联合唯一owner范围；旧中国16格58字由新统一范围完整覆盖替换，不叠加重复香港/高雄/泰国等实例。扩区后新进入的Padang原点已实看补回。所有源、译名、原点、布局和接缝逐项独立审核，原alpha全账，未自动批准候选。

当前30区798网格1317标签13原海拔517符号；515源非空、283透明。798=全透明损失覆盖218+双方非空覆盖257+原基准透明255+新取原图68，515非空=218+257+新取非空40。原1569尚余1351，1846双方非空尚余1589；新取/结构格不能冒充空白损失恢复，全图目标仍未完成。

正式SHA4b30daf486d4eb4dbd3acdf10ec2a2e113a19cf459202cde1ac3a0664c882d82，独立Python完整重建逐字节相同、全局无重叠/重复原实例/非法抑制；215规则唯一canonical。实际C#资源/双入口/缓存/全4608WWP40904检查通过；30区两入口RGBA均等批准。包外仅已存在6/19/28精确移除1旧点（24→23候选），保留6个其余可绘标签；原.orig、terrain、marker失败/重试与损坏恢复通过。更新验证host允许编译输出在work子目录，写入边界仍从实际引擎仓库限定work/tile_recovery，增加可见异常诊断，不修改生产逻辑。

后续乌克兰、韩国、西亚、北非及西部山峰仍并行恢复，未写游戏/发布，R53未闭环。

### R53 续：精确官方低alpha背景归账（仍未闭环）

原图确证的RGBA(1,1,1,1)平坦背景会被alpha>0当成漏字。source_backgrounds仅接受独立审查的core原tile精确SHA/URL、固定RGBA、至少半tile像素和非空notes，拒绝重复/包外/稀疏/其他颜色/错源；不改源图、原预览或中文输出，不泛忽略低alpha。报告原total不变、额外background_pixels列出确切证据；检查原字/符号/shared框非空时先扣背景，不能用背景证明有文字。

62builder+14catalog+10import+13placename共99项通过，独立代码/真实8/43/102背景与context-only拒绝复核批准；原798瓦片重建逐字节不变。8/43/102全图精确背景64348像素（早先63659是未被正常源框记账的残余），非文字原ink1188像素；9/292/172对应63250背景与2286ink。原始PNG和剩余真实字形保持，未自动批准新地名数据。R53保持未闭环。


### R53 续：原海拔、无名符号与高zoom补回扩至1320瓦片（仍未闭环）

来源：已冻结原图与旧汉化逐tile比较。乌克兰、韩国与美洲高zoom原城市文字在旧WWP渲染中缺失；原点/三角、仅高度峰和无名圆点不能通过虚构名称补齐。本轮逐项中文补译并绑定独立源/身份/原符号/布局审核；旧Sierra z8两格由90格承接，全部旧覆盖保留。原海拔保持源数字，历史OSM数字异常不擅改成现代高度。

当前39区1320网格、2112名称+2仅海拔标签、19原海拔、649符号（其中1个原图无名点）。863源非空、457透明。1320=全透明损失422+双方非空321+原基准透明405+新取原图172，863非空=422+321+新取非空120。原1569尚余1147，1846双方非空尚余1525；新取/结构格不能冒充空白损失恢复，全图目标仍未完成。

正式SHA18c4587ec4a038297410753ff8cb5c0fceaf7bb9c63d09ff16e11c8a8d7b7eb5。独立Python重建逐字节同，1687源路径SHA、30shared owner精确实例、39区域和译名指纹通过；无重叠/重复原实例/非canonical或重复抑制，259规则。实际C#内嵌资源与双入口43552检查通过；39区两入口RGBA均等批准，3已存在包外缓存精确去重且保留其他名称；原.orig、terrain、marker失败/重试与损坏恢复通过。全部写操作限工作区fixture。

补充上一条低alpha背景记录的用词：“非文字原ink1188像素”应读作“扣除已确认背景后仍保留真实原ink1188像素”；它们是文字/符号墨迹，仍逐框归账，没有忽略。完整背景64348与真实墨迹1188之和65536。

后续北非、西亚、韩国南部、美洲及其他未覆盖区域仍并行补回；未写游戏、未发布，R53未闭环。


### R53 续：逐项补回扩至1690瓦片（仍未闭环）

来源：冻结原图与旧汉化逐tile差异，以及各区独立审查。修复：新增美洲z9山峰与城市、黎凡特z10/z9、北非z6、欧亚桥区和韩国南部z10；旧Sierra z9三格由72格完整承接，原八峰与Bishop中文旧图逐RGBA保持相同。增加的同一峰在不同zoom按原版分别保留，未按名称合并；北非摩洛哥/以色列两个已存在包外旧国家标签按同实体精确旧键去重。

当前50区域、1690网格、2701名称+2仅海拔、22原海拔、705符号（1原无名）；源非空1106、透明584。1569基准全透明损失覆盖516、尚余1053；1846双方非空覆盖390、尚余1456。其余499基准透明、285新源（200非空）分别列账，不冒充名称恢复或全图完成。

验收：正式包SHA1fccd381054f64a7dabeb96fbb9ef77f8f7f25b0961e03db3c370bf84bc8750f，独立Python逐字节重建相同，各批源/译名/点位/布局审批绑定；281精确canonical旧键保留同名异地。实际C#内嵌资源、双入口及4608WWP共45440检查通过；50区双入口RGBA等批准，5已存在包外缓存逐项精确过滤，原.orig/terrain不变、marker失败重试和缺损恢复通过。机器报告绑定实际数据、引擎、安装器fixture和宿主源码SHA。

教训：alpha全归账不能证明地名身份正确；必须保留独立原文字、坐标实体、中文、接缝和符号复核。未覆盖及未缓存世界仍继续，不写游戏、不发版，R53未闭环。


### R53 续：逐项补回扩至2119瓦片（仍未闭环）

来源：冻结原图与旧汉化逐tile差异，以及各区独立审查。修复：新增乌克兰东部z9、黎凡特南部z9、美洲中北部z7、地中海z7及韩国北部z10。跨矩形9处与跨区原文只记唯一owner；保留原点原位，排除把Iowa州名当Waterloo市名及Kansas/Missouri同名旧点混合，奥曼洞明确为编辑音译而非虚构官方汉字。

当前61区域、2119网格、3299名称+2仅海拔、22原海拔、1027符号（1原无名）；源非空1415、透明704。1569基准全透明损失覆盖585、尚余984；1846双方非空覆盖459、尚余1387。其余532基准透明、543新源（371非空）分别列账，不冒充名称恢复或全图完成。

验收：正式包SHAbcefed4dd9e0ba2daafb3df52101325a4b8c1fd31ef8096690d8ad98b8570190，独立Python逐字节重建相同，各批源/译名/点位/布局审批绑定；336精确canonical旧键保留同名异地。实际C#内嵌资源、双入口及4608WWP共47623检查通过；61区双入口RGBA等批准，7已存在包外缓存逐项精确过滤，原.orig/terrain不变、marker失败重试和缺损恢复通过。机器报告绑定实际数据、引擎、安装器fixture和宿主源码SHA。

教训：alpha全归账不能证明地名身份正确；必须保留独立原文字、坐标实体、中文、接缝和符号复核。未覆盖及未缓存世界仍继续，不写游戏、不发版，R53未闭环。
