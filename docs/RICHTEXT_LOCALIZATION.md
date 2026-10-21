# 帮助正文补译与核查（R53）

本轮范围是快捷键正文、同类界面帮助和静态提示。基线为上游提交
`7b129a51c0e12313038bc76f9caf43191262acc0`；只读提取本机 `Command.exe`
资源，文件 SHA-256 为
`dfae2a76a5a2c44c750b31240ea6da36c1ec412454107f36d62b5f7229670da0`。
文件版本 `1.0.0.0` 不能证明游戏内部构建号。

## 核查结果

| 类别 | 已核查内容 | 处理 |
|---|---|---|
| 快捷键 | 9 个文本块、89 行说明；基线缺少独立精确译文 44 条 | 补齐独立说明、带按键的原始行和完整段落；保留按键、Tab、换行及边缘空白 |
| 资源帮助 | 13 段：调试报告说明、相似对象复制提示、许可链接、任务规划器规则、任务阶段提示、迁移说明、图标自定义说明 | 补齐整段和逐行译文，保留图标语法示例 |
| 字符串池提示 | 7 段：飞行计划警告、不支持数据库重建提示、迁移限制、自动保存提示、声音署名 | 补齐完整显示文本，保留 URL、文件名和命令标记 |

快捷键按区块为：功能键 16 行、战术地图 25 行、基础操作 5 行、己方单位
18 行、接触目标 6 行、编辑器 12 行、技巧 3 行、Lua 3 行、高级任务规划器
1 行。全部译文统一维护于 `dict/src/richtext_help.json`，共 246 项（包括
整段、布局行和独立说明，不能将此数理解为 246 个独立界面缺口）。

实际资源提供 5 个快捷键文本块，另外 4 个由仓库字符串池快照补足；后者的
运行时可见性尚未实机确认。扫描覆盖 212 个资源集合、提取 47 个字符串值，
并记录无法按字符串解读的外部范围资源。`RICHTEXT_INVENTORY.json` 使用
原文哈希和资源键记录来源；工具遇到已审阅资源的新正文、原文漂移或无法
解析时会报告失败，避免把“未读到”计为“没有缺口”。

## 引擎修复

原先富文本帮助被输入保护规则跳过，单纯增加词条不能让正文显示中文。
现在翻译未聚焦的只读文本；对于游戏默认未设只读的快捷键帮助，仅允许
`Hotkeys` 窗体中明确列出的 9 个控件。其他固定帮助还以窗体完整类型名、
控件名及已审阅英文原文指纹识别：LuaEditorOperationPlanner.RichTextBox1、
DebugTool.TB_DetailedSensorReport、CopyOverSimilarityPrompt.TB_InfoUseSelected
及 IconeCustomizer 的 TB_Synthax / TB_Structure / TB_Examples。修改过的正文
不能通过原文校验；相邻脚本编辑区和聚焦控件仍受保护。

富文本按单元格倒序替换，快捷键首列不送入词典，保留字体、颜色、基线、
Tab 布局、选区、滚动位置和 Modified 状态。包含混合字体、颜色或基线的
单元格保留原文，避免展平格式；中文字体回退仍由 Windows 字体系统决定。
普通只读 TextBox 优先进行整段匹配，随后使用已有的分段翻译路径。
任务阶段悬浮提示使用实际 WinForms ToolTip 组件翻译，不改下拉项或业务键。
游戏回调若在翻译中替换正文，立即停止旧偏移写入，保留新正文及其状态。

`RICH_TEXT_DUMP Hotkeys` 导出完整帮助正文及控件状态，不受 `CTRL_TREE`
每条 120 字符截断限制。默认只导出只读或明确允许的帮助控件。
实机回归已改为逐控件核对全部说明，不再将快捷键正文计为跳过项。

## 可重复验证

```powershell
python scripts/build_dict.py
python scripts/audit_richtext.py
python scripts/test_richtext_audit.py
./scripts/build.ps1
./scripts/test_ui.ps1 -Suite richtext
./scripts/test_ui.ps1 -Suite help-tooltip
python scripts/verify_regressions.py --static
```

