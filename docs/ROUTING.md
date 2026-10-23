# 路由表（Routing Table）

> **这份表是给 agent 和人共用的地图。** 目的是让任何一次改动都能在 30 秒内找到
> "该改哪个文件"，以及"改完会影响谁"。
>
> **铁律：新增任何文件，必须在本文档登记它的职责，否则门禁 `verify_routing` 会直接红。**
> 这不是形式主义——没有登记的脚本，半年后没人知道它还有没有用、能不能删。

验证命令：`python scripts/verify_routing.py`

---

## 0. 先看这里：三条最容易踩的坑

1. **只改 `dict/src/*.json`，永远不要直接改 `dict/CMOLocalizer.json`。**
   后者是 `build_dict.py` 的产物，手改会在下次构建时被覆盖（单一事实源铁律）。
2. **`--static --installer` 与 `--live` 必须分两次跑**，installer 用例会 `kill` 游戏进程。
3. **发版动作（`package.sh` / `publish_release.py` / 打 tag）必须获得用户明确授权。**
   已发布资产只增不改。

---

## 1. 系统总览：数据是怎么流动的

```
            dict/src/*.json  (人工维护的翻译源，唯一事实源)
                    │
                    ▼  build_dict.py  (确定性构建，字节可复现)
            dict/CMOLocalizer.json  (产物：exact + words + offsets + 注入池)
                    │
                    ▼  build.sh  (csc 4.8 编译，C# 5)
        build/CMOLocalizer.dll   ←─ src/engine/CMOLocalizer.cs + Reflect.cs
                    │
                    ├── deploy.sh ──→ 游戏目录（含 Command.exe.config 注入 appDomainManager）
                    │
                    └── package.sh ──→ CMO_Chinese_Pack_<ver>.zip
                                       CMO_Chinese_Setup_v<ver>.exe  ←─ src/installer/
```

**运行时**：`LocalizerDomainManager` 由 CLR 加载 → 挂 WPF 钩子 + 翻译回调 +
池注入 → 读 `dict/CMOLocalizer.json` → 改动界面文本。

---

## 2. 顶层锚点文件

| 路径 | 职责 | 谁会读它 |
|---|---|---|
| `VERSION` | 唯一版本号（如 `4.2.2`）。**发版只改这里** | `build.sh`/`package.sh`/`verify_all.gate_version`/`publish_release.py` |
| `GAME_BUILD` | 适配的游戏内部版本（`1900.23`）。游戏更新时才改 | `verify_all.gate_version`、`L-build-adaptation` |
| `STABLE_VERSION` | 稳定版版本号（README 稳定版通道的事实源） | `sync_readme_downloads.py` |
| `README.md` | 用户主文档 + 下载区（下载区由脚本生成，勿手改） | `package.sh`（改名成 `汉化说明.md` 入包）、`sync_readme_downloads.py` |
| `AGENTS.md` | **agent 强制约定**：先读路由表、新文件必须登记、数据源铁律、编码约定、发版需授权 | agent 进入仓库时读；与 `docs/ROUTING.md` 互为入口 |

---

## 3. 构建 / 部署 / 发布

| 路径 | 职责 | 何时改它 |
|---|---|---|
| `scripts/build.sh` | 编译引擎 DLL、更新器、共享测试、安装器；顺带跑 `build_dict.py` | 增删编译目标、改编译参数 |
| `scripts/build.ps1` | **仅引擎构建**（不碰游戏、不写安装包）：自动收集 `src/engine/*.cs` + `src/shared/MapLabelRenderer.cs`，并把 `WebViewTranslation.js` 作为嵌入资源编进 DLL。新增引擎源文件无需改脚本 |
| `scripts/deploy.sh` | 把 `build/` 产物装进游戏目录 + 写 `Command.exe.config` | 部署清单变了 |
| `scripts/package.sh` | 打绿色包 zip（含 `--no-report` 防覆盖 live 报告） | 包内文件清单变了 |
| `scripts/publish_release.py` | 建 GitHub Release + 上传 4 个资产。**`--verify-only` 是纯只读校验** | 发布流程变了 |
| `scripts/release_check.py` | 发版统一入库检查（17 项）。未发布前最后一道闸 | 新增发版前置条件 |
| `scripts/sync_readme_downloads.py` | 生成 README 下载区；`--check` 模式供门禁 | 下载表格式变了 |
| `scripts/verify_release.py` | 词典产物级发布门禁（条目数/结构） | 词典契约变了 |
| `scripts/verify_no_secrets.py` | 密钥门禁：禁止明文凭据入库 | 凭据规则变了 |
| `scripts/hooks/pre-commit` | git 钩子，调用上面的密钥门禁 | 很少改 |

---

## 4. 引擎（运行时汉化核心）

