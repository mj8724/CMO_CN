# 中文地图标签绘制

地图地名统一使用像素单位绘制。城市默认 em 为 14px，国家和海域按缩放级别使用 14、15、16px；输出仍为 256×256 透明 PNG。运行时本地标签服务和安装器缓存重绘编译、调用同一份 `src/shared/MapLabelRenderer.cs`。

## 绘制规则

- 使用 `GraphicsUnit.Pixel`，按 `GraphicsPath.AddString` 的实际字形路径计算范围，避免用 Point 字体测量后再按像素路径绘制。14px 是逻辑 em，并不表示每个汉字的可见高度恰好为 14 个像素。
- 城市使用微软雅黑常规字重，国家和海域保持粗体；正文为不透明白色，深色圆角描边宽 2px。路径抗锯齿使用 `SmoothingMode.AntiAlias`。
- 居中、边距与避碰均包含描边的实际范围。国家和海域优先，同类别保持 WWP 输入顺序；高缩放级别的离线缓存不再漏掉国家标签，与运行时规则一致。
- 过长名称只在轮廓超出可用瓦片时等比缩小，保留完整文字；描边仍为 2px。边缘标签向瓦片内部夹取，源 WWP 名称、坐标和 flags 不变。
- 空瓦片直接返回透明图片，不创建字体、画笔或字形资源。非空调用的绘制对象由本次调用独占并及时释放。

本次不改变地图瓦片传输线程、地形底图或最终 GPU 纹理尺寸，不包含双倍离屏采样。密集地区仍会跳过碰撞标签，超长名称的实际字号可能低于 14px，因此不能保证所有地名同时可见。

## 验证

- 核心几何回归 22 项：实际中文字形大小、像素居中、四角描边边界、真实最长名、极端长名、碰撞、稳定顺序、缩放选择、透明空瓦片及运行时/共享 PNG 一致性。
- 缓存专项 55 项、真实 CLI 缓存集成 4 项：包含城市、国家、空瓦片及真实最长名的离线/共享 PNG 对照，保留已有的失败保存计数、完整备份发布和安全重试保护。
- 同一宿主环境可逐字节比较 PNG；跨进程 DPI 上下文可能改变 PNG 的 pHYs 元数据，判断绘制是否一致应比较解码后的 RGBA 像素。

复跑时先按既有 `scripts/build.sh` 编译引擎，再执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_map_label_renderer.ps1 -Engine build/CMOLocalizer.dll
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_refresh_placename_tiles.ps1
python -B scripts/test_placename_cache.py -v
python -B scripts/test_placenames.py -v
python -B scripts/test_verify_placenames.py -v
```

缓存专项与 CLI 集成应串行运行，避免临时构建的新 DLL 差集探测互相干扰。发布、回归及缓存刷新编译入口均包含新的共享源文件。

## 成本与应用

此前同机固定样本的历史离线测量中，渲染加 PNG 编码的 CPU 批均值中位数由 1.953 增至 2.372ms/张，约增加 21.4%。每组 11 批、每批 224 次；旧基准是重编的旧绘字逻辑，不是实际 E099 旧引擎的性能对照，也不是本次 PR 分支的新计时或游戏 FPS 测量。较大文字可能使密集视角显示更少地名，PNG 存储和生成 CPU 成本需一并考虑。

已有 14px 直接绘制版本曾从 Steam 启动验证；本 PR 的共享绘制实现与该版本相同。历史截图和性能数据只支持对应固定视角、机器和输入，不保证其他显示器、硬件或全部场景的效果。

应用字体修改需部署新版引擎并重绘 `WW/Cache/Stamen Terrain - Labels`，退出后重启游戏加载。安装器和地名刷新脚本使用相同的缓存绘制逻辑；只更新 WWP 或只替换引擎都可能留下旧缓存。修改前应保留当前引擎、配置和标签缓存备份，原英文 `.orig` 备份继续保留。仅运行 `apply_placenames.py` 不会替换游戏引擎。
