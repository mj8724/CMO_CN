# CMO_CN

《Command: Modern Operations（现代行动：指挥）》完整汉化补丁与本地化工程。

基于 .NET `AppDomainManager` 机制加载，不修改游戏 `Command.exe` 主程序文件。

---

## 版本与适配

- **适配的游戏内部版本：`1900.23`** —— 即游戏标题里的 `Command: Modern Operations v1.10 - Build 1900.23`
  （仓库根目录 `GAME_BUILD` 是它的单一来源，`verify_all` 会校验各处一致）。
- 本补丁自身的版本号见仓库根目录 `VERSION`：安装包文件名、安装器标题、更新助手提示都会显示它。
- **游戏每次更新（构建号变化）都可能让本补丁失效**（游戏会重命名内部混淆成员、改动界面文本）。
  此时本仓库**次版本号 +1、修订位归零**（例：4.1.8 → **4.2.0**）并重新适配；判断是否匹配，就看上行的构建号与你游戏标题里的 Build 号是否一致。

---

## 包含内容

- **界面与菜单**：主界面、顶部工具栏、全部 10 大主菜单及多级子菜单、右键菜单。
- **业务窗口**：航空作业（Air Ops）、就绪飞机（Ready Aircraft）、作战条令（RoE）、任务编辑器、作战序列（OoB）、推演选项等。
- **装备数据库**：飞机、舰船、潜艇、地面设施、武器、传感器等 8 大类别的常规参数、战术能力、工作频段及武器挂载包线。
- **教程与战情消息**：涵盖全部 45 个官方教程（飞行、多域打击、对地打击、水面、潜艇、反潜、电子战、空中加油及马克大叔教程），教程操作指引中的按键（F1~F12 等）与按钮名称已对齐。
- **地图地名**：全球 321 处国家/海域及 4,287 座城市的地名汉化。

---

## 实机截图

### 教程战情消息与就绪飞机窗口
![教程消息与就绪飞机](evidence/live/tutorials/17_message_2_and_ready_aircraft_side_by_side.png)

### 航空作业 (Air Ops) 窗口
![航空作业](evidence/live/tutorials/08_air_ops_ramat_david_fixed.png)

### 数据库查看器（外挂武器与挂载包线）
![数据库查看器](evidence/live/tutorials/07_db_viewer_weapons_translated.png)

### 战场主界面
![战场主界面](evidence/live/tutorials/10_right_panel_flying_frame1.png)

---

## 安装与使用

### 1. 使用安装器（推荐）

下载发布包中的 `CMO_Chinese_Setup.exe`：

- **图形界面**：双击运行，选择游戏安装目录，点击「一键安装汉化」。如需恢复英文原版，点击「还原英文原版」。
- **命令行静默安装**：
  ```cmd
  CMO_Chinese_Setup.exe /install "游戏目录路径"
  ```
- **命令行静默卸载**：
  ```cmd
  CMO_Chinese_Setup.exe /uninstall "游戏目录路径"
  ```

### 2. 手动安装

将发布包解压后复制到游戏根目录（覆盖同名文件前请先备份原文件）：
- `CMOLocalizer.dll` -> 游戏根目录
- `CMOLocalizer.json` -> 游戏根目录
- `Command.exe.config` -> 游戏根目录（添加了 AppDomainManager 引导配置）
- `DB/Templates/*.html` -> 覆盖到 `DB/Templates/` 目录
- `Symbols/Menu/*.png` -> 覆盖到 `Symbols/Menu/` 目录

---

## 从源码构建

需要 Windows 环境，并安装 .NET Framework 4.8 与 Python 3.10+。

```bash
# 1. 生成词库
python scripts/build_db_enums.py
python scripts/build_dict.py

# 2. 编译引擎与安装器
bash scripts/build.sh

# 3. 运行校验门禁
python scripts/verify_all.py
```