| 路径 | 职责 |
|---|---|
| `src/engine/CMOLocalizer.cs` | **整个汉化引擎**（约 7600 行，C# 5）。一轮改动的主战场，见下方内部地图 |
| `src/engine/Reflect.cs` | 反射辅助（私有字段/方法访问），引擎的地基 |
| `src/engine/ComboBoxTranslation.cs` | 下拉项增量翻译：动态回填排队，保留业务键/选中值/原生类型 token |
| `src/engine/DarkTreeTranslation.cs` | 深色树分片遍历：核对实时集合归属，分片避免长阻塞 |
| `src/engine/UiLifecycle.cs` | UI 生命周期挂钩（控件创建/重挂载），替代纯周期扫描来发现新控件 |
| `src/engine/UiIdleContinuation.cs` | 空闲续做：`Application.Idle` + 1ms 兜底 Timer 的单次续接器（只在 UI 线程创建/取消） |
| `src/engine/UiTranslationQueue.cs` | 有界增量翻译队列：按 UI 线程派发，执行前复核控件归属与输入保护 |
| `src/engine/UiPerfMetrics.cs` | 实际 UI 回调耗时与待执行计量；投递、执行、完成分别计数，供性能探针识别排队与超帧 |
| `src/engine/WpfTranslationQueue.cs` | 每 Dispatcher 一份有界合并队列，以节点/Inline 游标分片；换根或卸载取消旧工作，避免全树异线程积压 |
| `src/engine/WebViewTranslation.cs` | WebView2 译文注入宿主：注册文档脚本、限制来源/nonce/容量、拒绝旧回包与编辑区写入 |
| `src/engine/WebViewTranslation.js` | 上面宿主注入页面的脚本。**以嵌入资源 `CMOLocalizer.WebViewTranslation.js` 打进 DLL**（`scripts/build.ps1` 负责 `-resource:`），改名或移动会运行期抛 `Missing WebViewTranslation.js embedded resource` |
| `src/engine/WpfValueTranslation.cs` | WPF 值翻译（`Content`/`Text`/绑定安全写回），从 `CMOLocalizer.cs` 抽出的 partial 分片 |
### `CMOLocalizer.cs` 内部地图（按子系统找，不要按行号找）

| 子系统 | 关键字（grep 这些） | 干什么 |
|---|---|---|
| 域管理器入口 | `LocalizerDomainManager`、`CustomAppDomainManager` | CLR 加载入口，挂载一切 |
| 翻译主流程 | `DoTranslate`、`TranslateString`、`TranslateBracketSegments` | 文本 → 译文的核心管道 |
| 中英混排修复 | `TryFixMixedBeforeChineseGuard`、`FixMixedEnglishPrefix`、`TranslateMixedValueTail` | R42/R45：`Inherited, 中文`、`状态: Unassigned` 这类 |
| 组合翻译 | `TryComposeTranslate`、`TranslateComposedName`、`TranslateNumberWithUnit` | R44：结构交给代码、词汇交给 `dict/src/terms.json` |
| WPF 钩子 | `HookWpf*`、`SetTextKeepBinding`、`SetContentKeepBinding` | 挂钩控件属性，改文本但保留绑定 |
| 重绘节拍 | `StartWpfRepaintTick`、`_repaintPeriod`、`_idleTicks`、`TranslateWpfElementHostsNow` | R46/R47：Timer 驱动的补扫（**不是** `CompositionTarget.Rendering`，那个在静止面板永不触发） |
| 写入门禁 | `AllowWrite`、`WRITE_BUCKET_*`、`hookedWpfElements` | R43：令牌桶熔断，防中英拉锯 |
| 池注入 | `POOL_DUMP`、`poolDisplay`、`inject` | 把中文直接写进游戏的字符串池 |
| 地图标签 | `TileClient`、`MapLabel` | 地图瓦片/标签服务（与 `src/shared/MapLabelRenderer.cs` 配合） |
| 诊断探针 | `XLATE`、`XLATE_BATCH`、`WPF_DUMP`、`WPF_TEXTLOG`、`CTRL_TREE`、`TRACE_*`、`FLICKER_*` | 实机验证用命令通道（见 §9） |

---

## 5. 共享层

| 路径 | 职责 |
|---|---|
| `src/shared/CMOShared.cs` | 引擎/安装器/更新器共用的工具函数（版本比较、路径、JSON 等） |
| `src/shared/CMOSharedTest.cs` | 上面的自测入口 |
| `src/shared/MapLabelRenderer.cs` | 地图中文标签渲染（GDI+ 绘制），配 `MapTileServerTests.cs` |

---

## 6. 安装器 / 更新器

| 路径 | 职责 |
|---|---|
| `src/installer/CMO_Chinese_Setup.cs` | 安装器（约 2000 行，**CRLF**）：安装→清单→设置保留→回退 |
| `src/updater/CMO_Updater.cs` | 更新助手（约 1500 行）：`--check` 检测新版本、提示升级/回退 |

两者都依赖 `src/shared/CMOShared.cs`。改动后必须跑 installer 门禁（见 §8）。

---

## 7. 词典数据（`dict/`）

### 7.1 唯一事实源：`dict/src/`

`build_dict.py` 会 `glob("dict/src/*.json")` 全部读取，所以**放进去就是生效的**。

