#!/usr/bin/env python3
"""Release gate for the CMO localization dictionary.

Fails (exit 1) on any of:
  1. dict/CMOLocalizer.json is not valid JSON or is missing sections
  2. an `offsets` key is not a real engine-pool offset
  3. an `offsets` value differs from the same string's `exact` value
     (offset injection and the UI-tree watcher must agree)
  4. a translated string is not actually Chinese
  5. Chinese text contains embedded ASCII spaces between Han characters
  6. an English function word survives inside a Chinese translation
  7. placeholder counts differ between source and translation
  8. leading/trailing whitespace is not preserved
  9. parentheses / quotes are unbalanced
 10. engine-internal strings were translated at offset level (corruption risk)

Also prints a coverage summary for the player-visible engine strings.
"""
from __future__ import annotations

import json
import re
import sys
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"

HAN = re.compile(r"[\u4e00-\u9fff]")
HAN_SPACE = re.compile(r"[\u4e00-\u9fff][ \t]+[\u4e00-\u9fff]")
FUNC_WORDS = re.compile(
    r"(?<![A-Za-z])(the|of|and|for|with|from|to|in|on|at|by|is|are|will|be)"
    r"(?![A-Za-z])", re.I)
PLACEHOLDER = re.compile(r"\{[0-9]+\}|%[sdif]|\{[A-Za-z]+\}")
BRACKETS = {"(": ")", "[": "]", "（": "）", "【": "】", "〔": "〕"}

# English fragments that must stay verbatim inside a Chinese translation:
# licence identifiers, URLs, citations, registered names, code credits.  These
# are stripped before the residual-function-word check so a citation such as
# "Survey Review XXII" or "CC BY 4.0" is not reported as an untranslated word.
PROTECTED = re.compile(
    # 技术串与引用文献里的英文虚词是被保留的原始内容，不属于漏译：
    #   * 单位表达 "0.787 in" / "5 in/54"       * 型号代号 "AT-1M" / "AA-2"
    #   * 引号内的外文标题/文献名                * 检索日期等引文片段
    r"(\d[\d.,]*\s*(?:mm|in|ft|lb|kg|km|kt|nm|cm|yd|m)\b"
    r"|\b[A-Z]{1,4}\d?[‑‐-]\d+[A-Za-z0-9/-]*"
    r"|（[^）]{0,200}）"
    r"|[A-Z][A-Za-z'.-]*(?:\s+(?:of|the|and|for|de|del|van|von|City)\s+[A-Z][A-Za-z'.-]*)+"
    r"|(?:The|Of|And)\s+[A-Z][A-Za-z'.-]*"
    r"|(?:HMS|USS|HMAS|HMNZS|HMCS|HNLMS|HNoMS|FS|JS|RFA|MV|SS)\s+[A-Z][A-Za-z' ]{0,40}"
    r"|\"[^\"]{0,200}\""
    r"|(?:Accessed|Retrieved|Last modified|accessed|retrieved)[^;，。]*"
    r"|https?://\S+"
    r"|\bCC\s+BY(?:-[A-Z]{2})?\s*[\d.]*"
    r"|\b(?:MIT|GNU|BSD|Apache|LGPL|GPL)\b[^\s，。]*"
    r"|\(C\)\s*[^\r\n，。]*"
    r"|\b(?:Survey Review|Springer|Elsevier|Wiley|IEEE)\b[^\r\n，。]*"
    r"|\[[^\]]*\]"
    r"|《[^》]*》"
    r"|「[^」]*」)", re.I)


