# 地图地名复核与生成

Cities 资产包含 4,287 个地名点，既有城市，也有旧市名、町、街区等。已有中文字样并不代表译名正确。本轮按原始名称及坐标复核 318 条，修订其中 289 条，保留 29 条已正确的名称；其他 3,969 条沿用旧译，尚未完成逐条语义复核。Synoptic 的 321 条国家/海域资产不在本轮改动范围内。

| 审核文件 | 记录数 | 范围 |
|---|---:|---|
| `dict/placenames_reviewed/japan.json` | 204 | 原始资产内日本段，含旧名和街区 |
| `dict/placenames_reviewed/korea.json` | 59 | 原始资产内韩国 45 条、朝鲜 14 条 |
| `dict/placenames_reviewed/vietnam.json` | 6 | 已有一手中文来源的越南条目 |
| `dict/placenames_reviewed/world.json` | 49 | 中国同名城市及南亚、东南亚、欧美、非洲、中东等已确认问题 |

例如：阿特苏吉 → 厚木、新德尔希 → 新德里、穆姆巴伊 → 孟买；黑龙江的 Yichun → 伊春，江西的 Yichun 仍为宜春。法国 Brest 保持布雷斯特，白俄罗斯 Brest 改为布列斯特。不能用一次全局英文键替换处理同名异地。

## 数据来源与编辑原则

每条审核记录保留原英文 `name`、实际纬经度 `lat/lon`、原中文 `previous`、复核中文 `zh` 及一手公开资料 `source`；必要的实体定位、异写及行政层级说明放在 `note`。坐标取自原始 WWP，仅作为已有记录的身份，不是重新测绘的市中心。

- 日本名称主要核对日本国土地理院/海上保安厅的[《地名集日本 2021》](https://www.gsi.go.jp/common/000238259.pdf)，并检查[官网所列勘误](https://www.gsi.go.jp/kihonjohochousa/gazetteer.html)。这里是在其日文汉字、罗马字及位置资料基础上人工整理中文显示名，不能将该来源描述为官方中文译名表。旧名及街区另附地方政府来源。与野、德山、胜田等保留原实体；调布ケ丘保留名称中的必要假名，不改成整个调布市。
- 韩国主要核对[京畿道政府中文表](https://chinese.gg.go.kr/31-si-gun-shortcut/)、[韩国访问委员会城市目录](https://vkc.or.kr/sc/pr-center/tour-site/culture-tour/)、韩国旅游发展局及地方政府中文页。和道邑采用南杨州市政府和行政区划资料的汉字，不混同其他地区的华道。
- 朝鲜及其他地区主要采用中国外交部、驻外使领馆和地方政府资料；松林的中文及 Songrim 对应关系另据红十字国际委员会官网，来源身份写在记录中。
- 不把行政后缀机械音译为“斯”“伊”，不擅自把旧市、町或街区提升为现代城市，不重定位或删除旧地名点。

`dict/placenames_cities.json` 是未复核条目的兼容默认值。复核记录优先于它；后续修订应加入审核表，不要把同名城市统一改成一个中文名。旧生成脚本的硬编码大表和按字母拼凑中文的兜底已移除。新的未知英文标签会保留原样，等待有来源的翻译。

## 生成与应用

仅使用 Python 标准库，不需要网络查询或新增运行时组件：

```bash
python -B scripts/build_cities_placenames.py --output build/Cities.wwp
python -B scripts/verify_placenames.py --asset build/Cities.wwp --baseline assets/placenames/Cities.wwp
```

默认输入是仓库已有中文资产；也可用 `--source` 指定原英文 WWP。输入、旧中文和修正后的中文均通过同一个解析器处理。必须显式指定输出，构建命令不会自动发现或写入游戏目录，也不会重写词典。

需要实际应用到游戏时显式选择目录，并在游戏退出后执行：

```bash
python -B scripts/apply_placenames.py --game-dir "游戏目录" --verify
python -B scripts/apply_placenames.py --game-dir "游戏目录"
python -B scripts/apply_placenames.py --game-dir "游戏目录" --revert
```

`--verify` 只报告待改名数量，不写入、不创建备份，也不是已安装译名的语义验收门禁。首次实际应用创建 `.wwp.orig`，后续应用不覆盖它。已自定义且不属于原英文/原中文/复核中文别名的标签保持原样。

匹配必须同时满足精确别名和每轴不超过 `0.0001°` 的坐标差，以容纳 float32 与小数记录的舍入。冲突或重复审核记录会在输出前报错。`wwp.py` 的字段现按真实纬经度命名，二进制格式仍是纬度 float、经度 float、flags，没有调整线上读取格式。

## 验证范围

```bash
python -B scripts/test_placenames.py -v
python -B scripts/test_verify_placenames.py -v
python -B scripts/verify_placenames.py
# 使用改动前保存的资产检查全部记录：
python -B scripts/verify_placenames.py --baseline previous-Cities.wwp --report build/placenames-check.json
```

- 13 项核心回归覆盖同名异地、旧中文升级、重复应用、两个入口一致、float32 及长字符串读写、只读验证、备份恢复、审核冲突及新输出目录。
- 7 项门禁回归覆盖中文错译、未批准改名、坐标/flags/顺序变化、空表、缺记录、非法坐标及审核范围缩水。
- 正式门禁要求全部审核记录在资产中唯一命中且译名正确，并对照 `dict/placenames_reviewed_scopes.json` 检查文件集合和条数。扩展审核范围时同步更新清单，不能通过删除审核记录静默降低覆盖率。
- `--baseline` 额外检查全部 4,287 条的顺序、坐标、flags，只允许审核表批准的名称变化。默认不带 baseline 的门禁不认证未审核条目的位置或译名；单纯含汉字不再被当作翻译质量证明。
- 本轮资产比较中只改变 289 个名称；Synoptic 逐字节保持原样。生成后的资产再次生成不会发生变化。没有修改本地化引擎、地图线程或安装器逻辑，不增加游戏运行时查表开销。

本轮是原始数据集范围内的译名修订，不是全球地名或现代行政区划的全面更新；尤其不应从 318 条通过推断其余地名已全部正确。