| 路径 | 职责 |
|---|---|
| `dict/src/terms.json` | 组合翻译术语表（127+ 条），镜像实现在 `scripts/compose_terms.py` |
| `dict/src/force_offsets.json` | 按池索引强制改写的条目（**谨慎**：形状铁律见 §11） |
| `dict/src/pool_display_inject.json` | 池级注入表（6000+ 条），形状由 `scripts/inject_filter.py` 判定 |
| `dict/src/blacklist.json` | 绝不允许翻译的串（引擎标识符、代码片段） |
| `dict/src/identifiers.json` | 引擎标识符清单（图层名、缓存键） |
| `dict/src/labels.json` | 界面标签 |
| `dict/src/menu_terms.json` | 菜单术语 |
| `dict/src/campaign_ui.json` | 战役界面串 |
| `dict/src/scenario_titles.json` | 想定标题 |
| `dict/src/scenario_settings.json` | 想定设置项 |
| `dict/src/ui_cleanup_terms.json` | 界面清理类替换 |
| `dict/src/term_replacements.json` | 术语级替换规则 |
| `dict/src/legacy_fixes.json` | 历史遗留修复 |
| `dict/src/legacy_preserved.json` | 旧产物独有 exact/安全 offsets 的显式源，最低优先级保全；构建不再读取输出作为底座 |
| `dict/src/legacy_garbage_cleaned.json` | 早期"按词拼接"垃圾串的人工清洗结果 |
| `dict/src/sentence_fragment_fixes.json` | 句子碎片修复 |
| `dict/src/live_audit_fixes.json` | 实机审计发现缺口的补齐 |
| `dict/src/live_message.json`、`dict/src/live_side_p1.json`、`dict/src/live_side_p2.json` | 实机消息/简报补译 |
| `dict/src/tutorial_gap_fixes.json`、`dict/src/tutorial_prose_fixes.json` | 教程缺口与散文修复 |
| `dict/src/db_*.json` | 数据库汉化（详见 §7.4） |
| `dict/src/*_message.json`、`dict/src/*_side.json` | 各战役简报正文（Chains of War / Desert Storm / Falklands / Kashmir Fire / Northern Inferno / Red Tide / Shifting Sands / The Silent Service / Tides of War / Fail Safe 等） |
| `dict/src/chunk0*.json` | 早期批量翻译的分片（历史批次，勿新增） |
| `dict/src/air_ops_terms.json` | 空中作战术语 |
| `dict/src/dlc_briefing_fixes.json` | DLC 简报修复 |
| `dict/src/live_message.json`、`dict/src/live_side_p1.json`、`dict/src/live_side_p2.json` | 实机审计补译（消息正文 / 简报正文分片） |
| `dict/src/showcase_message.json`、`dict/src/showcase_side.json` | Showcase 战役简报正文 |
| `dict/src/standalone_scenarios_message.json`、`dict/src/standalone_scenarios_side_p1.json`、`dict/src/standalone_scenarios_side_p2.json`、`dict/src/standalone_scenarios_side_p3_c1.json`、`dict/src/standalone_scenarios_side_p3_c2.json`、`dict/src/standalone_scenarios_side_p3_c3.json`、`dict/src/standalone_scenarios_side_p4_c1.json`、`dict/src/standalone_scenarios_side_p4_c2.json`、`dict/src/standalone_scenarios_side_p4_c3.json` | 独立想定简报正文（消息 + 按章节切分的 side 分片） |
| `dict/src/chains_of_war_message.json` | Chains of War 简报正文 |
| `dict/src/desert_storm_message_p1.json`、`dict/src/desert_storm_message_p2.json`、`dict/src/desert_storm_side.json` | Desert Storm 简报正文 |
| `dict/src/falklands_message.json`、`dict/src/falklands_side.json` | Falklands 简报正文 |
| `dict/src/kashmir_fire_message.json`、`dict/src/kashmir_fire_side.json` | Kashmir Fire 简报正文 |
| `dict/src/northern_inferno_message.json`、`dict/src/northern_inferno_side.json` | Northern Inferno 简报正文 |
| `dict/src/red_tide_message.json`、`dict/src/red_tide_side.json` | Red Tide 简报正文 |
| `dict/src/shifting_sands_message.json`、`dict/src/shifting_sands_side.json` | Shifting Sands 简报正文 |
| `dict/src/the_silent_service_message.json`、`dict/src/the_silent_service_side.json` | The Silent Service 简报正文 |
| `dict/src/tides_of_war_message.json` | Tides of War 简报正文 |
| `dict/src/tutorial_gap_fixes.json`、`dict/src/tutorial_prose_fixes.json` | 教程缺口 / 散文修复 |

### 7.2 产物

| 路径 | 职责 |
|---|---|
| `dict/CMOLocalizer.json` | **构建产物**（纯 LF）。引擎运行时读它。**禁止手改** |
| `dict/strings_pool.json` | 游戏字符串池的抽取结果（`triage.py` 产出，翻译工作队列的来源） |
| `dict/scenario_meta.json` | 想定元数据（标题/设置/战役名）。由 `scenario_meta.py` 生成，键已排序保证稳定 |
| `dict/ui_terms.json` | 界面术语表 |
| `dict/glossary.json` | 术语对照（人工裁定） |
| `dict/cmo_master_dictionary.json` | 早期主词库（历史，保留参考） |
| `dict/all_scen_items.json`、`dict/extracted_campaigns_scens.json`、`dict/scen_tree_dict.json` | 想定/战役结构抽取结果 |
| `dict/briefing_dict.json` | 简报词典 |
| `dict/brief_work.json`、`dict/brief_translations.json`、`dict/brief_after.json` | 简报翻译工作队列/结果中间态 |
| `dict/placenames/cities.json` | **城市地名审核源**（4,287 条，按 WWP 顺序）。逐项含原名/坐标/中文/来源/说明，是 Cities.wwp 的唯一事实源 |
| `dict/placenames/synoptic.json` | **区域标签审核源**（321 条）。同上结构，对应 Synoptic.wwp |
| `dict/placenames/metadata.json` | 地名审核的固定元数据指纹。`verify_all` / `verify_placenames.py` / `package.sh` 都用它判断「记录集合是否被改过」（改 flags、换顺序、容差内挪坐标都会失败） |
| `dict/all_scen_items.json` | 从所有想定抽出的可译条目快照 |
| `dict/extracted_campaigns_scens.json` | 战役内想定清单抽取结果 |
| `dict/scen_tree_dict.json` | 想定树结构词典（加载对话框用） |
| `dict/brief_work.json`、`dict/brief_translations.json`、`dict/brief_after.json` | 简报翻译工作队列 / 译文中转 / 合并结果 |
| `dict/chunks/*`、`dict/brief_src/*`、`dict/brief_chunks/*` | 批量翻译的分片输入/输出（历史批次；`chunk0*.json` / `brief0*.json`） |

### 7.3 数据库汉化来源

