# AGENTS.md — agent 工作约定（本项目）

> 本文件是 agent 进入本仓库后的**强制约束**。做任何改动前先读 `docs/ROUTING.md`。

## 0. 第一件事：读路由表

**动手前先读 `docs/ROUTING.md`**。它告诉你每个文件干什么、改哪里、改完要跑什么门禁。
不要靠猜文件名，也不要一上来就 `rg` 全仓库。

## 1. 新增文件必须登记到路由表（硬性）

**写任何新文件（脚本、源文件、数据、文档）都必须同时更新 `docs/ROUTING.md`**，
在该文件对应的表格里加一行：

```
| `路径` | 一句话职责（干什么、谁调用、什么时候该改） |
```

一次性/遗留文件归到路由表 §13「遗留与一次性文件」，并写明"已废弃/已完成使命"。

**门禁会强制检查**：`python scripts/verify_routing.py`（已接入 `verify_all` 与 `release_check`）。
未登记 ⇒ 门禁红 ⇒ 不允许提交/发版。

## 2. 数据源铁律

- 只改 `dict/src/*.json`，**永不直接改** `dict/CMOLocalizer.json`（构建产物）。
- 改完必须 `python scripts/build_dict.py` 重建，且字节可复现。
- 池注入（`offsets` / `pool_display_inject`）禁止放入：SQL 片段、代码、路径、格式串、
  派发 token、构建号版本串、引擎标识符。

## 3. 构建与验证

```bash
bash scripts/build.sh                # 编译 + build_dict
bash scripts/deploy.sh               # 装进游戏目录
python scripts/verify_regressions.py --static              # 静态回归
python scripts/verify_regressions.py --static --installer   # 安装器（会 kill 游戏）
python scripts/verify_regressions.py --live                 # 实机（会 kill 游戏）
```

- `--static --installer` 与 `--live` **必须分两次跑**（installer 用例会 `kill` 游戏）。
- 打包用 `--no-report`，避免覆盖 live 回归报告。

## 4. 修 bug 必须登记台账

每个修复写进 `docs/BUG_REGISTRY.md`（只增不改），包含：来源、根因、修复、验收证据、教训。
编号接续现有最大号（当前 R50）。

## 5. 发布需要人工授权

**严禁自作主张**执行 `package.sh` 打包、`publish_release.py` 发版、打 tag 或修改版本号。
这些动作必须由用户明确授权。已发布资产只增不改。

## 6. 文件编码约定（改错会整文件变 diff）

| 文件 | 换行 |
|---|---|
| `dict/CMOLocalizer.json` | 纯 LF |
| `src/installer/CMO_Chinese_Setup.cs` | CRLF |
| `README.md`、`docs/CHANGELOG.md`、`scripts/verify_all.py`、`docs/BUG_REGISTRY.md` | CRLF |
| 其余脚本/文档 | LF |

改写前先读原始字节判断换行符，按原样写回。

## 7. 引擎是 C# 5

`src/engine/CMOLocalizer.cs` 用 `csc` 4.8 编译：**没有 ValueTuple / 表达式体成员 / 插值字符串的新语法**。
新增代码要符合 C# 5 语法。

## 8. 举证优先于自称

- 说"修好了"要给出可复现的度量（计数、截图、门禁输出、A/B 对照），不要只描述。
- 探针本身也可能坏（本项目多次发生）：怀疑结论时先验证探针，
  用**负向用例**证明门禁真的会红。
