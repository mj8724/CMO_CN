# 全球地图中文补回 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 将原版地图名称逐项关联中文，并在来源、译名、显示验证齐全后才生成可用的全球目录。

**Architecture:** 原有 4,287 城市和 321 概览 WWP 保持数量、坐标、flags、顺序不变。全球标签另建带稳定来源 ID、类别、坐标、缩放范围的目录；审计与编译共用一份分母。全量来源或中文审核不完整时构建失败，不能输出中文子集或英文兜底。

**Tech Stack:** Python 标准库、C# 5 / .NET Framework 4.8、现有共享地图渲染器。

## Global Constraints

- 用户要求全部中文，未译名称也须补译。
- 工作在 master，按可验证的小步提交；不发布、不改版本、不写已安装游戏。
- 不直接改生成词典；不增加原 WWP 记录；新文件同时登记 ROUTING。
- 公开 Stamen commit 与官方冻结瓦片存在样式差异，历史 OSM 湖泊和公园来源尚未获得。该事实必须阻断“全量完成”声明。

### Task 1: 目录审计和构建阻断

**Files:** 新增 `scripts/global_map_catalog.py`、`scripts/test_global_map_catalog.py`；更新 `docs/ROUTING.md`。

**Interfaces:** CLI 接收 `--manifest`、`--inventory`、`--translations`、`--report`，可选 `--output`。输入 JSONL 保留所有原版 ID；审核译名按 ID 关联。输出审计 JSON；仅当全部通过时原子生成 `CMOMAP01` 二进制目录。

- [x] 先写缺译、半译、候选未审、重复 ID、未知来源、遗漏图层、来源指纹变化、未确认快照、未完成显示验收的负例；失败不能破坏已有产物。
- [x] 运行 `python scripts/test_global_map_catalog.py`，确认新脚本缺失导致断言失败。
- [x] 实现严格审计、确定性排序和 .NET 字符串编码；运行同一测试确认通过。
- [x] 运行 `python scripts/verify_routing.py` 后提交该完整增量。

### Task 2: 可复现的来源清单与中文候选

**Files:** 全球来源导入脚本、来源清单、缺译工作表；批量中间数据放在 `build/`，交付证据另存。

**Interfaces:** 每条原标签保留 `id, source_id, name, kind, lat, lon, min_zoom, max_zoom, priority`。译名另存 `id, zh, status, source`；仅 `approved` 且纯中文可进入编译。GeoNames/NE 自动关联保留候选身份，不等于人工审核。

- [x] 固定 Stamen commit、源文件 SHA 和许可证，导出每条城市和其他可获得类别。
- [x] 用现有审定 WWP、NE 和 GeoNames 中文关联，检查同名近邻歧义，输出全部未译项。
- [x] 按样式解释源 rank；不能把文件名 z4to10 或包含 rank11/12 的全文件直接当作 CMO 可见分母。
- [x] 用 Task 1 对真实数据跑负向验收，确认历史 OSM/快照未知与缺译都会失败。

### Task 3: 共享目录读取和显示范围

**Files:** 修改 `src/shared/MapLabelRenderer.cs`、`src/tests/MapLabelRendererTests.cs`。

**Interfaces:** `MapLabelCatalog.Load(path)` 读取并验证 `CMOMAP01`。每条名称可明确配置 min/max zoom 和 priority；旧 WWP 默认渲染行为保留。Python/C# 互读和坏包负例必须通过。

- [x] 先为低缩放城市、明确缩放范围、目录损坏编写失败测试。
- [x] 实现读取与范围/优先级支持，运行引擎构建和位图测试。
- [x] 审查、路由校验后提交；资料未完整前不接入已安装游戏。

### Task 4: 全量启用前置条件

- [ ] 获得与官方冻结服务相符的全图层语料，特别是 OSM 水体/公园和正确标签变体。
- [ ] 所有属于实际 CMO 范围的名称均有审核中文，歧义清零。
- [ ] 逐原版可见标签核对同缩放和相邻瓦片；碰撞过滤不得被当作可忽略的缺失。
- [ ] 保留山峰原海拔/单位、显示别名、符号、多行与跨瓦片布局；不得以当前数据库新值替换已证原值。
- [ ] 运行时与安装器使用同一完整目录，按目录指纹刷新旧缓存，保留 `.orig`。
- [ ] 只有以上证据齐全后才能宣布全量补回；目前尚未达到该条件。

