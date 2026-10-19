# 全量地名复核实施计划

日期：2026-10-19。基础：上游 master `979c1d3`，已包含 PR #3 的地名解析和 14px 绘制。

## 目标与约束

复核原始 Cities 的 4,287 个点及 Synoptic 的 321 个点。逐点保留英文、坐标、旧中文、复核中文、来源和必要的实体说明；保留 PR #3 已有复核记录。只有名称可变化，二进制顺序、坐标、flags 和点数必须不变。

国家和城市的通行中文译名优先；中日韩及其他有汉字原名的地名须核对当地实体，不能从罗马字重新拼音译写。其他地区使用可核验的已有中文名称，少数需要整理的译名必须说明原名及译写依据。不得用通用英文字母替换规则、机器翻译批量生成中文并直接宣布通过。

GeoNames、Wikidata 等现成地名资源用于名称、坐标和中文异名候选。候选匹配与语义复核分开，来源的实际身份必须保留；聚合地名库不冒称政府审定名录。歧义点、旧市名、街区和行政后缀逐条处理，不以最近的大城市替代原点。不确定项继续核对并单列，不能计作已精校。

## 有序增量

- [x] 检查 PR #3 与最新上游，建立独立 master 工作副本。
- [x] 独立提交上游文本 LF/最终 LF 归一化，配置后续文本保持 LF。
- [x] 建立完整原始点清单、可追溯来源候选及区域复核任务；下载的原始资源仅留在工作目录。
- [x] 沿用 `dict/placenames_reviewed/` 完成城市复核，另建 `dict/placenames_synoptic_reviewed/`；同步两份审核范围清单。
- [x] 扩展现有生成、应用和验证入口支持两种集合，发行验证必须每个点唯一命中已审核记录；自定义用户点保持原样。
- [x] 分地区完成语义复核与交叉检查，逐个完整增量验证并提交；未确认项不得用旧机翻凑足审核数。
- [x] 从原英文和当前中文各生成两份资产，检查结果一致、重复生成一致、仅批准名称变化。
- [x] 运行核心、门禁、缓存及必要构建检查，检查全库覆盖、来源、异常长名和同名异地；提交最终说明和可审阅结果。

## 文件与分工

- `dict/placenames_reviewed/*.json`：Cities 的坐标复核记录。
- `dict/placenames_synoptic_reviewed/*.json`：Synoptic 的坐标复核记录。
- `dict/placenames_reviewed_scopes.json`：分别列出 `files` 与 `synoptic_files` 的审核文件及条数。
- `scripts/placename_translation.py`：共享集合配置和坐标解析。
- `scripts/build_cities_placenames.py`：复用已有离线生成入口，显式选择集合和完整覆盖要求。
- `scripts/apply_placenames.py`：两份集合独立解析，继续保护自定义名称与既有备份。
- `scripts/verify_placenames.py` 及对应测试：完整覆盖、唯一身份和已批准改名门禁。
- `assets/placenames/{Cities,Synoptic}.wwp`：经验证的最终名称资产。
- `docs/PLACENAME_LOCALIZATION.md`：来源、覆盖范围、复现命令和实际限制。

不改标签绘制、运行时调度或其他词库。本任务不发布远端 PR，也不部署或替换本地游戏文件。

## 验收命令与判定

使用现有 `test_placenames.py`、`test_verify_placenames.py`、`test_placename_cache.py` 及 PowerShell 缓存专项检查；新增完整覆盖和 Synoptic 反例。分别对 Cities、Synoptic 运行带 baseline 的 `verify_placenames.py`，要求 4,287/4,287 和 321/321 复核记录唯一命中，未审核点为零，未批准改名、坐标/flags/顺序变化均为零。

语义结论依据逐条复核资料和交叉审阅，程序只能证明资产符合审核表，不能自动证明来源或译名正确。提交前检查 Git 追踪文本为 LF、非空文本有最终 LF。

已完成4,608点全量审核及独立终审；相对上游修订3,073城市名、4区域名。长直、渤海经补充实体证据与用户确认采用，保留原点；另两处城市来源跨链冲突明确标注。验证、原图标签遗漏和离线编译范围，以及缺少上游二维码资源导致无法生成完整正式安装器的限制，见[复核说明](PLACENAME_LOCALIZATION.md)及[结构核验报告](PLACENAME_REVIEW_REPORT.json)。
