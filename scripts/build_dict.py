#!/usr/bin/env python3
"""Deterministically build dict/CMOLocalizer.json from all translation sources.

Sources, merged in order (later wins):

    dict/src/legacy_preserved.json  lowest-priority explicit legacy translations
    dict/glossary.json              terminology seed (fills missing keys)
    dict/src/*.json                 curated translation batches (sorted; later wins)
    dict/src/labels.json            UI-only translations, never pool-injected
    dict/src/blacklist.json         English keys to purge from every section

The output file is never an input. Removing the sole source of a translation
therefore removes it from the next build, regardless of any old output bytes.

Placement
---------
An English string triaged as `display` *and* present in the engine string pool is
written to `offsets`, so the engine's own string table returns Chinese everywhere
it is used.  It is also written to `exact` as a UI-tree safety net.  Everything
else lands in `exact` only.

Consistency
-----------
The engine injects `offsets` into the game's string pool at startup, so for pool
strings the offset value is what the player actually reads.  Any disagreement
between `offsets[off]` and `exact[pool[off]]` would surface as the same English
text rendering two different Chinese strings.  After merging, the two sections are
therefore reconciled:

    curated    entry -> the curated translation wins in both sections
    preserved  entry -> explicit pool locations use the surviving exact value

`partial` is intentionally empty; `words` comes only from `dict/src/terms.json`
and retains the engine's longest-phrase composition vocabulary.

Output is byte-stable: two runs over identical sources produce identical files.
"""
from __future__ import annotations

import json
import os
import re
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"
SRC = DICT / "src"
MAIN = DICT / "CMOLocalizer.json"
# The engine's own scanner (scripts/verify_engine_parse.py) and the offset
# injection path require exactly these top-level sections.  Any other producer
# (see the legacy engines) must fail loudly rather than ship a foreign schema.
TOP_LEVEL_SECTIONS = frozenset({"_meta", "offsets", "exact", "partial", "words"})

sys.path.insert(0, str(ROOT / "scripts"))
from triage import classify, is_script_or_schema  # noqa: E402
from inject_filter import injection_safe, harmful_shape  # noqa: E402

ELLIPSIS = re.compile(r"…+")
CJK_ANY = re.compile(r"[一-鿿]")
DOT_RUN = re.compile(r"(?:\.\s*){3,}")
# Literal two-character escape text that must become real control characters.
LITERAL_ESC = re.compile(r"\\r\\n|\\n|\\r|\\t")
ESC_MAP = {"\\r\\n": "\r\n", "\\n": "\n", "\\r": "\r", "\\t": "\t"}
HAN = re.compile(r"[\u4e00-\u9fff]")
# Chinese text does not space-separate Han characters; translators sometimes leave
# an ASCII space where the English source had one ("小牛 同级武器").
HAN_GAP = re.compile(r"(?<=[\u4e00-\u9fff])[ \t]+(?=[\u4e00-\u9fff])")
BRACKET_PAIRS = ("()", "[]", "（）：", "【】", "〔〕")
BRACKET_PAIRS = (("(", ")"), ("[", "]"), ("（", "）"), ("【", "】"), ("〔", "〕"))


def load(path: Path):
    with path.open(encoding="utf-8") as fh:
        return json.load(fh)


def iter_source_maps():
    """Yield curated `dict/src/*.json` translation maps."""
    if not SRC.is_dir():
        return
    for p in sorted(SRC.glob("*.json")):
        if p.name in ("blacklist.json", "labels.json", "identifiers.json",
                      "force_offsets.json", "dlc_briefing_fixes.json",
                      "term_replacements.json", "terms.json", "legacy_preserved.json"):
            continue
        data = load(p)
        if isinstance(data, dict):
            yield p.name, {k: v for k, v in data.items()
                           if isinstance(v, str) and not k.startswith("_")}


def load_terms() -> dict:
    """术语表（组合翻译的词汇层）。

    引擎按「结构 + 术语」拼装显示串，所以这里只放词：加新装备时只要它由已有词
    组合而成就无需任何改动；只有出现新词才需要加一行。写入产物的 `words` 段。
    """
    p = SRC / "terms.json"
    if not p.is_file():
        return {}
    data = load(p)
    if not isinstance(data, dict):
        return {}
    terms = {}
    for k, v in data.items():
        if k.startswith("_") or not isinstance(v, str):
            continue
        terms[k] = v
    return terms