### Task 5: 新译名的审核源与显示成分守卫

**Files:** `scripts/global_map_catalog.py`、`scripts/import_stamen_map_catalog.py` 及各自测试；新增 `dict/placenames/global_reviews.jsonl`。

- [x] 为 `--reviews` 写正负用例；确认新增审核可替换候选、过期/重复/未知 ID 不覆盖旧产物。
- [x] 实现审核源合并并记录其 SHA，加入加拿大十省的 11 个已核对中文实例；审核文字与锚点/整体显示状态分开。
- [x] 以实际山峰丢失的海拔为依据，先写名称有中文却省略/改动高度的失败用例，再实现必需成分与数值边界校验。
- [x] 运行两个 Python 测试文件共 24 项、路由与差异检查；按完整增量提交。

## 2026-10-21 实际恢复续作

用户已明确要求实际修复，并批准以原图、中文补译、双入口接入和缓存重建推进。全球完整验收约束保留；新增有限范围的已核验中文瓦片，可逐批实际恢复，不把局部结果称为全球完成。原 WWP、未覆盖区域和原版备份不被当作新完整目录。工作继续在 master。

### Task 6: 已核验中文瓦片的双入口与缓存接入

**Files:** 修改 `src/shared/MapLabelRenderer.cs`、`src/engine/CMOLocalizer.cs`、`src/installer/CMO_Chinese_Setup.cs`；新增 `src/tests/MapLabelTilePackTests.cs`、`scripts/test_map_tile_pack.ps1`，登记 ROUTING。变更 BUG_REGISTRY 由控制者在审查完成后追加。

**Interfaces:** 在现有 shared 文件内新增 `CMOShared.MapLabelTilePack`，与旧 renderer 一起编译，不引入额外第三方依赖。`Load(string path)`、`Fingerprint`、`TryGetTile(int z, int x, int y, out byte[] png, out int labelCount)`、`ApplyToCache(string gameDir)`。新 `MapLabelRenderer.RenderTileBytes(IList<MapLabelPlace> places, MapLabelTilePack pack, int z, int x, int y, out int candidates, out int rendered)`：命中完整核验瓦片时原样返回其 PNG 字节，否则沿用旧 renderer，旧行为像素不变。

**Binary contract:** `ChineseMapTiles.dat` 为 8 ASCII magic `CMOTIL01`，随后 .NET 7-bit UTF8 string（后续 payload 小写 SHA256），payload 为 Int32LE 正条数；每条依次 Int32LE `z,x,y,labelCount,pngLength`，随后 PNG bytes。z3–10，坐标范围合法，不允许重复坐标；labelCount 非负；PNG 必须可解码且为 256×256。条数/长度/哈希/尾随字节错误全部拒绝。哈希仅防损坏，不证明中文或内容覆盖；离线构建器负责中文、来源和范围验收。TryGetTile 返回不可修改内部状态的字节副本。

**Runtime:** 启动地图服务时从游戏根目录读取可选 `ChineseMapTiles.dat`；不存在时行为不变。存在但损坏应记录明确错误并终止地图服务初始化，不能静默当成不存在。加载后调用 ApplyToCache，确保已有透明缓存也得到更新。RenderChineseTileBytes 保留现有反射测试签名，调用新的共享 bytes 入口。可选完整 `ChineseMapLabels.dat` 只有通过既有全量编译的产物才使用，读取后替代旧 WWP，不混合重复实例。

**Installer/offline:** LocalizeMapLabelTiles 读取相同 pack，使用相同 bytes 入口；先枚举既有缓存，叠加 pack 覆盖的新瓦片路径，避免只修旧文件而漏掉跨瓦片输出。返回实际写入总数；refresh 脚本原有无 pack 用例契约不变。安装主程序资源/发布包暂不改，后续离线成果提供数据文件，禁止 package.sh、改版本、发布或写实际 E: 游戏。

