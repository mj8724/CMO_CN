---
title: 数据库查看器 Comments 显示在列表行尾：模板里查不到不等于不显示
type: tip
created: 2026-10-27T20:48:23.737Z
keywords:
  - 数据库查看器
  - Comments
  - 可见性
  - 实机对照
  - 静态误判
lifecycleStatus: active
relatedPaths:
  - dict/src/db_comments.json
  - src/engine/CMOLocalizer.cs
---

# 数据库查看器 Comments 显示在「列表行尾」——模板里查不到不等于不显示

## 现象
`Data*.Comments`（型号批次/年代/备注）在 106 个 DB 版本里合计 15,020 个唯一值，
其中 `DB3K_519 + CWDB_519` 有 35,849 条非空行。判断"要不要译"时，
静态侧给出的结论是**不显示**：

- `assets/db_templates/*.html`（8 个详情面板模板）都没有 `Comments` 字段；
- 游戏自带 `DB/Templates/*.html` 同样没有；
- `Command.exe` 里 `Comments` 只出现 4 次（1 次是 MemberRecords 表结构、2 次是 INI 解析器成员名、
  1 次是版本资源）——没有任何 UI 标签。

## 真相（来自实机截图对照）
`evidence/live/dbviewer/103_pane_rendered.png` 的列表行尾与 DB 值**逐字一致**：

| DB 记录 | `Comments` | 截图行尾 |
|---|---|---|
| A-300-600ST Beluga | `5x, Super Guppy Replacement` | `… 1995, 5x, Super Guppy Replacement` |
| A.400M Atlas (ID 271) | `7x` | `… 2020, 7x` |
| A.400M Atlas (法国) | `-` | 无尾巴 |
| 3MS-1 Bison B | `-` | 无尾巴 |

⇒ Comments 由游戏的**混淆代码**拼进列表行字符串，模板与串表里当然查不到。

## 教训
1. **"模板/串表里没有"只能证明详情面板不显示，不能证明列表不显示。**
   列表行是运行期字符串拼接，判据必须是**实机截图与数据逐字对照**（`Comments='-'` 的行恰好没有尾巴，
   这是最强的反向对照——比"有值且出现"更难造假）。
2. 教训通用化：判断某字段是否可见，先找**同一界面的正负样本**（有值/占位符），
   再拿渲染结果对齐；不要只依赖静态资源清单。

## 落地（本仓库）
- 引擎新增顶层分区 `comments`（源 `dict/src/db_comments.json`），
  由 `TranslateDBRowCommentsTail` 在 DB 列表行尾查表；`comments` 分区必须排在
  `offsets` 之前，旧引擎才会整段忽略它（向后兼容）。
- 该表**不能**并进 `exact`：`Tank`(油箱)/`Mount`(挂架)/`Group`(编组) 与界面同名词条
  含义不同，实测冲突 47 条。
- 验收度量：Comments 行"段内已无可译英文词"从 0 → 88.8%（31,821/35,849），
  词表 4,070 条 + 4 类形态规则（数量/编制、句首模板、Assoc 家族、交付类动词）。