| 路径 | 职责 |
|---|---|
| `dict/src/db_enums.json`、`dict/src/db_enums_complete.json`、`dict/src/db_enums_extra.json` | 数据库枚举值（国家/阵营等） |
| `dict/src/db_enum_displays.json` | 枚举显示名 |
| `dict/src/db_record_names.json` | 装备/设施记录名 |
| `dict/src/db_descriptions.json` | 数据库查看器描述正文（**最大的单个源**） |
| `dict/src/db_platforms_facilities.json` | 四类高原生平台/设施名 |
| `dict/src/db_viewer_html_terms.json` | 数据库查看器 HTML 模板术语 |
| → 目录 | `assets/db_templates/*.html` 是 8 个 HTML 模板（见 §10） |

---

## 8. 门禁与验证（改完必须跑对应项）

### 8.1 总入口

| 路径 | 职责 |
|---|---|
| `scripts/verify_all.py` | 统一验收：`gate_version`（五处版本 + 构建号）、`gate_scenario_meta`、`gate_readme_downloads` 等 |
| `scripts/verify_regressions.py` | 回归测试（台账 `BUG_REGISTRY.md` 的可执行版本）。`--static` / `--static --installer` / `--live` |
| `scripts/verify_routing.py` | **本文件的门禁**：每个受控文件都必须在本表登记 |

### 8.2 专项门禁

| 路径 | 职责 |
|---|---|
| `scripts/verify_engine_parse.py` | 按引擎的方式解析 `dict/CMOLocalizer.json`（防"字典合法但引擎读不了"） |
| `scripts/verify_terminology.py` | 术语一致性 |
| `scripts/verify_menu.py` | 菜单串全译 + 输出术语表 |
| `scripts/verify_placenames.py` | 已评审地名检查（可证明只发布批准过的名字） |
| `scripts/verify_live_test.py` | 实机测试产物/截图/证据完整性 |
| `scripts/compose_terms.py` | 与引擎组合规则**一一对应**的镜像实现；`--check` 审计半译残留 |
| `scripts/inject_filter.py` | 池级注入形状安全判定（**单一事实源**，构建与门禁共用） |
| `scripts/identifiers.py` | 清除绝不该翻译的引擎标识符 |

### 8.3 审计 / 统计工具（只读）

| 路径 | 职责 |
|---|---|
| `scripts/audit_coverage.py` | 词典覆盖率审计 |
| `scripts/db_coverage_audit.py` | 数据库汉化覆盖审计（按缺口分类） |
| `scripts/db_prose_gaps.py` | 数据库描述散文缺口审计 |
| `scripts/db_prose_inventory.py` | 数据库描述散文存量盘点 |
| `scripts/db_text_inventory.py` | 数据库文本面盘点 |
| `scripts/db_find_text.py` | 定位某个英文串在 DB 的哪张表/哪一列 |
| `scripts/db_desc_work.py` | 抽取"还需要翻译"的描述行 |
| `scripts/triage.py` | 把字符串池分成翻译工作桶 |
| `scripts/brief_chunks.py` | 把简报工作清单切成均衡分片 |
| `scripts/make_chunks.py` | 把 `dict/worklist.json` 切成有序分片 |
| `scripts/batch_translate_chunks.py` | 并发批量翻译分片 |

### 8.4 实机驱动

| 路径 | 职责 |
|---|---|
| `scripts/live_harness.py` | **实机驱动核心**：启停游戏、发引擎命令、像素采样（`pixel_change_count`） |
| `scripts/live_load_tutorial.py` | 加载想定/教程并驱动入口对话框 |
| `scripts/live_sweep_tutorials.py` | 全教程实机巡检 |
| `scripts/verify_all_tutorials.py` | 教程综合验证harness |
| `scripts/test_subwindows.py` | 打开/截图/检查 7 个核心子窗体 |
| `scripts/capture_all_menus.py` | 抓取 10 个顶级菜单截图 |
| `scripts/test_installer_ui.py` | 安装器界面实机验证 |
| `scripts/test_updater_live.py` | 更新助手实机验证（`--check` 四条路径） |
| `scripts/perf_probe.py` | 卡顿探针（R49）：补扫频率 + 静止界面翻译事件 |
| `scripts/perf_probe_large.py` | **大想定**卡顿探针（R53）：渲染帧/补扫频率比值、僵尸 WPF 窗口、视觉树节点规模；`--load "Blue Dawn"` 可直接加载指定想定 |
| `scripts/live_load_blue_dawn.py` | 一键进入战争链战役第一关 Blue Dawn（战役关卡，不在想定列表，必须走 战役→系列战役→开始想定→选阵营 这条链） |
| `scripts/zombie_window_watch.py` | 观测「想定特性与拟真度选项」僵尸 WPF 窗口是否持续增长（区分真实泄漏与一次性残留） |

### 8.5 词典构建辅助

| 路径 | 职责 |
|---|---|
| `scripts/build_dict.py` | **核心构建**：`dict/src/*.json` → `dict/CMOLocalizer.json`（确定性、字节可复现） |
| `scripts/build_db_enums.py` | 生成 `db_enums.json`（扫所有 `Enum*` 表） |
| `scripts/build_db_enums_complete.py` | 国家/阵营官方规范译名对照 |
| `scripts/generate_db_platforms_facilities.py` | 生成四类平台/设施名 |
| `scripts/db_desc_merge.py` | 把译好的描述行合回 `dict/src/` |
| `scripts/clean_legacy_garbage.py` | 清洗 242 条早期"按词拼接"垃圾串 |
| `scripts/fix_sentences.py` | 修复被整句误译的简报碎片 |
| `scripts/briefing_html.py` | 保结构的 HTML 简报翻译 harness |
| `scripts/localize_html_files.py` | 翻译磁盘上的教程/战役 HTML |