def normalize_ellipsis(en: str, zh: str) -> str:
    """Keep the ellipsis style the engine expects: ASCII '...' when the source
    uses dots, so string comparisons elsewhere in the game stay intact."""
    if "…" in zh and DOT_RUN.search(en):
        return ELLIPSIS.sub("...", zh)
    return zh


def sanitize(en: str, zh: str, allow_sensitive: bool = False) -> str | None:
    """Repair mechanical defects, or return None when the entry must be dropped.

    Drops rather than guesses: a missing entry falls back to the offset-injection
    translation, which is always better than showing broken text to a player.

    `allow_sensitive` is set for `labels.json` entries.  Those are translated in
    the UI layer only and never injected into the engine string pool, so a token
    such as `.scen` inside a menu label is safe there.
    """
    if not zh:
        return None
    # Code / schema / path strings must never carry a translation.
    if not allow_sensitive and is_script_or_schema(en):
        return None
    # Literal "\\r\\n" written as text while the source holds real CR/LF.
    if any(c in en for c in "\r\n\t"):
        zh = LITERAL_ESC.sub(lambda m: ESC_MAP[m.group(0)], zh)
    zh = HAN_GAP.sub("", zh)
    # Restore whitespace the engine relies on for concatenated fragments.
    lead_src = en[:len(en) - len(en.lstrip())]
    lead_dst = zh[:len(zh) - len(zh.lstrip())]
    if lead_src and not lead_dst:
        zh = lead_src + zh
    trail_src = en[len(en.rstrip()):]
    trail_dst = zh[len(zh.rstrip()):]
    if trail_src and not trail_dst:
        zh = zh + trail_src
    # A translation must actually be a translation.
    if not HAN.search(zh):
        return None
    # Bracket balance must match the source's balance.
    for o, c in BRACKET_PAIRS:
        if (en.count(o) - en.count(c)) != (zh.count(o) - zh.count(c)):
            return None
    # Line structure is part of the string contract.
    if en.count("\n") != zh.count("\n"):
        return None
    return zh


def load_term_replacements() -> tuple[list[tuple[re.Pattern, str]], list[re.Pattern]]:
    """Naming standard: deterministic value rewrites plus banned-pattern gate."""
    path = SRC / "term_replacements.json"
    if not path.is_file():
        return [], []
    data = load(path)
    rules = [(re.compile(r["pattern"]), r["to"])
             for r in data.get("replacements", []) if isinstance(r, dict)]
    banned = [re.compile(b["pattern"]) for b in data.get("banned", [])
              if isinstance(b, dict)]
    return rules, banned


def apply_naming_standard(text: str, rules: list[tuple[re.Pattern, str]]) -> str:
    for pattern, repl in rules:
        text = pattern.sub(repl, text)
    return text


def load_preserved() -> tuple[dict[str, str], dict[int, str]]:
    """Explicit legacy facts, not an artifact fallback or automatic migration.

    Offset entries are locations -> original English, never duplicate translations.
    Both pool identity and the surviving exact source are checked at placement.
    Named batches remain authoritative over these lowest-priority translations.
    """
    path = SRC / "legacy_preserved.json"
    if not path.is_file():
        return {}, {}
    data = load(path)
    if not isinstance(data, dict) or not isinstance(data.get("exact"), dict) \
            or not isinstance(data.get("offsets"), dict):
        raise ValueError(f"invalid explicit legacy source: {path}")
    exact = {k: v for k, v in data["exact"].items()
             if k and not k.startswith("_") and isinstance(v, str)}
    locations: dict[int, str] = {}
    for key, en in data["offsets"].items():
        if key.startswith("_"):
            continue
        if not isinstance(en, str) or not en or not key.isdigit():
            raise ValueError(f"invalid legacy pool location {key!r}")
        locations[int(key)] = en
    return exact, locations


