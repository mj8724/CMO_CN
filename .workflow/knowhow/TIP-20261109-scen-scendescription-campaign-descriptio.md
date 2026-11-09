---
title: 战役正文有两层：.scen ScenDescription 与 .campaign Description/EndingText
type: tip
created: 2026-11-09T02:27:45.055Z
keywords:
  - DLC 战役
  - 系列战役
  - campaign
  - ScenDescription
  - 盘点覆盖面
sourceRef: BUG_REGISTRY R83
lifecycleStatus: active
relatedPaths:
  - scripts/audit_campaign_text.py
---

# 现象

用户反馈「某两个 DLC 战役没汉化」，截图是**系列战役选单**右侧的介绍面板。

# 根因结构

战役正文有两层，失败方式不同：
- `.scen` 的 `<ScenDescription>`：内联 HTML 走词典整段键；或 `[LOADDOC]xxx.html`
  由 `Scenarios_HTML.zip` 就地落盘汉化（不经词典）。
- `*.campaign` 的 `<Description>`/`<EndingText>`：同样是「内联 HTML 或 LOADDOC」二选一。
  本仓库 11 个战役里只有 Fail Safe、Tides of War 是内联，其余 9 个是 LOADDOC。

只扫 `.scen` 的盘点脚本会给出「全部已汉化」的假结论——覆盖面本身就是验收对象。

# 做法

1. 盘点必须两层都扫（`scripts/audit_campaign_text.py` 的 `scenario_rows` + `campaign_rows`）。
2. 内联正文的键必须**取自运行时文本节点**（`WEBVIEW_JS` / `BRIEFING_RAW_DUMP`），
   不能从源文件推导：一个 `<BR>`、一个内联标签的空白差异就让整段键落空。
3. 段落若由多个已审定整句平铺而成，直接拼接既有译文，术语自然一致
   （例：东盟团结演习/十段线/小蓝人/海上民兵）。

# 关联

`dict/src/dlc_briefing_paragraphs.json`、`docs/BUG_REGISTRY.md` 的 R83。
