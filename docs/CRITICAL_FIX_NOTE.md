# CMO汉化核心故障与修复备忘
## 根因定位
1. 报错 `[string "chunk"]:1: <name> expected near '<\230>'` 的物理原因：
   字符串池 Offset 732882 (Lua沙箱白名单关键字列表) 和 Offset 733572 (Lua内置库函数列表) 被旧版汉化词库错误地翻译成了中文（例如 'error' 翻译为 '错误'，0xE6即八进制230），导致游戏初始化 LuaSandBox / RefreshStats 时直接语法解析崩溃！
2. 游戏目录 `G:\soft\steam\steamapps\common\Command - Modern Operations\CMOLocalizer.json` 仍处于 11266 offsets 的未修复旧版本，未同步最新字典。

## 解决方案
1. 严格清洗 CMOLocalizer.json，剔除所有包含代码、Lua沙箱、SQL语法、格式化模板的危险 offset。
2. 在 CMOLocalizer.dll 源码中添加底层防御阻断：针对代码、Lua关键词等特征一律禁止 pre-populate 覆盖。
3. 全面部署干净词库与 DLL 到游戏根目录。
4. 真实启动游戏、真实加载战役想定，全链路验证无任何异常弹窗。
5. 更新重新编译一键安装包 CMO_Chinese_Patch_Setup.exe。