### 8.6 地名 / 地图

| 路径 | 职责 |
|---|---|
| `scripts/wwp.py` | **World Wind Placename (.wwp) 读写库**（地图标签数据格式） |
| `scripts/apply_placenames.py` | 把译文写回游戏的 `.wwp` |
| `scripts/placename_translation.py` | 坐标感知的译名映射（原版/本地化一对） |
| `scripts/build_cities_placenames.py` | 离线构建译好的 Cities.wwp（优先用已评审坐标） |
| `scripts/test_global_placenames.py` | 全量地名审核的一致性测试（4,287+321 逐点唯一命中、译名一致） |
| `scripts/test_release_placenames.py` | 发布侧地名门禁测试（元数据指纹、flags/顺序/坐标篡改必须失败） |
| `scripts/test_static_html.py` | 静态 HTML 汉化断言（编码/完整译文/`.orig` 备份保留） |
| `scripts/test_scenario_html.ps1` | 想定 HTML 导出的 PowerShell 回归 |
| `scripts/test_web_translation.cjs` | WebView2 译文脚本的 Node 侧契约测试 |
| `scripts/test_web_translation_state.cjs` | 独立无头 Edge 验证 Web 配额续做、同节点取消、来源/nonce/编辑保护；不操作游戏 |
| `scripts/test_webview2.ps1` | 真实 Edge/WebView2 DOM 回归 |
| `scripts/test_ui.ps1` | UI 模式回归（控件/下拉/设置抽查），需要游戏在跑 |
| `scripts/scan_english_residue.py` | **英文残留扫描（R52）**：拿游戏数据本身（DB 各表 Name、枚举 Description、想定源）当语料喂给引擎 `TranslateString`，判据是「翻完还剩多少真英文词」。这是唯一能发现「没进词典的字符串」的机制——`L-ui-residuals` 只能查已入词典的条目 |
| `scripts/build_translation_batches.py` | 缺口翻译流水线（R52）：`--source db/corrupt` 生成批次清单 → 交 LLM 逐条精修 → `--merge` 按**原归属**写回并跑守卫（拒收句子碎片、结构性内容、长度异常） |
| `scripts/term_gap_analysis.py` | 术语缺口分析（R52）：逐字复现引擎匹配规则，报出每条缺口卡在哪个词上，算出「加哪个词解锁最多条目」。输出收益排行 + 可生成术语批次 |
| `scripts/merge_terms.py` | 术语合并（R52）：把 LLM 精修的术语写进 `dict/src/terms.json`。守卫拒收未翻译/半译/句子/过长；术语表是组合翻译的词汇层，一个坏词会污染上千条名字 |
| `scripts/merge_record_names.py` | 数据库装备名合并（R52）：写回 `db_record_names.json`。守卫拒收残留英文词、二选一写法、结构性内容；型号/编号/`Type 63A` 这类正确放行 |
| `scripts/merge_scenario_texts.py` | 想定文本合并（R52）：按批次清单的 `src` 写回**原归属** side/message 文件（全塞一个池会被字母序覆盖），DTG 时间码放行 |
| `scripts/gen_v3_batches.py` | 按 `build/residue_gaps.json` 的当前真实剩余缺口，按表族生成 v3 精修批次（每类一套专用 instructions：传感器北约绰号、挂载弹种、设施编制、飞机绰号、卫星代号、武器弹头、基座形式、推进装置、舰艇舰级）。批次写入 `build/v3_batches/`，供 teammate 逐条精修 |
| `scripts/verify_placenames.py` | 地名发布门禁 |
| `scripts/test_placenames.py` | 地名离线回归（写操作只落在 `build/` 临时目录） |
| `scripts/test_placename_cache.py` | 用合成 WWP/PNG 做 Windows 集成测试 |
| `scripts/test_verify_placenames.py` | 地名门禁自身的回归用例 |
| `scripts/refresh_placename_tiles.ps1` | 重建地名瓦片（PowerShell） |
| `scripts/test_refresh_placename_tiles.ps1` | 上面脚本的测试 |
| `scripts/test_map_label_renderer.ps1` | 地图标签渲染测试驱动 |

### 8.7 杂项

| 路径 | 职责 |
|---|---|
| `scripts/scenario_meta.py` | 抽取想定元数据（键已排序 + 优先正式想定路径，保证产物稳定） |
| `scripts/credentials.py` | **凭据读取唯一入口**（密钥绝不入库） |
| `scripts/cmocmd.sh` | 命令行快捷封装 |
| `scripts/migrate.sh` | 迁移脚本（历史） |
| `scripts/verify_launch.sh` | 启动验证（历史） |

---

## 9. 引擎诊断命令（实机验证靠它）

这些不是文件，而是**引擎内部命令通道**，通过 `scripts/live_harness.py` 的 `send()` 发送。
加新命令的落点：`src/engine/CMOLocalizer.cs` 的 `ProcessTestCommands()`。

