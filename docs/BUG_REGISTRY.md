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

### R52 续二：逐条精修全域推进 + 三处门禁/守卫误判修正（2026-10-21 晚 ~ 10-22 凌晨）

**背景**：主缺口已从 38,128 降到 12,611 后，剩余缺口不再有「一个词解锁一批」的结构性红利，
必须改为按表逐条 LLM 精修。为此建立 v2/v3 两代批次体系（`build/v2_batches`、`build/v3_batches`），
每类一套专用 instructions（传感器北约绰号表、挂载弹种表、设施编制表、飞机绰号表、
卫星代号表、武器弹头表、基座形式表、推进装置表、舰艇舰级表）。

**逐条精修落地（全部经守卫合并，拒收半译/未译/结构串）**
- 舰艇 18 批（DataShip 全域）、传感器 10 批、挂载 11 批、武器 7 批、推进 6 批、
  设施 5 批、飞机 5 批、基座 5 批、卫星 2 批。
- 术语表 127 → 3,157 条（24.9 倍）。
- 另用「术语表直接法」批量修正历史半译 4,700+ 条（只替换残留英文词全部命中术语表的位置，
  型号/编号原样保留，并清理逐词替换产生的中文间多余空格）。

**修掉三处门禁/守卫把正确译文判成错误的假阴性**
1. `verify_release` 把 `Be` 当英语虚词 be —— 它其实是意大利奥托·梅莱拉舰炮的
   型号前缀（`234mm/50 Be`、`76mm/62 Be`），已从虚词表排除。
2. 同一处把 `Rotax 915 iS` 的 `iS` 当 `is` —— `.lower()` 撞上虚词表。
   改为**只报整词小写**的虚词（`w == w.lower()`），型号后缀（iS、eSeries）自动放行；
   正向用例 `目标 is 已摧毁` 仍能被抓到。
3. `merge_record_names` 的「中文/中文」判据过严：`博斯威克/塔科马`（双厂并列）、
   `维克斯/罗尔斯·罗伊斯`（联合承制）、`火箭助推/主发动机`（双级动力）
   都是数据库名称里的合法写法，放宽到「两边各 >= 8 汉字」。
4. `merge_record_names` 增加 `--allow-new`：新增条目时不再被防呆拦截（防呆仍保留）。
5. `build_dict.py` 写入流程增加 Windows 文件锁退避重试（6 次 × 0.25s，
   超时降级截断写），解决游戏/索引器持句柄时的 `WinError 5`。

**引擎再修两处**
1. 后缀数量允许右侧是代号而非纯数字：`10 MIRV x 550kT Nuclear`、
   `3 MIRV x W58 200kT Nuclear] RV` —— 此前 `x` 被认成生词，78 条弹头描述整条回退英文。
2. 数量前缀 `N x` 与纯分隔符（`+`/`-`/`--`/空 token）识别。

**量化效果（唯一字符串真缺口）**
| 阶段 | db | scenario | ui | 合计 |
|---|---|---|---|---|
| 昨夜起点 | 34,203 | 3,909 | 16 | 38,128 |
| 现在 | **1,997** | **288** | 29 | **2,314** |
| 降幅 | −94.2% | −92.6% | — | **−93.9%** |

**验收**：`verify_all` 11/11、`verify_regressions --static` 26/26、`--live` 19/19 全绿。

**补充（2026-10-22 凌晨收尾）**
- 剩余 1,979 条缺口的性质已抽样定型：绝大多数是**纯型号/代号与保留性战术代号**——
  `SPO-3 Sirena-3`、`Kurs`、`Avtomat 2/3`、`SPS-4M Klyukva`、`Stan Patrol 5009`、
  `SA-`/`AIM-`/`MR-`/`MGK-`/`BetAB-` 等系列前缀，以及 `Shotgun`/`Winchester`
  这类在中文军语里**刻意保留原文**的战术状态代号。
  这些不属于漏译，继续「精修」只会制造臆造译名，因此停止对该批的机械替换。
- 对可译部分仍做了收尾：`Stan Patrol=斯坦巡逻艇级`、`Patroller=巡逻者`、
  `NCPV=新型近海巡逻艇`，并清理了残留普通词。
- 最终：`verify_all` 11/11、`--static` 26/26、`--live` 19/19、`routing` PASS，
  工作区 clean，全部提交已推送。

---

## R53 大想定「卡到电脑不能动」：全树补扫挂在渲染帧上 + 僵尸 WPF 窗口（v4.2.2）

- **来源**：用户 2026-10-22 反馈「单位数量稍多的想定就巨卡，卡到电脑不能动的那种」，
  点名 **战争链 DLC 第一关 Blue Dawn**。同时要求「针对大型任务做性能测试工具以及方法」。

- **根因（两条缺陷叠加，都是 R43/R47 遗留）**
  1. **全树补扫挂在渲染帧上，而非定时器**。`OnWpfRenderTick`（`CompositionTarget.Rendering`）
     里每 8 帧调一次 `TranslateWpfWindows()`，而该函数对**每个** HwndWrapper 窗口都
     `BeginInvoke` 一次全树翻译。实测渲染帧 103/s ⇒ 排队量与窗口数相乘。
     R47 只修了定时器那条路的放大，**漏了渲染钩子这条路**。
  2. **僵尸 WPF 窗口永不销毁**。实测进程里累积 **22 个「想定特性与拟真度选项」窗口**，
     同位置、同尺寸、`visible=True`，共 4209 个视觉树节点（正常状态仅 805）。
     窗口越多，单次全树遍历越贵，UI 线程越堵。

  ⇒ 单位数量多的想定里，消息日志/单位面板节点更多、地图动画更频繁（帧率更高），
  于是这条缺陷的代价**随想定规模非线性增长**。这解释了为什么教程想定只是「有点卡」，
  而 Blue Dawn 这类大想定会「卡到电脑不能动」。

- **可复现度量（本次实测，同一台机器同一进程）**
  | | 修复前 | 修复后 | 设计值 |
  |---|---|---|---|
  | 渲染帧率 | 69.9/s | 56.8/s | — |
  | **补扫频率** | **70.0/s（与渲染帧 1:1）** | **1.9~4.8/s** | **约 5/s** |
  | WPF 窗口 | 27 个 / 4209 节点（21 个僵尸） | 5~6 个 / 805 节点（0 僵尸） | 0 僵尸 |

- **修复**
  1. 渲染帧钩子里**彻底不再做全树遍历**，只保留 `liveWpfTargets` 的轻量重写
     （上限 400 个具体元素，不做树遍历，代价与想定规模无关）；
  2. 全树补扫（`TranslateWpfWindows`）改由 40ms/200ms 定时器驱动；
  3. **单拍 4ms 时间预算 + 游标续扫**：扫不完就记住位置下一拍接着扫，
     窗口数再多也不会吃掉整帧；窗口集合变化时重置游标，避免错位漏扫；
  4. 补扫**同步执行**（不再逐个 `BeginInvoke` 排队——排队量正是淹没 UI 线程的来源），
     并按 `Dispatcher.CheckAccess()` 分发到**该窗口自己的 Dispatcher**上：
     定时器跑在 WinForms UI 线程，WPF 窗口往往属于另一个 Dispatcher，
     跨线程直接碰视觉树会抛「The calling thread cannot access this object」（已实测清零）；
  5. 补扫前用 `IsWindowVisible` 过滤掉隐藏/零尺寸残留窗口。

