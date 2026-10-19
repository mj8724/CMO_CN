# 网页首屏与增量翻译回归

## 行为

`WebViewTranslation.cs` 在 UI 线程通过反射接入 WebView2，无 SDK 编译依赖。控件尚未初始化时先订阅初始化事件；Core 就绪后异步注册文档创建脚本，等待注册完成再补译当前文档。正常消息路径的重复发现不会执行全页扫描。注册失败转为脚本回退，同一 Core 不再反复注册或记录失败日志。

注册完成时若控件暂时没有句柄，或已排队的完成回调随旧句柄销毁，保留任务结果，待句柄恢复或下次发现时在 UI 线程消费一次。消费前核对任务、已接入 Core 和控件当前实际 Core；Core 替换、暂时为空或控件销毁后，不得用旧结果启动补译或回退。同步补译重入并替换 Core 后，旧消息设置不能开启新 Core 的回退。重复发现和 WinForms `RecreateHandle` 不重复注册或执行当前文档补译。

文档脚本先处理一小组由现有词库生成的常见标签，其余文本通过原生消息桥批量调用现有 `TranslateBriefingText`。`MutationObserver` 处理新增子树和文本变化，`WeakMap` 记录节点当前值。回包必须同时满足文档 nonce 一致、节点仍连接、原文仍匹配、节点当前不可编辑。不会重建 `innerHTML`。

桥只接受 `about:blank`、`about:srcdoc`、游戏进程基目录以内的本地文件和 HTTP(S) 回环页面，同时检查消息来源、原生当前来源及回写时来源。外站也不安装本地短词翻译器；`[LOADDOC]` 命令按原文完成，不进入会读取文档的翻译分支。

游戏禁用 WebMessage、缺少消息/文档创建 API 或注册失败时，只要 `ExecuteScriptAsync` 可用，就通过该 API 提取同一 Observer 队列中的待译增量并回写。回退每 250ms 检查一次，每轮最多连续取 8 包；不会反复抽取或替换整个 body。缺少文档创建 API 时，在发现新文档没有脚本后补装一次，因此不保证首帧中文。回退不改动游戏 Settings；同一 Core 后来重新启用消息时，回退仍可工作。句柄销毁时停止计时器，重建后恢复，控件销毁时释放。所有来源、文档 nonce、原文和输入区校验继续适用。包含无法 URI 编码的孤立 UTF-16 surrogate 的节点保留原文并释放请求，不阻断后续文本。

单节点超过 16,384 个 UTF-16 字符时保留原文。每包最多 64 条且 URI 编码后不超过 256 KiB；同时最多保留 2,048 个不同原文请求，超出的节点保留原文直至其内容再变化。无回包的请求 15 秒后释放节点引用；节点从文档移除时也释放引用。每文档最多缓存 2,048 条结果。当前仅支持顶层文档，与原来的 WebView2 翻译路径一致。脚本按引擎启动时的词库生成，词库热更新后应重启引擎。

## 真浏览器行为测试

需要 Node.js、可解析的 `playwright` 包和本机 Microsoft Edge；不需要游戏或登录态。

```powershell
node scripts/test_web_translation.cjs --benchmark
```

测试使用独立无头 Edge 和本机 HTTP fixture，替代的只有原生消息传输。覆盖首个 animation frame 的短标签、复杂段落、输入保护、节点复用、导航旧结果、未命中不重发、超长节点、长段落中英混排分包、请求上限、外站不接入，以及 1,000 次新增节点的批次/扫描数量。`--benchmark` 输出受控轮询等待与增量路径延迟；该数字不包括游戏负载或真正的 WebView2/.NET 跨进程成本。

## .NET 反射适配器测试

```powershell
./scripts/build.ps1
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$test = Join-Path $PWD 'build/WebViewTranslationHostTests.exe'
& $compiler /nologo /target:exe /reference:System.Windows.Forms.dll ('/out:' + $test) (Join-Path $PWD 'src/tests/WebViewTranslationHostTests.cs')
if ($LASTEXITCODE) { throw 'Test compilation failed' }
& $test (Join-Path $PWD 'build/CMOLocalizer.dll')
if ($LASTEXITCODE) { throw 'Adapter tests failed' }
```

该测试共 47 条检查，反射调用实际引擎 DLL，替代 WebView2 的控件/API 事件，验证初始化、异步注册、复用、注册失败回退、缺少注册/消息 API、同 Core 消息设置变化、回退轮次预算、消息边界、来源与文档命令限制、设置保持及销毁解绑。句柄用例使用真实隐藏 WinForms 控件的销毁/创建，并通过不派发消息的队列检查覆盖完成回调已排队后销毁句柄；另覆盖无句柄时成功/失败、Core 替换/为空、`RecreateHandle` 幂等和旧回退提取结果失效。不会运行安装器或游戏。

## 真 WebView2 独立宿主

`test_webview2.ps1` 仅从 `-GameDir` 读取 SDK/Loader DLL，复制到被忽略的独立构建目录；浏览器 profile 和日志也保存在该目录。不会运行游戏或写入游戏目录。

```powershell
./scripts/test_webview2.ps1 -GameDir '你的游戏目录' -Interval 1500 -Samples 8
```

默认构建当前引擎。比较旧版本时指定 `-EnginePath`、`-DictionaryPath`，并为两次运行使用相同词库和样本数。首帧、导航及动态文本时序由真实 WebView2 采集；不应将独立宿主结果当作游戏负载下的性能保证。

计时从 Core 初始化完成后的页面导航开始，不含浏览器 Runtime 冷启动；完成状态由 20ms 间隔查询，因此包含宿主查询分辨率。请报告相同词库、Runtime 和样本数下的多轮分布，并同时验证输入、可编辑正文、链接、过期回包及节点原文保护。集成候选的验证范围见 [本地化验证说明](../../docs/LOCALIZATION_VALIDATION.md)。

微软 API 保证文档创建脚本在全局对象建立后、HTML 解析前运行，但仅适用于注册完成后的导航。已开始加载的首个文档只能补译；复杂文本还依赖原生异步消息及 CLR 工作线程池，不保证所有页面均在首次绘制前完成。
