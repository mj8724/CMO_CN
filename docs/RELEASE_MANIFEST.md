# 发版打包清单（Release Manifest）

> 本文件是「要发什么、发之前必须检查什么」的**唯一权威清单**。
> 动手发布前先跑 `python scripts/release_check.py`（统一入库检查，逐项 PASS/FAIL + 退出码），
> 全绿后再按 `docs/RELEASE_TEST_SOP.md` 的顺序执行。清单与脚本互相校验，改一处要同步另一处。

## 1. 发什么（发布资产，4 个 + 发布说明）

| 资产 | 由谁产出 | 上传目标 | 说明 |
|---|---|---|---|
| `build/CMO_Chinese_Setup_v<VER>.exe` | `scripts/package.sh` | GitHub Release（tag `v<VER>`） | 一键安装器；内嵌引擎 dll、词典、DB 模板、地名集、菜单按钮图、启动器 resx、Scenarios 简报 zip、助手 exe、二维码 |
| `build/CMO_Chinese_Setup_v<VER>.exe.sha256` | `scripts/package.sh` | 同上 | 校验值（`publish_release.py` 会作为独立资产上传） |
| `build/CMO_Chinese_Pack_<VER>.zip` | `scripts/package.sh` | 同上 | 绿色包（解压覆盖到游戏根目录即用），内含 `汉化说明.md` |
| `build/CMO_Chinese_Pack_<VER>.zip.sha256` | `scripts/package.sh` | 同上 | 校验值 |
| 发布说明正文 | `docs/CHANGELOG.md` 顶部 `## v<VER>` 段 | Release body | 只写用户可感知的变化；不写内部过程 |

- 版本号来源唯一：仓库根的 `VERSION`；发布 tag 必须等于 `v$(cat VERSION)`。
- **不得**上传：`build/` 下的中间产物（`*.log`、`regression_report.json`、`Scenarios_HTML.zip`、`GuiDriver.exe`、`CMOSharedTest.exe`）、`evidence/`、`.workflow/`、任何凭据。

## 2. 必须先入库的东西（源与资产，缺一不可）

| 类别 | 文件 | 检查 |
|---|---|---|
| 引擎 | `src/engine/CMOLocalizer.cs` | `verify_regressions.py --static` 的 R0/R19 断言（含输入控件保护、崩溃取证、拉锯熔断） |
| 安装器 | `src/installer/CMO_Chinese_Setup.cs` | `--installer` 隔离回归（清单/还原/设置保留/回退） |
| 更新助手 | `src/updater/CMO_Updater.cs` | `verify_regressions.py --static` + `verify_all.py` version 门禁 |
| 共享状态层 | `src/shared/CMOShared.cs`、`src/shared/CMOSharedTest.cs` | `build/CMOSharedTest.exe` 全 PASS（R18-static-selftest） |
| 词库 | `dict/CMOLocalizer.json` + `dict/src/*.json`（含 `campaign_ui.json`） | `verify_all.py` 的 dictionary / scenario meta（标题+设置+**战役剧本名**） |
| DB 模板 | `assets/db_templates/*.html`（8 个） | `verify_all.py` db templates（Platform.html 为纯字体壳） |
| 地名集 | `assets/placenames/Synoptic.wwp`、`Cities.wwp` + 两套坐标审核表及范围清单 | `verify_all.py` placenames（4,287城市+321区域逐点唯一命中审核、译名一致、文件集合与条数完整）；`package.sh` 编译前再验两套资产 |
| 菜单按钮图 | `assets/symbols_menu/*`（≥28 文件） | 打包含入 + 实机菜单按钮为中文 |
| 启动器 | `assets/launcher/stringsChinese.resx` | 打包含入 |
| 说明 | `README.md`（随包为 `汉化说明.md`） | R17-static-pack-readme（含「登记安装状态/--adopt」说明） |
| 版本 | `VERSION`、安装器 `CurrentVersion`、安装器标题、助手 `UpdaterVersion`、词典 `_meta.version` | `verify_all.py` version（五处一致） |
| 台账 | `docs/BUG_REGISTRY.md` | 本轮每个用户可感知问题都有 R 条目 |
| 发布说明 | `docs/CHANGELOG.md` | 顶部有 `## v<VER>` 段 |
| 回归 | `scripts/verify_regressions.py`（static/installer/live）、`scripts/verify_all.py` | 见 §3 |
| 脚本 | `scripts/build.sh`、`deploy.sh`、`package.sh`、`publish_release.py`、`release_check.py` | 存在且可执行 |