- **同时修掉两处**门禁假红**（比缺陷本身更危险——它让人误判成功能失效）**
  1. `L-build-adaptation` 报 `注入=MISSING`：`_log_tail` 取尾部 40 万字符，
     而 `CMOLocalizer.log` 已累积 3.9 MB / 83 次启动，偏移池注入的
     `Successfully pre-populated` 被挤出窗口。新增 `_log_this_session()` 按最后一个启动标记切段，
     判据从此与日志体积无关。
  2. `L-ui-residuals` 报 `WPF 想定特性 0 条`：Realism 窗口是异步构建的，
     前序用例关过全部窗体时 4.5s 抽不到。改为重试 4 次，
     并显式区分「窗口未构建」（环境/时序）与「翻译缺失」（真缺陷）。
     *注：该用例在**修复前的基线上同样失败**，属既有脆弱判据，非本轮引入。*

- **新增工具**：`scripts/perf_probe_large.py`（已登记路由表）。
  大想定卡顿必须用它，`perf_probe.py` 的教程想定数值覆盖不到这类缺陷。
  三条硬判据：①补扫频率必须由定时器主导（>15/s 即失败，抓住「每帧一次」这类数量级错误）；
  ②僵尸 WPF 窗口必须为 0（同标题重复，排除 ElementHost 这类容器重名）；
  ③视觉树节点总数上限 3000。

- **方法论（可复用）**：用户报「卡」时，先用 `git stash` 回退引擎做 A/B 对照，
  同一进程同一台机器量「补扫频率 / 渲染帧率」。比值≈1 就说明补扫挂在渲染帧上——
  这是这次定位的决定性证据。绝对 CPU 数字不可用（纯英文模式也有 112%），
  但**计数器的比值是确定性的**。

- **验收**：live 19/19、static 26/26、`perf_probe_large` 三项判据 PASS、
  本次启动跨线程报错 0 条、无僵尸窗口。

### R53 补充二：Blue Dawn 实测完成（此前「无法验证」的结论是错的）

- **更正**：上一版记录称「Blue Dawn 因 DLC 未授权无法验证」——**这个结论是错的**。
  实际是我点错了坐标：在战役列表里误点到「Tides of War」卡片（未购买，故提示「缺少授权」），
  然后把那个提示当成 Chains of War 未授权。**该战役本机完全可用。**

- **正确入口**（已固化成 `scripts/live_load_blue_dawn.py`）：
  `战役` → 系列战役 → 点 Chains of War 卡片 → `开始/启动新建战役` →
  `开始想定` → 阵营选择 `进入想定` → `开始/继续推演`。
  这条链每一步都要「点→等窗体出现→没出现就重点」，
  且卡片列表是自绘的，没有可点击控件名，只能按窗口内坐标点。

- **Blue Dawn 实测（活跃单位 505 个，战役模式）**
  | | 修复前（5808aed） | 修复后 |
  |---|---|---|
  | 渲染帧率 | 58.5/s | 58.6/s |
  | **补扫频率** | **62.9/s（比值 1.07）** | **4.3/s（比值 0.07）** |
  | 判据结果 | **FAIL** | **PASS** |
  | 视觉树 / 僵尸窗口 | 819 / 0 | 819 / 0 |

  与 Reds（1259 单位）那组结论一致：**补扫从「每帧一次全树」回到「定时器主导」**。

- **本轮追加修复：完全重叠窗口去重**
  Blue Dawn 里实测会累积 4 个「想定特性与拟真度选项」窗口，**同位置同尺寸、全部堆叠**，
  `IsWindowVisible` 对它们都返回 true（WS_VISIBLE 置位），所以之前的可见性过滤拦不住。
  但它们互相完全遮挡，用户只能看到最上面一个，逐个全树翻译纯属浪费（4×153 节点）。
  现在补扫按 `GetWindowRect` 矩形去重：同位置同尺寸只保留第一个。
  实测僵尸窗口 4 → 0，且三个窗口的 18 条文本**全部是中文**（去重没有造成英文残留）。
  另用 `scripts/zombie_window_watch.py` 连测 6 分钟确认数量稳定
  （3,3,3,3,3,3,3），证明是**一次性残留**而非持续泄漏。

- **门禁调整**：战役模式下「想定特性与设置」是模态窗口，前序用例 `CLOSE_FORM ALL`
  关掉后无法重开 ⇒ `L-ui-residuals` 必然抽到 0 条。
  改为：窗口根本不存在则跳过该段（`wpf_skipped`）；窗口存在但文本不足才是真失败。
  区分「环境前提」与「翻译缺失」，避免把战役模式的机制当成汉化回归。

- **方法论教训（第二次踩同一类坑）**：
  ① 「加载/进入某状态」的步骤**必须核对返回的窗口标题**确认真进去了
  （`MainForm.text` 或对应窗体名），否则会把别的场景测出来的结果当成目标场景；
  ② 自绘列表按坐标点时，**先截图确认选中项**再点确认按钮——本轮就是没截图，
  在 Chains of War 卡片上点中了下面的 Tides of War，导致后面全盘误判。

- **验收**：Blue Dawn 下 live 19/19、static 26/26、`perf_probe_large` PASS。

### R53 补充三：补上「单次耗时」与「浸泡」两类真度量 + 记录一处既有缺口

- **修正一个说法**：前文写「Blue Dawn 下 live 19/19」是不准确的。
  `verify_regressions --live` 里有三个探针会**自己加载想定**
  （`L-panel-flicker` → Shamal、`L-repaint-tick` → 飞行教程 1、`L-doctrine-combo` → Shamal），
  所以跑完门禁后游戏已经不在 Blue Dawn 里了。
  **正确表述**：19/19 是在「各探针自己加载的想定」上通过的；
  Blue Dawn 上的结论来自 `perf_probe_large.py` 的**就地**测量。

- **新增两类真度量（都已经落到 `perf_probe_large.py`）**
  1. **单次补扫耗时**（`sweepAvgMs` / `sweepMaxMs` / `sweepFullPasses`，从引擎 `TRACE_DUMP` 输出）。
     补扫频率只说明「扫了多少次」，不说明「每次多贵」——用户眼里的卡顿就是单次超一帧。
     判据：峰值 > 16ms 判失败。
  2. **浸泡测试** `--soak <分钟>`：让推演真跑起来，每分钟采样补扫频率/单次耗时/
     窗口数/节点数/僵尸窗口/写拉锯/RSS，看趋势而非快照。
     判据：补扫峰值 > 15/s、节点或内存持续增长、出现僵尸窗口或写拉锯，任一即失败。
  3. **就地「已入词典却仍显英文」**：`verify_regressions` 那条会切换想定，
     所以在大想定上就地重查一遍（屏幕上不含中文的文本去查 `exact`，命中即残留）。

- **Blue Dawn 浸泡实测（8 分钟，505 活跃单位）**
  ```
  t=0min  补扫 5.3/s  帧 73/s  窗口 5/831  僵尸 0  拉锯 0  补扫异常 0
  t=4min  补扫 4.8/s  帧 66/s  窗口 5/831  僵尸 0  拉锯 0  补扫异常 0
  t=8min  补扫 4.8/s  帧 68/s  窗口 5/831  僵尸 0  拉锯 0  补扫异常 0
  PASS: 浸泡 8 分钟无劣化（补扫峰值 5.3/s，节点 831→831）
  ```
  节点与窗口数全程不变 ⇒ 没有随时间累积的结构性问题。

- **新发现一处既有缺口（不是本轮引入）**
  右侧单位状态面板的 `No unit selected` 仍显英文（词典里有 `未选择单位`）。
  定位到它是一个 `TextBlock`（ElementHost → ScrollViewer → StackPanel → TextBlock），
  而引擎日志显示 **`WPF TextBlock Translated` 在全部 94 次启动里累计 0 条**
  （`WPF Run Translated` 有 930 条）——即 TextBlock 直译路径从未真正执行过，
  Run/Inline 路径才是唯一在工作的一条。
  因此这条英文**与本次性能改动无关**，是一个长期存在的覆盖缺口，
  已记入待修清单（下一步：查 `TranslateWpfElementInner` 的 TextBlock 分支为何不进写入）。