**Cache:** ApplyToCache 只写 pack 精确列出的 Labels 路径与指纹状态，不碰 terrain/WWP；原有 `.orig` 永不覆盖。首次备份须完整复制已有 Labels 缓存到同级 GUID staging 后原子发布 `.orig`，不能创建不完整可信目录；空/缺失旧缓存可以备份为空。拒绝穿过 reparse point。每个新 PNG 使用同目录临时文件后原子替换；失败不得发布完成指纹。重复调用不重写相同字节，目标被删除或损坏时须恢复，不能只信指纹 marker。指纹文件位于 Labels 缓存根 `.chinese-map-tiles.sha256`。不删除别的缓存文件，不需要清空整个地图。

- [x] 为 pack 格式损坏、PNG尺寸、重复tile、白名单范围、返回字节不可变、缓存首次备份/重复/源指纹改变/损坏修复和 terrain 哨兵写失败测试。
- [x] 运行负例确认当前缺功能，最小实现共享读取与双入口；使用模拟目录，无实机写入/进程操作。
- [x] 构建引擎、位图与新 pack 测试；原无 pack 用例保持通过；审查后提交完整增量。

### Task 7: 原图有限范围目录与实际中文瓦片

**Files:** 新增 `scripts/build_map_tile_pack.py`、`scripts/test_build_map_tile_pack.py`；新增 `dict/placenames/tile_regions.json`，登记 ROUTING。图像和临时结果放 build/work，正式交付放 outputs。

**Interfaces:** manifest 定义同 zoom 的矩形 tile region，原图 tile SHA、文字/符号逐项原记录与中文、atlas 像素位置、文字大小/对齐/类型、原图海拔与独立审核记录、明确有限覆盖范围。以原图及相邻图完整核对后标为 approved；不能把 OCR 自动候选直接视为覆盖证明。使用 Pillow/微软雅黑重建透明标签层，保存每个中文名与海拔，支持多行/城市圆点/山峰三角、跨瓦片裁切。输出 Task 6 格式及 SHA 审计；纯中文校验复用 global_map_catalog；缺译/未审/源图片变化/跨区不完整/布局截断应拒绝。原始语料、审查证据和未决问题保留，不能改全量 gate 的 ready 条件。

**Builder scope and exact schema:** 本 Task 7 的代码增量只实现构建/审计器与测试，真实数据由控制者独立核对后添加，不能把草案直接晋升。新增 `scripts/requirements-map.txt` 声明离线构建依赖 Pillow 与 fonttools（地图运行时不依赖 Python）。CLI 参数 `--manifest`、`--original-root`、`--font`、`--bold-font`、`--output`、`--report`，可选 `--preview-dir`。源码复用 `global_map_catalog.chinese_name/http_url/record_fingerprint/dotnet_string`；二进制严格匹配 Task 6。`compile_manifest(manifest, original_root, font_path, bold_font_path, preview_dir=None)` 返回 `(binary_or_none, report)`，失败 ready=false 并完整列出 gaps；输入严重结构错误可 ValueError。CLI 仅 ready=true 时原子替换output；失败必须保留既有产物。不提供跳过中文/审核或英文兜底开关。

manifest 顶层 `schema_version:1, scope:{type:"cached_regions",global_complete:false}, font_sha256,bold_font_sha256, regions:[...]`。每个 region 为：`id,z,x,y,width_tiles,height_tiles,original_tiles,context_tiles,features,translations,layouts,symbols,review`。`original_tiles` 必须精确覆盖矩形，项为 `x,y,path,sha256,source_url`；context_tiles 可为空，仅提供源文字跨边界的判读来源，格式相同，不声明覆盖context。路径相对 `--original-root`，必须保持在根内，逐图SHA和256×256验证。region不得重叠相同tile。region大小应合理有界，z3–10/坐标范围/正宽高/有限数字/ID唯一均验证。

