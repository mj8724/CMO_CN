# CMO 汉化修复进展与突破记录 (Turn 124 Checkpoint)

## 1. 致命 Bug 彻底根治：“Ship with Database ID 1478 is missing from the database”
- **根因追查结果**：
  - 玩家/我们在选择拉塔基亚海战进入想定推演时，游戏引擎抛出该异常。
  - 通过抓取 `G:\soft\steam\steamapps\common\Command - Modern Operations\Logs\ExceptionLog_*.txt`，发现真实底层异常链是：
    `KeyNotFoundException: The given key was not present in the dictionary.`
    发生在：`Command_Core.DAL.DBFunctions.EW_GetAllPossibleEmissionsForUnits(...)`
    其内部通过字典检索了 `SensorType`、`ComponentType` 等 SQLite/DAL 列名或属性键名！
  - 原因是 `CMOLocalizer.json` 中的 `offsets`（底层智能解密注入点）包含了 `SensorType` -> `传感器类型` 等底层代码字符串，将数据库结果集列名在 IL 层解密时替换成了中文，导致 DAL 层在比对或字典查询字典列名时直接抛出 `KeyNotFoundException`，使舰船 `GetShip` 失败返回 null，最终引发上层误报“Ship with Database ID 1478 is missing from the database”！
- **修复方案与落实**：
  - 已编写脚本对 `CMOLocalizer.json` 的 `offsets` 进行了系统级安全清洗，将 541 条包含 SQL 关键字（`SELECT`、`FROM`、`WHERE`、`AND`、`OR`、`ORDER BY` 等）、数据库列名、驼峰数据标识（如 `SensorType`、`AircraftID`、`ShipID` 等）全部从底层 `offsets` 强制迁移到纯 UI 层的 `exact` 字典！
  - offsets 从 4045 精简为 3504 条纯引擎安全显示字符串，彻底绝收对底层数据库引擎的意外侵入。

## 2. 战役想定简报（LoadScenario 右侧）汉化方案
- **控件形态**：LoadScenario 右侧使用的是 `Microsoft.Web.WebView2.WinForms.WebView2` 或者是原生的 `WebBrowser` 渲染 HTML。
- **简报词库准备**：已将 `briefing_translation_dict.json` 中 4,828 条战役简报高质量译文完整合并入 `CMOLocalizer.json` 的 `exact` 词库（总量达 11,543 条）。
- **注入实现**：
  - 在 `CMOLocalizer.cs` 中增加针对 `WebView2` 和 `WebBrowser` 的 HTML 翻译注入。
  - 使用 DOM TreeWalker 或正则表达式逐节点匹配替换，确保不破坏 HTML 标签与样式。

## 3. ScenarioFeatures（想定特性）WPF 窗口汉化
- ScenarioFeatures 是一个基于 WPF 的独立窗口，包含大量的 CheckBox 与 ComboBox。
- 文本已在 `exact` 词典中完备收录（如“无限基地弹药库”、“先进舰炮射击”等）。
- 在 `CMOLocalizer.cs` 的 `TranslateWpfElement` 中补充对 CheckBox、RadioButton、ComboBoxItem 的 Content/Text 的递归汉化，以及其窗口弹窗时的自动化监听。

## 4. WebView2 控件精准识别与 DOM 树翻译突破 (Turn 155 Checkpoint)
- 经反射扫描 `Command.exe` 全部窗体字段，确认：
  - `Command.LoadScenario`、`Command.SelectSide`、`Command.SideBriefing` 等简报窗口内部使用的并非 `ChromiumWebBrowser` 或旧版 `WebBrowser`，而是 `Microsoft.Web.WebView2.WinForms.WebView2`。
  - 原 `CMOLocalizer.cs` 仅检测了 `ctrl is WebBrowser` 与 `ChromiumWebBrowser`，导致 `WebView2` 完全未被拦截处理。
- `CMOLocalizer.json` 的 `exact` 词典中已包含 11,543 条精准词条（含 `briefing_translation_dict.json` 中的全部 4,885 条战役简报段落）。
- 解决方案：在 `CMOLocalizer.cs` 中针对 `WebView2` 控件，通过 `ExecuteScriptAsync` 结合 C# 端的 `exactMap`/`TranslateString` 实现 DOM 文本节点级与段落级双向替换。
