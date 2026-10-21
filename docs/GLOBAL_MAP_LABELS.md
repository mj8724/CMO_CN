# 全球地图标签补回：数据契约与验收边界

当前工作未完成“与原版地图一致、全部中文、无遗漏”的验收。
全球完整目录仍被全量门禁阻断；现已另外接入逐区核验的中文瓦片包，第一批实际数据已构建并通过双入口隔离验证。已安装游戏尚未部署。

## 为什么不能只扩充 WWP

原有 Cities 4,287 项和 Synoptic 321 项不是原版 PNG 标签的完整名称表。
旧渲染器在透明画布上只画这两个集合，另外还有 `z <= 5` 隐藏城市和碰撞直接跳过的规则。
因此，原版中的省州、水体和额外城市会消失；即使名称在目录中，也可能在对应缩放下不显示。
原 WWP 的数量、顺序、坐标、flags 和固定元数据指纹继续由原门禁验证，不能靠追加记录绕过。

## 已取得的来源和缺失材料

- [terrain-classic 固定 commit](https://github.com/stamen/terrain-classic/tree/d6bf46afefe4dc0b4959b4fc70877b44e72722cb)：静态候选共 104,768 条，其中城市 98,206 条。
- [城市样式](https://github.com/stamen/terrain-classic/blob/d6bf46afefe4dc0b4959b4fc70877b44e72722cb/labels.mss#L648)按 rank、人口筛选，只有 41,400 个城市点在公开样式的 z3–10 内至少一次合格。该数字不是已证明的 CMO 原版分母。
- 实际原版 PNG 中，Kisii、Khilok 的观察缩放与公开样式不符。原图证实的缩放必须保留，公开样式未选中的其余源记录仍须列入显示范围未确认清单。
- [水体和公园定义](https://github.com/stamen/terrain-classic/blob/d6bf46afefe4dc0b4959b4fc70877b44e72722cb/terrain-classic-labels.yml)：历史 OSM 水体 z9–10、公园 z8–10 数据库未获得；海域在公开 labels 变体中也与原版图片不符。
- [Natural Earth](https://www.naturalearthdata.com/about/terms-of-use/)提供公开领域的中文国家、省州、海域和主要城市候选；当前数据不等于历史冻结快照，也不能用实体中心点代替原标签锚点。
- [GeoNames](https://download.geonames.org/export/dump/readme.txt)及其中文别名按 CC BY 4.0 使用。匹配与歧义检查必须包含没有中文的同名近邻实体。自动关联只标为 `source_candidate`。

目前不能把 112 个已证实体、7,328 张本机原版缓存，或上述公开数据的计数，写成全球完整证明。

## 目录与译名分离

`scripts/global_map_catalog.py` 读取三份输入：

1. manifest JSON：schema 版本、z3–10、来源 URL/文件 SHA256/许可、逐图层来源与完整性证据、清单 SHA256、快照和显示验收状态。
2. inventory JSONL：每行一个源标签实例，字段为 `id, source_id, name, kind, lat, lon, min_zoom, max_zoom, priority`。额外来源字段可保留；同名不同地点不能去重。非连续缩放分成不同实例，不能用首尾范围填洞。
3. translations JSONL：`id, zh, status, source, record_sha256`。译名指纹使用 `record_fingerprint(inventory_record)`，原记录改动后必须重审。

新增人工译名保存在 `dict/placenames/global_reviews.jsonl`，用 `--reviews` 覆盖对应的候选输入，
不直接修改可重建的 translations。每项还须有非空 `review_notes`；未知 ID、失效指纹、非中文、候选状态均直接拒绝。
当前人工增补依据[外交部加拿大行政区划](https://www.mfa.gov.cn/gjhdq_676201/gj_676203/bmz_679954/1206_680426/1206x0_680428/)
审核了加拿大十省的中文名称，对应 104 项原图观察版本中的 11 个缩放实例。
这只批准名称，不批准标签锚点或整张地图一致性；不能把这份审核表套到缺少相同 ID 的旧 94 项观察版本。

图层为 `city/admin0/admin1/marine/water/park/peak/region`。未取得的图层必须在 manifest 里明确 `complete: false`，而不是从分母删除。
全部中文、逐条审核和完整来源是不同条件。只有 `approved`、含汉字且没有其他文字体系、绑定原记录并附来源的译名，才能编译。
Han 字符范围固定为 Unicode 15，Python 和 .NET 4.8 共用同一范围，避免 Unicode 表版本不同而生成不可读取的目录。
数字和地名分隔符也使用固定允许集合，不依赖两边不同版本的 Unicode 分类表。

来源导入由 `scripts/import_stamen_map_catalog.py` 完成。`--catalog`、`--source-summary` 指向固定来源导出；
`--city-matches` 可同时读取完整 matched/untranslated 两份城市表，`--other-matches` 读取其它类别候选，
`--confirmed-matches` 加入原图已证的缩放例外。`--output-dir` 生成下列审计输入及 `unresolved_visibility.jsonl`。
导入器始终把快照/显示/全部图层标为未验证，不能用导入成功代表全量恢复。

2026-10-21 的冻结导入核对结果如下；这些是可获得来源的候选范围，不能当作原版游戏缺失总数。

| 项目 | 数量 |
|---|---:|
| 静态来源原记录 | 104,768 |
| 公开样式或原图证据选中的源记录 | 47,576 |
| 非连续缩放拆段后的目录行 | 47,589 |
| 显示范围等仍未决、完整保留的源记录 | 57,192 |
| 选中源记录中尚无中文 | 18,795 |
| 复用既有审核或新增文字审核已批准的目录行 | 2,213 |
| 仅有未审中文候选的目录行 | 26,578 |

独立核验 `104,768 = 47,576 + 57,192`，两集合无重叠；105 个能关联到该源的原图已证实体的观察缩放都保留。
包括 Smithers 原图 z6 和公开样式 z10，不能填成连续 z6–10。七峰另有历史 OSM 来源，未伪造为 Stamen 记录。
这仅证明导入保留性，不证明新地图已经显示这些标签。47,589 行中仍有 45,376 行缺译或待审，15 类来源/显示阻断未解除。

```powershell
python scripts/global_map_catalog.py `
  --manifest build/global-map/manifest.json `
  --inventory build/global-map/inventory.jsonl `
  --translations build/global-map/translations.jsonl `
  --report build/global-map/audit.json `
  --output build/global-map/ChineseMapLabels.dat
```

缺译或待审项会完整出现在 `translation_gaps`；来源/快照/显示问题出现在 `source_gaps`。
需要人工增补时在上面命令附加 `--reviews dict/placenames/global_reviews.jsonl`。
`required_display_text` 可声明不能丢失的文字成分，例如 `4344米`；名称有中文但高度省略、数值错误或符号错误也会阻断。
公开峰源的 `source_properties.elevation` 仅提供待核的要求，`height_basis` 明示尚未与原图核对，不能把该值直接当作原海拔。
任意问题使退出码为 1，已有二进制产物不被覆盖。输入结构错误直接报错。
manifest 的完整性签认是受信的审核输入，工具无法替代对历史来源和原版图片的实际调查。

## 二进制格式与读取

`CMOShared.MapLabelCatalog.Load(path)` 返回 payload `Fingerprint` 和 `Places`：

- 8 字节 ASCII `CMOMAP01`。
- .NET 7-bit UTF8 字符串：后续全部 payload 的小写 SHA256。
- payload：Int32LE 条数；每条依次为 .NET UTF8 中文名、SingleLE 纬度/经度、Int32LE minZoom/maxZoom/priority/level。
- `level=0` 表示大地名字样，`level=1` 表示城市。priority 越小越先尝试绘制，同优先级保持输入顺序。

SHA256 是内容版本和损坏校验，不能证明原版覆盖。新增显式范围不会再自动套用旧城市 z<=5 隐藏规则。
旧 WWP 输入仍由原路径使用；本阶段的测试证明其像素没有改变，不能将该测试当作缺失已补回。

## 全量启用的剩余条件

1. 确认官方冻结快照、实际样式变体，以及包含历史 OSM 水体和公园的完整分母。
2. 完成每个属于实际地图范围的名称、显示别名与中文审核，清零歧义和未译。
3. 验证每个原版可见名称在同缩放、相邻瓦片、日期线、密集区域和长中文情况下仍然可见。当前碰撞跳过规则尚未满足这一条件。
4. 运行时与安装器接入同一完整目录，按指纹刷新旧缓存并保留 `.orig`；完成实机前后对照。

## 后续现有资料核查

- 加拿大十省、Smithers 一城以及七座山峰新增原图/当前中文缓存对照，逐图确认的缺失下限从 94 扩至 112 个名称实体。
- 7,328 张原图都已登记 SHA/alpha 并复用已有 OCR。其中原图非空 3,415 张；1,569 张在当前中文缓存中完全透明。这是瓦片数，不是名称数。双方非空的 1,846 张仍须逐标签核对。
- 自动名称/地理交叉候选 3,351 条，透明原图过滤后保留数量不变；原始 4,634 段未决文字另存原样，过滤后保留 4,520 段、拒绝 114 段。93 张透明原图曾被 OCR 分配 135 词，另有 448 张非空原图 OCR 为零词，说明 OCR 不能证明原版没有文字。这些自动结果均未进入逐图确认名单或已审目录。
- 七峰除名称还带原海拔 `4145/4269/4149/4005/4138/4421/4344 m`。历史 OSM node 版本的名称、坐标和海拔与原图吻合；当前 OSM/NE 有些数值已不同，不能以新值覆盖原图值。
- 山峰三角符号、多行布局和跨瓦片放置尚须实现/验证。惠特尼峰文字在一个瓦片、峰符号在相邻瓦片，单点在所属瓦片居中绘字不能保证原版布局。
- 针对已知候选中 26,070 个缺中文城市，现有 GeoNames、NE、Wikidata 及既有显式链接已做有界全量补查：10,351 个找到待审中文路径，784 个有身份/译名冲突，482 个当前身份未匹配，14,453 个在已查询路径仍无中文。该结果不是“世界没有中文名”，也没有自动晋升为已审核。
- 在双向身份、英文名、双坐标 5 km 与纯中文约束下，最终仅纳入 8,245 个新候选（Wikidata 8,241、GeoNames 4），新增自动批准为零。错误主体 QID、同名医院和 NE 错 GNID 等冲突完整保留；选中城市源记录的缺中文数从 26,070 降至 17,825。

以上调查与候选产物独立保存，未对已安装游戏生效，也不能据此宣称全游戏无遗漏。

验证命令：`python scripts/test_global_map_catalog.py`、`python scripts/test_import_stamen_map_catalog.py`、`python scripts/verify_routing.py`、`powershell -File scripts/build.ps1`、`powershell -File scripts/test_map_label_renderer.ps1`。
这些离线测试通过只代表本阶段代码可用。

## 已实际构建的有限区域恢复

`dict/placenames/tile_regions.json` 保存独立原图要素、中文、原海拔、图标与完整布局。每区审批同时绑定除review以外的记录SHA和实际RGBA像素SHA；字体或栅格改变须重审，不能沿用旧审批。源图SHA、字体SHA、原图alpha记账、严格中文、字形、裁切、碰撞与审批均由 `scripts/build_map_tile_pack.py` 检查。原图机械记账不能替代对框内每一项的人工辨认。

第一批7个区域含15张瓦片、30处名称实例（其中13处带原海拔）、23个原图符号。包括加拿大省名/城市、内华达山脉与邻近区域的九座山峰及毕晓普。独立视觉审查发现的两个人工峰锚点已按原图框中心修正后重审。15张中12张旧汉化全透明，另3张双方非空；这些计数不等于全图已完成或30个全新实体。

正式 `assets/placenames/ChineseMapTiles.dat` 通过相同生产reader、运行时RenderChineseTileBytes、安装器LocalizeMapLabelTiles与隔离缓存110项实数据检查。15张均逐字节一致、重复应用0写、删除一张后恢复1写；原有 `.orig` 逐张SHA不变；7区重新拼接的模拟缓存RGBA与独立批准atlas一致。当前有跟踪的资产不代表已写入本机游戏。

### 重建已审核瓦片

安装 `scripts/requirements-map.txt` 中的离线工具依赖。字体不随仓库分发，使用本机 `C:\Windows\Fonts\msyh.ttc` 与 `msyhbd.ttc`，必须匹配清单所钉SHA。按每区 original_tiles/context_tiles 的source_url取得PNG，放到同一 source root 下相应path；只能按清单逐张取图，构建器会复核每一SHA。可从已有原版 `.orig` 只读复制同路径图；context不算已恢复覆盖。

```powershell
python scripts/build_map_tile_pack.py `
  --manifest dict/placenames/tile_regions.json `
  --original-root build/map-originals `
  --font C:\Windows\Fonts\msyh.ttc `
  --bold-font C:\Windows\Fonts\msyhbd.ttc `
  --output build/ChineseMapTiles.dat `
  --report build/map-tile-audit.json `
  --preview-dir build/map-tile-previews
```

只有ready=true时才原子写output，失败保留旧产物。更换Pillow/FreeType版本可能改变字形像素，旧审批会阻断输出；必须重新目视审核，不能自动改render_sha256来解锁。

瓦片包为 `CMOTIL01` 或带精确旧WWP排除规则的 `CMOTIL02`，与全球点目录 `CMOMAP01` 不同。安装器和运行时共用严格校验/渲染入口；精确命中的包瓦片原样返回，不再次经过旧的低缩放隐藏或碰撞丢弃。包外暂沿用旧渲染，仅排除经审核关联的同zoom精确旧条目；因此尚未覆盖的缺失仍然存在。正常build嵌入MAP_TILES、绿色包/部署脚本复制同一文件，卸载先删除包再还原原图。现阶段只运行隔离fixture，未执行发布/部署整链。跨区实例和失败标记规则见 `PLACENAME_LABEL_RENDERING.md`。

后续逐批扩大 `tile_regions.json`，每批独立核验原图、译名和实际像素，不能把候选自动标成approved。全部1,569张原图非空/中文透明、1,846张双方非空及未缓存区域仍是完整验收范围，R53未闭环。

## 当前可运行数据快照

已逐项独立审核142区域、5503张网格瓦片：8401处中文名称、10处仅海拔标签、67处原海拔和3114个原版符号（含2个无名符号）。名称与仅海拔标签合计8411，两类分别统计。源非空3599张、透明1904张；透明项用于矩形闭合或旧错位清理，不计名称恢复。

冻结1569张原图非空/中文全透明中覆盖1191张，尚余378张；1846张双方非空中覆盖1148张，尚余698张。另外1234张为冻结基准透明图，1930张为新取得原图（其中1260张非空）。5503 = 1191 + 1148 + 1234 + 1930；3599源非空 = 1191 + 1148 + 1260。这些数值只表示已核验范围，全图目标仍未完成。

本轮变化：新增896瓦片、1280处中文名称，补入希腊西部、菲律宾、埃及、缅甸、伊朗及华南等已独签内容。全部2843旧符号经三组逐原PNG复核，914处按完整可见原栅格支持框中点修正，1929处保持，形状与大小不变；仅伊宁县中文右移3像素避碰。春武里、瓦达和奔巴等跨区原实例保持唯一绘制归属并补齐真实原圆，新271符号单独按原图审核。未知原矢量亚像素中心不作断言。包内8411显示实例全中文检查通过，数字海拔和原数名另列，不采用原文兜底。全图尚未完成，剩余与已签待合批次继续单列。

独立Python完整重建与正式包逐字节一致；逐区原文、中文、来源、原符号、字体、布局和实际RGBA审批有效。665条精确canonical旧WWP规则按同zoom、中文名、float32坐标及level匹配，避免同实体旧点重复；合法同名异地和多placement不按名称合并。共享引用只归账原实例，唯一owner实际绘制中文。

实际C#5 MAP_TILES内嵌资源、runtime/installer/缓存和完整4608条WWP共64752检查通过；142区两入口重拼RGBA均等独立批准图。18张已存在包外缓存逐项验证精确过滤及其他标签保留；完成标记失败/重试、原.orig逐SHA、terrain、重复写入和缺图/损坏修复均通过。正式包SHA：`6f997d9309261a71e646924298f569a1b02194c523e0c96ce789119a3e18ca84`。全部写操作仅在工作区隔离fixture，未写已安装游戏、未执行主安装/卸载、Bash发布整链或发版。未覆盖原缓存及未缓存世界仍需核验，R53保持未闭环。