`features` 是独立原文要素分母，每项 `id,kind,original,original_bbox,source_url`，kind为city/admin0/admin1/marine/water/park/peak/region；peak另有原图 `original_elevation_m` 字符串。bbox为atlas像素 `[left,top,right,bottom)`，允许因原文跨区域边界而部分超出，但须与region相交。`translations` 每项 `id,zh,status:"approved",source,review_notes,record_sha256`，按源feature的record_fingerprint绑定；ID集合必须与features完全一致。`layouts` 每项 `id,x,y,font_size,font_style,lines?`，ID集合亦完全一致，x/y为实际多行字形块的中心像素，font_style仅regular/bold，font_size为像素；可选lines的字符串拼接必须等于zh，不能偷偷省字或改值。peak名称后由构建器自动追加单独一行 `{original_elevation_m}米`，不得允许layout自行覆盖原值。所有汉字/数字/标点须在所用字体的Unicode cmap存在，拒绝缺字方框；fontTools TTFont/getBestCmap支持检查ttc索引0，字形与原文件/字体SHA一起写入report。font文件不复制入repo/交付（Windows字体许可），重建使用本机字体。

`symbols` 每项 `id,feature_id,kind,x,y,size,outline_width,original_bbox`，kind为circle/triangle，size为像素直径/三角外接尺寸，须关联已有feature；必须保留原图实际可见符号，不能只凭源点存在便新增图标。原文features+symbols的original_bbox在atlas上须覆盖全部原图 alpha>0 像素，若任何未记账像素则阻断并给出数量/bbox供复核；每一feature与symbol的框须实际覆盖一些alpha。此机械检查辅助发现漏项，不能替代人工对框内内容的辨认。符号黑填白边，文字白填深色边1px，透明背景，输出RGBA；字体采用微软雅黑常规/粗体，文字多行间距2px，可3–4倍超采样抗锯齿。任何文字/符号在region外被裁断或中文强像素重叠（不能默默丢弃）应列gap；tile内缝合法裁切，拼接须等于完整atlas。

`review` 为 `status:"approved",source_verified:true,render_verified:true,record_sha256,render_sha256,reviewer,notes`，record_sha256绑定region全部字段（除review），原文/译文/布局/来源图变化后都必须失效。render_sha256 必须绑定实际 atlas.convert("RGBA").tobytes() 的 SHA256；字体或栅格改变后旧审批无效，PNG 无损编码变化不必重审。所有 region ID/最终预览路径预先按 Windows 大小写不敏感检查冲突并拒绝设备名；不得先写坏独立审查证据。预览模式可输出中文结构正确但待视觉审核的draft atlas/png以便审核，report仍ready=false，不产生pack。每区report包含源alpha记账、输出每个标签/高度/符号bounds、PNG SHA、跨tile实例、覆盖tile、字体与输入SHA、全局完成=false；二进制 per-tile labelCount仅统计实际alpha与该tile相交的标签实例（高度属于该标签，独立图标不增加labelCount）。

**References already checked:** Pillow ImageDraw textbbox/text APIs — https://pillow.readthedocs.io/en/stable/reference/ImageDraw.html ; fontTools TTFont/getBestCmap — https://fonttools.readthedocs.io/en/latest/ttLib/ttFont.html 。相关源草案在 `../tile_recovery/canada_regions.json`、`../tile_recovery/peak_regions_draft.json`，是帮助理解的未审输入，不是最终正式schema，不能绕过review自行生产数据。

**Code-only acceptance:** 首先写source hash变化、原alpha遗漏、feature/layout/translation集合缺失或重复、非中文、错源指纹、未审核/改动review、peak原高度追加、lines省字、缺字体字符、重复tile/边界/强像素碰撞、跨tile拼接、.NET头及payload、失败保留旧产物、draft可预览而不可出包等负例并观察红灯，再实现。不必为每个机械字段写镜像测试，但关键门禁必须实际会失败。运行Python新测试+既有24项、routing及一次生成样例让Task6 reader读取；不部署、发版、改版本、不写真实游戏。完成独立增量后commit，真实数据的后续checkbox保持未完成。

