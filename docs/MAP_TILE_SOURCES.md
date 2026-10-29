# 地图标签数据维护

地图源采用 `split-map-regions-v1`。`dict/placenames/tile_regions.json` 只保存公共设置和有序文件索引，不再内嵌全部区域。

| 路径 | 内容 |
|---|---|
| `dict/placenames/map_tiles/regions/<区域ID>.json` | 原名、中文、排版、符号、跨区关系和原图引用；每个数组元素一行，坐标保持行内 |
| `dict/placenames/map_tiles/tiles.jsonl` | 每个原图路径一条：坐标、SHA256、来源；区域只引用路径，特殊上下文元数据留在引用上 |
| `dict/placenames/map_tiles/sources.jsonl` | 每个来源 URL 一条，ID 为 URL 的 SHA256 前16位；记录中的 `@source/<ID>` 引用此表 |
| `dict/placenames/map_tiles/reviews.jsonl` | 每区一条既有审核结果，绑定完整展开输入指纹和实际 RGBA 指纹 |

译名仍含来源及对应原文记录的指纹。同名异地、同名不同缩放、不同原图印次不能按名称合并。公共原图字段发生冲突必须查清来源，不能以最后写入覆盖。

长篇研究过程不再重复写入正式地图记录。`@note/<类别>` 引用索引中的公共短说明；确有必要的实体消歧或例外说明可以直接写简短文本。来源 URL、原图 SHA、几何数据和必要关联不得随说明一起删除。需要追溯迁移前的完整研究文本时，可查看提交 `bbab5df6a4e22f1157216c5d19de898f7aade155` 的原清单及对应历史证据。

## 编辑与构建

1. 修改对应区域文件；新增来源和原图时分别补公共表，复用已有条目。
2. 新区域追加到索引的 `region_files`，保留既有区域顺序。
3. 通过正常来源、译名和实际图像核验后更新审核表。拆分工具不产生审批，不会自动刷新指纹；数据修改后沿用旧指纹仍会失败。
4. 原构建命令的 `--manifest` 继续指向 `dict/placenames/tile_regions.json`。构建器自动展开引用，并在报告中列出全部 `manifest_inputs`。

Python 工具应使用 `map_tile_manifest.load_manifest(path)` 读取逻辑清单，使用 `manifest_files(path)` 取得完整输入闭包。不能只给索引文件做哈希，也不能直接用 `json.loads(index)['regions']` 读取全部区域。

旧分析工具暂需单文件时，生成临时兼容输入：

```sh
python -B scripts/map_tile_manifest.py expand \
  --manifest dict/placenames/tile_regions.json \
  --output build/map/tile_regions.expanded.json
```

输出必须是未存在的文件。展开结果是构建或分析产物，不得作为人工维护源重新提交。`split` 子命令可把已有完整逻辑清单拆到一个新位置，用于迁移或私有候选；它逐项保留原值，不会精简说明或替调用者审批。

## 重建与审核边界

源码重建包包括索引、所有区域文件、三张公共表、读取器、原图、字体和原有外部批准文件；可以搬迁后独立重建。既有单文件历史清单仍可读取，旧冻结包无需改写。

本次迁移只精简说明、共享重复数据和转换存储格式。名称、位置、字号、符号、原图、原顺序及既有 RGBA 指纹保留。说明参与过旧 whole 指纹，因此新的输入指纹与旧值不同；不能声称新清单与旧清单整对象相等。迁移前后绑定及重建证明保存在工作证据中。

引用 whole 或 symbol 指纹的外部批准必须显式做仅元数据的重绑定，不能假装旧 SHA 批准了新输入。重绑定不授予新增符号、改动几何或新增绘制的权限，原批准文件和历史成果包保持原样。

验证入口：`scripts/test_map_tile_manifest.py`、`scripts/test_build_map_tile_pack.py`、`scripts/verify_routing.py`。数据迁移还须完整重建并与既有 `.dat` 逐字节比较；不能以计数或非空图片替代。

## 原有 WWP 地名补绘

原图未印出的游戏原有地名可单独记录在区域的 `canonical_captions` 中。记录保留原始 WWP 文件 SHA、记录序号、中文、float32 坐标位、flags 和 level；`source_bbox`、`source_png`、`source_native` 均为 null。这类记录只补绘原有中文文字，不作为原图墨迹、海拔或原生符号入账。

构建时通过 `--canonical-caption-approvals` 和 `--canonical-caption-approvals-sha256` 显式传入选择文件。其区域、缩放、caption ID 和完整正文指纹必须精确覆盖本次输入。正式整图选择使用 `root-approved-canonical-WWP-caption-v1` 的 `full_current` 角色，并分别绑定模型、实际审核清单、审核结果及独立目标渲染签证。构建器检查引用形状与原始 WWP 成员，调用方须先核验这四项证据的实际内容；单独提供引用不能代替来源与图像审核。

完整原字、越界、遮挡、接缝、旧区域和原符号保留仍需实际核验。正文或布局变化会使旧选择失效，须重新选择与审核。源码重建包保存选择文件、原 WWP 和完整拆分清单，使补绘结果能够独立重建。测试用的 `TEST_ONLY` 及私有有限预览的 `named_private` 角色不能代替正式整图审批。