编译产物位于 `build/` 目录下：
- `build/CMOLocalizer.dll`
- `build/CMO_Chinese_Setup.exe`

---

## 目录结构

```text
├── src/
│   ├── engine/       # 本地化引擎源码 (CMOLocalizer.cs)
│   └── installer/    # 一键安装器源码 (CMO_Chinese_Setup.cs)
├── dict/
│   ├── CMOLocalizer.json     # 运行时主词库
│   ├── glossary.json         # 核心军语术语表
│   ├── brief_translations.json # 简报与教程译文库
│   └── src/                  # 各模块源词库 (菜单/航空作业/数据库枚举等)
├── assets/           # 汉化后的数据库 HTML 模板
├── scripts/          # 构建、打包、校验与部署脚本
├── docs/             # 术语表、实测报告与变更记录
└── evidence/live/    # 实机测试截图
```

---

## 说明

本项目仅包含汉化补丁相关源码、词库与资源，不包含游戏本体或任何商业文件。请在支持正版游戏的前提下使用。

## 常见问题（FAQ）

**Q：Steam 校验游戏文件后汉化不见了？**
A：Steam 的「验证游戏文件完整性」或游戏更新会还原它管理的文件（含地图/简报/按钮图）。
重新运行一次 `CMO_Chinese_Setup.exe` 即可恢复；绿色包用户重新解压覆盖同样有效。

**Q：地图/工具栏按钮还是英文？**
A：请确认装的是 **v4.1.4 或更高**——早期版本未随包分发中文地名集与中文按钮图，
重装旧包不会改变。装好后地图标签由引擎内置的本地中文瓦片服务实时生成，浏览任何区域都是中文。

**Q：怎么更新到新版本？**
A：v4.1.4 起自带「更新助手」：随 Windows 启动、每 6 小时自动检查，托盘图标右键可立即检查/
设置/查看日志；安装器内也有「检查升级」「不再提醒」按钮。**≤ v4.1.3 的版本没有更新助手，
需要手动下载安装一次 v4.1.4**，之后即可自动升级。

**Q：怎么彻底还原英文？**
A：运行 `CMO_Chinese_Setup.exe` → 「一键还原原版英文」。还原会结束更新助手进程、清掉开机启动项，
但**不会**删你设过的更新频率/提醒方式等设置（重装后继续生效）；确实想一并清掉设置，
请勾上旁边的「还原时同时清除我的全部设置」（或命令行 `--uninstall ... --purge-settings`）。

**Q：装了新版后发现不合适，能退回上一版吗？**
A：能。安装/升级时会自动把安装包缓存到本机，安装器上的「回退到上一版本 vX」按钮或
更新助手托盘菜单的「回退到上一版本…」可以直接离线回退（回退目标必须是本地缓存过的版本）。
命令行等价写法：

```
CMO_Chinese_Setup.exe --rollback "<游戏目录>" --silent
CMO_Chinese_Setup.exe --list-versions "<游戏目录>"      # 查看已装版本与本地缓存版本
```

**Q：绿色包（ZIP）怎么用？装了之后状态显示对吗？**
A：把压缩包里的文件解压覆盖到游戏目录即可。为了让安装状态、更新与回退都能正常工作，
解压后请**至少运行一次同包的 `CMO_Chinese_Setup.exe` 或 `CMO_Updater.exe`**：它们会自动
**登记安装状态**（读取实际部署的文件并写入清单，无需重新覆盖文件）；也可以手动执行：

```
CMO_Chinese_Setup.exe --adopt "<游戏目录>"
```

未登记也不影响汉化生效，只是安装器会显示「已安装汉化补丁（已登记当前状态）」。

**Q：检查更新时提示「发现可回退版本」是什么意思？**
A：只有当你本机**已经缓存过**那个版本的安装包时才会出现该提示（用于回退）；
若你正在跑一个比线上更新（未发布）的版本且本机没有缓存，检查更新会静默处理，不会打扰你。