def build() -> dict:
    exact, preserved_locations = load_preserved()
    offsets: dict[int, str] = {}
    # `partial` is retained for file-format compatibility but is intentionally
    # empty: the engine no longer performs word-level composition, and the
    # inherited entries contained split-word artifacts ("海里 到达返航油量").
    partial: dict[str, str] = {}

    blacklist: set[str] = set()
    blfile = SRC / "blacklist.json"
    if blfile.is_file():
        blacklist = set(load(blfile))

    # Engine identifiers -- INI section/key names and on-disk directory names.
    # They may be shown to a player, so they stay in `exact`, but they must never
    # be injected at offset level: the pool string is also the lookup key, and
    # translating it breaks configuration reads (proven: `Game Preferences`
    # causes a NullReferenceException during Application_Startup) or path
    # resolution (proven: `Config` produced a Chinese-named 配置/ directory).
    identifiers: set[str] = set()
    idfile = SRC / "identifiers.json"
    if idfile.is_file():
        data = load(idfile)
        if isinstance(data, dict):
            identifiers = set(data.get("identifiers", []))

    pool = load(DICT / "strings_pool.json")
    text_to_offset = {v: int(k) for k, v in pool.items()}
    bucket_of = {int(k): classify(v) for k, v in pool.items()}

    curated: set[str] = set()

    # --- glossary -------------------------------------------------------
    glossary = load(DICT / "glossary.json")
    for section, terms in glossary.items():
        if section.startswith("_") or not isinstance(terms, dict):
            continue
        for en, zh in terms.items():
            exact.setdefault(en, zh)
            curated.add(en)

    # --- curated batches -------------------------------------------------
    # --- curated batches -------------------------------------------------
    # Only UI surface definitions (menus, HUD overrides, scenario titles/settings)
    # are permitted into `offsets`.  General translation chunks (chunk02..08)
    # and briefing prose live strictly in `exact`, where the UI-tree watcher,
    # WPF visitor, and WebView2 DOM injector consume them safely without
    # overwriting DAL queries, database hashes, or Lua sandbox strings in memory.
    offset_safe_sources = {
        "menu_terms.json", "force_offsets.json", "legacy_fixes.json",
        "scenario_titles.json", "scenario_settings.json",
        # R38（2026-10-18）：数据库查看器之外的**自绘面板**（目标状态/探测接触等）
        # 不经控件/WPF/WebView 通道，直接读游戏字符串池绘制；池注入才是它们唯一的汉化通道。
        # 该文件由 build/gen_pool_display_inject.py 生成：池内存在 + 已有译文 +
        # classify()=='display' + 多词短语 + 形状排除（反斜杠/花括号/格式串/SQL/路径/…）。
        # 绝不能直接放行 chunk02..08：实测那样会把注册表路径、格式串、PowerShell 片段一起注入。
        "pool_display_inject.json",
        "db_enum_displays.json",
    }

    labels_only: set[str] = set()

    # 新放行的「显示文本」注入源必须过形状安全判定（见 scripts/inject_filter.py）。
    # 精选源（menus/force_offsets/…）是人工核验过的，不受此限。
    shape_gated_sources = {"pool_display_inject.json", "db_enum_displays.json"}

    for name, mapping in iter_source_maps():
        for en, zh in mapping.items():
            if not en or not zh or en in blacklist:
                continue
            zh = normalize_ellipsis(en, zh)
            exact[en] = zh
            curated.add(en)
            if name in ("db_viewer_html_terms.json", "db_descriptions.json"):
                labels_only.add(en)
            if name in offset_safe_sources:
                if name in shape_gated_sources and not injection_safe(en)[0]:
                    continue
                off = text_to_offset.get(en)
                if off is not None and bucket_of.get(off) == "display" \
                        and en not in identifiers:
                    offsets[off] = zh

    # --- labels-only batch (never injected at offset level) --------------
    labfile = SRC / "labels.json"
    if labfile.is_file():
        for en, zh in load(labfile).items():
            if en and zh and en not in blacklist and not en.startswith("_"):
                exact[en] = zh
                curated.add(en)
                labels_only.add(en)

    # --- explicit legacy locations (not a second source of translations) ---
    for off, en in preserved_locations.items():
        if pool.get(str(off)) == en and en not in blacklist and en in exact:
            offsets.setdefault(off, exact[en])

    # --- reconcile offsets <-> exact -------------------------------------
    for off, zh in list(offsets.items()):
        en = pool.get(str(off))
        if en is None:
            continue
        if en in curated:
            offsets[off] = exact[en]
        else:
            exact[en] = zh

    # --- clean and merge briefing paragraphs for WebView2 inline rendering ---
    # Scenarios with inline <ScenDescription> (182 files) and side briefings in
    # memory are rendered via WebView2 DOM tree traversal in CMOLocalizer.cs.
    # We clean and merge all briefing entries into `exact` (never into `offsets`),
    # fixing Zulu mistranslations and spacing so they pass the release gate.
    # DLC briefing prose is WebView2-rendered (never engine-pool injected), so it
    # is merged with allow_sensitive=True: the script/schema guard is meant for
    # engine strings, and it false-positives on prose such as task-unit numbers
    # ("544.01.06 (") or telegram styles ("... underground;" -> CODE_EXPR).
    brief1_file = DICT / "briefing_dict.json"
    brief2_file = DICT / "brief_translations.json"
    brief3_file = SRC / "dlc_briefing_fixes.json"
    all_briefs: dict[str, str] = {}
    if brief1_file.is_file():
        all_briefs.update(load(brief1_file))
    if brief2_file.is_file():
        all_briefs.update(load(brief2_file))
    if brief3_file.is_file():
        all_briefs.update(load(brief3_file))

    for en, zh in all_briefs.items():
        if not en or not zh or en in blacklist:
            continue
        zh_clean = zh.replace("祖鲁语", "标准时").replace("祖鲁时间", "标准时")
        zh_clean = HAN_GAP.sub("", zh_clean)
        zh_clean = re.sub(r"(?<![A-Za-z])(and|or|of|the|to|in|on|at|for|with|by|from|is|are|will|be)(?![A-Za-z])",
                          "", zh_clean, flags=re.I)
        zh_clean = re.sub(r"  +", " ", zh_clean)
        fixed = sanitize(en, zh_clean, allow_sensitive=True)
        if fixed is not None:
            exact[en] = fixed
            labels_only.add(en)

    # --- sanitize: repair or drop every surviving entry ------------------
    for en in list(exact):
        fixed = sanitize(en, exact[en], allow_sensitive=en in labels_only)
        if fixed is None:
            del exact[en]
        else:
            exact[en] = fixed
    for off in list(offsets):
        en = pool.get(str(off))
        if en in exact:
            offsets[off] = exact[en]
        else:
            del offsets[off]

    # --- purge -----------------------------------------------------------
    for bad in blacklist:
        exact.pop(bad, None)
        partial.pop(bad, None)
        bad_off = text_to_offset.get(bad)
        if bad_off is not None:
            offsets.pop(bad_off, None)

    # --- 污染自愈 -------------------------------------------------------
    # 源条目删除不会回生；仍对所有来源统一清理历史「短标签拼成电报体句子」污染。
    TELEGRAPH = ("如令查收", "收悉", "如令办理", "所部须", "兹令", "电悉", "奉此")
    _purged = 0
    for en in list(exact):
        zh = exact[en]
        if not isinstance(zh, str) or not any(t in zh for t in TELEGRAPH):
            continue
        # 只清「短标签被拼成句子」；真正的散文段落（原句带句末标点且较长）保留
        if len(en) > 60 or re.search(r"[.。!?！？]\s*$", en):
            continue
        del exact[en]
        _purged += 1
    if _purged:
        print(f"[purge] 清掉 {_purged} 条电报体污染条目")
    # --- safety invariant: offsets may only hold display prose -----------
    # Injecting a translation over a schema identifier, script statement, control
    # name or path/config key corrupts the engine (this is exactly what broke the
    # DAL, the Lua sandbox, the INI reader and the Scenarios directory lookup
    # before).  Only strings triage is confident about reach `offsets`; every
    # other string is still served through `exact`.
    offsets = {k: v for k, v in offsets.items()
               if bucket_of.get(k) == "display"
               and pool.get(str(k)) in exact
               and pool.get(str(k)) not in identifiers}

    # 形状安全不变量（R38）：所有来源（含显式 legacy）统一剪掉危险注入。
    # 真实事故：`LongName ASC` 注入后数据库查看器 ORDER BY 全废、列表 0 项。
    # force_offsets（人工实机核验的函数型注入）在这之后重新加回，不受此限。
    _unsafe = []
    for _k in offsets:
        _src = pool.get(str(_k))
        if _src is None:        # 池里查不到的偏移留给下面的 known 一致性过滤处理
            continue
        if harmful_shape(str(_src)):
            _unsafe.append(_k)
    if _unsafe:
        _samples = [str(pool.get(str(k)))[:40] for k in _unsafe[:5]]
        print(f"  [inject-filter] 剪掉 {len(_unsafe)} 条形状不安全注入，例如 {_samples}")
        for k in _unsafe:
            del offsets[k]

    # Explicit, vetted HUD/status bar offsets that must be injected into the pool
    force_file = SRC / "force_offsets.json"
    if force_file.is_file():
        fdata = load(force_file)
        if isinstance(fdata, dict):
            for k, zh in fdata.items():
                if k.startswith("_"):
                    continue
                off_i = int(k)
                en_str = pool.get(k)
                # force_offsets 是人工实机核验过的注入白名单，它必须能覆盖 identifiers 过滤：
                # 像 7916（Steam Workshop 页签）这种同时被 identlist 收录的显示串，
                # 一旦被静默跳过，重建出来的词典就与已发布词典不一致——工坊页签会退回英文。
                if en_str and en_str not in blacklist:
                    offsets[off_i] = zh
                    exact[en_str] = zh
    known = set(text_to_offset.values())
    offsets = {k: v for k, v in offsets.items() if k in known}

    # --- UI dictionary wins over briefing prose --------------------------
    # Briefing batches are merged last and some of them translate short UI
    # labels as telegraphic sentences ("Load" -> "挂载" came from a briefing).
    # The curated glossary and menu terminology are authoritative for their
    # keys, so re-apply them after everything else has been merged.
    for name in ("glossary.json", "menu_terms.json", "sentence_fragment_fixes.json",
                 "legacy_fixes.json", "ui_cleanup_terms.json"):
        path = (DICT / name) if name == "glossary.json" else (SRC / name)
        if not path.is_file():
            continue
        data = load(path)
        for en, zh in data.items():
            if not isinstance(zh, str) or not en or en.startswith("_"):
                continue
            if en in blacklist or en not in exact:
                continue
            # Curated short labels only: full-sentence briefing prose legitimately
            # lives in the briefing batches and must not be overwritten here.
            if len(en) > 40 or chr(10) in en:
                continue
            # 拼接片段依赖首尾空格，覆盖时保留原有空白形态
            prev = exact[en]
            if prev[:1] == " " and zh[:1] != " ":
                zh = " " + zh
            if prev[-1:] == " " and zh[-1:] != " ":
                zh = zh + " "
            exact[en] = zh

    # 词条被上层词典改写后，字符串池偏移必须同步，否则同一英文会出现两种中文
    for off in list(offsets):
        en_sync = pool.get(str(off))
        if en_sync is not None and en_sync in exact:
            offsets[off] = exact[en_sync]

    # --- R44 组合翻译修复：把历史条目里的半译残留（`AvGas (200k Liter 地下 Tank)`）
    # 用「结构规则 + 术语表」重算一遍，而不是逐条手改。只改「能整串组合成功」的条目，
    # 组合不了（含生词）的一律不动——半截翻译比不翻译更糟。
    try:
        sys.path.insert(0, str(ROOT / "scripts"))
        from compose_terms import compose as _compose, residue_words as _residue  # noqa: PLC0415
        _terms = load_terms()
        if _terms:
            fixed = 0
            for _en in list(exact.keys()):
                _zh = exact[_en]
                if len(_en) > 60 or "http" in _en or '"' in _en or ";" in _en:
                    continue
                # 含换行符的键绝不能交给 compose（它会正则压成单行，掉门禁）
                if any(c in _en for c in (chr(13), chr(10), chr(9))):
                    continue
                if not CJK_ANY.search(_zh):
                    continue
                    continue
                # 只修「中文里还留着英文术语」的条目。已全中文的（哪怕是 `Tank`→`坦克`
                # 这种语境专义）一律不动——否则术语表会覆盖人工裁定的正确译法。
                if not _residue(_en, _zh, _terms):
                    continue
                _better = _compose(_en, _terms, exact)
                if _better and _better != _zh:
                    exact[_en] = _better
                    fixed += 1
            if fixed:
                print(f"[compose] 组合翻译修复 {fixed} 条半译条目")
                # 同步池偏移：同一英文在 offsets 与 exact 里必须是**同一个**中文，
                # 否则池注入的界面串与 exact 查找会读出两种译文
                # （实测踩过：` kg (Current), ` 在 offsets 里是「kg (当前),」、
                #   在 exact 里是「千克(当前),」）。
                _synced = 0
                for _off in list(offsets):
                    _en2 = pool.get(str(_off))
                    if _en2 is not None and _en2 in exact and offsets[_off] != exact[_en2]:
                        offsets[_off] = exact[_en2]
                        _synced += 1
                if _synced:
                    print(f"[compose] 同步 {_synced} 个池偏移译文")
    except Exception as _exc:  # noqa: BLE001
        print(f"[compose] 跳过组合修复: {_exc}")

    # --- naming standard: one Chinese spelling per equipment/term ---------
    name_rules, _banned = load_term_replacements()
    if name_rules:
        exact = {k: apply_naming_standard(v, name_rules) for k, v in exact.items()}
        offsets = {k: apply_naming_standard(v, name_rules) for k, v in offsets.items()}

    terms = load_terms()
    ordered_words = {k: terms[k] for k in sorted(terms)}

    ordered_offsets = {str(k): offsets[k] for k in sorted(offsets)}
    ordered_exact = {k: exact[k] for k in sorted(exact)}
    ordered_partial = {k: partial[k] for k in sorted(partial)}

    return {
        "_meta": {
            "engine": "CMOLocalizer",
            "version": (ROOT / "VERSION").read_text(encoding="utf-8").strip(),
            # Keys are deliberately prefixed so this block can never be mistaken
            # for a top-level section by the engine's hand-rolled JSON scanner
            # (see scripts/verify_engine_parse.py).
            "counts": {
                "n_offsets": len(ordered_offsets),
                "n_exact": len(ordered_exact),
                "n_partial": len(ordered_partial),
                "n_words": len(ordered_words),
            },
        },
        "offsets": ordered_offsets,
        "exact": ordered_exact,
        "partial": ordered_partial,
        "words": ordered_words,
    }


