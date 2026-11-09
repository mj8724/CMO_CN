---
title: WebView2 桥：页→宿主通道必须用真实回执证明（单向可用是真实形态）
type: tip
created: 2026-11-09T02:27:33.890Z
keywords:
  - WebView2
  - postMessage
  - WebMessageReceived
  - WEBVIEW_DIAG
  - 通道存活
  - 轮询兜底
sourceRef: BUG_REGISTRY R84
lifecycleStatus: active
relatedPaths:
  - src/engine/WebViewTranslation.cs
---

# 现象

界面某处仍是英文，但词典里明明有整段译文（`XLATE` 的 `briefing` 通道返回中文）。
本项目实测案例：`CampaignPlayWindow/WebBrowser1`（系列战役描述面板）。

# 关键判据（别猜，先测）

WebView2 桥是**两向独立**的：宿主→页（`ExecuteScriptAsync`）和页→宿主（`postMessage`）。
「单向可用」是真实存在的形态：该面板上 `coreMatches=true`、`messagesWired=true`、
`messagesEnabled=true`、`registrationHandled=true`，但 `messageEvents=0`、
`documentNonce` 永远为空——页面 postMessage 一条都到不了宿主，宿主回包自然送不回去。

用只读探针 `WEBVIEW_DIAG <窗体>` 一次分清三种可能：
- `documentNonce` 有值 + `messageEvents>0` → 通道通，问题在译文/键；
- `lastReject` 有值 → 回包被拒（看原因：来源不符/陈旧 core/过期 nonce/队列满）；
- `messageEvents==0` 且 `lastReject` 空 → 通道死了（本条）。

# 判据的反面

`CoreWebView2.Settings.IsWebMessageEnabled` 只是设置位，`true` 不代表消息能到；
`WebMessageReceived` 事件挂上也不代表会触发。必须有一次**真实回执**。

# 做法

1. 通道能力用探帧证明：宿主执行 `postMessage('CMO_WEB_V1_HELLO\t'+documentId)`，
   1.6s 内收到即确认；超时自动切宿主轮询 `take()`（把已发出的请求重新取回翻译）。
2. 丢弃一律留痕（首个原因 + 计数），否则「词典有译文但界面不显示」无从定位。
3. 轮询是兜底不是默认：能确认通道就不开轮询，避免每 250ms 一次脚本执行。

# 关联

`src/engine/WebViewTranslation.cs`、`docs/BUG_REGISTRY.md` 的 R84。
