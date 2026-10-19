# 地图地名复核与生成

Cities 资产包含4,287个地名点，既有城市，也有旧市名、町、街区等；Synoptic 包含321个国家、地区、海域等标签。2026-10-19 已完成原始数据集的全部4,608点复核：保留 PR #3 的318条审核记录，补齐其余3,969个城市及321个区域标签。相对已吸收 PR #3 核心的上游 `979c1d3`，额外修订3,073个城市名和4个区域名。只有名称变化，点数、顺序、坐标及 flags 不变。

| 审核文件 | 记录数 | 范围 |
|---|---:|---|
| `dict/placenames/cities.json` | 4,287 | 全部城市、旧市名、町、街区等原始 Cities 地名点 |
| `dict/placenames/synoptic.json` | 321 | 全部国家、地区及海域等原始 Synoptic 标签 |

两份文件都是数组，按对应 WWP 的记录顺序保存；合并时所有记录字段和值原样保留，包括 PR #3 的318条记录。`dict/placenames/metadata.json` 从原元数据文件原字节移动，继续固定两套资产的点数、顺序、坐标和 flags，不保存地名译文。

本轮例子：安徽 Suzhou 的“苏州”→“宿州”，广东 Luoyang 的“洛阳”→“罗阳”，辽宁 Kaiyuan 的“开远”→“开原”；Kota Kinabalu → 亚庇、Pontianak → 坤甸；Erbil 纠正为城市“埃尔比勒”，不带省字；Can Giuoc 根据越南一手中文资料采用“芹湥”。Bristol Channel 的“布里斯托尔湾”→“布里斯托尔海峡”。同名异地均按坐标分别核对。

## 数据来源与编辑原则

每条审核记录保留原英文 `name`、实际纬经度 `lat/lon`、原中文 `previous`、复核中文 `zh`、公开来源 `source` 及裁决 `note`。新记录的 `source_type` 区分政府或机构直接用名、社区地名资料、人工编辑译写。坐标取自原始 WWP，仅作为已有记录的身份，不是重新测绘的市中心。完整资料、授权与证据边界见[来源说明](PLACENAME_SOURCES.md)，区域标签另见[Synoptic复核说明](PLACENAME_SYNOPTIC_REVIEW.md)。