| 命令 | 用途 |
|---|---|
| `XLATE <文本>` / `XLATE_BATCH <file>` | 只读翻译探针，验证某串会译成什么 |
| `WPF_DUMP` / `WPF_TEXTLOG` | 导出 WPF 控件树 / 文本变更日志 |
| `CTRL_TREE [过滤]` | 控件树（可按窗体名过滤） |
| `TRACE_START` / `TRACE_STOP` / `TRACE_DUMP` | 事件追踪（含 `renderTicks`、`elementHostScans` 计数） |
| `FLICKER_PROBE` / `FLICKER_RESULT` | 频闪检测（按树内位置路径做 key） |
| `DUMP_COMBOS` / `DUMP_LISTS` / `DUMP_MENUS` / `DUMP_OPEN_FORMS` | 下拉框/列表/菜单/窗体导出 |
| `CLICK_MENU` / `CLICK_BUTTON` / `CLICK_WPF` / `CLICK_TOOLBAR` / `SELECT_TAB` | 模拟点击（**`CLICK_BUTTON` 按窗体顺序匹配，跨窗体同名会打错**） |
| `SCEN_*`（`SCEN_CTRL_TREE` / `SCEN_SELECT` / `SCEN_LOAD` / `SCEN_EXPAND_ALL`） | 想定加载流程 |
| `POOL_DUMP` | 导出字符串池注入情况 |
| `RELOAD_DICT` | 重载词典（改词典后免重启） |

---

## 10. 资源文件

| 路径 | 职责 |
|---|---|
| `assets/placenames/Synoptic.wwp` | 译好的概览地名集（打包时落到 `WW/PlacenameSets/Synoptic/0000_0000.wwp`） |
| `assets/placenames/Cities.wwp` | 译好的城市地名集（同上，`Cities/`） |
| `assets/launcher/stringsChinese.resx` | 启动器的中文字符串表 |
| `assets/db_templates/*.html` | 数据库查看器 HTML 模板（Aircraft/Facility/GroundUnit/Platform/Satellite/Ship/Submarine/Weapon 8 个） |
| `assets/community/*.png` | README「社区交流」段引用的图片。当前为 `wechat_group.png`（微信群二维码）。**微信群二维码 7 天过期**：本张有效至 2026-10-27；续期只需用新图 **同名覆盖** 此文件（README 无需改），`.gitignore` 对 `assets/community/*.png` 开了例外才能入库 |

**运行时目录（不入库，`.gitignore`）**

| 目录 | 说明（不入库、不参与登记） |
|---|---|
| `build/` | 全部编译产物 + `regression_report.json` + 打包产物。**整目录 gitignored** |
| `evidence/` | 实机验证截图/证据。gitignored |
| `dict/worklist.json`、`dict/db_desc_work.json`、`dict/db_desc_chunks/`、`dict/db_desc_out/` | 中间态工作文件，gitignored |

---

## 11. 文档（`docs/`）

### 11.1 活文档（要维护，改代码可能连带要改）

| 路径 | 职责 |
|---|---|
| `docs/ROUTING.md` | **本文件**。新增文件必须登记 |
| `docs/BUG_REGISTRY.md` | 缺陷台账 R1–R50，**只增不改**。修 bug 必须登记 |
| `docs/CHANGELOG.md` | 发布说明；`publish_release.py` 取顶部段落作为 Release 正文 |
| `docs/RELEASE_MANIFEST.md` | 发版清单与规则（含"已发布资产只增不改"） |
| `docs/RELEASE_TEST_SOP.md` | 发版测试 SOP |
| `docs/project_memory.md` | 项目口径与历史决策（16 条，改口径要同步） |
| `docs/AUTO_UPDATE_DESIGN.md` | 更新助手设计 |
| `docs/GITHUB_AUTO_UPDATE_DESIGN.md` | GitHub 自动更新设计 |
| `docs/PLACENAME_LOCALIZATION.md` | 地名汉化方案 |
| `docs/LOCALIZATION_VALIDATION.md` | 汉化验证说明（各门禁与实机检查的边界、计时口径、已知限制）。PR#5 引入，是「哪些通过了、哪些没验」的权威说明 |
| `docs/PLACENAME_SOURCES.md` | 地名译名的资料来源说明（哪些是中文通行名、哪些来自当地名称、哪些人工整理） |
| `docs/PLACENAME_SYNOPTIC_REVIEW.md` | 321 个区域标签的专项审核记录 |
| `docs/PLACENAME_REVIEW_REPORT.json` | 地名审核报告（机读，供复核与回溯） |
| `docs/PLACENAME_GLOBAL_REVIEW_PLAN.md` | 全量核对 4,287+321 个点的执行方案（历史计划，已完成使命，保留备查） |
| `docs/PLACENAME_LABEL_RENDERING.md` | 地名标签渲染方案 |
| `docs/MENU_TERMINOLOGY.md` | 菜单术语对照 |
| `docs/MAP_TILE_SERVER_VALIDATION.md` | 地图瓦片服务验证 |
| `docs/TUTORIAL_REGRESSION_V413.md` | 教程回归记录 |
| `docs/design/updater_mockup.html` | 更新助手 UI 原型 |
| `docs/credentials.example.json` | 凭据配置示例（**不含真实密钥**） |

### 11.2 历史记录（只读参考，不再更新）

| 路径 | 说明 |
|---|---|
| `docs/CHECKPOINT_V2.md`、`docs/checkpoint_notes.md`、`docs/checkpoint_turn31.md`、`docs/SUBWINDOW_CHECKPOINT.md`、`docs/project_checkpoint_db1478.md` | 历次开发检查点快照 |
| `docs/DIAGNOSIS_AND_FIX.md`、`docs/CRITICAL_FIX_NOTE.md` | 历史故障诊断记录 |
| `docs/FULL_LIVE_TEST_REPORT.md`、`docs/TEST_REPORT.md`、`docs/LIVE_TEST_PLAN.md` | 历史测试报告/计划 |
| `docs/NEXT_V414_PENDING.md` | v4.1.4 待办（已过期） |
| `docs/DEEP_LOCALIZATION_PLAN.md`、`docs/TRANSLATION_BRIEF.md`、`docs/BRIEFING_BRIEF.md`、`docs/brief_remaining.json` | 深度汉化/简报翻译方案（历史） |
| `docs/project_optimization_record.md` | 优化记录 |
| `docs/menu_strings_dump.txt` | 菜单串导出（历史快照） |
| `docs/README_汉化说明.txt` | 面向用户的说明文本 |

