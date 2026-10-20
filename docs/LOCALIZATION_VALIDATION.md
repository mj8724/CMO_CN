# 中文加载优化的验证

本候选在主分支修复上加入界面增量翻译及 HTML 修复。地名由 PR #4 单独管理；不包含全仓换行归一化、`.workflow` 或 blame 配置改写。仅实际修改的文本使用 LF。

## 构建与隔离检查

在 Windows / .NET Framework 4.8 下从仓库根目录运行：

```powershell
./scripts/build.ps1
./scripts/test_ui.ps1
python scripts/test_static_html.py
./scripts/test_scenario_html.ps1
python src/tests/PanelFlickerProbeTests.py
python src/tests/DBLiveProbeTests.py
```

`test_ui.ps1` 默认检查全部控件、WPF、反射 WebView 宿主及 Bulk 业务契约。使用 `-Suite combobox` 等选取一类，或同时使用 `-Mode refill` 选取单项；`-Engine`、`-Dictionary` 和 `-OutputDir` 可指定隔离输入及输出。结果包含源码/引擎哈希、退出码和逐项日志。脚本不启动游戏；构建产生的测试输入不作为发行包。

真实 Edge DOM 与只读 SDK 的 WebView2 独立宿主检查见 [网页翻译回归](../src/tests/README_WebViewTranslation.md)。

## 游戏及静态门禁

```powershell
$env:GAME_DIR = '你的游戏目录'
python scripts/verify_regressions.py --static
python scripts/verify_regressions.py --live
```

门禁依赖共享测试宿主与安装器输入，缺失应失败。保留的本机地名若与主分支不同，逐字节一致性检查仍会失败；不能将其跳过后宣称全部通过。

数据库探针从当前数据库只读选择有效记录，核对新生成 HTML 的类型、ID 和标题名称；地面单位使用唯一可见查看器的原生列表。只有明确聚焦成功才发送键鼠。侧栏探针按宿主及节点路径保留重复节点，并通过只读 Lua 查询确认推演时间前进；空帧、旧页面、超时及新增熔断均失败，原始帧保存在 `evidence/live/regression/panel_flicker_samples.json`。

## HTML 与性能边界

HTML 导出使用当前正文，保留编码、模板和已有 `.orig`。部署前核对源哈希并备份当前正文；旧 `.orig` 及源机器的备份不能覆盖当前正文或目标机器备份。清单的 `partial` 标记表示仍有未译片段，隔离安装器检查不代表完整发行包验收。

性能比较应固定词库、Runtime、样本数及业务事件契约，记录 DLL 哈希和多轮分布。包含主分支修复的比较不能全部归因于 PR2；独立宿主计时不能代替当前 DLL 的游戏验证。