- **验收**：Blue Dawn 上 `perf_probe_large` PASS（含浸泡 8 分钟）、static 26/26、routing PASS。

---

## R54 WPF 文档分片用 IEnumerator 作游标，写 Run.Text 后静默中止（未发布）

- **来源**：2026-10-23 大文档续做回归（6000 Run）失败，用户可见形态为「消息日志/简报只翻第一条，其余永远英文」。
- **现象与证据**：`WpfSchedulingTests document` 输出 `translated=1 pending=0 slices=3`；
  引擎日志 `WPF Run Translated: Ready -> 就绪` 后紧接
  `WPF queued translation failed: 集合被修改；枚举操作可能无法执行。`
- **根因**：分片队列用 `IEnumerator`（`paragraph.Inlines` 等）保存跨切片位置；
  `TranslateFlowInline` → `SetRunTextKeepBinding` 写 `Run.Text` 会修改该集合，
  枚举器随即失效，续扫位置作废 → 只处理首条，其余节点丢失。
- **修复**：改用 WPF 文档模型的**稳定同级引用**作游标（`Blocks.FirstBlock`→`NextBlock`、
  `Inlines.FirstInline`→`NextInline`），写文本不会使其失效；不预分配子节点数组，
  30,000 Run 也不必在让出前扫完。
- **验收**：`R54-static-wpf-scheduling`（document/mutation/root/remote/input 五模式）
  6000 Run 全部译完、`pending=0`、切片数 >1、单切片 ≤16.7ms（实测峰值 7.57ms）；
  测试内含可重放的「旧枚举器」负向对照，必须失败。
- **教训**：**WPF 的集合不能用枚举器当跨切片游标**——任何写操作都会让 MeToo 状态失效；
  跨切片位置只能存对象引用（同级指针或显式下标 + 归属复核）。

## R55 补扫计量只覆盖窗口循环，ElementHost/render/layout 与未执行根未计入（未发布）

- **来源**：2026-10-23 本轮审查与实测。旧 `sweepMaxMs` 在真实启动界面报 **1ms**，
  而同一进程 `elementHosts` 实测峰值 **73.0ms**、`repaint` **74.4ms**、`render` **21.2ms**。
- **根因**：`sweepMaxMs` 只包住窗口循环；ElementHost 子树、`CompositionTarget.Rendering`
  与 `LayoutUpdated` 的工作在计时之外；`sweepWindowsTotal` 在投递阶段自增，把尚未执行的根也统计进去。
- **修复**：新增 `UiPerfMetrics`（posted/started/completed/pending/peakPending/maxQueueMs/over16ms
  分离，投递不算完成、取消不完成），WPF 队列/ElementHost/repaint/render/layout/WinForms 队列全部接线；
  `_wpfSweepWindowCount` 改为「已入队待执行」并在执行完成或取消时递减；分片耗时在 Drain 的 finally 累计。
- **验收**：`R55-static-ui-perf`（14 项，含排队不算执行、慢回调超帧、取消不完成、忙时拒绝重置）。
- **教训**：**投递 ≠ 执行**。计量必须落在真正执行 UI 工作的线程与区间上，否则「0ms」会掩盖整帧卡顿。

## R56 部署不刷新标签缓存，引擎/地名源更新后地图仍显示英文旧瓦片（未发布）

- **来源**：2026-10-23 用户反馈「地图翻译怎么没了」。截图为 Blue Dawn（506 活跃单位）全英文地名。
- **证据**：`WW/PlacenameSets/{Synoptic,Cities}/0000_0000.wwp` 已与仓库逐字节一致、
  标签服务日志显示正常出图（`LocalTile z=7 … rendered=8`），但
  `WW/Cache/Stamen Terrain - Labels/7/105/50.png` 内容仍是 `Liaocheng / Jining / Tai'an`。
  即：**缓存里的 PNG 由英文地名源渲染，且从未随部署刷新**；`deploy.sh` 只铺 dll/json/wwp。
- **修复**：`deploy.sh` 部署后调用 `scripts/refresh_placename_tiles.ps1` 重绘已有标签瓦片
  （保留 `.orig` 英文原件，不碰地形底图）；本次实机重绘 4,671 张，同一瓦片变为「聊城/济宁」。
- **验收**：`docs/PLACENAME_LABEL_RENDERING.md` 的缓存回归 + 本次同瓦片前后对照；
  实机判据为「当屏标签无英文残留」，并在证据里记录实际 `z/x/y`。
- **教训**：**只更新 `.wwp` 或只替换引擎都会留下旧缓存**；部署链路必须显式包含缓存重绘，
  否则本地验证与用户现场会出现"我这边是中文、你那边是英文"的错位。

## R57 单节点异常丢弃整个 Work 并从根重扫（未发布）

- **来源**：2026-10-23 分片实现审查（与 R54 同一状态机的第二个缺陷）。
- **根因**：队列级 `catch` 只记日志并结束该 Work，本拍已完成位置丢失；
  200ms 定时重新发现后又从根开始，形成「每拍重复失败、英文长期停留」。
- **修复**：节点翻译、附加子节点、子游标推进三处各自隔离异常，只跳过该节点/该集合，
  继续同级与父级；Work 仍能推进到 `pending=0`。
- **验收**：`R54-static-wpf-scheduling` 的 `mutation` 模式（扫描中途重建集合不抛错且收敛）。
- **教训**：**分片状态的异常边界必须与续做单位对齐**——抛一次就丢整棵树，等于把分片变成碰运气。

## R59 `DUMP_OPEN_FORMS` 在窗体集合变化瞬间抛「Collection was modified」（v4.2.3 发布后工具侧发现）

- **来源**：发布后自检时，Blue Dawn 入口脚本在战役窗口弹出的瞬间调用 `DUMP_OPEN_FORMS`，
  返回 `{"status":"error","message":"Collection was modified; enumeration operation may not execute."}`。
- **根因**：命令处理直接枚举 `Application.OpenForms`（活集合），游戏此时正在增删窗体；
  同类写法也出现在 `CLICK_BUTTON` / `CLOSE_FORM` / `DumpElementHostWpf` 等遍历处。
- **影响**：只影响诊断/自动化命令的瞬时可靠性，不影响游戏内翻译与游玩；
  但自动化会在窗体切换时误判为「观测失败」。
- **临时处置（已在工具侧落地）**：`scripts/live_load_blue_dawn.py` 对窗体观测做有限重试（8 次/1.5s），
  失败时如实报最后一次错误，不静默降级。
- **待修（下一版本）**：在引擎侧先把 `Application.OpenForms` 复制成快照（`new Form[Count]` + `CopyTo`），
  再遍历；每个窗体单独 try/catch，避免一个窗体失效中断整次导出。
- **教训**：**涉及 WinForms 活集合的命令必须先取快照**；这类错误是瞬时竞态，
  自动化的正确应对是「有限重试 + 保留真实错误」，而不是把失败当空结果。

## R60 部署/安装链路的两处工具侧陷阱（v4.2.3 期间发现，已修正流程）

- **① `localize_html_files.py --copy-to` 的目标必须是游戏根目录**：传 `Scenarios` 会把
  `Scenarios/…` 前缀再套一层，产生 `Scenarios/Scenarios/…` 的重复文件（实测文件数 340→680→1020）。
- **② 简报门禁统计中文前必须先解码 HTML 实体**：`&#26085;&#26399;`（“日期”）被判成未翻译，
  导致已汉化页面被计为英文（`verify_all.gate_briefings`，已修为 `html.unescape` 后再判 CJK）。
- **教训**：路径前缀与字符编码都属于「看起来对、结果错」的类别，必须用计数对照验证，
  不能只看「命令返回成功」。

## R62 诊断/自动化命令直接遍历 `Application.OpenForms` 活集合（工具侧，R59 未覆盖的 13 处）

