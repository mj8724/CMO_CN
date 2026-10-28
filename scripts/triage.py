#!/usr/bin/env python3
"""Triage the CMO engine string pool into translation work buckets.

The engine rewrites the game's decrypted string pool *by offset*, so translating
an offset changes every use of that string.  Some strings are load-bearing
identifiers (SQLite column names, Lua sandbox keywords, Tacview API calls, file
paths, registry keys, control names) and translating them corrupts the engine.
This module decides, per string, whether offset-level translation is safe.

Buckets
-------
display  : multi-word player-visible text; safe to translate at offset level
label    : short single-token player-visible label; translated into `exact`
           only, so the UI-tree watcher applies it without touching the pool
keep     : proper noun / model designation; must stay in English by convention
internal : code / data / identifier; never translate
"""
from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"

# ---------------------------------------------------------------- hard blocks

# SQL is detected by *statement structure*, not by lone keywords: "View",
# "Group", "Table", "Index" and "Left" are ordinary English UI words that also
# happen to be SQL keywords.  Only genuine statements are blocked.
SQL_STRONG = re.compile(
    r"(\bSELECT\s+.{2,}?\s+FROM\b"
    r"|\bINSERT\s+INTO\b"
    r"|\bUPDATE\s+\S+\s+SET\b"
    r"|\bDELETE\s+FROM\b"
    r"|\bCREATE\s+(TABLE|INDEX|VIEW|TRIGGER|UNIQUE)\b"
    r"|\bDROP\s+(TABLE|INDEX|VIEW|TRIGGER)\b"
    r"|\bALTER\s+TABLE\b"
    r"|\bORDER\s+BY\b"
    r"|\bGROUP\s+BY\b"
    r"|\bPRAGMA\s+\w"
    r"|\b(sqlite_master|sqlite_sequence|information_schema)\b"
    r"|\bVACUUM\b"
    r"|\b(AND|OR)\s+[A-Za-z0-9_.]+\s*(LIKE\b|=|<>|<=|>=|<|>|IN\s*\()"
    r"|\b(Data Source|Provider|Initial Catalog)\s*="
    r"|\bSET\s+\[)", re.I)
SQL_WEAK = re.compile(r"\b(FROM|WHERE|JOIN|HAVING|LIMIT|UNION|DISTINCT)\b", re.I)
SQL_COLUMNISH = re.compile(r"\b\w+(ID|Id|Type|Name|Flag)\b")

LUA_IDENT = re.compile(
    r"\b(elseif|collectgarbage|getmetatable|setmetatable|rawget|rawset|"
    r"tostring|tonumber|ipairs|require|theTable)\b")

CODE_PREFIXES = (
    "ScenEdit_", "math.", "string.", "table.", "os.", "io.", "LUA_", "VP_",
    "Tool_", "World_", "Tacview.", "System.", "Microsoft.", "Command.",
)

# Control / resource identifiers: prefix + underscore naming used by WinForms.
CONTROL_PREFIX = re.compile(
    r"^(TSMI_|CB_|CL_|LV_|Combo_|Analysis_|btn|txt|lbl|chk|rad|dgv|lst|tvw|"
    r"pnl|grp|tab|mnu|cms|tsm|img|pic|spl|nud|cmb|ToolStrip|MenuStrip|Timer|"
    r"ComboBox|Label|Button|TextBox|Panel|GroupBox|CheckBox|RadioButton|"
    r"TabPage|DataGridView|ListView|TreeView|PictureBox|SplitContainer|"
    r"ContextMenuStrip|NotifyIcon|HwndWrapper|Form\b)")
UNDERSCORE_IDENT = re.compile(r"^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)+$")

CAMEL = re.compile(r"^[A-Z][a-z0-9]*(?:[A-Z][a-z0-9]*)+$")
DB_ID_SUFFIXES = (
    "ID", "Id", "Type", "Name", "Flag", "Value", "Count", "Num", "Number",
    "Code", "Key", "Index", "Ref", "List", "Level", "Rate", "Mode", "Time",
    "Date", "Set", "Path", "File", "Info", "Data", "Size", "Range", "Text")

PATH_LIKE = re.compile(
    r"([A-Za-z]:\\|\\\\|^/[A-Za-z]|/Command;|\.xaml\b|\.dll\b|\.exe\b|\.db3\b|"
    r"\.scen\b|\.save\b|\.ini\b|\.xml\b|\.html\b|\.csv\b|\.png\b|\.log\b|"
    r"\.config\b|\.json\b|\.pdf\b|\.ttf\b|Templates\\|Scenarios\\|Campaigns\\|"
    r"^https?://|^www\.|^SOFTWARE\\\\|^HKEY_|^\*\.\w+\|)", re.I)