## 3. 发布前的证据要求（顺序见 SOP）

1. `bash scripts/build.sh` 编译通过（引擎 + 安装器 + 助手 + 测试宿主）。
2. `python scripts/verify_regressions.py --static --installer` 全绿。
3. `bash scripts/deploy.sh`（把本轮引擎/词库装进游戏目录，否则 R0-static-env 会红）。
4. `python scripts/verify_all.py` 全绿（词库/菜单/引擎解析/想定/战役名/简报/地名集/DB 模板/版本/密钥/工作区）。
5. `python scripts/verify_regressions.py --live` 全绿（需游戏运行；这一步会写 `build/regression_report.json` 的 live 模式）。
   - 与 `--static --installer` **分两次运行**：installer 用例会关闭游戏（`CMOShared.KillProcesses` 是安装器正确行为）。
6. `python scripts/release_check.py --pre` 全绿（统一入库检查；`--pre` 跳过尚未产出的产物检查）。
7. `bash scripts/package.sh` 产出 4 个资产。
8. `python scripts/release_check.py`（完整版：含产物哈希与绿色包内容/禁入项）全绿。
9. `python scripts/publish_release.py --version <VER>` 上传（会再校验回归报告：同版本、含 static/installer/live、24h 内）。
   - 打包门禁用 `--no-report` 运行，不会覆盖 live 报告；**发布前最后一次回归必须是带 `--live` 的那次**。

## 4. 版本与一致性约束

- 五处版本号一致（§2 版本行），由 `verify_all.py` 把关；词典 `_meta.version` 必须与 `VERSION` 一致。
- **两个号必须同时写清（用户 2026-10-17 明确要求）**：本补丁自己的版本号（`VERSION`）与**适配的游戏内部版本**（构建号，仓库根 `GAME_BUILD` ↔ `CMOShared.ReleaseConstants.GameBuild`）必须同时出现在：
  ① 安装器标题与副标题；② 更新助手（发现新版本/已是最新提示）；③ 发版说明（`publish_release.py` 自动在正文首行写 `> 适配游戏内部版本：X`）；④ `README.md`（随绿色包分发为 `汉化说明.md`）。
  `verify_all.gate_version` 校验 `GAME_BUILD` ↔ 共享常量一致、安装器引用了该常量、README 写有构建号。
- **游戏更新（构建号变化）时，本补丁次版本号 +1、修订位归零** 并重新适配（例：4.1.8 → 4.2.0；具体提升由用户确认后写入）。
  历史事故（R31）：Steam 把游戏从 1900.20 升到 1900.23，注入点被重命名导致汉化整批失效，
  而发布物里根本没有记录适配的构建号，用户与我们都无法一眼判断是否匹配。
- 游戏目录引擎必须含本轮 `DEPLOY_MARKERS`，防止「装回旧引擎后回归假绿」。
- 已发布资产只增不改：不覆盖、不删除历史 `build/CMO_Chinese_Setup_v*.exe` 与 GitHub Release 资产；本机重新打包得到的 `*.sha256` 与已发布资产不再对应，禁止混用。

## 5. 发布后自检

- `python scripts/publish_release.py --verify-only`（或人工 `curl` Release API）确认 4 个资产齐全、`latest` 指向新 tag。
- 在干净机器/干净目录上：安装器流程（安装→清单版本→设置保留→回退）与绿色包流程（`--adopt`）各走一遍。
- 更新助手路径：`releases/latest` 返回新版本 → 提示升级；远端更旧且本地有缓存 → 提示可回退。

## 6. 回滚路径

- 安装器界面「回退到上一版本 vX」/ 助手托盘「回退到上一版本」/ `CMO_Chinese_Setup.exe --rollback <游戏目录>`（需要 `%LOCALAPPDATA%\CMOChinese\packages` 有对应安装包缓存）。
- 极端情况：`--uninstall`（保留设置）+ 用上一版安装包重装；GitHub Release 上删除错误 tag 前必须先在 CHANGELOG 记录原因。

## 7. 禁止入库清单（打包与仓库双向）

- 凭据/令牌：`.env*`、`*token*`、`credentials*.json`、任何 `ghp_`/`github_pat_` 明文（`verify_no_secrets.py` 硬门禁）。
- 过程材料：`evidence/`（截图）、`.workflow/`（会话/恢复）、`docs/*checkpoint*`、`*.log`、`build/_res_probe/`。
- 构建缓存与中间件：`build/Scenarios_HTML.zip`、`build/regression_report.json`、`build/GuiDriver.exe`、`build/CMOSharedTest.exe`（不进包）。