---

## 12. 测试工程（`src/tests/`）

| 路径 | 职责 | 对应门禁 |
|---|---|---|
| `src/tests/MixedPrefixTests.cs` | R42「英文前缀 + 中文尾」混排修复测试 | `R42-static-mixed-prefix-wiring` |
| `src/tests/IdempotencyTests.cs` | R45 翻译幂等（括号层数收敛） | `R45-static-translate-idempotent` |
| `src/tests/MapTileServerTests.cs` | 地图瓦片服务 7 模式并发回归 | `R34-static-tile-*` |
| `src/tests/MapLabelRendererTests.cs` | 地图标签渲染 | `R33`/`R38`/`R39` |
| `src/tests/MapLabelSamples.cs` | 渲染测试用的样例数据 | — |
| `src/tests/CMOLocalizerTest.cs`、`src/tests/CMOLocalizerTestV2.cs` | 引擎早期冒烟测试 | — |
| `src/tests/TestV3.cs`、`src/tests/TestHijack.cs`、`src/tests/TestDarkReflection.cs` | 挂钩/反射验证（历史） | — |
| `src/tests/DummyHost.cs` | 测试用宿主桩 | — |
| `src/tests/BulkTranslationTests.cs` | 批量译文契约（34 条） | — |
| `src/tests/ComboBoxTranslationTests.cs` | 下拉项增量翻译：业务键/选中值/token 保持 | — |
| `src/tests/DarkTreeTranslationTests.cs` | 深色树分片遍历与集合归属核对 | — |
| `src/tests/UiIdleContinuationTests.cs` | 空闲续接器的模态/DoEvents 宿主行为 | — |
| `src/tests/UiLifecycleTests.cs` | UI 生命周期挂钩 | — |
| `src/tests/UiSchedulingTests.cs` | UI 调度顺序（不吞掉新投递的回调） | — |
| `src/tests/UiPerfMetricsTests.cs` | UI 执行计量负向验证：排队不算执行、慢回调超帧、取消不完成、忙时拒绝重置 | `R55-static-ui-perf` |
| `src/tests/WpfSchedulingTests.cs` | WPF 分片与归属回归：稳定同级游标续扫、跨 Dispatcher 合并、换根取消、输入让出 | `R54-static-wpf-scheduling` |
| `src/tests/WpfSafetyTests.cs` | WPF 写回安全（不重入属性系统、编辑区不写） | — |
| `src/tests/FirstPaintTests.cs` | 首帧中文（不等下一轮慢扫） | — |
| `src/tests/UpstreamCompatibilityTests.cs` | 与上游 master 修复的兼容性 | — |
| `src/tests/WebViewTranslationHostTests.cs` | 注入宿主：来源/nonce/容量/回退预算 | — |
| `src/tests/WebView2LatencyTests.cs` | WebView2 中文可见延迟 | — |
| `src/tests/ScenarioHtmlInstallerTests.cs` | 想定 HTML 的安装/绿色包覆盖语义 | — |
| `src/tests/DBTranslationTests.cs`、`src/tests/DBLiveProbeTests.py` | 数据库翻译与实机探针 | — |
| `src/tests/PanelFlickerProbeTests.py` | 面板中英翻转探针的自测 | `L-panel-flicker` |
| `src/tests/PerformanceProbeTests.py` | 性能探针负向自测 + 分层报告：超时、零工作、未推进、错想定、漏健康、旧绿残留必须失败 | `R56-static-perf-probe` |
| `src/tests/ReleaseContractTests.py` | 隔离 unittest/mock 验证报告、四资产、冻结构建及绿色包安装器契约；不读凭据/写网络/操作游戏 | `R57-static-release-contract` |
| `src/tests/PoolDisplayInjectionTests.py` | 池级注入白名单回归 | `R38-static-pool-inject` |
| `src/tests/DictionarySourceTests.py` | 词库完整字节重建、产物独立性、源删除不回生及污染注入负向测试；只写临时目录 | `R58-static-dictionary-source` |
| `src/tests/README_WebViewTranslation.md` | 上面 WebView 系列测试的说明 | — |
| `src/tests/search_methods.cs`、`src/tests/test_db_load.cs` | 方法搜索 / DB 加载试验（历史） | — |
| `src/tests/README_MapTileServer.md` | 瓦片服务测试说明 | — |
| `src/tests/ResidueScan.cs` | R52 扫描器的 C# 宿主：加载引擎 DLL、反射调 `TranslateString`，把 TSV 语料逐条翻译并标注 `ok/none/partial` |

> **注意**：`MixedPrefixTests` / `IdempotencyTests` 接受第 2 个 CLI 参数为引擎 DLL 路径，
> 目的是防止读到过期的 `build/CMOLocalizer.dll` 造成假绿。

---

## 13. 遗留与一次性文件

### 13.1 `src/tools/`（82 个，全部是逆向探测工具）

**只有一个还在用**：`src/tools/GuiDriver.cs`（编成 `build/GuiDriver.exe`，
提供 list/children/postchar/capture/focus，实机测试驱动依赖它）。

其余 81 个是**一次性逆向探测脚本**，按前缀分族，需要时可参考其手法，但**不要以为它们还在链路里**：

