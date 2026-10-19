# CMO 深度汉化重构与军事实战精修记录 (v3.0)

## 1. 根本原因诊断
1. **“根本用不了 / 想定树为空”的根因**：
   - 提取的硬编码字符串表 `offsets` 中，偏移量 `[2490]` 恰好为游戏内部引用的目录名 `"Scenarios"`。
   - 字典将其机械翻译为 `"战役剧本"`，导致当游戏遍历想定目录时，在磁盘寻找不存在的 `"战役剧本"` 文件夹，导致 `DirectoryNotFound`，想定树因此呈现为空白！
   - 同时，文件对话框过滤器字符串 `Scenario files (.scen)|*.scen` 也被破坏。
2. **“机翻味严重”的根因**：
   - `wordsMap` 与粗暴分词拼接算法（Split + 逐词替词）导致了大量怪物语句（如“方位-Only 出击/发射”、“Difference 在...内高度”等）。
   - 菜单栏 903 项条目充斥机械直译（如 `Unit actions` -> `单位动作`，`Add Decoy` -> `添加 诱饵`，`Weapons Hold` -> `武器保持（禁止开火）`，空格泛滥，术语极不规范）。

## 2. 核心修复与优化措施
1. **彻底清除路径/代码冲突**：
   - 彻底移除了 `offsets` 和 `exact` 中对 `Scenarios`、`Campaigns`、文件过滤器 `*.scen`、Tacview 属性键值及内部对象名的一切翻译。
2. **重构菜单体系为专业现代军事实战术语**：
   - 对 79700~122000 区间共 903 项菜单条目进行了逐项人工精修与校验。
   - 实现了对“条令、交战规则与武器控制 (Doctrine / ROE / WCS)”、“任务与参考点 (Missions + Ref. Points)”、“想定编辑器 (Scenario Editor)”、“态势感知与地图视图 (View & Map Overlays)”等所有系统菜单的信达雅翻译。
3. **清洗半中半英与机翻空格拼凑**：
   - 清除了 5700 余条半生不熟、夹杂未翻译英文实词的残缺翻译。
   - 修复了汉字与汉字之间不合习惯的空格断词。
4. **补充五大核心子系统专家术语字典**：
   - 针对“加载想定界面”、“条令设置窗口”、“任务编辑器”、“推演选项”、“右侧单位状态栏”注入了完全符合现代军事规范的标准术语（如 Bingo Fuel 译为“返航最低油量标准”，WCS 译为“武器开火受限状态”等）。

## 3. 部署与测试目标
- 重新编译并注入 `CMOLocalizer.dll`。
- 将清理后的高纯度字典同步至游戏目录。
- 启动游戏并自动化/实机验证想定树完整性、菜单展开内容、窗口文字渲染。


## 5. 启动时 `beta-1 application startup` 异常排查
- 现象：直接运行 `Command.exe` 时弹出 `Error found during beta-1 application startup. Error details: 未将对象引用设置到对象的实例。`（偏移量 740142）。
- 关联上下文：
  - `[740142] 'Error found during beta-1 application startup. Error details: '`
  - `[740270] 'CommandHost'`
  - `[740296] 'CommandHost.exe'`
- 正在反汇编定位该异常的触发条件（是否由于直接启动 `Command.exe` 而非通过 Launcher/Steam 启动，或由于某个静态变量初始化顺序引起）。