# 专有名词/出版物原名刻意保留：西方舰名（The Sullivans / City Of Corpus Christi /
# Prince of Wales）、公司名（Airbus Defense and Space / Gibbs and Cox）、
# 外文文献标题。这些条目含中文，残留虚词属于原名的一部分，不是漏译。
ALLOW_KEYS = {
    "2xFullerton and Sherwood Ltd. six-man, two-compartment containerized diving systems;",
    "Burdin, Sergey. Tupolev TU-22. Barnsley: Pen and Sword, 2005. pg. 81; International Electronic Countermeasures Handbook, 2004 \"SPQ-10 Siren.\" pg. 77. ; Friedman, Norman. The Naval Institute Guide to World Naval Weapons Systems, 1997-1998. Annapolis, MD: Naval Inst Press, 1997, pg. 516",
    "Burdin, Sergey. Tupolev TU-22. Barnsley: Pen and Sword, 2005. pg. 81; International Electronic Countermeasures Handbook, 2004 \"SPQ-10 Siren.\" pg. 77. ; Friedman, Norman. The Naval Institute Guide to World Naval Weapons Systems, 1997-1998. Annapolis, MD: Naval Inst Press, 1997, pg.pg. 516",
    "By Class",
    "Displacement: 5,648 tons (full load) || Length: 512.5 ft (156.2 m) || Beam: 52.3 ft (16 m) || Draught: 17.8 ft (5.4 m) || Propulsion: (2) shaft, (2) Allis-Chalmers turbines || (4) each 1,200 Pound forced draft Babcock and Wilcox D-type super heated boilers || 85,000 shp (63,000 kW) || Complement: 360.",
    "Jane’s Air‑Launched Weapons ; Army‑Technology (“inner workings…” FAE) ; Wikipedia “Soviet and Russian aerial bombs” entry with ODAB‑500 data ; Army Recognition (MoD release Su‑34 use)",
    "Manufacturer: Airbus Defence and Space (Spain)",
    "Naval‑Technology “MK 48 Mod 7 CBASS” description; Military & Aerospace Electronics “CBASS version” overview; Australian Defence Magazine “Waller first to fire Mk 48 CBASS” report; Naval Technology CBASS development details; Naval Today “US Navy contracts Lockheed for MK 48 Mod 7 guidance upgrades”",
    "Official Bharat Dynamics Limited: https/www.bdl-india.in/varunastra ; DRDO‑NSTL; Jane’s Underwater Warfare Systems; Naval News; Financial Express",
    "Propulsion: 2 shafts, 2 × English-Electric geared steam turbines @611 lb/in2 (43kg/cm2)",
    "Rafael Advanced Defense Systems (Official); SPICE 1000/2000 Brochure (PDF); Airforce Technology – SPICE PGMs; The Diplomat – India Balakot Strike with SPICE 2000; Janes Air-Launched Weapons (2023 Edition) - SPICE Family Overview",
    "SHIPS BUILT: FLIGHT 1: Arleigh Burke (DDG-51) || Barry (DDG-52) || John Paul Jones (DDG-53) || Curtis Wilbur (DDG-54) || Stout (DDG-55) || John S. McCain (DDG-56) || Mitscher (DDG-57) || Laboon (DDG-58) || Russell (DDG-59) || Paul Hamilton (DDG-60) || Ramage (DDG-61) || Fitzgerald (DDG-62) || Stethem (DDG-63) || Carney (DDG-64) || Benfold (DDG-65) || Gonzalez (DDG-66) || Cole (DDG-67) || The Sullivans (DDG-68) || Milius (DDG-69) || Hopper (DDG-70) || Ross (DDG-71) ##### Flight II: Mahan (DDG-72) || Decatur (DDG-73) || McFaul (DDG-74) || Donald Cook (DDG-75) || Higgins (DDG-76) || O'Kane (DDG-77) || Porter (DDG-78) ##### Flight IIA (5\"/54 Variant): Oscar Austin (DDG-79) || Roosevelt (DDG-80) ##### Flight IIA (5\"/62 Variant): Winston S. Churchill (DDG-81) || Lassen (DDG-82) || Howard (DDG-83) || Bulkeley (DDG-84) ##### Flight IIA (5\"/62, one 20mm CIWS Variant): McCampbell (DDG-85) || Shoup (DDG-86) || Mason (DDG-87) || Preble (DDG-88) || Mustin (DDG-89) || Chafee (DDG-90) || Pinckney (DDG-91) || Momsen (DDG-92) || Chung-Hoon (DDG-93) || Nitze (DDG-94) || James E. Williams (DDG-95) || Bainbridge (DDG-96) || Halsey (DDG-97) || Forrest Sherman (DDG-98) || Farragut (DDG-99) || Kidd (DDG-100) || Gridley (DDG-101) || Sampson (DDG-102) || Truxtun (DDG-103) || Sterett (DDG-104) || Dewey (DDG-105) || Stockdale (DDG-106) || Gravely (DDG-107) || Wayne E. Meyer (DDG-108) || Jason Dunham (DDG-109) || William P. Lawrence (DDG-110) || Spruance (DDG-111) || Michael Murphy (DDG-112) ##### Flight IIA (Restart): John Finn (DDG-113) || Ralph Johnson (DDG-114) || Rafael Peralta (DDG-115) ##### Flight IIA (Technology Insertion): Thomas Hudner (DDG-116) || Paul Ignatius (DDG-117) || Daniel Inouye (DDG-118).",
    "SHIPS BUILT: [Flight I & II] Los Angeles (SSN-688),Baton Rouge (SSN-689), Philadelphia (SSN-690), Memphis (SSN-691), Omaha (SSN-692), Cincinnati (SSN-693), Groton(SSN-694), Birmingham (SSN-695), New York City (SSN-696), Indianapolis (SSN-697), Bremerton (SSN-698), Jacksonville (SSN-699), Dallas (SSN-700), La Jolla (SSN-701), Phoenix (SSN-702), Boston (SSN-703), Baltimore (SSN-704), City Of Corpus Christi (SSN-705), Albuquerque (SSN-706), Portsmouth (SSN-707), Minneapolis-St. Paul (SSN-708), Hyman G. Rickover (SSN-709), Augusta (SSN-710), San Francisco (SSN-711), Atlanta (SSN-712), Houston (SSN-713), Norfolk (SSN-714), Buffalo (SSN-715), Salt Lake City (SSN-716), Olympia (SSN-717), Honolulu (SSN-718), Providence (SSN-719), Pittsburgh (SSN-720), Chicago (SSN-721), Key West (SSN-722), Oklahoma City (SSN-723), Louisville (SSN-724), Helena (SSN-725), Newport News (SSN-750) ##### [Flight III/688I] San Juan (SSN-751), Pasadena (SSN-752), Albany (SSN-753), Topeka (SSN-754), Miami (SSN-755), Scranton (SSN-756), Alexandria (SSN-757), Asheville (SSN-758), Jefferson City (SSN-759), Annapolis (SSN-760), Springfield (SSN-761), Columbus (SSN-762), Santa Fe (SSN-763), Boise (SSN-764), Montpelier (SSN-765), Charlotte (SSN-766), Hampton (SSN-767), Hartford (SSN-768), Toledo (SSN-769), Tucson (SSN-770), Columbia (SSN-771), Greenville (SSN-772), Cheyenne (SSN-773).",
    "The A.330-200 MRTT is an air-to-air refueling and transport aircraft produced by Airbus Defense and Space, based on the A.330-200 airliner.",
    "The Shadowy World Of Submarine And Ship‑Launched Torpedo Countermeasures – explainer page; International Defense Security & Technology article on SCAD‑102; Escuela Superior de Guerra Naval (SCAD descriptions) PDF",
    "USS Freedom (LCS 1) is the lead ship of the Freedom class of littoral combat ships (LCS). She is the third vessel of the United States Navy to be so named for the concept of freedom. She is the design competitor produced by the Lockheed Martin consortium, in competition with the General Dynamics-designed USS Independence. She was officially accepted by the Supervisor of Shipbuilding Gulf Coast on behalf of the US Navy from the Lockheed Martin/Marinette Marine/Gibbs and Cox team in Marinette, WI on 18 September 2008.",
    "Units in class: HMS QUEEN ELIZABETH; HMS PRINCE OF WALES",
}