- **发现方式**：R59 修完 `DUMP_OPEN_FORMS` 后，把同一类写法全仓扫了一遍，发现引擎里还有
  **13 处**直接 `FormCollection forms = Application.OpenForms; foreach (Form f in forms)`
  （`CTRL_TREE`、`DUMP_COMBOS`、`DUMP_LISTS`、`CLICK_MENU`、`SCEN_*`、`DROP_DOWN`、
  `WEBVIEW_*`、`FindDBViewerForm`、`FindMainMenuStrip`、UI 补扫主循环、`FLICKER_PROBE` 采样线程）。
- **根因**：`FormCollection` 是活集合，游戏同时开关窗口时枚举会抛
  「Collection was modified; enumeration operation may not execute」。R59 只修了一个命令，
  其余命令（尤其自动化会被反复调用的 `CLICK_MENU`/`SCEN_*`）仍会偶发整次失败。
- **修复**：统一改用 `OpenFormsSnapshot()`（先复制成 `Form[]` 再遍历），13 处全部落地；
  文件里剩下的唯一活集合访问就是快照函数自身。
- **验收**：`R61-static-forms-snapshot` 覆盖快照遍历零异常并保留负向对照；实机连续切换窗体
  跑 `CTRL_TREE`/`DUMP_OPEN_FORMS`/`WPF_DUMP` 不再出现该异常。
- **教训**：同类缺陷要**按写法全仓扫**，不能只修被报出来的那一个入口。

## R63 实时面板文本被「旧快照」回写截断 + 冒号右值半译钉死（v4.2.4 后两轮）

- **现象**（用户）：选中飞机后，右栏「单元武器 / 单元燃油」中英频闪，且部分行显示成
  `挂载方案: A/A: …` 这种半译串长期不动。
- **根因（两处叠加）**：
  1. **写入用旧快照**：补扫拿到 `cur` 后先算译文，再写回；游戏在这 0.2–0.3 秒内已经重建了
     整个文本块（多行块变成更长的串）。此时把基于旧快照的译文写回去，等于把新文本**截成前缀**，
     下一拍再被游戏刷回 → 用户看到的就是「中英交替 + 文本忽长忽短」（WPF_TEXTLOG 里
     大量 `new` 是 `old` 前缀的写入）。
  2. **冒号右值不放行**：`TranslateMixedValueTail` 的右值正则不允许冒号，
     `挂载方案: A/A: AIM-7M Sparrow III, Heavy` 这类「值里还有子键」的串被判为不可译，
     于是永远停在半译状态。
- **修复**：
  1. TextBlock / ContentControl / Run 三条写路径一律**写前复核**：`tb.Text == cur` 才写，
     否则丢弃本拍（下一拍自然拿到新值）。
  2. 右值正则允许冒号，右值内部按「子键: 值」再拆一层。
- **验收**：`XLATE_BATCH` 直调翻译可得整串中文；`WPF_TEXTLOG` 不再出现前缀截断写。
- **教训**：**任何「读—算—写」都必须假设对象在两者之间变了**；
  校验条件和写入必须是同一拍的数据。翻译层最贵的 bug 不是译错，是**把过期数据写回**。

## R64 同一文本被两条写路径译成两种说法，逐拍互相覆盖（机型行频闪的真根因）

- **现象**（用户两次强调）：选中飞机后，右栏机型行 `F-16I 战隼 [苏法/风暴]` 与
  `F-16I 战隼 [Sufa]` 来回跳；用户称「单元武器/单元燃油里面也在频闪」。
- **取证**：`FLICKER_PROBE 45000`（8ms 采样）抓到 `Block_UnitClass` 节点
  **20 个样本严格交替**：`F-16I 战隼 [苏法/风暴]` ↔ `F-16I 战隼 [Sufa]`，同一时刻
  航向/燃油/飞行时间节点都是单调正常值，排除「游戏自身刷新」。
- **根因**：两条写路径对同一输入给出**两种译文**——
  1. 词典整串命中 `F-16I Falcon [Sufa]` → `F-16I 战隼 [Sufa]`（词典刻意保留希伯来绰号原文）；
  2. 兜底函数 `TranslateMilitaryUnitName` 对**已含中文**的串仍执行
     `.Replace("Sufa", "苏法/风暴")` → `F-16I 战隼 [苏法/风暴]`。
  200ms 补扫读到 ① 就写成 ②；游戏每次刷新回英文后同步钩子又写回 ①，如此逐拍循环。
- **修复**：`TranslateMilitaryUnitName` 开头加护栏——**输入已含中文即原样返回**。
  该函数只负责把纯英文军名换成中文；词典已经是译文时它没有职责，
  再跑一遍只会制造第二种说法。词典因此成为唯一事实源。
- **验收**：
  - 静态：`IdempotencyTests` 新增三断言（兜底不动已译串、兜底仍处理纯英文、两路译名一致），
    `ALL PASS`。
  - 实机（r64 部署后，选中飞行中的 F-16I）：`FLICKER_PROBE 20000` 的 `changed` 列表里
    **不再出现 `Block_UnitClass`**，只剩航向 259→278、燃油递减、飞行时间递增三类正常变化。
  - 实机帧采样：`verify_regressions._analyze_panel_samples` 16/16 帧有效、推演时间前进、
    中英翻转 0、双译法回摆 0。
- **教训（探针本身也会坏）**：旧判据只比「有无汉字」，两种中文变体之间的交替**完全看不见**，
  于是第一轮探针报了 PASS 而用户仍在看闪屏。判据必须覆盖「同一元素出现多种译文」，
  且要区分**回摆**（A→B→A，缺陷）与**单调递增**（航向/计时，正常）——
  这次把判据改成「值再次变回之前出现过的相同值 ≥2 次」才既不漏报也不误报。

## R65 新武器行元素先入有界队列，翻译晚于首个绘制帧（「单元武器」中英频闪的真根因）

- **现象**（用户截图 + 三次追问）：选中飞机后，右栏「单元武器」四行（`4x Python 5` /
  `5x 20mm/85 M61A1 Vulcan Burst` / `12x Generic Chaff Salvo` / `10x Generic Flare Salvo`）
  在中英文之间来回闪；R64b/R64c/R64d 连续三轮补扫（LayoutUpdated 就地补救、登记实时目标、
  Rendering 根补扫）都没有收敛。
- **取证**（探针用最小 WPF 宿主复现，四种变体同一次运行，见
  `build/weapons-flicker/order/wpf-weapons-first-frame-order.log`）：

  | 变体 | 首个渲染帧文本 |
  |---|---|
  | 0 普通子树新元素（Loaded → 有界队列） | `WPF safety ready`（英文） |
  | 1 入树前同步翻译 | `安全就绪` |
  | 2 武器子树 + `TranslateWpfElement`（仍入队） | 英文（修复前） |
  | 3 武器子树 + Loaded 内同步 `TranslateWpfNode` | `安全就绪` |

  事件顺序实测为 `loaded → render → text`：**Loaded 早于首个绘制帧**。
- **根因**：`OnWpfElementLoaded` 拿到新元素后调用的是 `TranslateWpfElement` →
  `QueueWpfElement`。队列是 96 步 / 4ms 的有界分片，且排在已有工作之后，于是**翻译
  必然落在首帧之后** ⇒ 英文一定先被画上一帧。缺的不是“扫描时机太晚”，而是
  “入队即延后”这个机制本身；所以 R64b/c/d 三类补扫无论怎么加密都治不好。
- **被证伪的假设**（不要再据此改代码）：
  - “聚合 `TextBlock.Text` 改写会扁平化 Hyperlink”：显式 Inlines 时 `TextBlock.Text`
    为空，该分支根本不触发，结构完好（`controls/wpf-weapons-negative-controls.log`）。
  - “双渲染器 / GDI 叠加”：无二进制与运行时证据，不作为根因。
  - “显示转换器投影”：首帧同步已足够，无需通用绑定框架。
