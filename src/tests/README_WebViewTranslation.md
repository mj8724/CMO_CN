# 网页翻译回归

`WebViewTranslation.cs` 在 UI 线程通过反射接入 WebView2。Core 就绪后异步注册文档脚本，再补译当前页面；句柄重建保留注册结果，但消费前核对任务、当前 Core 和控件生命周期。重复发现不重复注册或扫描全文；失败回退遵守轮次预算。

脚本先译常见标签，再由 `MutationObserver` 收集增量，通过原生桥调用现有 `TranslateBriefingText`。回包核对来源、文档 nonce、节点连接、原文及可编辑状态，不重建 `innerHTML`。桥仅接受 `about:blank`、`about:srcdoc`、游戏目录内的文件及 HTTP(S) 回环地址；外站不安装脚本，`[LOADDOC]` 保留命令语义。

消息或文档注册 API 不可用时，通过 `ExecuteScriptAsync` 读取同一增量队列，每 250ms 检查、每轮最多 8 包，不改游戏 Settings。缺少文档创建 API 时不保证首帧中文。销毁句柄暂停回退，重建恢复，销毁控件释放。

每节点上限 16,384 个 UTF-16 字符，每包最多 64 条、URI 编码后 256 KiB；待译原文及缓存各最多 2,048 条。无法编码的节点保留原文且不阻断队列。无回包请求 15 秒后释放，不主动重试；移除节点也释放请求并清除匹配的旧标记，允许重新插入。仅支持顶层文档；词库热更新需重启引擎。

## 真实 Edge DOM

需要 Node.js、可解析的 `playwright` 包及本机 Microsoft Edge，无需游戏或登录态：

```powershell
node scripts/test_web_translation.cjs
```

独立无头浏览器只替代原生消息传输。检查首帧标签、段落、输入保护、节点复用、导航及超时旧结果、请求释放、编码/分包/容量边界、外站隔离和 1,000 次新增节点的批次及扫描成本。

## .NET 反射宿主

```powershell
./scripts/build.ps1
./scripts/test_ui.ps1 -Suite webview
```

宿主反射调用实际 DLL，替代 WebView2 API/事件，使用真实隐藏 WinForms 句柄检查初始化、异步注册、Core 替换/为空、句柄销毁/重建、回退预算、来源限制、设置保持和销毁解绑。不会运行游戏。

## 真实 WebView2 独立宿主

```powershell
./scripts/test_webview2.ps1 -GameDir '你的游戏目录' -Interval 1500 -Samples 8
```

仅从游戏目录读取 SDK/Loader，profile、副本和日志写入 `build/`。比较旧版时指定 `-EnginePath`、`-DictionaryPath`，固定词库、Runtime 和样本数，并同时检查输入、链接及旧回包保护。计时从 Core 就绪后的导航开始，含 20ms 完成查询的分辨率，不含 Runtime 冷启动。

文档创建脚本只保证在注册完成后的导航解析前运行；已开始加载的页面只能补译，复杂文本依赖异步原生消息。独立宿主结果不能保证游戏全负载性能，完整验证边界见 [本地化验证](../../docs/LOCALIZATION_VALIDATION.md)。