| 路径 | 族 | 用途（历史） |
|---|---|---|
| `src/tools/GuiDriver.cs` | — | **在役**：GUI 自动化驱动 |
| `src/tools/*.cs` | 全部 | 兜底覆盖（上面已逐族说明；新增工具请加具体行） |
| `src/tools/Inspect*.cs` | Inspect 族 | 反射探查游戏 DLL/窗体/方法 |
| `src/tools/inspect_*.cs` | inspect 族 | 同上，后期批量版本（更细粒度） |
| `src/tools/Probe*.cs`、`src/tools/probe_*.cs` | Probe 族 | 针对具体控件/IL 的定点探测 |
| `src/tools/dump_*.cs` | dump 族 | 导出 XML/IL/加密简报/沙箱/刷新逻辑 |
| `src/tools/scan_*.cs` | scan 族 | 扫描 core/DAL/UI 对话框 |
| `src/tools/find_*.cs`、`src/tools/Find*.cs` | find 族 | 查找调用者/方法/类型/字符串 |
| `src/tools/check_core.cs`、`src/tools/ILScanner.cs`、`src/tools/Disasm.cs`、`src/tools/DumpIL.cs`、`src/tools/DumpTokens.cs`、`src/tools/WinProbe.cs`、`src/tools/build_chinese_tiles.cs` | 其他 | 反汇编/令牌导出/窗口探测/中文瓦片构建 |

### 13.2 一次性脚本

| 路径 | 说明 |
|---|---|
| `scripts/archive/*` | 归档的历史脚本（`translate_shard2.py` + 说明） |
| `scripts/bisect_offsets.py` | 二分定位导致启动失败的偏移条目（排障时可能还用得上） |
| `scripts/patch_engine_escapes.py` | 一次性源码补丁器（给引擎 JSON 扫描器补转义），**已完成使命** |
| `scripts/translate_shard1_a.py`、`scripts/run_translate.py`、`scripts/deep_translator_engine.py`、`scripts/deep_translator_engine_v2.py`、`scripts/generate_deep_localization.py` | 早期批量机翻流水线（已废弃，机翻质量不合格） |
| `scripts/find_nav_calls.py` | 早期探索脚本 |
| `scripts/verify_tutorials_v413.py` | v4.1.3 专用静态回归（版本已过） |

---

## 14. 常见任务 → 该改哪里

| 我要做的事 | 按顺序看这些 |
|---|---|
| 加一条界面翻译 | `dict/src/` 里对应的 json → `python scripts/build_dict.py` → `bash scripts/build.sh` → `bash scripts/deploy.sh` → `verify_regressions --static` |
| 修引擎翻译逻辑 | `src/engine/CMOLocalizer.cs`（先看 §4 内部地图）→ 加/改用例到 `src/tests/` → `verify_regressions.py` 里加 static 项 → build/deploy → 跑门禁 |
| 修"中英混排/半译" | `compose_terms.py`（规则镜像）+ `dict/src/terms.json`（词汇）+ 引擎 `TryComposeTranslate` 族，**三处要同步** |
| 修"面板频闪/越翻越长" | 引擎 `AllowWrite` 令牌桶 + `SetTextKeepBinding` 幂等短路；用 `FLICKER_PROBE`/`WPF_TEXTLOG` 取证 |
| 修"卡顿" | 先跑 `scripts/perf_probe.py`（看补扫频率与翻译事件）；大想定/多单位必须跑 `scripts/perf_probe_large.py`（渲染帧与补扫是否 1:1、有无僵尸 WPF 窗口）；改 `StartWpfRepaintTick`/`_repaintPeriod`/`TranslateWpfWindows`（R53 起全树补扫有 4ms 预算与游标续扫） |
| 加一个门禁用例 | `scripts/verify_regressions.py`（static/live 各自列表）；live 用例写在 `--live` 段 |
| 改安装器 | `src/installer/CMO_Chinese_Setup.cs` → `build.sh` → `verify_regressions --static --installer` |
| 改更新助手 | `src/updater/CMO_Updater.cs` + `docs/AUTO_UPDATE_DESIGN.md` → `test_updater_live.py` |
| 改地名译名 | `dict/placenames/cities.json` / `synoptic.json`（审核源）+ `scripts/placename_translation.py` → `apply_placenames.py` / `build_cities_placenames.py` → `verify_placenames.py` |
| 改数据库描述 | `dict/src/db_descriptions.json`（或走 `db_desc_work.py` 抽取 → 译 → `db_desc_merge.py` 合回） |
| 游戏更新适配 | `GAME_BUILD` → 结构探测 → 偏移复核 → `verify_all.gate_version` + `L-build-adaptation` |
| 发版 | `docs/RELEASE_MANIFEST.md` → `docs/RELEASE_TEST_SOP.md`（**先拿用户授权**） |
| 加新脚本/新源文件 | **必须在本表登记**，否则 `verify_routing` 报红 |

---

## 15. 新增文件的规矩

1. **写在能一眼看出归属的目录**：引擎改动进 `src/engine/`，源翻译进 `dict/src/`，
   门禁进 `scripts/`，测试进 `src/tests/`。不要往根目录丢。
2. **在本表对应小节加一行**：`| \`路径\` | 一句话职责（干什么、谁调用、什么时候该改） |`。
   - 路径用反引号包裹，否则门禁解析不到。
   - 只需登记**路径模式**，一批同类文件可用 `dir/*` 或 `前缀*`。
3. **一次性脚本必须归到 §13**，并写明"已废弃/已完成使命"，否则后人不敢删。
4. **跑 `python scripts/verify_routing.py` 确认绿灯**。

> 门禁的匹配规则：表格首列反引号内的内容，`*` 匹配任意字符（**含 `/`**），
> `?` 匹配单字符，其余字面量。所以 `src/tools/*.cs` 能覆盖整个目录。