FORMAT_SPEC = re.compile(r"\{[0-9]\s*[:,][^}]*\}|%[0-9.]*[fdsxeg]|\{\d+\}")

IDENT = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
DOTTED = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$")
GUID = re.compile(r"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}")
HEX = re.compile(r"^[0-9A-Fa-f]{16,}$")

ERROR_PROBE = re.compile(r"^Error at\b")
XML_FRAGMENT = re.compile(r"^\s*</?[A-Za-z_]|</[A-Za-z_]+>\s*$|<[A-Za-z_]+>.*</[A-Za-z_]+>\s*$")
HTML_FRAGMENT = re.compile(r"<\s*(td|tr|table|br|b|i|u|font|span|div|p)\b", re.I)
CODE_EXPR = re.compile(r"(==|!=|&&|\|\||=>|::|;\s*$|\bnull\b|\bvoid\b|\bstring\b\s*\()")
# `;` 结尾的**自然语言句子**不是代码：想定简报里的
# `There is risk that North Korea will launch an immediate ground attack;` 被
# `;\s*$` 当成代码/模式串丢掉，玩家永远看不到译文（R76）。
# 只对「有虚词、短英文词成句」的串豁免，`int i;`、`x = 1;` 仍算代码。
NATURAL_WORDS = re.compile(
    r"\b(the|and|of|to|is|are|was|were|will|would|we|our|there|that|this|with|for|not|from|be|has|have|may|can)\b",
    re.I)
# 编译后源码字面量的碎片（枚举/switch 标签表里被截出来的），例如 ", 'Aircraft', '"。
# 它不是界面文案，而偏移池注入会改坏游戏代码里的字面量（R31 实证：偏移 280066）。
CODE_LITERAL_FRAGMENT = re.compile(r"^\s*[,']\s*'[^']{0,40}'\s*,?\s*'?\s*$")
MEMBER_CALL = re.compile(r"\.[A-Za-z_]\w*\s*\(|\w+\s*\(\s*\)\s*$")
# `.003 (` 这类**小数/部队番号后面的括号**不是成员调用：标识符必以字母/下划线开头。
# 否则 `Abeche: Escadron EC 01.003 (Mirage 2000D)`、`Task Unit 544.01.06 (Based at …)`
# 会被当成代码丢掉（R76）。
# Executable script text.  Injecting a translation over one of these corrupts the
# engine exactly the way translating a Lua sandbox keyword did before.
SCRIPT_CODE = re.compile(
    r"(^\s*local\s+\w+"                       # local roll = 10
    r"|^\s*--"                                  # Lua comment
    r"|\bfunction\s*\("                        # function (
    r"|\bprint\s*\("                           # print(...
    r"|\w+\s*=\s*[0-9\"'\[{]"                  # assignment to a literal
    r"|^\s*(end|then|else|elseif|return)\s*$"
    r"|\w+\s*:\s*\w+\s*\("                    # obj:method(
    r"|\$\w+\s*="                              # $-variable assignment
    r")")
# Version / build identifiers, e.g. "DB3000 v510", "Cold War DB v512".
VERSION_ID = re.compile(r"^\s*\S+\s+v\d{3,}\s*$")
# .NET namespace / assembly identity, e.g. "Gibraltar.Agent.LogWriteMode, Gibraltar.Agent"
NAMESPACE_LIKE = re.compile(r"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+(\s*,\s*[A-Za-z_]\w*)+$")
# DateTime custom-format strings, e.g. "d/M/yyyy h.mm.ss tt"
DATE_FORMAT = re.compile(r"^(?=.*(?:yyyy|MM|dd|HH|mm|ss|tt))[dMyHhmstfF/:. \-]+$")
# Two-letter codes and unit-ish technical tokens.
SHORT_CODE = re.compile(r"^[A-Za-z]{1,3}$")
# lowercase-first camelCase: isActive, creationOptions, urlTemplate
LOWER_CAMEL = re.compile(r"^[a-z][a-z0-9]*(?:[A-Z][a-z0-9]*)+")
# WinForms designer field names: findAndReplaceToolStripMenuItem, linkLabel1
DESIGNER_FIELD = re.compile(
    r"(ToolStripMenuItem|MenuItem|ToolStrip|LinkLabel|Label|Button|TextBox|"
    r"ComboBox|CheckBox|RadioButton|Panel|GroupBox|TabPage|DataGridView|"
    r"ListView|TreeView|PictureBox|SplitContainer|ContextMenuStrip|"
    r"NotifyIcon|NumericUpDown|TrackBar)\d*$")