- **修复**：`OnWpfElementLoaded` 对**单位武器子树**（父链命中 `UnitWeapons_WPF`）的受限
  只读节点改为在 Loaded 内**同步** `TranslateWpfNode`；其余元素仍走有界队列（全树同步会
  淹没 UI 线程，R53）。同步路径保留全部既有闸门：`IsEditableWpfTarget`、
  `SetTextKeepBinding` 幂等短路、`AllowWrite`、写前快照复核。
  同时撤除三处已证伪的绘制前补救：`SweepRootsBeforePaint`/`RegisterSweepRoot`/`sweepRoots`、
  `OnLayout` 的 `TranslateDirtyInline`、队列 `DirtyRewritten` 优先级升级。
- **验收**：
  - `WpfSafetyTests weapons-first-frame-order`（8 检查）：武器子树首帧中文；**同一忙祖先队列
    下的普通子树仍为英文**作为探针负向对照（若该对照也变中文，说明探针已失效，不许判通过）。
  - `weapons-diagnostic`（19 检查）覆盖命令可达性、三种绑定元数据、Inline 图、源 setter 与
    转换器零调用、空/错目标、节点与字节超限、零帧拒绝。
  - `weapons-negative-controls` 如实记录“链接前提已被证伪”，不伪造成通过。
  - 回归：wpf 8/8（含 headers/editable/collection/lifecycle/dispatchers）、queue 7/7、
    idle 5/5 全绿；候选 `CMOLocalizer.dll` sha256 `50c36b4d…f4f3c2fb`。
- **教训**：
  1. **先测事件顺序，再改代码**。“补扫不够”不是频闪的通用解释；本例实测 Loaded 早于
     首帧，所以正确答案是“别入队”，而加多少补扫都没用。三轮补扫的失败不是力度不够，
     而是方向错了。
  2. **负向对照要能随修复一起失效**。修复后武器子树的“旧路径变体”自然变中文，必须把
     负向对照换到**仍走队列的普通子树**上，否则探针会退化成“永远通过”。
  3. 批量连续跑用例时出现过一次 `lifecycle` 偶发失败；改动前后两个 DLL 各单独跑 5 次
     均通过，判为批量运行的时序竞态而非确定性回归——但对竞态仍应留待独立复核。

## R67 武器行文本来自 MultiBinding，绑定被游戏重建后投影丢失（诊断未完成，待重新设计）

- **现象**：R65 部署后用户报告「开始推演了还在频闪」。真实帧序列（`build/weapons-flicker/deploy/A_*.png`）
  显示逐行阶梯式回退：整块被英文重绘 → 逐行改回中文，实测约 6 次/秒。
- **已确证的机制**（全部为实测，不是推断）：
  1. 武器行文本在 `TextBlock → Hyperlink → Run`，`Run.Text` 由
     **`MultiBinding`（`ViewModel.Qty` + `ViewModel.Name`，`StringFormat " {0}x {1}"`）** 求值产出。
     因此任何「写 `Run.Text`」的修法都会被下一次求值覆盖——这解释了 R64b/R64c/R64d 与 R65 全部无效。
  2. 游戏每次重建行时会把绑定**换回原始 MultiBinding**。连续采样 6 次得到：
     `projected=5 lang=CCCCC` → `projected=0 lang=EEEEE` → `projected=5 …`，
     即「投影被丢掉 → 全英文 → 重新投影后恢复中文」的循环，就是那约 6 次/秒的回退来源。
  3. 45 个渲染帧、225 个绑定 Run 样本在**属性层**已是全中文（english=0），
     说明投影本身有效，缺口在「绑定被替换的当拍」。
- **已做的尝试与结果**（同一子系统累计超过 3 次，已按规则停止继续叠加）：
  - R64b（LayoutUpdated 就地补救）、R64c（登记实时目标）、R64d（Rendering 根补扫）：全部失败。
  - R65（Loaded 内同步翻译）：解决了「首次生成」的首帧，但没有解决「绑定重建」。
  - Run 值变更钩子：钩子能触发，但写入值被下一次绑定求值覆盖。
  - 绑定投影 `WeaponsTextTranslationConverter`：安装后完全正确（不写回源、结构不变、
    `Qty` 更新直接出中文），但**被游戏的重建覆盖**。
- **当时的中间状态**：候选 `f87f4ce1…` 呈现「中文为主、周期性回退」，未达验收；由此引出 R68 的根因。
- **下一步需要的判断**（不是继续打补丁）：
  - 方案 1：在绑定**被替换的瞬间**重新投影（例如挂钩 `TargetUpdated`/`DataContextChanged`，
    或在容器生成时同步投影），需要先证明该时机早于首帧。
  - 方案 2：不再逐次投影，改为在**数据侧**一次性解决（例如让 `Name` 的取值经过词典），
    风险是会触及游戏业务对象。
  - 方案 3：接受现状（改善但不彻底），或重新界定需求。
  三者都需要用户确认后才继续；本轮不再自行叠加第四种文本修补。
- **教训**：
  1. **先确认「文本是谁产出的」再决定改哪里**。本例文本由绑定求值产出，
     所以「改文本」这一整类修法（补扫、同步、值钩子）在原理上都无效；
     查清 `MultiBinding + StringFormat` 这一条比再加十轮补扫更有价值。
  2. **游戏会重建绑定**：任何依赖「我替换了绑定」的方案，都必须证明能存活于重建，
     否则只是把回退频率降低一点。连续快照里的 `projected` 计数是判断这件事的可靠口径。
  3. 诊断探针要把「属性值」和「屏幕像素」分别记录：属性全中文而屏幕有英文，
     正好把问题定位到「重建与投影之间的窗口」，而不是继续怀疑翻译本身。

## R68 每行元素被游戏持续重建，投影必须挂在稳定容器上而非元素上（「单元武器」频闪终解）

- **来源**：R67 的投影方案上线后仍回退。用户催促「想全新的方法修」，遂放弃继续在元素上加钩子。
- **决定性取证**（`WEAPONS_DIAG SNAPSHOT` 连续 14 次采样，每次间隔约 500ms）：

  ```
  UnitWeapons=1  ItemsControl=5  runIds=2832,2844,2856,2868,2880
  UnitWeapons=1  ItemsControl=5  runIds=2892,2904,2916,2928,2940
  UnitWeapons=1  ItemsControl=5  runIds=2952,2965,2978,2991,3004   …
  ```

  `UnitWeapons_WPF` 与内部 `ItemsControl` 的对象 id **始终是 1 / 5（稳定）**，
  而每行的 `Run` **每次采样都换新 id**。即：游戏在**持续重建整行元素**，
  不是「改同一个绑定」。
- **根因**：凡是把状态挂在 `Run`（元素）上的做法——补扫、同步翻译、值变更钩子、
  绑定投影——都会在元素重建时**一起被丢掉**，只能等下一拍重做，
  屏幕上就是阶梯式的中英回退。这也解释了 R64b/c/d、R65、R66/R67 全部无效或只部分有效。
- **修复（新方法）**：把投影挂到**稳定的容器** `ItemsControl` 上
  （`AttachWeaponsContainerProjection` + `WeaponsContainerHook`）：
  容器 `Loaded` 时挂 `LayoutUpdated`，每次行重建触发布局更新后就
  **遍历当前所有已生成容器里的绑定 Run 重新投影**。
  因为元素是新建的，去重只能看「绑定上是否已是我们的转换器」，不能按对象身份。
- **为什么可行**：容器与祖先不随行重建，钩子不会被丢掉；
  `LayoutUpdated` 在重建后必然触发，因此英文行的存活期被压到同一布局周期内。
