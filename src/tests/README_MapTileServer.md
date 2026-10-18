# 中文地图标签服务

地图标签使用已安装的 Synoptic、Cities 地名集，在本机回环地址生成透明 PNG 瓦片。服务只负责地图地名，不改写想定单位、呼号或其他底图里已经烧录的文字。

## 并发与超时

服务使用 1 个专用接受线程、4 个专用工作线程和最多 64 项的等待队列，不依赖 CLR 全局线程池。游戏下载占满线程池时，本地标签请求仍可处理；少量未完成请求也不会让所有正常请求串行等待。

每个连接从接受时开始，首行共有 1500ms 预算，排队时间包含在内。读取每个字符之前和之后均检查单调时钟，并按剩余预算设置同步读取超时。持续发送零散字节不能重置预算；首行最多 1024 个字符。响应写入另有 1500ms 同步操作超时。

队列已满时立即关闭新连接；排队后预算已用尽的连接也会关闭。资源上限不等于任意负载下所有请求都能成功，游戏可在后续加载中重新请求瓦片。

请求限定为 `GET /MapTiles/Labels/z/x/y.png HTTP/1.0` 或 `HTTP/1.1`，其中 `z` 为 0–22，`x/y` 必须位于对应缩放级别范围内。错误路径或坐标返回 400。请求、拒绝及错误共采样最多 16 条诊断日志，计数和日志写入均支持多个工作线程。

## 独立回归测试

`src/tests/MapTileServerTests.cs` 可用 Windows .NET Framework C# 编译器构建，引用 `System.Drawing.dll`。它通过反射启动 DLL 中的地图服务，不调用游戏域初始化，也不运行安装器。

测试程序参数为：

```text
MapTileServerTests.exe <CMOLocalizer.dll> <独立数据目录> <模式>
```

每个模式使用独立进程和全新数据目录；将 `assets/placenames/Synoptic.wwp`、`Cities.wwp` 分别复制到该目录下的 `WW/PlacenameSets/Synoptic/0000_0000.wwp`、`WW/PlacenameSets/Cities/0000_0000.wwp`。

先构建引擎为 `build/CMOLocalizer.dll`，再从仓库根目录运行以下 Windows PowerShell 命令。自动运行器应为每个测试进程设置 35 秒超时并等待退出。

```powershell
New-Item -ItemType Directory -Force build | Out-Null
& "$env:WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /reference:System.Drawing.dll /out:build/MapTileServerTests.exe src/tests/MapTileServerTests.cs
if ($LASTEXITCODE) { throw 'Test host compilation failed' }
$mapRun = [Guid]::NewGuid().ToString('N')
foreach ($mapMode in @('starvation', 'protocol', 'idle', 'batch', 'multi-idle', 'trickle', 'pressure')) {
    $mapFixture = Join-Path $PWD ('build/map-tests-' + $mapRun + '-' + $mapMode)
    foreach ($mapSet in @('Synoptic', 'Cities')) {
        $mapTarget = Join-Path $mapFixture ('WW/PlacenameSets/' + $mapSet)
        New-Item -ItemType Directory -Force $mapTarget | Out-Null
        Copy-Item -LiteralPath ('assets/placenames/' + $mapSet + '.wwp') -Destination (Join-Path $mapTarget '0000_0000.wwp')
    }
    & ./build/MapTileServerTests.exe (Join-Path $PWD 'build/CMOLocalizer.dll') $mapFixture $mapMode
    if ($LASTEXITCODE) { throw ('Map test failed: ' + $mapMode) }
}
```

| 模式 | 验证内容 |
| --- | --- |
| `starvation` | 确认所有 CLR 工作线程已被占用后，仍返回有效中文标签 PNG；释放后继续正常服务。 |
| `protocol` | 多个缩放级别的有效 PNG，以及非法路径、方法和越界坐标的拒绝。 |
| `idle` | 半行连接超时关闭，正常请求仍可完成。 |
| `batch` | 8 个同时请求、20 个连续请求及 16 条日志采样上限。 |
| `multi-idle` | 3 个半行连接存在时，正常请求仍可完成。 |
| `trickle` | 每 250ms 发送一个字节，连接仍按首行总预算关闭，之后可正常请求。 |
| `pressure` | 额外压力验证：96 个空闲连接受到队列上限约束，过期连接清理后可恢复正常请求，服务线程数不随连接数增长。 |

测试客户端为超时检查留有系统调度容差；1500ms 是服务端预算，不能理解为实时系统的精确关闭时刻。离线测试不代替实际游戏中的地图显示验证。

## 回归结果（2026-10-18）

修复前的单线程服务通过原有四个模式，但新增回归确实失败：三个半行连接使正常请求等待超过 3 秒；每 250ms 发送字节的连接没有按首行总预算关闭；96 个空闲连接未受到应用层等待队列上限约束。

修复后，六个核心模式及额外 `pressure` 模式全部通过。该轮慢速连接约 1515ms 关闭；压力测试中 28 个连接提前关闭，96 个连接最终全部关闭，服务进程线程数保持 13→13；清理后正常瓦片请求恢复。所有测试均在独立 Framework 进程执行，未启动游戏或安装器。