# trailing-separator enum prefixes: Custom_, FacilityType_
ENUM_PREFIX = re.compile(r"^[A-Za-z][A-Za-z0-9]*_$")
# internal error probes without spaces: Error32135465132129
ERROR_NOSPACE = re.compile(r"^(Error|Err|E)\d{5,}$")

# Model / platform designations that must stay in English.
MODEL_DESIG = re.compile(
    r"^[A-Za-z]{1,4}[-‑]?\d{1,4}[A-Za-z0-9/\-‑.]*$|"
    r"^(F|Su|MiG|Tu|B|A|C|E|KC|P|H|Ka|Mi|Il|An|Yak|J|DF|HQ|S|K|M|R|T|Z|Type)"
    r"[-‑\s]?\d")

# Neutral tokens that are engine enumerations, compared by equality.
INTERNAL_TOKENS = {
    "None", "Null", "null", "True", "False", "true", "false", "Auto", "Manual",
    "Culture", "Neutral", "publicKeyToken", "Version", "retargetable",
    "DockingOps", "AirOps", "CargoOps", "ScenEdit",
}

# Single tokens that ARE shown to the player and are worth translating.
DISPLAY_TOKENS = {
    "Exit", "Time", "Group", "Flags", "Chalk", "OpFor", "Ready", "Cancel",
    "General", "Weapons", "Doctrine", "Sensors", "Mounts", "Loadout",
    "Magazine", "Airbase", "Port", "Mission", "Unit", "Units", "Contact",
    "Contacts", "Target", "Targets", "Side", "Sides", "Zone", "Zones",
    "Weather", "Terrain", "Fuel", "Damage", "Status", "Range", "Speed",
    "Altitude", "Depth", "Heading", "Course", "Name", "Type", "Class",
    "Country", "Operator", "Category", "Role", "Notes", "Comment", "Summary",
    "Scenario", "Campaign", "Database", "Settings", "Options", "Help",
    "About", "Save", "Load", "New", "Open", "Close", "Print", "Export",
    "Import", "Search", "Filter", "Sort", "Refresh", "Delete", "Add",
    "Remove", "Copy", "Paste", "Undo", "Redo", "Apply", "Reset", "Default",
}


def _is_camel_db_field(s: str) -> bool:
    if " " in s or "_" in s or "-" in s:
        return False
    if not CAMEL.match(s):
        return False
    return s.endswith(DB_ID_SUFFIXES) or len(s) > 12


def classify(s: str) -> str:
    """Return 'display' | 'label' | 'keep' | 'internal'."""
    if not s:
        return "internal"
    t = s.strip()
    if len(t) < 2 or not re.search(r"[A-Za-z]", t):
        return "internal"

    # --- hard internal blocks -------------------------------------------
    if ERROR_PROBE.match(t):
        return "internal"
    if SCRIPT_CODE.search(t):
        return "internal"
    if VERSION_ID.match(t):
        return "internal"
    if PATH_LIKE.search(t):
        return "internal"
    if XML_FRAGMENT.search(t) or "</" in t:
        return "internal"
    if t.startswith(CODE_PREFIXES):
        return "internal"
    if GUID.match(t) or HEX.match(t):
        return "internal"
    if CONTROL_PREFIX.match(t) or UNDERSCORE_IDENT.match(t):
        return "internal"
    if DOTTED.match(t):
        return "internal"
    if MEMBER_CALL.search(t):
        return "internal"
    if CODE_EXPR.search(t):
        return "internal"
    if CODE_LITERAL_FRAGMENT.match(t):
        return "internal"
    if _is_camel_db_field(t):
        return "internal"
    if LUA_IDENT.search(t) and t.count(" ") <= 1:
        return "internal"
    # A string ending with a colon or question mark is a UI prompt, never a SQL query.
    if not (t.endswith(":") or t.endswith("：") or t.endswith("?")):
        if SQL_STRONG.search(t):
            return "internal"
        if SQL_WEAK.search(t) and SQL_COLUMNISH.search(t):
            return "internal"
    if t in INTERNAL_TOKENS:
        return "internal"
    if HTML_FRAGMENT.search(t):
        return "internal"
    if NAMESPACE_LIKE.match(t) or DATE_FORMAT.match(t):
        return "internal"
    if LOWER_CAMEL.match(t) or DESIGNER_FIELD.search(t):
        return "internal"
    if ENUM_PREFIX.match(t) or ERROR_NOSPACE.match(t):
        return "internal"
    if t.startswith("_") or t.endswith("_"):
        return "internal"
    if SHORT_CODE.match(t) and t not in DISPLAY_TOKENS:
        return "internal"

    # --- proper nouns ----------------------------------------------------
    if MODEL_DESIG.match(t) and t not in DISPLAY_TOKENS:
        # Distinguish real designations (PLZ-07, Tu-123, RGB-16MK) from
        # internal composite tokens (Type2Color, SMA2Max, Id2).
        if not re.search(r"[-‑]", t):
            return "internal"
        return "keep"

    # --- display decisions ----------------------------------------------
    words = t.split()
    if len(words) >= 2:
        return "display"
    if t in DISPLAY_TOKENS:
        return "label"
    if IDENT.match(t):
        # Single CamelCase / lowercase tokens are WinForms control names or
        # engine enumerations; translating them risks breaking equality checks.
        if CAMEL.match(t) or t.islower() or t.isupper():
            return "internal"
        return "label"
    # A bare English word possibly with trailing punctuation ("Selected:",
    # "Weapons").  Safe as a UI-tree label; never injected at offset level.
    if re.match(r"^[A-Za-z][A-Za-z'\-]*\s*[:.?]?$", t):
        return "label"
    return "internal"