`test_ui.ps1` 的 corpus 模式自动生成本次语料，支持 `-Python`、`-Dictionary`
和 `-OutputDir`。`R53-static-richtext` 使用本次源码编译的引擎，运行真实
WinForms 控件测试及语料审计，不依赖游戏安装。

再次提取游戏资源（不会启动游戏窗体）时，先独立编译工具：

```powershell
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $compiler /nologo /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /out:build\ExtractRichTextResources.exe (Join-Path $PWD 'src\tools\ExtractRichTextResources.cs')
./build/ExtractRichTextResources.exe --assembly '游戏目录\Command.exe' --output build/all-resource-report.json --resource-filter '' --decoder-type 'EJsBjqD3uVy8IwCD9w.G7JZLAxiZym8sY748W' --decoder-method eBxqprrF8
python scripts/audit_richtext.py --resources build/all-resource-report.json
```

默认提取采用只读反射；混淆版本须显式指定已经检查过签名的资源解码方法。
工具只接受静态 `string[](Assembly)` 方法，不猜测或调用游戏入口。
不同游戏版本应先审查解码入口和新增资源，再更新来源清单。

上述验证证明所列语料的译文和隔离控件行为；尚未启动游戏逐窗体验收，
也不表示整个游戏所有版本均已无英文。原文资源导出、测试二进制和报告
留在忽略的 `build/`，不随源代码提交或作为游戏资源重新分发。

## 本轮验证记录

- 帮助来源与逐行审计：29 段，缺译 0，未审阅 UI 帮助 0；另记录 8 项范围外资源。
- 审计器反例测试：14 项通过；连续重建词典字节一致。
- 相关 UI 回归：21 个模式全部通过；其中富文本 all 255 项、实际快捷键 corpus 314 项、悬浮提示 13 项检查通过。
- 独立复核确认：混合样式不展平、同步回调替换正文后不再使用旧偏移、不导出被改写的编辑正文。
- 静态回归：R53 通过，总计 24/27；R5 因仓库缺少原有 13 张按钮 PNG，R18/R16 因测试专用注册表键访问被沙箱拒绝而失败。相关资产期望和共享状态代码均未在本轮修改。

## 部分资源报告的筛选语义

提取器的 `--resource-filter DebugTool` 等部分报告只要求所选资源实际存在。
范围外的快捷键和帮助正文使用已审阅快照，输出标为 `reviewed-source`；
字符串池正文仍单独标为 `string-pool-snapshot`。范围内缺失、原文漂移、
未解析或出现新的 UI 帮助仍使审计失败。空过滤器代表全部资源，未指定时
沿用 Hotkeys。审计器反例测试现为 17 项（已验证新增用例先红后绿）。

## 上游同步与复审（2026-10-21）

本次合并的上游 master 为 `aa795c45d4fb7295f180790e7c2a037d6bfdba60`。
生成词典冲突以最新上游产物为起点，再从合并后的源文件运行 `build_dict.py`，
未使用旧版整份词典覆盖。上游全部 62,753 个 exact 键和 8,756 个 offsets 键
均保留，数据库源文件及合并/质量检查脚本与该上游版本一致。仅 6 个 exact
与 3 个 offsets 的已有值采用已审阅帮助源的最终裁定；246 条帮助源译文
全部匹配。最终词典 exact 为 62,939，连续两次构建字节一致。

复审结果：审计器 17 项、独立反例矩阵 18 项、相关 UI 的 21 个模式均通过。
真实提取数据中的 DebugTool 部分报告可通过审计，删除所选正文后正确失败；
范围外的已审阅快照不会掩盖范围内缺失。完整资源报告仍可通过。

静态依赖使用官方 v4.2.2 绿色包中的 28 张中文按钮 PNG（覆盖 R5 所需 13 张）
及官方 `CMO_Chinese_Setup_v4.2.2.exe`。安装包仅供 R18 读取版本信息，未执行。
文件经发布摘要和逐文件哈希核对，分别存放于忽略的 `assets/symbols_menu/`
与 `build/`；干净检出仍需补齐这些依赖并编译 `CMOSharedTest.exe`。
共享状态单测需要访问独立的 `HKCU\Software\CMOChinese__selftest` 测试键，
测试后清理，不使用真实设置键。

