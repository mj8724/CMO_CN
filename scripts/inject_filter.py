#!/usr/bin/env python3
"""池级注入的形状安全判定 —— 单一事实源（build_dict 与两个生成器共用）。

为什么需要它：`offsets` 是**在游戏内存里改写字符串池**。一旦注入到 SQL 片段、
格式串、控件名、路径或配置键上，游戏逻辑当场坏掉。真实事故（2026-10-18）：
把 `LongName ASC`（chunk02 里的一条 ORDER BY 片段）注入后，数据库查看器的
`ORDER BY LongName ASC` 变成 `ORDER BY 长名称升序` → 查询全废、列表 0 项、
详情页 html_len=0（实机回归 R1 八连红）。

判定只回答一个问题：**这条英文串像不像「给人看的显示文本」**。
宁可漏注入（界面上仍走控件/WPF/WebView 通道），也不能改坏游戏内部串。
"""
from __future__ import annotations

import re

# 派发用 token：游戏按这些文本查找/比较控件项（R1 的 CB_ObjectType 就是这一类）。
DISPATCH_TOKENS = {"Aircraft", "Ship", "Submarine", "Facility", "Ground Unit",
                   "Satellite", "Weapon", "Sensor", "Yes", "No", "None", "true", "false"}

BAD_CHARS = re.compile(r"[\\{}\[\]<>|~^`\u201c\u201d]")
BAD_PATTERNS = re.compile(
    r"(://|::|&&|\|\||%\d|\{\d|Get-|e\.Message|SOFTWARE|HKEY_|SELECT |INSERT |UPDATE |"
    r"DELETE |DROP |DBID|\.exe\b|\.dll\b|\.html\b|\.scen\b|\.db3\b|\.xml\b|<[a-zA-Z/]|"
    r"\bSystem\.|\bConvert\.|\bFile\.|\bDirectory\.|\bProcess\.|\bRegex\.)")
# SQL 片段：ORDER BY 项（`LongName ASC`）、子句头、尾部的 LIKE 条件
SQL_FRAGMENT = re.compile(
    r"(^|\s)(ASC|DESC)$"
    r"|^(ORDER|GROUP|HAVING|LIMIT|OFFSET|WHERE|SELECT|FROM|JOIN|UNION|INSERT|UPDATE|"
    r"DELETE|CREATE|DROP|ALTER|PRAGMA|VACUUM)\b"
    r"|\b(LIKE|NOT LIKE)\s*'?%?$"
    r"|(=|<>|>=|<=)\s*'?%?$", re.I)
# 尾部标点只挡「像代码/格式串」的；`:`/`,` 是常见界面标签结尾，不能挡
# （`Max Distance (NM):` 就是被这条误杀的，2026-10-19 实测）
TRAILING_CODE = ('"', "'", "=", "(", "%", ";")

MAX_LEN = 200

# 剪枝用的**窄口径**判定：只挡「明显是代码/路径/SQL/格式串」的形状，不用
# 单词 token / CamelCase / 尾部标点这些启发式——那些会把既有的界面标签注入
# （`Options` / `General` / `Unit System` / `Acoustic (1-10 kHz)` …）误杀，
# 导致快捷键等窗口退回英文（2026-10-19 实测）。
HARMFUL_CHARS = re.compile(r"[\\{}\[\]<>|]")
HARMFUL_SHAPE = re.compile(
    r"(://|::|&&|\|\||%\d|\{\d|Get-|e\.Message|SOFTWARE|HKEY_|SELECT |INSERT |UPDATE |"
    r"DELETE |DROP |DBID|\.exe\b|\.dll\b|\.html\b|\.scen\b|\.db3\b|<[a-zA-Z/]|"
    r"\bSystem\.|\bConvert\.|\bFile\.|\bDirectory\.|\bProcess\.|\bRegex\.)"
    r"|(^|\s)(ASC|DESC)$"
    r"|^(ORDER|GROUP|HAVING|LIMIT|OFFSET|WHERE|SELECT|FROM|JOIN|UNION|INSERT|UPDATE|"
    r"DELETE|CREATE|DROP|ALTER|PRAGMA|VACUUM)\b"
    r"|\b(LIKE|NOT LIKE)\s*'?%?$"
    r"|\bBuild\s+\d+|1900\.\d+", re.I)


def harmful_shape(en: str) -> bool:
    """基座词典剪枝用：只判定「明显会改坏游戏」的形状（不误杀单词界面标签）。"""
    if not en:
        return False
    return bool(HARMFUL_CHARS.search(en) or HARMFUL_SHAPE.search(en))


def injection_safe(en: str, valid: set[str] | None = None) -> tuple[bool, str]:
    """返回 (是否可注入, 拒绝原因)。valid 为 None 时跳过「池内存在」检查。"""
    if not en or en.startswith("_"):
        return False, "空串/内部标记"
    if en in DISPATCH_TOKENS:
        return False, "派发 token"
    # 版本与构建号必须交由引擎动态正则处理，绝不能注入池偏移（否则游戏更新后
    # 标题栏/大厅会把新版本的构建号覆盖成旧版，例如 1900.23 显示成 1900.20）
    if re.search(r"\bBuild\s+\d+|1900\.\d+", en, re.I):
        return False, "含构建号版本串（由引擎动态规则处理，禁止池注入）"
    if valid is not None and en not in valid:
        return False, "池里不存在"
    if any("\u4e00" <= c <= "\u9fff" for c in en):
        return False, "已含中文"
    if len(en.split()) < 2:
        return False, "单词 token"
    if len(en) > MAX_LEN:
        return False, "超长"
    if BAD_CHARS.search(en) or BAD_PATTERNS.search(en) or SQL_FRAGMENT.search(en):
        return False, "形状像代码/路径/格式串/SQL 片段"
    if en.endswith(TRAILING_CODE):
        return False, "尾部像代码或标签"
    # 短串 + CamelCase 词几乎都是表/列名（`LongName ASC` 曾漏进来）。
    # 但带括号/数字的单位标签是合法显示串（`Acoustic (1-10 kHz)`），所以只在
    # 每个词都是纯字母数字时才按标识符处理。
    _words = en.split()
    if len(_words) <= 3 and re.search(r"[a-z][A-Z]", en) \
            and all(re.fullmatch(r"[A-Za-z0-9]+", w) for w in _words):
        return False, "短串含 CamelCase 标识符"
    return True, "ok"