- **验收（真实运行中的游戏，推演进行中）**：
  - 像素：固定采样武器行区域 **60 秒 / 964 帧 / 0 次变化 / 1 种状态**（`deploy/V_*.png` 仅一张）。
  - 属性：46 个渲染帧、230 个 `Run` 文本样本 **全中文，英文 0**。
  - 身份：PID 60300、引擎 sha256 `5946d719…f6fb2f`、词典 `c9ff3b8f…dbb7e`（未变）。
  - 用户主观确认：「不闪了」。
  - 自动化：wpf 10/10（含 `weapons-binding-projection` 8 项、`weapons-first-frame-order`、
    `weapons-diagnostic` 19 项、`weapons-bound-run-sync`）、queue 7/7、idle 5/5。
- **教训**：
  1. **先分辨「改值」还是「重建元素」**。两者修法完全相反：
     改值要抢时机，重建必须挂到**不重建的父节点**上。
     连续采样相邻两次的对象 id 就能一眼区分，比任何猜测都快。
  2. **挂载点的寿命决定了方案上限**。前五轮都把状态挂在最短命的 `Run` 上，
     所以每轮都"生效但被丢掉"；换成容器后一次就成。
  3. **采样区域必须随 UI 布局变化重新定位**。本轮曾因燃油详情把面板挤上去，
     固定坐标采到的是面板位移（240 次"变化"），差点误判成仍频闪；
     验收前要先截图确认采样点仍覆盖目标文字。

## R69 燃油面板整数值丢小数位导致行宽跳动（「预留备用油量」行左移一格）

- **来源**：用户截图指出「单元燃油」里「预留备用油量」那一行，数值变成整数时会向左缩进一格，
  而上面一行总燃油量没有这个问题。
- **根因**：游戏用「有小数才显示小数」的格式输出重量。`5870.0` 显示成 `"5870"`，
  比 `"5870.3"` 窄两个 ASCII 字符宽度；该行随推演刷新时宽度来回跳，
  折行位置跟着变，视觉上就是抖动/缩进。不是翻译引入的，英文界面同样存在，只是中文更明显。
- **修复**：`NormalizeFuelWeightDecimals`——只把「整数 + 空格 + kg」补成 `x.0 kg`，
  不改数值，宽度恒定后折行不再跳。严格限定形态，不影响 `22.6 nm`、`14 分钟`、
  `1312.0 海里`、`26.76 kg`、`1500kg`（无空格）、武器行等。
- **验证**：
  - 11 条正反用例全部符合预期（应改 3 条、应不变 8 条）。
  - 实机采样 60 个重量样本：修复前抓到 `"5870 kg"`（裸整数）；
    修复后裸整数 **0 个**，整数位显示为 `7706.0 kg` / `6235.0 kg` 等。
  - 用户确认：跳动消失。
- **教训**：**数字格式化也是翻译层的一部分**。词典只换文字，但宽度变化引起的
  布局跳动同样会被用户读成「界面在闪」；排查频闪时不能只看中英切换。

## R70 「燃油消耗率被写死」是误判：飞机无航向持续盘旋，不是显示层 bug

- **现象**：用户报告切油门档位（经济巡航/军用推力/全加力）时，燃油消耗率闪一下正确值
  又变回固定的 `26.76`，怀疑被写死。
- **排查与证伪**：
  - 引擎写入记录（`TRACE_DUMP`）里**没有任何** kg / 消耗率相关写入，
    只有 4 个控件和 6 个静态标题（`编队燃油` 等），且都是重复写同一中文。
  - `26.76` 不在词典任何源文件里，是运行时值，排除「翻译产物」。
  - 真正原因（用户提供）：**飞机没有设置航向，一直在盘旋**，
    盘旋状态下燃油消耗率本来就是恒定值；设置了航向/任务后即随档位变化。
- **副产物（真实缺陷，已修）**：`FLICKER_PROBE` 把文本截断到 60 字符，
  而燃油面板整块约 180 字符、消耗率在第 3 行，因此该行**从未进入任何采样结果**。
  排查期间多次得到「油耗率无记录」的假结论，一度把它当成「被写死」的证据。
  已放宽到 400 字符。
- **教训**：
  1. **探针的截断长度是观测边界**。截断位置之外的问题不是「不存在」，而是「看不见」。
     报告「某项无变化」前，必须先确认探针真的覆盖了那一行。
  2. **恒定值要先怀疑场景，再怀疑代码**。盘旋/悬停/停泊等稳定状态下，
     消耗率、速度、航向本来就该是常数；在没有航向的想定里做档位对照，
     样本本身就是无效的。

---

## R71 数据库查看器仍有整屏英文：括号外残留未译 + 场景源句子碎片覆盖干净译法（v4.2.5 后）

- **来源**：用户 2026-10-27 反馈「继续检查数据库没有翻译的内容」。

- **现象与度量（引擎真实产出，不是估数）**
  用 `scripts/scan_english_residue.py --scope db` 把 106 个 DB 版本的
  `Data*.Name` 与 `Enum*.Description` 按文本去重（50,643 条）直接喂给引擎的
  `TranslateString`，判据是「翻完还剩多少拉丁字母」：
  | | 语料 | 已中文 | 半译 | 未翻 | 真缺口 |
  |---|---|---|---|---|---|
  | 修复前 | 50,643 | 13,429 | 31,501 | 5,713 | **1,979** |
  | 修复后 | 50,643 | 13,608 | 31,361 | 5,674 | **1,596** (净修 383，回归 0) |

  缺口结构再分三类（**这是本轮最重要的判断**）：
  | 类别 | 条数 | 是否该动 |
  |---|---|---|
  | 纯代号 / 仅括号内保留外文绰号（`SA-7a 圣杯 [9K32 Strela-2]`）| ~1,133 | **不动**，项目既有约定 |
  | 括号外可译残留（`Type 73`、`Vicon 78-200`、`M Sperry VisionMaster`）| 837 | 该动 |
  | 无任何译文的纯代号（`SRS-6 Romb-4A`）| 165 | 不动（凭空造名） |

- **根因（两条，性质完全不同）**
  1. **括号外残留**是历史批次只译了「主体词」，`Type NNN`、厂名、参数词原样留下。
  2. **场景源句子碎片覆盖干净译法**（真正的缺陷，R52「单一事实源」铁律的漏网）：
     `dict/src/*_side*.json` 里存在把 **DB 记录名**当键、值写成「英文前缀 + 整句」的条目
     （`"AAA Bty (37mm Type 65 Twin x 4)": "AAA Bty高炮连辖37mm Type 65 Twin型高炮4门之阵地列为要点目标 (必须拔除)"`）。
     `build_dict.py` 按**文件名字母序**合并源，`standalone_scenarios_side_*.json` /
     `showcase_side.json` 排在 `db_*.json` 之后 ⇒ 后写者胜 ⇒ DB 查看器直接显示脏值。
     这类条目本来是给想定**阵营简报文本**准备的，被误用在同一份 exact 字典上。

- **修复**
  1. 从 6 个场景源删除 **34 条**同键脏条目（`standalone_scenarios_side_p1/p2/p3_c2/p4_c1/p4_c3`、
     `showcase_side`），让 `db_record_names` 的干净译法胜出。
  2. 新增 `scripts/fix_db_residue.py`：**确定性**改写，只做两类——
     `Type NNN` → `NNN型`（中国制式 `NNN式`）、`WHOLE` 里逐条核过的整串译名。
  3. 经 `merge_record_names.py` 守卫合并 **376 条**进 `dict/src/db_record_names.json`。

- **关键教训（下一轮别再走的两条岔路）**
  1. **不要按词替换 DB 记录名**。实测把 `terms.json` 当词表整词替换会产出
     `40mm AGS/7.62mm MG Trackfire RWS` → `…巡逻艇…`（`PC`=巡逻艇 命中 `PC2`）、
     `AGM-84D` → `空地导弹-84D`、`Long Track` → `长 履带`、
     `警戒雷达` + `Radar`→`雷达` 拼成 `警戒雷达雷达`。这正是项目文档反复禁止的
     「按词替换产出垃圾串」。厂名/绰号保留原文，按 `merge_record_names.py` 的专名放行处理。
  2. **`Type NNN` 要按语料判断「型/式」**：中国制式用「式」（`65式`、`81式`），
     其余用「型」（`209-1100型`、`965M型`）。