补齐上述真实依赖后，完整 `verify_regressions.py --static` 为 27/27 通过，
其中 R18 的共享状态自测为 33 项 PASS。路由、发布质量与术语检查均通过。
本次复审未 push、打 tag、修改版本、发布或重新部署游戏。

## 异常清理修复（R54，2026-10-21）

上游 R27 与更新日志已明确说明：快捷键正文因 DarkRichTextBox 的 Tab
排版风险及 R19 输入回写风险而有意暂缓翻译。本次是在这些保护边界下补充
帮助翻译；没有查到作者逐项说明其他固定帮助也被有意跳过。

异常诊断发现：显式使用 WinForms `ThrowException` 时，恢复选区回调抛异常
会跳过重绘恢复、线程级 TextChanged 保护和 mutation depth 的清理，后续
控件的事件翻译因而停止；`Invalidated` 回调抛异常也会遗留保护状态。
尚无证据表明当前 Steam 游戏启用了该异常模式，不能视为游戏必现问题。

修复将取得 mutation 配额后的控件访问全部纳入外层 `try/finally`，独立
恢复进入前的 hook 标志与深度。停绘、正文替换及选区恢复另由绘制清理
保护；仅向仍存活的原 HWND 恢复重绘。正常翻译算法、输入保护和帮助状态
登记条件不变；外部回调异常继续传播，不以吞掉异常代替清理。

新增 6 个真实 WinForms 回归场景：选区恢复、正文写入、Invalidated 回调
抛异常，各覆盖最外层和已有嵌套保护。检查原异常身份、原有保护状态、
原生 WM_SETREDRAW 成对恢复及后续 Label 的 TextChanged 翻译。旧引擎的
选区恢复和 Invalidated 两组共 4 场景失败，正文写入两场景原已通过；
修复后 6 场景全部通过，已纳入 `richtext/all`（现为 306 项检查）。

独立输入保护宿主在真实完整词典下确认 `m → 米` 映射存在，再验证普通
TextBox、可编辑 RichTextBox 及非 TextBoxBase 的 DarkTextBox / EditBox，
覆盖聚焦/失焦、扫树/100 轮 TextChanged、模拟游戏回调先挂/后挂：32 场景、
3443 项断言通过。输入始终为 `m`，翻成“米”和游戏回写次数均为 0；
旧错误模拟负对照成功检出反馈递归。它不替代实际游戏任务名框验收。

此前 Steam 实测中，搜索框聚焦与失焦各观察超过 53 秒，`m` 保持不变；
快捷键 9 个文本块的 89 行说明均显示中文，按键列、Tab 与换行保持一致。
其中 88 行与目标译文逐字匹配，实时模式一行的 `1: 1` 存在额外空格；
没有对原始游戏 RTF 做逐字节前后比较。这些实测使用此前部署版本，
不代表本次异常清理修复已经在 Steam 内重新验收；该修复仅在隔离宿主验证。

本次修复重跑完整静态回归为 27/27，通过当前源码重新编译的 R53 控件
回归；相关 UI 21/21 模式、快捷键语料 314 项、提示 13 项、审计器 17 项
及路由、词典质量和术语检查均通过。词典文件本轮未修改。

上传前另合并上游 `0ac6845f3a9cd528a51f490716d4c7b4f1a70feb` 的数据库
译文更新，并从源重建词典。全部上游 63,969 个 exact 键和 8,756 个 offsets
键均保留，最终 exact 为 64,155；帮助源 246 项全部一致，仍仅采用此前
审阅的 6 个 exact / 3 个 offsets 值裁定。数据库源与该上游一致，连续
两次构建字节相同；异常清理源码和测试相对 `97314ab` 未变。
合并后重新验证完整静态回归 27/27、独立输入矩阵 32 场景 / 3443 项断言，
以及路由、词典质量和术语门禁，均通过。
