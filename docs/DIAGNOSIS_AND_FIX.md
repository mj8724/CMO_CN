# CMO 深度汉化实机玩家视角测试与全面修复记录

## 1. 玩家反馈问题根因分析 (2026-10-10 实机复测)
- **现象 1：启动引导大厅（Start Window）还是全英文**
  - **根因**：该窗口是由独立线程托管的 WPF 窗口（类名 `HwndWrapper[Command.exe;StartGameWindowThread;...]`）。原 `CMOLocalizer` 仅依赖 `System.Windows.Forms.Application.OpenForms`，完全扫描不到 WPF 树，导致 "Start New Game", "Quick Battle", "Campaigns" 等主菜单按钮未被翻译。
  - **解决**：在 `CMOLocalizer.cs` 的 UI 监视循环中增加 UIAutomation / WPF VisualTree 遍历，直接将该窗口内的 Button 和 TextBlock 翻译为中文。

- **现象 2：想定选择界面部分英文与机器翻译残留**
  - **根因**：
    - `Delete selected` / `Browse...` 之前未进入字典；
    - `Saved Database Version` 被翻成“已保存 数据库 版本”（带空格生硬）；
    - `Steam Workshop` 被翻成“蒸汽轮机 Workshop”；
    - `Command: Modern Operations` 在标题栏被翻成“Command: Modern 作战”。
  - **解决**：彻底清洗字典，替换为“删除选定项”、“浏览...”、“已保存数据库版本”、“创意工坊 (Steam Workshop)”以及“现代行动”。

- **现象 3：游戏主界面菜单和想定加载完全正常**
  - 核心翻译池（AoIBWWlDJbaf7LijnA / 10550 条）与主界面所有菜单（Record -> 记录，Create AGU... 等）工作完全正常。
  - 战役剧本列表与树形图均能正确汉化。

## 2. 修复步骤
1. 修改 `CMOLocalizer.json`：修正上述生硬与残留词汇，注入 WPF 启动菜单的 9 大操作项。
2. 在 `CMOLocalizer.cs` 中增加独立后台线程，定期使用 UIAutomation（标准 .NET 4.0 自带）扫描并就地替换 HwndWrapper 窗口中的所有 TextBlock 与 Button 文本。
3. 重新编译 `CMOLocalizer.dll`，分发到游戏目录和汉化安装包目录。
4. 真实模拟玩家点击流测试并截图确认。