- [x] 优先选有完整原图证据、可逐项审查的区域完成端到端真实恢复；必须包含加拿大省/城市、七峰与跨瓦片内容，不能只做合成样例。
- [x] 为编译格式、中文限制、原海拔、省略标签、边界裁切和原图 hash 写失败测试，再实现构建器。
- [x] 导出原图/当前汉化/新恢复并列预览，独立视觉审核首批每个原标签和符号；记录实际恢复实体/实例/tile 范围。
- [ ] 继续向全部已缓存 1,569 张空白瓦片及其相邻范围扩展，未核验的部分保留在待办，不能以 PNG 非空替代名称齐全。
- [ ] 完成逐区审查后提交译名与布局数据，不把未审候选写入生效资产。

### Task 8: 全地图来源与译名收敛

- [ ] 沿用全部 104,768 个源记录与剩余中文候选，补译所有未译名称并审查身份/坐标/缩放。
- [ ] 对原图 PNG 逐区对照，补齐水体、公园和额外峰源；用独立证据收敛官方冻结版本范围。
- [ ] 同时核对双方非空瓦片，补齐被密集标签/碰撞/跨瓦片吞掉的名称。
- [ ] 运行全范围审核、实际入口与缓存端到端测试，只有分母/中文/显示全部通过才宣称完整地图汉化。

### Task 9: 已核验瓦片资产进入正常交付链

**Goal:** 已批准的真实 `assets/placenames/ChineseMapTiles.dat` 随正常安装器、绿色包和部署脚本进入游戏根目录，随后由 Task 6 双入口读取；不能停留为手工放置才生效的文件。

**Files:** `scripts/build.sh`、`scripts/deploy.sh`、`scripts/package.sh`、`src/installer/CMO_Chinese_Setup.cs`、`src/shared/CMOShared.cs`；必要的隔离测试脚本/host须登记ROUTING。真实asset/manifest由控制者单独提交，实施者不批准数据。

**Contract:** build.sh 将 asset 以资源 `MAP_TILES` 嵌入安装器，存在正式asset时不能静默漏带；绿色包复制至根目录并加入覆盖断言；deploy.sh 原子复制并逐字节验证。安装器在 LocalizeMapLabelTiles 之前，通过可隔离测试的 helper，从安装器同目录（含既有patch_files布局）或嵌入资源获取数据；先使用 MapLabelTilePack.Load 验证候选，再原子替换游戏根目录 ChineseMapTiles.dat，错误必须传回安装失败。不可用坏包破坏已存在好包。旧测试/不带pack的独立fixture可保留可选无资源行为；正常build明确嵌入正式asset。不能从其他任意路径捡到过期pack。InstallManifest.HashEngineFiles 仅在文件存在时纳入 ChineseMapTiles.dat，旧两项契约不变；卸载删除中文pack并沿用原Labels .orig还原，随后不允许旧pack复活中文图层。不要加入假的 ChineseMapLabels.dat，也不生成/批准完整全球目录。

**Verification:** TDD 负例覆盖磁盘来源、嵌入资源、同路径、损坏候选保留旧包、安装调用先后与卸载pack清理。使用工作目录内模拟游戏，不运行 Install/Uninstall 全流程的注册表、进程或启动副作用。编译真实C#5安装器fixture与引擎，运行Task6 55项和legacy刷新测试确认不回退。现环境无Bash时明确说明shell脚本未执行，以可运行的内容/资源fixture验证补足；不能宣称发布链全量运行。完成后独立审查并提交完整增量，不执行build.sh全安装器发布名构建、package.sh、deploy.sh、改版本、tag或真实游戏写入。

- [x] 数据部署helper、损坏包与卸载的隔离失败测试。
- [x] 接入安装资源、绿色包、部署与安装清单；不运行发布/实机动作。
- [x] 独立审查、C#与既有回归通过后提交。

**实际进度记录：** Task6独立Approve；Task7代码经两项必要修复后独立Approve（67Python测试）；Task9独立Approve（46C#隔离检查、5脚本契约），shell整链未运行。首批真实7区15瓦片30名称/13海拔/23符号，独立视觉Approve，110C#实数据检查与7拼图RGBA核对通过，数据提交4844124。全量Task4/8及继续扩大覆盖仍未完成。