- 日本名称主要核对日本国土地理院/海上保安厅的[《地名集日本 2021》](https://www.gsi.go.jp/common/000238259.pdf)，并检查[官网所列勘误](https://www.gsi.go.jp/kihonjohochousa/gazetteer.html)。这里是在其日文汉字、罗马字及位置资料基础上人工整理中文显示名，不能将该来源描述为官方中文译名表。旧名及街区另附地方政府来源。与野、德山、胜田等保留原实体；调布ケ丘保留名称中的必要假名，不改成整个调布市。
- 韩国主要核对[京畿道政府中文表](https://chinese.gg.go.kr/31-si-gun-shortcut/)、[韩国访问委员会城市目录](https://vkc.or.kr/sc/pr-center/tour-site/culture-tour/)、韩国旅游发展局及地方政府中文页。和道邑采用南杨州市政府和行政区划资料的汉字，不混同其他地区的华道。
- 朝鲜及其他地区主要采用中国外交部、驻外使领馆和地方政府资料；松林的中文及 Songrim 对应关系另据红十字国际委员会官网，来源身份写在记录中。
- 不把行政后缀机械音译为“斯”“伊”，不擅自把旧市、町或街区提升为现代城市，不重定位或删除旧地名点。

GeoNames/Wikidata 的中文字段只作为候选，逐项排除了车站、遗址、公园、整省或邻近大城的误配。新城市记录中542条属编辑整理，区域记录中7条属组合译名，均保留实际证据边界；并非全部4,608条都有政府完整中文名审定，也不代表全部经过真人专家审签。地名生成与应用没有通用英文字母替换或机器翻译兜底。

旧平面表 `dict/placenames_cities.json`、`placenames_countries.json` 和原按国家、区域或审核批次拆分的 JSON 已删除，可通过 Git 历史追溯。部分旧记录 note 中的旧表路径和行号原样保留，引用的是当时的历史资料。未知名称或未知坐标保持原样；后续修订加入坐标审核表，不能把同名城市统一改成一个中文名。只有显式传入 `--dictionary` 才使用自定义 Cities 映射，应用入口的自定义 Synoptic 映射另用 `--synoptic-dictionary`。

## 生成与应用

词表生成和校验仅使用 Python 标准库，不需要网络查询。已有 PNG 标签缓存的刷新另需 Windows PowerShell 与 .NET Framework 4.8，离线临时编译安装器和共享标签渲染代码；地名脚本本身不部署游戏引擎。14px 字体优化还需安装同版本的新引擎，详见[标签绘制说明](PLACENAME_LABEL_RENDERING.md)：

```bash
python -B scripts/build_cities_placenames.py --review-file dict/placenames/cities.json --output build/Cities.wwp
python -B scripts/verify_placenames.py --asset build/Cities.wwp --baseline assets/placenames/Cities.wwp
python -B scripts/build_cities_placenames.py --set Synoptic --output build/Synoptic.wwp
python -B scripts/verify_placenames.py --set Synoptic --asset build/Synoptic.wwp --baseline assets/placenames/Synoptic.wwp
```

默认输入是仓库已有中文资产，审核表按 `--set` 自动选择上述两份数组；也可用 `--source` 指定原英文 WWP，用 `--review-file` 指定自定义审核文件。应用入口分别使用 `--review-file` 和 `--synoptic-review-file` 指定两套审核表。输入、旧中文和修正后的中文均通过同一个解析器处理。必须显式指定输出，构建命令不会自动发现或写入游戏目录，也不会重写词典。

构建默认要求完整覆盖：Cities 为4,287点，Synoptic 为321点。缺记录、未批准状态或未经核准改名会在写输出前失败。仅对自定义子集或测试夹具可显式用 `--allow-partial`，它不构成全量审核认证；自定义平面字典也不能代替严格门禁的审核记录。可选 `status` / `review_status` 字段只接受 `approved` 或 `reviewed`，`pending` 等状态不能作为通过记录。

需要实际应用到游戏时显式选择目录，并在游戏退出后执行：

```bash
python -B scripts/apply_placenames.py --game-dir "游戏目录" --verify
python -B scripts/apply_placenames.py --game-dir "游戏目录"
# WWP 已更新但地图仍显示旧缓存时：
python -B scripts/apply_placenames.py --game-dir "游戏目录" --refresh-tiles
python -B scripts/apply_placenames.py --game-dir "游戏目录" --revert
```

`--verify` 只报告待改名数量，不写入、不创建备份、不编译或重绘缓存，也不是已安装译名或 PNG 内容的语义验收门禁。首次实际应用创建 `.wwp.orig`，后续应用不覆盖它。已自定义且不属于原英文/原中文/复核中文别名的标签保持原样。

WWP 名称和已生成的地图 PNG 是两份数据。实际改名后会自动刷新已有标签缓存；名称已正确而缓存仍旧时，必须显式使用 `--refresh-tiles`。`--revert` 先恢复 WWP，再按恢复后的名称重绘当前全部标签 PNG，包含首次备份之后新增的瓦片；不通过复制旧 PNG 备份来替代同步。无标签缓存或无 PNG 时跳过。

刷新入口为 `scripts/refresh_placename_tiles.ps1 -GameDir "游戏目录"`，只调用现有安装器的标签重绘方法，不执行安装入口。它仅处理 `WW/Cache/Stamen Terrain - Labels`，首次重绘保留其 `.orig` 备份，后续不覆盖或消耗备份，不删除缓存、不修改 `Stamen Terrain` 地形底图。调用前检查 PNG 的 `z/x/y.png` 路径与两份 WWP 的完整可读性；每份 WWP 都必须包含记录，不能以另一份的非空记录代替，并在重绘期间保持 WWP 只读共享锁。调用后要求渲染器成功保存数与预检 PNG 数相同，并检查文件仍在；未完成重写时报失败，不依赖文件系统时间精度。失败时 WWP 可能已更新，排除问题后可用 `--refresh-tiles` 补刷新。临时编译产物位于被忽略的 `build/`。

首次标签备份先复制全部缓存文件（包括非 PNG）到新的同级 GUID 临时目录，完整成功后才通过目录重命名发布为 `.orig`，然后开始重绘。复制失败会清理本次临时目录；即使清理被权限阻止，遗留临时目录也不会被下一次刷新当成正式备份。已发布的完整备份不会因之后的重绘失败被删除。已有历史 `.orig` 保持原样，不覆盖，也不因目录存在而宣称其内容已经过完整性认证。

匹配必须同时满足精确别名和每轴不超过 `0.0001°` 的坐标差，以容纳 float32 与小数记录的舍入。冲突或重复审核记录会在输出前报错。`wwp.py` 的字段现按真实纬经度命名，二进制格式仍是纬度 float、经度 float、flags，没有调整线上读取格式。

## 验证范围

```bash
python -B scripts/test_placenames.py -v
python -B scripts/test_verify_placenames.py -v
python -B scripts/test_placename_cache.py -v
python -B scripts/test_global_placenames.py -v
python -B scripts/test_release_placenames.py -v
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test_refresh_placename_tiles.ps1
python -B scripts/verify_placenames.py
python -B scripts/verify_placenames.py --set Synoptic
# 使用改动前保存的资产检查全部记录：
python -B scripts/verify_placenames.py --baseline previous-Cities.wwp --report build/placenames-check.json
```

- 核心回归覆盖同名异地、旧中文升级、重复应用、两个入口一致、float32 及长字符串读写、只读验证、备份恢复、审核冲突及新输出目录。
- 门禁回归覆盖中文错译、未批准改名、坐标/flags/顺序变化、空表、缺记录、非法坐标及审核记录缺失；文件加载回归检查单文件数组格式与记录校验。
- Windows 缓存集成回归使用合成 WWP/PNG，覆盖自动刷新、零改名显式补刷、只读验证、备份保留、恢复后重绘新增瓦片及底图不变，不读写游戏。缓存与运行时现共用 14px 像素渲染器；这些文件级检查不等同于全部缩放级别的文字完整性或像素 OCR 验收。
- 全量审核回归覆盖两集合隔离、默认固定点数、缺记录/未批准状态阻断、显式局部构建、自定义字典不能冒充审核、未知坐标默认保持原文，以及原文/旧译/核准译名输入一致、重复生成一致。
- 发布接线回归覆盖无游戏目录时验证两份仓库资产、任一集合的错误译名、缺失审核记录，以及 flags、记录顺序、审核匹配容差内的坐标误改；固定指纹误写为 null 时也必须失败。CLI 与统一门禁都必须拒绝这些元数据改动，不能依赖调用方另传 `--baseline`。`package.sh` 在编译前执行两套严格验证，不以游戏目录中的待改名数替代资产验收。
- PowerShell 入口 55 条专项断言检查成功保存计数、锁定或只读瓦片失败、非法路径或坐标范围、缺失/截断/任一份为空的 WWP、空缓存跳过编译、首次备份复制失败后的安全重试，以及原备份与底图保护；另外覆盖高缩放国家标签、透明空瓦片、真实最长名及离线/共享 PNG 一致性。原有单份空 WWP 与部分备份失败的反例继续保留。
- 正式门禁固定要求 Cities 4,287点、Synoptic 321点，每个点均须唯一命中审核记录且译名正确。删除记录、重复记录或遗漏点均会失败；不再按审核批次维护范围清单。
- 默认 CLI 和 `verify_all.py` 均对照 `dict/placenames/metadata.json` 的固定元数据指纹，严格保证记录数量、顺序、float32 坐标和 flags 与上游 `69322c27b3eafea65a285e2723698201336ab89f` 相同。指纹为小端 int32 点数加逐条 `<ffI>` 纬度、经度、flags 的 SHA-256，不包含允许修改的名称，也不从待验资产动态生成。自定义完整测试集可用 `--metadata-baseline` 显式提供预先固定的指纹文件。
- `--baseline` 仍可额外逐点检查所选集合的记录元数据和改名依据，不能替代或绕过固定指纹。默认门禁要求全部点唯一命中审核，单纯含汉字不能作为翻译质量证明；源文件自身的锚点准确性也不能靠程序认证。
- 本轮实际验证结果保存在[结构核验报告](PLACENAME_REVIEW_REPORT.json)：两集合分别4,287/4,287、321/321，错误为零；从原始英文与上游中文生成得到逐字节相同结果，再生成亦相同。PR #3 的318条记录逐字段保持原样。标签绘制仍使用已有14px共享渲染器，此轮未修改绘制或传输线程。
- 离线编译已验证引擎、更新助手和共享测试宿主，并用仅供验证的安装器宿主反射读取两份内嵌地名资源，比对其 SHA256 与资产一致，未执行安装入口。完整安装器构建缺少上游未跟踪的 `assets/donate/wechat_pay.png`、`alipay.jpg`；验证宿主跳过这两项资源，不能作为正式发布包。未运行需部署或启动真实游戏的发版验收。

Changzhi 的原点35.208889/111.738609结合 GeoNames1815463、NGA GNS UFI-1900120的原生中文和罕见别名，支持垣曲长直地区。2026-10-19 用户确认采用“长直”，保留“长治”为旧译匹配别名，不添加乡或村后缀，不移动坐标；此前将它解释成远处长治市坐标错误的理由撤回。

Bohai Wan 的原点与 GeoNames1816290的 Bohai Sea 同坐标，目录将 Bohai Wan、Po-hai Wan 列为别名；中科院地理所资料明确区分渤海与狭义渤海湾。2026-10-19 用户确认本点采用“渤海”，保留“渤海湾”为旧译匹配别名，不移动坐标；此前仅将该点解释为渤海湾锚点偏东的理由撤回。

2026-10-19 用户复核接受最后9项现译：Azadshahr（阿扎德沙赫尔）、Aru Sea（阿鲁海）、Mantampay（曼坦派）、Muricay（穆里凯）、Amaigbo（阿马伊博）、Shahkot（沙科特）、Inisa（伊尼萨）、Punasa（普纳萨）、Soyibug（索伊布格）。保留各词条已有来源、裁决说明及其证据边界。

本轮覆盖补丁原始数据集的全部点，不新增当代城市或更新全世界行政区划。Batang、第二个 Bahawalnagar 的资料跨链冲突仍在记录及报告中单列；其他旧驻地或海域标签锚点疑问也在 note 说明。中文按原文所指名称复核，未借改名移动或删除这些点。来源等级与编辑译写仍可继续订正，完整覆盖不代表每个词条均获得地名主管部门审定。

覆盖范围限于两套 WWP；本次现场核查的 Steam 安装中也只有 Cities 和 Synoptic 两套正式 WWP，`.orig` 是原文件备份，两套索引分别只指向各自的 `0000_0000.wwp`。原始地图 PNG 还含集合之外的湖泊、省州及其他城市标签，这不表示存在第三套未使用的 WWP。现有缓存重绘有原图标签遗漏，低缩放过滤及碰撞规则也会影响可见名称，不能以保存瓦片数或 WWP 全覆盖证明原图信息完整保留。