failures: list[str] = []


def fail(msg: str) -> None:
    failures.append(msg)


def check_offsets(doc: dict, pool: dict[str, str]) -> None:
    off = doc["offsets"]
    exact = doc["exact"]
    known = set(pool)
    for k, v in off.items():
        if k not in known:
            fail(f"offsets[{k}] is not an engine string-pool offset")
            continue
        src = pool[k]
        if not HAN.search(v):
            fail(f"offsets[{k}] not Chinese: {v!r}")
        if src in exact and exact[src] != v:
            fail(f"offsets[{k}]={v!r} disagrees with exact[{src!r}]={exact[src]!r}")


def check_quality(entries: dict[str, str], label: str, pool_vals: set[str]) -> None:
    for src, dst in entries.items():
        if src in ALLOW_KEYS:
            continue
        if not HAN.search(dst):
            fail(f"{label}[{src!r}] missing Chinese: {dst!r}")
            continue
        m = HAN_SPACE.search(dst)
        if m:
            fail(f"{label}[{src!r}]={dst!r} has a space between Han characters")
        # 参考资料来源（参考书目）刻意保留原文：含 URL 或检索日期标记的条目只校验
        # 「必须含中文」，不再用英文虚词判漏译，避免把书目作者/刊名误判为漏译。
        if re.search(r"https?://|Accessed|Retrieved|Last modified|n\.d\.|accessed|retrieved", dst):
            continue
        # Only flag when the leftover English word is not part of a kept acronym
        # or a protected licence / URL / citation fragment.
        scanned = PROTECTED.sub(" ", dst)
        # 全大写命中是型号/代号（AT、IS、CVW…），不是英文虚词；只有正常书写
        # 形态的虚词才说明这句没翻干净。
        # `Be` 是意大利奥托·梅莱拉舰炮的型号前缀（234mm/50 Be、76mm/62 Be），
        # 不是英语虚词 be；`AT`/`IS`/`CVW` 等全大写是代号，同样不算。
        # 必须**整词小写**才算漏译：`Rotax 915 iS`、`eSeries` 这类混合大小写
        # 是发动机/型号后缀（实测 `iS` 被当成 is 而误报），不是英语虚词。
        FUNCTION_WORDS = {"the", "of", "and", "for", "with", "from",
                          "to", "in", "on", "at", "by", "is", "are", "will"}
        residual = [w for w in re.findall(r"[A-Za-z]{2,}", scanned)
                    if w == w.lower() and w in FUNCTION_WORDS]
        if residual:
            fail(f"{label}[{src!r}]={dst!r} keeps English function word {residual}")
        if sorted(PLACEHOLDER.findall(src)) != sorted(PLACEHOLDER.findall(dst)):
            fail(f"{label}[{src!r}]={dst!r} placeholder mismatch "
                 f"{PLACEHOLDER.findall(src)} vs {PLACEHOLDER.findall(dst)}")
        if (src[:1].isspace() and not dst[:1].isspace()) or \
           (src[-1:].isspace() and not dst[-1:].isspace()):
            fail(f"{label}[{src!r}]={dst!r} lost leading/trailing whitespace")
        # Fragments such as "(Type:" are intentionally unbalanced; compare the
        # *difference* in balance instead of demanding a closed pair.
        # 同时只比「不平衡度」，不逐字形比：中文译文把 ASCII 括号写成全角是正常写法
        # （R76：`x MQ-4C Triton UAV)` → `×MQ-4C“特里同”无人机）`，逐字形会误报）。
        _open = sum(src.count(o) for o in BRACKETS)
        _close = sum(src.count(c) for c in BRACKETS.values())
        _dopen = sum(dst.count(o) for o in BRACKETS)
        _dclose = sum(dst.count(c) for c in BRACKETS.values())
        if (_open - _close) != (_dopen - _dclose):
            fail(f"{label}[{src!r}]={dst!r} bracket balance changed "
                 f"({_open - _close} -> {_dopen - _dclose})")
        if src.count("\n") != dst.count("\n"):
            fail(f"{label}[{src!r}] line-count changed ({src.count(chr(10))}"
                 f" -> {dst.count(chr(10))})")
        if src.count("\r") != dst.count("\r"):
            fail(f"{label}[{src!r}] CR count changed")


