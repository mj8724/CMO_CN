# 增量本地化：行为契约与验证方法

本说明用于审查基于上游 `9d441f8` 的净功能改动。文本 LF 归一化单列提交；本机计划、实验报告、部署与回滚记录不属于公开改动。上游 R38/R39、注入安全过滤和 8,735 条 offsets 数据须在集成时保留。

**基础集成验证（2026-10-18）：公开集成候选已完成下列独立回归；未启动或部署游戏。** 初次集成候选引擎 SHA-256 为 `c1ad99aa5a5ea29c2d3f68ecfe1573b3110d85a69a761700a6458d3f4a77796c`，归一化后的上游词库为 `7fe4770eed6270cd698d9ec2e0a53b475c7fba2cebcbaaa8b3ecf1e781fedcc6`。后续自动审查修复的专项回归列于下文；该初次哈希不代表追加修复后的二进制。二进制不入库，重新编译的 PE 时间戳等可能改变哈希。

| 验证 | 本轮结果 |
| --- | --- |
| 首轮绘制 / ComboBox / DarkTree | 8 / 14 / 25 模式通过；ComboBox 非计时模式 84 条断言 |
| UI 队列 / Idle / 窗口生命周期 | 7 / 5 / 9 模式通过 |
| WPF 安全 / 上游 R39 真实控件兼容 | 5 模式 89 条断言 / 10 条断言通过 |
| 批量文本、Tab、ComboBox 契约 | 3 模式通过，`contractFailures` 均为空 |
| WebView2 反射适配器 / 真实 Edge DOM | 47 / 19 检查通过，DOM 零页面异常 |
| 静态 HTML / 词库质量、引擎解析、术语 | 16 测试 / 3 项检查通过；8,735 offsets 与 57,051 exact 全量可解析 |
| 真实安装器 HTML 部署与回退 | 122 条断言通过；使用合成 ZIP，不执行安装入口 |
| 上游静态回归门禁 | 17/21 通过；R34 地图服务另含 7/7 真运行模式 |

DarkTree 首轮 24/25 通过，唯一失败是测试反射了未公开的诊断探针；移除该探针断言后单项重跑通过，业务回调顺序、128 次回调、64 次布局和 4,096 次测量断言均保留。静态回归的 4 个失败为上游未跟踪的发布输入：R5 缺 13 张按钮图片；R18 缺 `build/CMOSharedTest.exe`；R38 缺 `build/gen_pool_display_inject.py`；R39 缺 `build/test_db_translation.cs`。对应文件并非本次删除。R39 另由已入库的真实控件兼容宿主验证；缺失原宿主仍如实报失败，不替换或跳过原断言。静态测试使用不存在的游戏目录，R0/R2 的部署比较因此跳过，不能称为部署验证。

本轮确认并回归的副作用包括：树集合回调清空重填/移除重加/添加后移除造成订阅失配；WPF Header 与燃油文本破坏绑定、动态进入编辑区后仍被回写、依赖属性监听器在未加载或 Dispatcher 退出后保留目标；ComboBox 对象项和已有 Format 映射 token 被字符串替换，以及 Format 同栈换源后旧计划覆盖新项。上游 R39 兼容测试还覆盖混合中英文条目被增量计划提前跳过的问题。

以上是行为回归结果，不是所有机器上的性能保证。没有将此前开发二进制的计时当作本轮候选成绩，也未做本轮真实游戏的新旧性能对照或完整安装包验证。

## 自动审查追加回归