**后续有限数据进度：** 16区域128网格、113名称/13原海拔/65符号；原1569损失覆盖30、尚余1539。55源非空、73透明结构/清理。加拿大中部、中国/邻国、海域闭合子集均独立审批；白海欧洲整区暂缓。903项实际资源/双入口/缓存检查通过。全量Task4/8继续未完成，不能用有限包替代全球完成证明。

## 2026-10-21 最大并发续作

主agent与3数据agent分别负责整合、美洲、欧非、亚太；逐批提交，不能把阶段成果当全图完成。原WWP文件仍不改。

### Task 10: 跨区旧WWP精确去重与较大区域

现有瓦片只精确替换包内；同一原标签文字跨区域边缘，中文放回后会与包外旧WWP点重复。无止境扩矩形会递归引入其他国家/海域。本增量在已审核区域内记录明确关联的旧条目，以原中文名+float32纬经度+level+同zoom精确匹配，仅从旧WWP回退中排除；新的完整中文标签仍必须实际渲染。不能按同名泛删，IsLegacy=false的完整目录条目不受影响；原版合法不同placement保留。

region新增可选 legacy_suppressions 列表；每项 feature_id,name,lat,lon,level,source,review_notes。必须关联本区已审中文feature且确实有输出，原名/坐标/level必须逐位匹配仓库固定Cities/Synoptic WWP允许集，notes与来源齐全。整个列表纳入region指纹，修改旧关联须重审；此审批包括同一原placement身份判断，不能把同水体其他合法placement任意抹除。builder报告给出规则及旧点tile。无规则时保持CMOTIL01确定性字节；有规则时CMOTIL02，旧PNG payload之后Int32规则条数，每项Int32 z、.NET中文name、Single lat、Single lon、Int32 level，整个payload同SHA。重复规则可由多个原placement共引用但wire按精确key去重。

C#兼容01/02并严格验证02范围/中文/重复/尾部。RenderTileBytes包命中原样返回；未命中只过滤精确关联旧条目，原集合不修改。新增不同名ApplyToCacheWithLegacy(gameDir,places)，复用完整备份/原子写并在完成全部PNG和已有旧点cache刷新后才写marker；只刷新实际已有的包外旧点tile，保留其他旧名。原ApplyToCache签名不变。运行时加载WWP后调用新方法；安装器既有共享bytes路径同样过滤包外旧点，既有计数契约保留。失败不能吞掉，重复和损坏修复仍检查实际字节。

MAX_REGION_TILES从64提高到256（宽高仍各≤16），MAX_TOTAL_TILES从4096提高到16384，允许真实66/99格大区闭合；保留合法边界与内存上界。不改全局完整性gate。

- [x] Python先写较大区域、有效规则02、未知条目/feature/缺证据/过期审批拒绝、无规则01不变负例并观察红灯。
- [x] C#先写02读取、同名异地/不同zoom/level/非legacy保留、旧点实际PNG去重、原备份和失败/重试用例并观察红灯。
- [x] 最小实现、引擎编译、地图/pack/刷新/交付回归、独立review修复后提交。
- [ ] 将各洲草案跨区原实例唯一归属、规则与大区重新审核，继续实际恢复剩余队列。

### Task 11: 相邻源区的同一原标签只绘制一次

矩形分区不能保证所有原字串都落在一个区内。佛得角原字串横跨美洲与欧非分区；将两边各作独立标签会重复，而删掉其中一边的原像素记账又会漏账。新增可选 `shared_sources`，只引用另一同zoom区域已实际绘制的同一原文字实例，不增加本区中文输出或标签计数，不允许用来豁免未知原字。

每项为 `id,owner_region,feature_id,kind,original,original_bbox,source_url,review_notes`。独立ID不得与本区feature冲突；owner必须存在且不是本区；目标feature原名、kind、source_url以及换算为世界像素后的完整original_bbox须逐项相同。目标必须通过正常译文/区域审批并有实际非空中文输出。引用须覆盖本区实际源alpha，按正常源bbox规则核查；不支持引用链或循环。引用字段纳入本区指纹与独立源/像素审批。任何引用失效均阻断整个包；不能因引用先出现而跳过后续owner验证。