def write(doc: dict, path: Path) -> None:
    """Install `doc` at `path` completely, or not at all.

    The payload is written to a sibling `*.tmp`, parsed back as a self-check
    (valid JSON, expected top-level sections), and only then swapped into place
    with os.replace, which is atomic on a single filesystem.  An interrupted or
    invalid build therefore leaves any previous artifact untouched instead of a
    truncated multi-megabyte JSON file that a later packaging step could ship.

    Serialisation settings are deliberately unchanged (indent=1, ensure_ascii=
    False, no key reordering, single trailing newline) so the installed bytes are
    exactly what a direct write produced.
    """
    text = json.dumps(doc, ensure_ascii=False, indent=1, sort_keys=False)
    tmp = path.with_name(path.name + ".tmp")
    try:
        # newline="\n" 是硬要求：仓库里的 dict/CMOLocalizer.json 是 LF，
        # 若按 Windows 文本模式默认转成 CRLF，一次重建就是 49,000+ 行的纯换行 churn
        # （项目约束：词典保持 LF、最小 diff、禁止无序格式化）。
        tmp.write_text(text + "\n", encoding="utf-8", newline="\n")
        with tmp.open(encoding="utf-8") as fh:
            probe = json.load(fh)
        if set(probe) != TOP_LEVEL_SECTIONS:
            raise ValueError(
                f"refusing to install {path.name}: top-level sections "
                f"{sorted(probe)} != {sorted(TOP_LEVEL_SECTIONS)}")
        # Windows 下瞬时句柄可能拒绝原子替换；有界重试，仍失败则保全旧产物。
        # 不退回 truncate write，否则验证过的旧词典也可能被中断写入毁掉。
        for attempt in range(6):
            try:
                os.replace(tmp, path)
                break
            except PermissionError:
                if attempt == 5:
                    raise
                time.sleep(0.25)
    except BaseException:
        # Never leave a partial staging file behind, even on Ctrl+C / SIGTERM.
        tmp.unlink(missing_ok=True)
        raise


def main() -> int:
    doc = build()
    write(doc, MAIN)
    c = doc["_meta"]["counts"]
    print(f"built {MAIN}")
    print(f"  offsets={c['n_offsets']}  exact={c['n_exact']}  "
          f"partial={c['n_partial']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