- **新增门禁**：`R71-static-db-residue`（`src/tests/DatabaseResidueTests.py`），三道判据各带负向用例：
  ① 场景源不得用「比 db_* 更多英文残留」的值覆盖同键；
  ② 本轮清掉的 23 条具体脏值不得回生（**逐条断言**，比"猜句子碎片形态"精确——
  后者会把 `1x YaMZ 238 V 型柴油机，240 马力` 这类合法规格串误判）；
  ③ `fix_db_residue.py` 的改写不得污染型号连续体、括号内原文，且重复中文必须能被检出。

- **验收**：`verify_all` 10/11（`repository` 因本轮改动未提交而红）、
  `verify_regressions --static` 32/34（`R71` PASS；`R0` 因未部署到游戏目录而红，
  `R57` 为本机 `powershell.exe` 启动 60s 超时的环境抖动——单独执行该用例 31/31 OK）、
  `verify_routing` PASS、词典二次构建 sha256 一致（字节可复现）。

---

## R72 数据库列表行尾的 Comments 从未进入翻译通道（v4.2.5 后）

- **来源**：用户 2026-10-28「做1和2」——先确认 `Data*.Comments` 到底会不会显示给玩家。

- **可见性取证（这一步决定了后面 9,884 条要不要做）**
  静态侧会给出**错误结论**：8 个 `assets/db_templates/*.html`（以及游戏自带的
  `DB/Templates/*.html`）都**没有** Comments 字段，`Command.exe` 里 `Comments`
  只出现 4 次（1 次是 MemberRecords 表结构、2 次是 INI 解析器成员名、1 次是版本资源）。
  但实机截图 `evidence/live/dbviewer/103_pane_rendered.png` 的列表行把结论钉死：
  | DB 记录 | `Comments` 列 | 截图行尾 |
  |---|---|---|
  | A-300-600ST Beluga | `5x, Super Guppy Replacement` | `… 1995, 5x, Super Guppy Replacement` |
  | A.400M Atlas (ID 271) | `7x` | `… 2020, 7x` |
  | A.400M Atlas (法国) | `-` | 无尾巴 |
  | 3MS-1 Bison B | `-` | 无尾巴 |
  ⇒ **Comments 显示在列表行尾**（由游戏混淆代码拼接，所以在模板/串表里查不到）。

- **根因**：`TranslateDBListEntry` 只处理三件事——方括号里的平台子类型、
  ` -- 国家 (兵种)`、一小撮导弹/platform 双语化；行尾 Comments 原样输出。
  也就是说这批词条即使译好也显示不出来（同 R42 的「函数好但没接线」失效模式）。

- **为什么必须单独建分区（本轮最重要的架构决定）**
  不能把 Comments 词表并进 `exact`：Comments 里的
  `Tank`(油箱)、`Mount`(挂架)、`Group`(编组) 与界面同名词条**含义不同**
  （界面上 `Tank`=坦克、`Group`=编队），实测与现有 exact **冲突 47 条**，
  一旦合并就会把界面译坏。因此新增顶层分区 `comments`：
  1. 该分区**排在文件最前**（`_meta` 之后、`offsets` 之前）。
     旧引擎的分区扫描只认 `offsets/exact/partial/words`，且**忽略第一个已知分区之前的内容**，
     所以新词典仍能被旧引擎正常加载（向后兼容硬要求，`verify_engine_parse` 已加断言）。
  2. 引擎只在 DB 列表行尾查这张表（`commentsMap`），界面词库零风险。

- **修复**
  1. `build_dict.py`：新增 `comments` 分区（源 `dict/src/db_comments.json`），
     并在 `iter_source_maps()` 里**排除**该文件（否则它会同时污染 exact）。
  2. 引擎：`ParseJsonSections/ParsePairs` 支持 `comments`；
     `TranslateDBRowCommentsTail()` 先试「整段后缀命中」（Comments 自带逗号时只有整段才对），
     再退化为「按逗号分段逐段查表」；`IsYearOrCount` / `TranslateCommentQuantity`
     把 `1992`、`4x`、`4x Mounts`、`3x pr Bty`、`2 tons`、`8k lb` 这类结构式内容交给规则，
     词表只放**词**。未命中一律原样保留，绝不拼词。
  3. `dict/src/db_comments.json`：441 条人工核过的词表（国家、舰种缩写、兵装通用词、
     卫星项目名、编制/功能短语）。
  4. `scripts/db_comments_work.py`：待译清单 + 词表守卫（门禁 `--check`）。

- **量化（`scripts/db_comments_work.py`，DB3K_519 + CWDB_519，35,849 行 / 15,731 个唯一值）**
  | 指标 | 数值 |
  |---|---|
  | 完全覆盖（每个逗号段都能出中文） | 19,330 行 / 53.9% |
  | 部分覆盖 | 6,857 行 / 19.1% |
  | 未动 | 9,662 行 / 27.0% |
  | 剩余里仍有「可译词未译」的段 | 约 7,000 个 / 覆盖 10,400 行 |

  **用户可见验收（真实 DB 行，不是抽样）**：把 `DB3K_519 + CWDB_519` 的 35,849 条非空
  Comments 行逐条过「词表 + 数量规则」，翻完之后：
  | 口径 | 结果 |
  |---|---|
  | 段内已无可译英文词（型号批次词 `Mod`/`Blk`/`Mk`/`Series` 不算可译） | **31,830 行 / 88.8%** |
  | 严格口径（连 `Mod 2` 这类型号批次词也算作残留） | 见 `db_comments_work.py --check` 输出 |

  开工前这两项都是 **0**（Comments 完全没进翻译通道，行尾永远是英文）。
  `Mod`/`Blk`/`Flight`/`Lot` 在中文军事文献里本就保持原文，属**不译**而非漏译。

  词表 4,070 条（`comments` 分区）。剩余长尾几乎都是型号/代号（`F17 Mod 2`、`Mk37 Mod 3`、`SA-6`、
  `Mk45 Mod 1/2`），按既有约定保留原文——它们**不是漏译**。
  继续加词的边际收益已降到每 50 条约 +0.15%，续做时按
  `python scripts/db_comments_work.py --top 200` 的清单从高频段往下取。

  覆盖率是**门禁阈值**：`scripts/db_comments_work.py --check` 断言完全覆盖率 ≥ 44%，
  词表被误删/清空会直接判红（否则「表还在但内容没了」会静默通过）。

- **尚未验证的一点（如实记录）**：`TranslateDarkListView` 对「已含中文」的行整体跳过，而本项是在 `TranslateDBListEntry` **同一次调用内**接上 Comments 的（所以不存在 R42 那种「函数好但被前面的含中文守卫挡住」的问题）。但如果将来有别的路径先给整行写入了中文，行尾步骤就不会再跑——实机验证时若发现某类行尾仍英文，先查这一点。实机 A/B 仍受本机 Steam 未登录阻塞（游戏启动即退出），部署已就绪，登录后可直接复核。

### R72 复查修正（独立 reviewer 复核后，2026-10-28）

独立 reviewer 按 4e57dd7 复验后给出 3 条发现，均已修并补了能红的用例：

1. **[HIGH] 端到端用例假绿**：原断言是
   `realHit.Contains("超级孔雀鱼替代") || !realHit.Contains("Super Guppy Replacement")`，
   而 `5x, Super Guppy Replacement` 当时根本不在词表里 ⇒ 右半边恒真 ⇒ 换成任何输入都能过，
   台账里「译出超级孔雀鱼替代」的验收从未真正被执行。
   **修**：把该真实 DB 值加进词表，断言改成**精确译文** `EndsWith("5x、超级孔雀鱼替代")`。
