# 国家、海域标签全量复核

`dict/placenames_synoptic_reviewed/global.json` 覆盖原始 Synoptic 的全部321点：193个国家/地区标签，128个海域、海峡、海湾、湖泊和礁标签。每条保留原文、原坐标、旧译、核准译名、来源和裁决说明。城市与区域标签分开解析，避免同名、同坐标相互覆盖。

此次修正4处：纽芬兰 Green Bay 的“绿湾”改为“格林湾”；Ceram Sea 的“斯兰海”改为“塞兰海”；Bristol Channel 的“布里斯托尔湾”改为“布里斯托尔海峡”；Bohai Wan 经实体核查及用户确认，由“渤海湾”改为“渤海”。其余317项经过复核保留，坐标、顺序和 flags 不变。

国家/地区中文主要核对外交部领事服务网实际列出的目的地名称；海域主要核对联合国《第二次世界海洋评估》中文及专业资料。记录中的 source 提供对应证据，note 解释异写、历史名称和原始锚点偏移。来源只支持实体或中文专名时，明确使用编辑性裁决，不声称获得政府完整中文名审定。例如 Aru Sea 根据 SeaVoX/NOAA 的实体资料和中国地震部门“阿鲁”用字，保留“阿鲁海”，标记 `source_type: editorial`。

Bohai Wan 与 GeoNames1816290的 Bohai Sea 同坐标，Bohai Wan、Po-hai Wan 是该目录条目的别名；结合现成中文与用户确认，采用整个渤海的名称，撤回此前仅当作狭义渤海湾点位偏移的解释。保留原英文及旧中文供升级匹配，不改坐标或边界。

原始标签另有少量锚点疑问，如 Natuna Sea、苏格兰西岸内海；此次只校对名称，未通过换成邻近地物来掩盖偏移。

离线构建与发布核验：

```powershell
python scripts/build_cities_placenames.py --set Synoptic --require-complete --output assets/placenames/Synoptic.wwp
python scripts/verify_placenames.py --set Synoptic --baseline <原始英文Synoptic文件>
```

核验要求321/321覆盖，拒绝只因含中文便视为审核完成；同时检查名称变更属于核准记录，坐标、flags、顺序与原始文件一致。