def _code_expr_hit(t: str) -> bool:
    """`CODE_EXPR` 命中是否为真代码（R76）。

    只有「句尾分号」一种命中时，**自然语言句子**不算代码：想定简报里的
    `There is risk that North Korea will launch an immediate ground attack;`
    曾因 `;\s*$` 被当作模式串丢弃，玩家永远看不到译文。
    仍算代码的：`int i;`、`x = 1;`（无虚词、无成句）、以及带 `==`/`::`/`null` 等任何其它命中。
    """
    hits = [m.group(0).strip() for m in CODE_EXPR.finditer(t)]
    if not hits:
        return False
    if any(h != ";" for h in hits):
        return True
    if NATURAL_WORDS.search(t) and len(re.findall(r"[A-Za-z]{2,}", t)) >= 5:
        return False
    return True


def is_script_or_schema(s: str) -> bool:
    """True when translating this string at source level could corrupt the engine.

    Covers SQL / Lua / schema identifiers / control names / paths / dates -- the
    classes that previously produced a DAL `KeyNotFoundException` and a broken
    Lua sandbox.
    """
    if not s:
        return False
    t = s.strip()
    # A string ending with a colon is a UI prompt, never a SQL query.
    is_sql = False
    if not (t.endswith(":") or t.endswith("：") or t.endswith("?")):
        if SQL_STRONG.search(t) or (SQL_WEAK.search(t) and SQL_COLUMNISH.search(t)):
            is_sql = True
    return bool(SCRIPT_CODE.search(t) or PATH_LIKE.search(t)
                or is_sql or LUA_IDENT.search(t)
                or CONTROL_PREFIX.match(t) or UNDERSCORE_IDENT.match(t)
                or DOTTED.match(t) or NAMESPACE_LIKE.match(t)
                or MEMBER_CALL.search(t) or _code_expr_hit(t)
                or ERROR_PROBE.match(t) or LOWER_CAMEL.match(t)
                or DESIGNER_FIELD.search(t) or t.startswith(CODE_PREFIXES)
                or (SQL_WEAK.search(t) and SQL_COLUMNISH.search(t)))


def load_pool() -> dict[str, str]:
    return json.loads((DICT / "strings_pool.json").read_text(encoding="utf-8"))


def load_dict() -> dict:
    return json.loads((DICT / "CMOLocalizer.json").read_text(encoding="utf-8"))


def triage() -> dict[str, dict[str, str]]:
    out: dict[str, dict[str, str]] = {
        "display": {}, "label": {}, "keep": {}, "internal": {}}
    for off, text in load_pool().items():
        out[classify(text)][off] = text
    return out


def main() -> int:
    buckets = triage()
    d = load_dict()
    off, exact = d["offsets"], d["exact"]
    print(f"pool={sum(len(v) for v in buckets.values())}")
    work: dict[str, str] = {}
    for name in ("display", "label", "keep", "internal"):
        b = buckets[name]
        done_off = sum(1 for k in b if k in off)
        done_ex = sum(1 for v in b.values() if v in exact)
        print(f"  {name:<8} n={len(b):<6} in offsets={done_off:<6} in exact={done_ex}")
        if name in ("display", "label"):
            work.update({k: v for k, v in b.items() if k not in off and v not in exact})
    print(f"\nremaining translation work: {len(work)}")
    (DICT / "worklist.json").write_text(
        json.dumps(work, ensure_ascii=False, indent=0, sort_keys=True),
        encoding="utf-8")
    print("wrote dict/worklist.json")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