仅修改既有 `scripts/build_map_tile_pack.py` 和对应测试；不改变运行时格式、不自动审批真实数据。先写相邻字串被分区两边记账但只输出一次、未知owner/错误实体/全局bbox不符/无输出/自引用拒绝、修改引用使旧审批失效的失败测试，观察RED后最小实现。无引用的正式128瓦片包须逐字节不变。实现后由独立审查再合并真实分区数据。

**机制验收记录：** Task10/11已独立审查批准；01/02安装器提前标记问题按实际失败探针修正后，109pack、48renderer、63刷新、46资源交付、5脚本契约、86Python检查通过。原128瓦片01重建字节一致。大区循环仅缓存每要素alpha一次，50builder测试通过；正式新增数据须另行实际入口/缓存验收，不把代码检查等同地图全量覆盖。

### Task 13: 原版无峰名的仅海拔标注

美国西部z8新源实际存在只有`5307m`、`9342m`和三角点的实例，没有原峰名。不能为满足中文名守卫而造“无名峰”，也不能用当前地理资料改原图数字。peak feature新增可选布尔 `elevation_only:true`，original必须仅为原数字与m（可有间隔），且逐字匹配original_elevation_m；译文必须恰为同数字加“米”。布局只能一个完整行，输出不再自动追加第二遍海拔，不支持此标记套用非peak或原来有名字的峰。源框、真实三角点、审批/字体/实际像素规则照常；仅高度记录不能关联具名旧WWP排除。原无标记的峰和现有asset输出保持逐字节不变。

先补可用单行高度、拒绝造名/数字不符/丢高度/非peak/更改标记旧审批失效的RED测试，再最小实现与回归；真实两处原图由作者和独立reviewer提供证据，代码不自动批准。报告与覆盖度区分具名标签及仅海拔实例，不把新增描述当原峰名。只修改现有builder/tests及相关说明，独立审查后提交。

**同批无字符号：** 西非8/119/114原图实际只有城市圆点，没有文字。symbols可明确标 `unlabeled:true`，不关联feature_id，须独立source_url和review_notes；源框及区域/实际像素审批照常。仅恢复真实几何，不新增feature/translation/layout或标签计数。缺标记仍按旧关联规则拒绝；非布尔标记、又关联具名feature、缺资料/审核说明均拒绝。先写独立无字点输出与非法证明RED，再最小实现，原有具名符号路径不变。

### Task 14: 官方原图的精确低alpha背景记账

官方9/292/172和8/43/102原PNG与本机.orig逐字节一致，但分别有63250/63659个恰RGBA(1,1,1,1)的平坦背景像素。它们不是文字，不得用巨型城市框吞掉，也不能改源图、泛忽略alpha<=1或自动认定所有低alpha都是背景。新增可选region.source_backgrounds，每项为 `x,y,sha256,rgba:[1,1,1,1],source_url,review_notes`；只接受本区core源tile，坐标须整型、SHA与source_url须精确等原图条目且资料已通过原图SHA校验，notes非空，不允许重复tile。仅该明确RGBA取值、且占该tile至少一半像素时可作为经审查的平坦背景记账，其他颜色/alpha/稀疏像素拒绝。

只改变源alpha归账mask，不修改原图/原预览或中文输出。原始total_pixels仍按原alpha>0统计，报告额外列每条背景证据和background_pixels；accounted含背景，未记账实际ink仍阻断。feature/symbol/shared原框“非空”检查须基于排除背景后的ink，不能用纯背景证明存在文字。全部字段纳入region指纹，修改旧审批失效。缺元数据时原始严格alpha规则不变；不提供跳过原图核验选项。

只修改既有builder/tests。先观察原平坦背景造成漏账RED，再验证精确元数据可通过、源PNG/已有中文输出字节不变；负例包含错SHA/URL/颜色、低占比、重复/包外tile、空说明、只有背景的假文字框及过期审批。运行现有55builder和来源回归，正式798瓦片须仍逐字节一致；独立审查后提交。真实两处原图仍由地区作者与独立审者确认，不自动批准data。