2. **[MEDIUM-HIGH] 形态闸门太松，会误伤非 DB 列表**：`TranslateDarkListView` 对游戏里
   **所有** DarkListView 生效，原来只卡「含 ` -- `」，而 `Alpha Team -- Bravo, Charlie, Delta`
   这类非 DB 行也含 ` -- `，词表里的通用词（`Charlie`=查理级、`Active`=主动）会把它误译。
   **修**：闸门加第二个特征——行内必须出现**四位年份**（DB 行必有服役年份区间）。
   补 3 条负向用例（含 ` -- ` 但无年份的行必须原样返回）。
3. **[MEDIUM] `comments_freq.json` 无生成者**：`db_comments_next.py` 读它排序，
   但 `db_comments_work.py` 从不写它。`build/` 一清理，`--limit` 会静默输出「待译段 0 个」
   并以 0 退出，翻译循环无提示断粮。
   **修**：`db_comments_work.py` 负责写出频次表；`db_comments_next.py` 缺文件时明确报错并返回 2。

复查确认无问题的部分（原样记录）：五分区边界逐字节复刻校验一致、两分区译文路径在全部
15,731 个真实 Comments 值上只在行尾命中、黑名单解除未引入任何池注入（offsets 仍 8,756）、
`verify_engine_parse` 与引擎镜像一致。

- **后续补充（2026-10-28 凌晨）**：把成批出现的**系统性形态**从词表移到引擎规则，
词表只留"词"：
  ① `Retired 1977` / `Operational 1976` / `Reactor boosted to high orbit <date>` 等句首模板；
  ② `Nx Bty/Plt/Bn/Regt`、`N Rnds`、`Nx Tubes/Barrels/DCs/Missiles/Salvos/…` 数量+单位；
  ③ `Assoc w/ X` 家族（8 种拼写变体：`Assoc w/`、`Assoc. w/`、`Assow w`、`Associated with`…）。
  每条规则都有对应用例；词表因此**净减** 68 条 Assoc 键 + 51 条纯数量键。

*（顺带修掉一个同源缺陷：年份闸门最初用 `@"…"` 写进 C# 时被脚本转义成了退格字符，
正则在实机上永不匹配——现已改为正确的逐字字符串，并由上面的负向用例守住。）*

**独立 reviewer 二次复验（HEAD 27ff362）：verdict = pass。** 三项修复均确认落地、
用例真实能红（改错期望 → 用例红、rc=1）；五分区边界逐字节一致、offsets 恒为 8756、
无新问题。证据：`agent://08261963-674a-4702-a0b3-d21ce97da18a`。
- **回归**：`R72-static-db-comments`（接线 + 行为 + 端到端 + 负向：未命中不拆词、
  纯代号不污染；端到端用**真实词库**跑真实 DB 行 `A-300-600ST Beluga … 5x, Super Guppy
  Replacement` → 译出「超级孔雀鱼替代」）、`R72-static-comments-table`（词表守卫 +
  覆盖率阈值）。两者都在 `--static` 里跑。

---

## R73 blacklist 把 96 条「已有干净译名」的想定舰名/基地名整段清掉了（v4.2.5 后）

- **现象**：Desert Storm / Fail Safe / Red Tide 等想定的简报与编成表里，
  `CG-55 Leyte Gulf`、`DD-963 Spruance`、`HMS Fearless [LPD]`、
  `RFA Sir Lancelot [LSL]`、`Task Unit 70.01.01` 等**永远是英文**，
  而 `dict/src/*_side.json` 里早就备好了中文（`CG-55 莱特湾号` 等）。

- **根因（git 取证，不是猜测）**：`c053fa3`（2026-10-15）当时这些键的值是**句子碎片**
  （`"CG-55 Leyte Gulf": "CG-55 Leyte Gulf巡洋舰担负护航。"`），
  那轮把它们整批写进 `dict/src/blacklist.json` 止血（黑名单会从 exact/offsets 里 clear 掉）。
  后续批次把值修成了干净译名，**但黑名单条目没有一起撤**，
  于是"译好了却被自己清掉"。

- **修复**：按「源里已有干净译文（含中文、无句读、不是"键+句子"）」判据解除 64 条，
  黑名单 163 → 42（剩 37 条真 SQL/结构片段 + 5 条无干净译名：
  `Task Pool` / `Shift + Ins` / `Be-12 Mail` / `LCVP #7050 [LCVP]` / `SourceAnnex…`）。
  同时删掉 `red_tide_side.json` 里 `Banak` 的句子碎片条目，
  让 `fail_safe_side.json` 的 `Banak基地` 胜出。

- **验收（可复核）**
  - `build_dict` 后 `offsets` 仍为 **8,756**（未新增一条池注入 ——
    side 源不属于 `offset_safe_sources`，解除黑名单不可能引入偏移注入风险）；
  - `exact` 70,667 → 70,731（+64，即解除的那些键）；
  - 全库缺口 1,913 → 1,818（scenario 288 → 193；db 1,596、ui 29 不变）。
  - 待办：实机 A/B 因本机 Steam 处于未登录状态（游戏启动即弹「Steam client Not running」
    并退出）未能执行，**需用户登录 Steam 后补跑**；静态侧已证明这与既有已上线译名
    （`HNoMS Bergen [DD]` 等同型串一直是译的）无差异。

---

## R74 缺口统计口径的三处虚高：把「不该译」算成「没译」（v4.2.5 后盘点）

- **来源**：2026-10-28 盘点「数据库还有多少要汉化」时，`db_coverage_audit.py` 报出
  23,514 条"可译缺口"，而 `scan_english_residue.py` 只报 1,596 条真缺口。
  两个数差 15 倍，必须查清谁在说谎，否则会把工时投到不需要译的地方。

- **三处口径虚高（全部实测）**
  1. **Descriptions 正文**：审计报 3,063 条"prose 缺口"。逐行分类后发现
     **88%（1,019/1,156 唯一行）是参考文献引用**——`Mladenov, Alexander. Soviet Cold War
     Fighters. Fonthill Media, 2017`、`https://www.military-today.com/...`；
     剔除引用后剩 126 行，其中绝大多数是**雷达参数**（`PRF: 27 pps`、
     `0.1m2 RCS: 54nm`、`6000m: 150 km`）、**机型代号**（`Su-25K`、`MiG-23ML`）
     与**鸣谢名单**（`CV60, apd1004, El Savior…`）。这些按惯例都保留原文，
     真需要译的正文≈0。
  2. **record_comment（Comments 列）**：审计报 9,884 条"可译缺口"，但它的 `has_word`
     判据把 `2x Mounts`、`F17 Mod 2` 这类型号/数量段也算作有可译词。
     分开统计后：型号/代号段约 4,300 个（不译），其余才是可译内容。
     （Comments 本身**确实显示**，见 R72，所以这块是真工作量，不是虚高。）
  3. **enum 面**：2,318 个唯一值里只剩 11 条可译缺口，早已完成。

- **结论（下一轮直接照这个排期，别再重算）**
  | 面 | 审计口径缺口 | 真实待译 | 说明 |
  |---|---|---|---|
  | Descriptions 正文 | 3,063 | ≈0 | 引用/参数/代号，保留原文 |
  | Comments 列 | 9,884 | 见 R72（已做完 56%） | 真工作量 |
  | 记录名 Name | 10,556 | ≈22 | 其余是型号/代号 |
  | 枚举 | 11 | 0 | — |

- **教训**：`db_coverage_audit.py` 的 `has_word` 是**粗筛**，不能当工时依据；
  定工作量必须用 `scan_english_residue.py`（引擎真实产出）+ 人工分类一层。
  两个脚本的职责已在路由表里分开登记，勿再把审计数当"待译条数"引用。