Codex bot 的 [P1 编码兼容意见](https://github.com/mj8724/CMO_CN/pull/2#discussion_r4309995341)经复现成立：`windows-1252` 载荷中的中文用十进制或十六进制数字实体存储，安装器只检查字面汉字，会用标题级回退覆盖完整译文。初次修复先解码实体再判断，旧代码 33 条断言中 6 条失败，修复后 34 条通过。

后续的 [P1 旧备份意见](https://github.com/mj8724/CMO_CN/pull/2#discussion_r4310071584)与 [P2 声明编码意见](https://github.com/mj8724/CMO_CN/pull/2#discussion_r4310071591)也经反例确认，并补齐下列行为：

- 导出器读取明确选择的 `GAME_DIR` 下当前 HTML，不再自动优先读取无版本信息的 `.orig`；包内源备份和清单 `source_sha256` 来自同一当前文件。旧备份与已更新英文、已更新中文两个反例先失败，修复后静态 HTML 16 项测试全通过。
- 安装器按受支持的声明编码读取当前 HTML，BOM 优先，并在实体解码后检查中文。GBK、Big5 原生汉字及数字实体译文保留原始载荷字节。未打包或仍为英文的文件也以当前正文执行标题级回退，不能从旧 `.orig` 恢复旧版本；已有备份保持不变。
- 标题回退按检测出的编码写回，仅将新增替换文本中不能表示的字符转为数字实体，包含全角标点；不转义整份文档，不改脚本或属性。原编码声明与 BOM、可表示的正文及非 ASCII 标点须保持一致。

`test_scenario_html.ps1` 编译完整安装器为测试 DLL，仅嵌入合成 HTML ZIP，并反射调用真实 `DeployScenarioHtml` / `LocalizeScenarioBriefings` 方法；不运行安装入口、窗体、注册表操作或游戏。追加回归在前版产生 13 条失败，最终 122/122 通过，覆盖旧备份/当前正文分歧、无打包回退、cp1252/GBK/Big5、BOM 与声明、不可表示的新增字符、脚本及重复执行。这不是完整发行安装器验证，前述缺失发布输入的限制仍然存在。

后续 [P2 WebView2 句柄重建意见](https://github.com/mj8724/CMO_CN/pull/2#discussion_r4310223309)也经复现成立：脚本注册完成时没有句柄，或完成回调已排队后旧句柄销毁，均可能丢失当前文档补译或失败回退。修复保留已完成任务，由句柄创建和同 Core 再发现恢复，在 UI 线程按任务、Core 及控件当前实际 Core 身份消费一次；Core 变为空时也停止旧回退。原 DLL 的无句柄成功/失败、已排队成功/失败、替换 Core、空 Core 六个专项模式失败；修复后适配器由 19 增至 47 条检查全部通过，真实 Edge DOM 的 19 项回归仍通过。此处用真实隐藏 WinForms 句柄和合成 Core 验证调度与身份边界，不等价于游戏或真实 WebView2 SDK 的新增实机验证。

## 问题与改后行为

窗口打开、组合框重填或网页导航时，原有周期扫描可能使英文等到下一轮才更新；批量重译又可能反复触发业务事件、布局和绘制。本次改动在生命周期与内容事件处登记增量工作，合并重复请求，并保留周期入口用于无通知变化的对账。

| 模块 | 预期行为与必须验证的边界 |
| --- | --- |
| ComboBox | 先形成实际写入计划，无命中或已为中文时不调用 `BeginUpdate`/`EndUpdate`。回写前检查源对象身份、原文、索引及当前真实绑定表；业务回调换表、重填或追加后不能用旧计划覆盖新数据。非字符串对象、已有 Format 映射的业务 token 与 `ValueMember` 不作为显示译文替换；这类显示文字可能保留英文，以保护业务值。仅抑制自身预期的原生删除/插入消息，保留业务事件。 |
| ComboBox 重试 | 每控件一次初始翻译最多追加两轮自触发重试，耗尽后停止自排队，并用 250ms 冷却约束周期同步入口。真实外部修改、绑定变化或用户提交选择可开启新链；重复发现不能补充未结束链的预算。输入焦点、展开期间及数据库 token 保护仍生效。持续业务重填时允许保留原文，优先避免 UI 异步自旋。 |
| DarkTree | 追加订阅，不替换或代理原有业务监听器。集合事件只标脏，之后按集合最新状态对账，覆盖清空重填、移动及嵌套业务回调。节点原文更新、移除、集合替换和控件销毁须淘汰旧任务。可见节点优先；无变化的绘制或鼠标事件不扫描整树。 |
| WinForms 队列 | 合并相同控件请求，可见工作优先，并为后台工作保留进度。首轮调度后通过 `Application.Idle` 续批，同时提供 WinForms Timer 回退。单批约 4ms 是检查点之间的软预算，不能抢占一个慢业务回调、冷初始化或树布局；上下层预算可能叠加。取消、销毁、句柄重建及重入调度后，旧 Idle 快照不能执行新任务。 |
| WPF | 在所属 Dispatcher 上登记生命周期与绘制前目标，使用弱引用，不在每帧重新遍历全部窗口。动态 `Loaded`、Run/Span 和 Header 的更新须可继续翻译；依赖属性绑定必须保留，可编辑容器及其嵌套显示元素不得回写；只读容器允许翻译。窗口关闭、目标失活和 Dispatcher 退出后应停止持有相应工作；这些契约由安全专项验证。 |
| WebView2 | Core 就绪后注册文档创建脚本，并补译当前文档；Observer 仅登记新增或改写文本。禁用 WebMessage、缺少消息/注册 API 或注册失败时，使用 `ExecuteScriptAsync` 取得同一增量队列并回写，不修改游戏 Settings。分页、队列上限、超时、坏 UTF-16、导航 nonce、节点原文和来源校验必须继续有效。 |
| 静态 HTML | 构建前从明确选择的只读游戏目录当前 HTML 导出显示文本，保留原始 markup、占位符、链接及脚本/可编辑区。完整构建必须有与当前词库及载荷哈希一致的清单；缺源、空清单、过期或损坏载荷失败。`partial` 可以构建，但必须披露未译数量与范围，不能称为全部中文。 |

## 离线构建与独立回归

在仓库根目录运行，要求 Windows、.NET Framework 4.8；Python 测试要求 Python 3.10+。以下 PowerShell 5 入口不会执行安装器或启动游戏。测试应使用独立桌面时段，避免与其他 UI 计时同时运行。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_first_paint.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_combobox.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_dark_tree.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_ui_scheduling.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_ui_idle.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_wpf_safety.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_upstream_compatibility.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_scenario_html.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_bulk_translation.ps1 -Mode contracts
python scripts/test_static_html.py
```

默认引擎为 `build/CMOLocalizer.dll`；上述 PowerShell 测试可通过 `-Engine`、`-OutputDir` 改用独立候选及日志目录，带词库的脚本另有 `-Dictionary`。`build.ps1 -CheckOnly` 仅检查输入，不等价于编译通过。引擎构建应包含全部 partial `.cs`，并嵌入逻辑资源 `CMOLocalizer.WebViewTranslation.js`。

窗口生命周期补充用例的现有独立宿主接口如下，要求在 PowerShell 中执行；它不加载游戏程序集：

```powershell
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$framework = Split-Path -Parent $compiler
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
    (Join-Path $framework 'WPF/WindowsBase.dll'),
    (Join-Path $framework 'WPF/PresentationCore.dll'),
    (Join-Path $framework 'WPF/PresentationFramework.dll'),
    (Join-Path $framework 'System.Xaml.dll'))
$hostExe = Join-Path $PWD 'build/UiLifecycleTests.exe'
$compilerArgs = @('/nologo', '/target:exe', ('/out:' + $hostExe), (Join-Path $PWD 'src/tests/UiLifecycleTests.cs'))
$compilerArgs += @($references | ForEach-Object { '/reference:' + $_ })
& $compiler @compilerArgs
if ($LASTEXITCODE) { throw 'Lifecycle host compilation failed' }
foreach ($mode in @('control', 'window', 'existing', 'inputs', 'secondary', 'wpf', 'dispose')) {
    & $hostExe (Join-Path $PWD 'build/CMOLocalizer.dll') (Join-Path $PWD 'dict/CMOLocalizer.json') $mode
    if ($LASTEXITCODE) { throw ('Lifecycle failure: ' + $mode) }
}
```

网页适配器与真实 DOM 需分别验证，不能用消息 mock 的结果替代真实 WebView2：

```powershell
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$hostExe = Join-Path $PWD 'build/WebViewTranslationHostTests.exe'
& $compiler /nologo /target:exe /reference:System.Windows.Forms.dll ('/out:' + $hostExe) (Join-Path $PWD 'src/tests/WebViewTranslationHostTests.cs')
if ($LASTEXITCODE) { throw 'Web host compilation failed' }
& $hostExe (Join-Path $PWD 'build/CMOLocalizer.dll')
if ($LASTEXITCODE) { throw 'Web adapter tests failed' }
node scripts/test_web_translation.cjs
```

真实 DOM 测试另需 Node.js、可解析的 `playwright` 包与 Microsoft Edge，使用无头浏览器和独立 HTTP fixture。若需真实 WebView2 宿主，在已安装匹配 Runtime 的测试环境运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_webview2.ps1 -GameDir 'C:/Games/Command - Modern Operations' -EnginePath build/CMOLocalizer.dll -Interval 1500 -Samples 8
```

`-GameDir` 替换为合法测试安装目录。该宿主只读取 SDK/Loader 文件，profile、复制的 SDK 与日志留在被忽略的独立构建目录，不写入游戏。比较旧/新引擎时需使用同一词库、样本、Runtime、DPI 和间隔，并显式指定不同 `-OutputDir`。统计初始化、导航、动态文本以及输入/链接保护；不应仅报告最快一轮。

## 完整静态载荷与发布门禁

仅构建引擎不需要游戏或完整发布图片资源；Git Bash 中等价入口为 `bash scripts/build.sh --engine-only`。完整安装器/绿色包另需带 `cygpath` 的 Windows Git Bash 和完整发布资产：

```bash
export GAME_DIR='C:/Games/Command - Modern Operations'
export PY=python
"$PY" scripts/build_db_enums.py
"$PY" scripts/build_dict.py
"$PY" scripts/localize_html_files.py --game-dir "$GAME_DIR" --output-dir build
"$PY" scripts/localize_html_files.py --output-dir build --verify
bash scripts/build.sh
```

上述构建不部署。`GAME_DIR` 当前文件是构建源；若输入已经汉化，现有中文会保留。需要从英文完整重译时，应提供对应游戏构建的干净英文源，不能自动信任遗留 `.orig` 的版本。完整发版还须按 [发版前测试 SOP](RELEASE_TEST_SOP.md) 在测试安装中完成环境、代码、安装器和实机回归，再运行 `bash scripts/package.sh` 与完整入库检查。打包会重新导出静态载荷并执行硬门禁；缺少发布资产时失败应如实记录，不能用引擎构建或独立 fixture 代替完整 Release 结论。生成的商业 HTML、原文备份、SDK、profile 和二进制不提交到仓库。

## 性能与兼容边界

应分别观察单项/小批量、大批量、未命中、已为中文、绑定刷新和业务回调重填，记录实际写入数、布局/事件数、首个可见中文与全部完成时间。可见优先可能延后隐藏项；按批让出线程不代表总完成时间必然缩短。控件消息分流、事件订阅、弱目标列表及有待办时的队列管理都有固定成本，弱 CPU、低内存、高 DPI 或复杂业务事件需单独测量。

WebView2 消息退化路径每 250ms 检查一次，即使暂时没有文本也可能有 IPC；恢复消息后仍可保留这条回退路径。缺少文档创建 API 时只能在导航后补装脚本；复杂段落仍依赖异步原生翻译。仅支持顶层文档，外站与可编辑内容不翻译，超限或未命中文本保留原文，不保证每个首帧都为中文。

本 PR 不发布官方 Release、不修改游戏主程序，也不承诺所有机器和窗口都更快。公开候选仍须在匹配游戏构建上确认上游 R38/R39、偏移注入安全和输入/选择行为，再决定是否发布。
