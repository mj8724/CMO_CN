# CMO 汉化深度精修与实机验证检查点

## 1. 启动弹窗静默消除
- 现象：直接运行 `Command.exe`（不带 `-mode 1`）时，主程序偶发弹出 `Error in Application_Startup: Object reference not set to an instance of an object.` 对话框。
- 方案：在 `CMOLocalizer.cs` 初始化时启动一个 50ms 周期的高优后台守护线程，通过 Win32 `FindWindow("#32770", ...)` 监测当前进程内包含 `Application_Startup` 的对话框并在其渲染首帧前直接发送 `BM_CLICK` 给 `OK` 按钮，同时自动关闭 `CPEBannerForm`，让玩家直接无感秒进中文主菜单。

## 2. 顶部 13 个战术快捷图标按钮 (DarkToolStrip2) 图像级汉化
- 现象：主界面顶部 `DarkToolStrip2` 的 13 个大按钮（`Auto Engage Target`, `Manual Engage Target`, `Plot Course`, `Throttle & Altitude`, `Formation Editor`, `Magazines`, `Aircraft Operations`, `Boat Operations`, `Mounts & Weapons`, `Sensors`, `Systems & Damage`, `Unit/Group Doctrine`, `Mission Editor`）并非使用 `.Text` 属性，而是将两行英文直接烘焙在按钮的 `Image` 位图下半部分！
- 方案：在 `CMOLocalizer.cs` 扫描到 `DarkToolStrip2`（或包含 13 个 `ToolStripButton` 的工具栏）时，按固定顺序（0~12）直接在按钮 `Image` 底部文本区（y > 30）绘制深灰底色与清晰锐利的微软雅黑中文标签（如“自动交战”、“手动交战”、“航线规划”、“油门/高度”、“编队设置”、“弹药库”、“舰载机作业”、“舰载艇作业”、“武器挂架”、“传感器”、“系统/损管”、“条令设置”、“任务编辑器”），彻底消除主界面最显眼的一排英文！

## 3. 全面消除菜单与词库的“机翻味”
- 对主界面 9 大顶级菜单及 400+ 子菜单逐条按照解放军标准军语与资深兵棋玩家习惯重构，精简累赘修饰词，恢复快捷键助记符。