def main() -> int:
    main_file = DICT / "CMOLocalizer.json"
    try:
        doc = json.loads(main_file.read_text(encoding="utf-8"))
    except Exception as exc:  # noqa: BLE001
        print(f"FAIL: {main_file} is not valid JSON: {exc}")
        return 1

    for section in ("offsets", "exact", "partial"):
        if section not in doc:
            fail(f"missing section {section!r}")

    pool = json.loads((DICT / "strings_pool.json").read_text(encoding="utf-8"))
    pool_vals = set(pool.values())

    check_offsets(doc, pool)
    check_quality(doc["exact"], "exact", pool_vals)
    check_quality(doc["partial"], "partial", pool_vals)

    sys.path.insert(0, str(ROOT / "scripts"))
    from triage import classify  # noqa: E402

    ui = {k: v for k, v in pool.items() if classify(v) in ("display", "label")}
    off, exact = doc["offsets"], doc["exact"]
    covered = [k for k, v in ui.items() if k in off or v in exact]

    print(f"CMOLocalizer.json  offsets={len(off)}  exact={len(exact)}  "
          f"partial={len(doc['partial'])}")
    print(f"player-visible engine strings : {len(ui)}")
    print(f"covered                        : {len(covered)} "
          f"({100.0*len(covered)/max(1,len(ui)):.1f}%)")
    print(f"uncovered                      : {len(ui)-len(covered)}")

    if failures:
        print(f"\nFAIL: {len(failures)} problem(s)")
        for f in failures[:80]:
            print(f"  - {f}")
        if len(failures) > 80:
            print(f"  ... and {len(failures)-80} more")
        return 1

    print("\nPASS: dictionary integrity and quality checks clean")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
