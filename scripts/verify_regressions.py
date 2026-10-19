#!/usr/bin/env python3
"""发版前回归测试（台账 docs/BUG_REGISTRY.md 的可执行部分）。

    python scripts/verify_regressions.py --static     # 无需游戏，package.sh 硬门禁
    python scripts/verify_regressions.py --installer  # 隔离临时目录验证安装/还原/回退（无需游戏）
    python scripts/verify_regressions.py --live       # 需游戏运行（发版前必跑）
    python scripts/verify_regressions.py              # 三者都跑

结果写入 build/regression_report.json；任一用例失败则退出码非零。
新增 bug 时：在 docs/BUG_REGISTRY.md 记一条，并在这里加一条用例（本文件是唯一权威）。
"""
from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
import warnings
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
ENGINE = ROOT / "src/engine/CMOLocalizer.cs"
DICT = ROOT / "dict/CMOLocalizer.json"
REPORT = ROOT / "build/regression_report.json"
SHOTS = ROOT / "evidence/live/regression"

OBJECT_TYPE_TOKENS = ["Aircraft", "Ship", "Submarine", "Facility",
                      "Ground Unit", "Satellite", "Weapon", "Sensor"]
# 游戏真实渲染出的详情页字节数下限（Aircraft 页面含大量小节，阈值更高）
MIN_HTML_LEN = {"Aircraft": 20000}
DEFAULT_MIN_HTML_LEN = 3000   # 空壳页（未生成详情）实测 1541B，真实最小详情页 4194B
FIRST_ROW_OFFSET = (560, 78)   # 列表首行相对窗体左上角，实测可用


# ── 用例框架 ────────────────────────────────────────────────────────────────
class Runner:
    def __init__(self) -> None:
        self.results: list[dict] = []

    def record(self, cid: str, name: str, ok: bool, detail: str,
               evidence: str = "") -> None:
        self.results.append({"id": cid, "name": name, "status": "pass" if ok else "fail",
                             "detail": detail, "evidence": evidence})
        line = f"  [{'PASS' if ok else 'FAIL'}] {cid} {name} — {detail}"
        try:
            print(line, flush=True)
        except UnicodeEncodeError:
            # 控制台是 GBK：任何一个不可编码字符（例如解码失败产生的 U+FFFD）
            # 都会把整轮回归带崩（2026-10-19 实测：package.sh 里直接中止）。
            enc = sys.stdout.encoding or "utf-8"
            print(line.encode(enc, errors="replace").decode(enc, errors="replace"), flush=True)


# ── static：源码/资产/词库断言（防止保护被删、资产漏发） ────────────────────
# R0 二进制标记：部署到游戏目录的引擎必须包含历次修复的关键符号。
# 不用整文件哈希：csc 每次编译都产生不同 MVID/时间戳，重新 build 后哈希必变，
# 但“旧包覆盖回不含修复的引擎”会被标记缺失抓住。
DEPLOY_MARKERS = ["IsProtectedDBViewerCombo", "FindDBViewerForm", "IsTextInputControl", "InstallCrashGuard",
                  "FindLoadScenarioForm", "propertyCache", "MemberKey", "AddPageGrams",
                  # R34（PR#1）：瓦片服务脱离共享线程池后的新增标识符，用于抓“旧包覆盖回无修复引擎”
                  # （不能用 TileClient：旧实现里的 HandleTileClient 含同名子串，会假绿）
                  "ReadTileRequestLine", "tileRequestLogLock"]  # 注：标记必须是元数据标识符（字符串字面量在 #US 堆是 UTF-16，ASCII 搜不到）


# ── R34 地图瓦片服务：独立编译 + 7 模式反射回归 ────────────────────────────────
# 背景（PR#1，2026-10-18 实机与独立测试双重确认的旧实现缺陷）：标签请求被排进游戏
# 共用的 CLR ThreadPool，且请求行读取没有总截止时间 ⇒ ① 池被占满时标签请求根本不执行；
# ② 每 250ms 发一个字节即可永久占住一个池 worker；③ 连接数无上限，96 个空闲连接可
# 占掉游戏 96 个池 worker（反过来拖慢游戏自身）。
# 测试宿主来自贡献者（src/tests/MapTileServerTests.cs）：用反射对真实 CMOLocalizer.dll
# 启动地图服务，不加载游戏、不跑安装器。为什么必须真跑而不是只查源码：三种失败中两种
# 只在并发时序下暴露（trickle 刷新读超时、pressure 连接无上限）。
TILE_CSC = Path("C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe")
TILE_WPF = Path("C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF")
TILE_XAML = Path("C:/Windows/Microsoft.NET/Framework64/v4.0.30319/System.Xaml.dll")
TILE_MODES = ["starvation", "protocol", "idle", "batch", "multi-idle", "trickle", "pressure"]


def _tile_harness(r: Runner, work: Path) -> None:
    """编译引擎与测试宿主，对当前源码跑 7 模式并发回归。"""
    dll, exe = work / "CMOLocalizer.dll", work / "MapTileServerTests.exe"
    refs = [f"-reference:{x}" for x in ("System.dll", "System.Core.dll", "System.Data.dll",
                                        "System.Windows.Forms.dll", "System.Drawing.dll",
                                        "System.Xml.dll", "System.Xml.Linq.dll")]
    refs += [f"-reference:{p}" for p in (TILE_WPF / "WindowsBase.dll",
                                         TILE_WPF / "PresentationCore.dll",
                                         TILE_WPF / "PresentationFramework.dll", TILE_XAML)]
    builds = [
        subprocess.run([str(TILE_CSC), "-nologo", "-target:library", "-platform:anycpu",
                        "-optimize+", f"-out:{dll}", *refs, str(ENGINE),
                        str(ROOT / "src/shared/MapLabelRenderer.cs"),
                        str(ROOT / "src/engine/Reflect.cs")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180),
        subprocess.run([str(TILE_CSC), "-nologo", "-target:exe", "-platform:anycpu",
                        f"-out:{exe}", "-reference:System.dll", "-reference:System.Drawing.dll",
                        str(ROOT / "src/tests/MapTileServerTests.cs")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180),
    ]
    if any(b.returncode for b in builds):
        r.record("R34-static-tile-harness", "R34 瓦片服务 7 模式并发回归", False,
                 "编译失败: " + (builds[0].stderr or builds[1].stderr or "").strip()[:160].replace("\n", " "),
                 "src/tests/MapTileServerTests.cs")
        return

    passed, evidence = [], []
    for mode in TILE_MODES:
        status = "FAIL"
        for attempt in (1, 2):
            fx = work / f"fx-{mode}"
            # 重跑也要全新 fixture：BATCH 断言服务日志恰为 16 条，旧日志会让它变成 32 条
            shutil.rmtree(fx, ignore_errors=True)
            for set_name in ("Synoptic", "Cities"):
                dst = fx / "WW/PlacenameSets" / set_name
                dst.mkdir(parents=True)
                shutil.copyfile(ROOT / "assets/placenames" / f"{set_name}.wwp", dst / "0000_0000.wwp")
            run = subprocess.run([str(exe), str(dll), str(fx), mode], capture_output=True,
                                 text=True, encoding="utf-8", errors="replace", timeout=60)
            if run.returncode == 0:
                status = "ok" + ("(重试)" if attempt == 2 else "")
                mark = [l for l in run.stdout.splitlines()
                        if l.startswith(("POOL ", "MULTI_IDLE", "TRICKLE", "PRESSURE", "BATCH", "RECOVERY"))]
                evidence.append(f"{mode}: " + (mark[0][:90] if mark else "PASS"))
                break
        passed.append(status.startswith("ok"))
    r.record("R34-static-tile-harness", "R34 瓦片服务 7 模式并发回归",
             all(passed), f"{sum(passed)}/{len(TILE_MODES)} 模式通过",
             " || ".join(evidence) or "无输出")


def run_static(r: Runner) -> None:
    print("== static 用例 ==", flush=True)
    src = ENGINE.read_text(encoding="utf-8")

    # R0：环境一致性——游戏目录必须装的是含本轮修复的产物。
    # （历史踩坑：发版前被旧安装包覆盖成不含修复的引擎，回归“失败”实为环境错）
    drift = []
    if GAME.is_dir():
        deployed_json, deployed_dll = GAME / "CMOLocalizer.json", GAME / "CMOLocalizer.dll"
        if not deployed_json.is_file() or deployed_json.read_bytes() != DICT.read_bytes():
            drift.append("stale in game: CMOLocalizer.json")
        if not deployed_dll.is_file():
            drift.append("missing: CMOLocalizer.dll")
        else:
            blob = deployed_dll.read_bytes()
            lack = [m for m in DEPLOY_MARKERS if m.encode() not in blob]
            if lack:
                drift.append(f"deployed engine lacks fix markers: {lack}")
    r.record("R0-static-env", "R0 游戏目录引擎/词库含本轮修复", not drift,
             "; ".join(drift) or f"词库同哈希 + 引擎标记齐全{tuple(DEPLOY_MARKERS)}",
             "scripts/deploy.sh 或安装包")

    has_helper = "private static bool IsProtectedDBViewerCombo(ComboBox cb)" in src
    generic_guarded = re.search(
        r"LocalizeComboBoxSafe\(ComboBox cb\)\s*\{[^}]*?IsProtectedDBViewerCombo\(cb\)", src, re.S)
    dedicated_guarded = re.search(
        r"LocalizeDBViewerComboBox\(ComboBox cb\)\s*\{.*?IsProtectedDBViewerCombo\(cb\)", src, re.S)
    hwnd_fallback = re.search(r"FindDBViewerForm\(\)[\s\S]{0,900}?EnumWindows", src)
    r.record("R1-static-guard", "R1 CB_ObjectType 保护（通用+专用路径）",
             bool(has_helper and generic_guarded and dedicated_guarded),
             f"helper={bool(has_helper)} generic={bool(generic_guarded)} dedicated={bool(dedicated_guarded)}",
             str(ENGINE.relative_to(ROOT)))

    r.record("R1-static-hwnd", "R1 辅助线程窗体解析（EnumWindows 回退）", bool(hwnd_fallback),
             "FindDBViewerForm 使用 EnumWindows + Control.FromHandle",
             str(ENGINE.relative_to(ROOT)))

    skip = re.search(r"if \(ctrl is ComboBox \|\| ctrl is ListControl \|\| ctrl is UpDownBase\)\s*\{",
                     src)
    r.record("R8-static-text-ban", "R8 禁止改写 ComboBox/ListControl/UpDownBase 的 .Text",
             bool(skip), "TranslateControlText 跳过分支存在", str(ENGINE.relative_to(ROOT)))

    missing = []
    if GAME.is_dir():
        for set_name, rel in (("Synoptic", "WW/PlacenameSets/Synoptic/0000_0000.wwp"),
                              ("Cities", "WW/PlacenameSets/Cities/0000_0000.wwp")):
            srcf, dstf = ROOT / "assets/placenames" / f"{set_name}.wwp", GAME / rel
            if not srcf.is_file():
                missing.append(f"asset missing: {srcf.name}")
            elif dstf.is_file() and srcf.read_bytes() != dstf.read_bytes():
                missing.append(f"stale in game: {rel}")
    r.record("R2-static-placenames", "R2 汉化地名集资产存在且已分发",
             not missing, "; ".join(missing) or "Synoptic+Cities 与游戏目录逐字节一致",
             "assets/placenames")

    eng_names = set(re.findall(r'"(Btn_[A-Za-z0-9_\-]+\.png)"', src))
    have = {p.name for p in (ROOT / "assets/symbols_menu").glob("*.png")}
    lack = sorted(eng_names - have)
    r.record("R5-static-buttons", "R5 中文按钮图覆盖引擎期望文件名",
             bool(eng_names) and not lack,
             f"expect={len(eng_names)} missing={lack or 'none'}", "assets/symbols_menu")

    doc = json.loads(DICT.read_text(encoding="utf-8"))
    bad = [k for k in doc["offsets"]
           if any(t in k for t in ("Data Source", "Connection", "SELECT ", " AND ", "INSERT "))]
    bad += [k for k, v in doc["offsets"].items()
            if any(t in str(v) for t in ("Data Source", "SELECT ", " AND "))]
    r.record("R6-static-sql", "R6 offsets 不含 SQL/连接串片段",
             not bad, f"offenders={bad[:5] or 'none'}", "dict/CMOLocalizer.json")

    term = subprocess.run([sys.executable, str(ROOT / "scripts/verify_terminology.py")],
                          cwd=str(ROOT), capture_output=True, text=True)
    tail = (term.stdout or term.stderr).strip().splitlines()[-1:] or [""]
    r.record("R7-static-terms", "R7 术语/机翻门禁", term.returncode == 0, tail[0][:120],
             "scripts/verify_terminology.py")

    # R18：共享状态层单测（设置/清单/包缓存/门控），测试宿主由 build.sh 产出
    shared = ROOT / "build/CMOSharedTest.exe"
    if not shared.is_file():
        r.record("R18-static-selftest", "R18 共享状态层单测（设置/清单/缓存/门控）", False,
                 "缺少 build/CMOSharedTest.exe，请先跑 bash scripts/build.sh")
    else:
        p = subprocess.run([str(shared)], capture_output=True, text=True,
                           encoding="utf-8", errors="replace")
        out = (p.stdout or "") + (p.stderr or "")
        fails = [ln for ln in out.splitlines() if ln.startswith("FAIL ")]
        passes = [ln for ln in out.splitlines() if ln.startswith("PASS ")]
        r.record("R18-static-selftest", "R18 共享状态层单测（设置/清单/缓存/门控）",
                 p.returncode == 0 and not fails,
                 f"{len(passes)} 项 PASS" + (f"，失败 {fails[:3]}" if fails else ""),
                 "src/shared/CMOSharedTest.cs")
        # R16：降级提示门控真值表（负向：本地==远端 / 更旧但无缓存 → 不得提示回退）
        gate_ok = ("PASS gate-truth-table" in out and "PASS gate-empty-inputs" in out
                   and "PASS pkgversion-filename-wins" in out)
        r.record("R16-static-gate", "R16 降级提示门控（无缓存不得提示回退）", gate_ok,
                 "gate-truth-table + gate-empty-inputs + pkgversion-filename-wins 均通过"
                 if gate_ok else "门控断言缺失或失败", "src/shared/CMOShared.cs")

    # R17-static-pack-readme：绿色包说明（打包为 汉化说明.md）必须告知可自动登记安装状态
    readme = (ROOT / "README.md").read_text(encoding="utf-8", errors="replace")
    pack_doc_ok = ("登记安装状态" in readme) and ("--adopt" in readme)
    r.record("R17-static-pack-readme", "R17 绿色包说明含自动登记安装状态（adopt）", pack_doc_ok,
             "README.md 含 adopt 说明（随包为 汉化说明.md）" if pack_doc_ok else "README.md 缺少 adopt 说明",
             "README.md")

    # R19：输入期不得改写输入控件正文 + 崩溃取证 + 回写拉锯熔断。
    # 实证（2026-10-16 用户输入 m 闪退）：DarkUI 自定义输入框不继承 TextBoxBase，
    # 旧代码把它们当普通标签翻译 → 引擎写「米」、游戏写回「m」→ 日志里
    # “UI Controls Translated count … (Sample: m -> 米)”每 2.5s 涨 50，直到
    # 进程以 0xc00000fd 栈溢出闪退。
    input_helper = re.search(r"private static bool IsTextInputControl\(Control ctrl\)", src)
    input_early_return = re.search(
        r"IsTextInputControl\(ctrl\)\)\s*\{\s*TranslateWatermark\(ctrl\);\s*\}", src)
    input_skip = re.search(r"if \(IsTextInputControl\(ctrl\)\) return;", src)
    crash_guard = ("private static void InstallCrashGuard()" in src
                   and "CMOLocalizer_Crash.log" in src
                   and "AppDomain.CurrentDomain.UnhandledException" in src
                   and "Application.ThreadException" in src)
    loop_guard = ("private static bool AllowWrite(int key, string target, string what)" in src
                  and "WRITE LOOP BLOCKED" in src)
    depth_guard = ("private static bool EnterMutation(string what)" in src
                   and "MUTATION DEPTH CAP" in src)
    r.record("R19-static-input-guard", "R19 输入控件正文不被改写 + 崩溃取证 + 熔断",
             bool(input_helper and input_early_return and input_skip and crash_guard
                  and loop_guard and depth_guard),
             "IsTextInputControl/早退/InstallCrashGuard/AllowWrite/EnterMutation 均已就位"
             if (input_helper and input_early_return and input_skip and crash_guard and loop_guard and depth_guard)
             else f"helper={bool(input_helper)} 早退={bool(input_early_return)} skip={bool(input_skip)} "
                  f"crash={crash_guard} loop={loop_guard} depth={depth_guard}",
             "src/engine/CMOLocalizer.cs")

    # R20：战役内剧本名与窗口标签必须进词典（含工坊叠译自愈项）。
    dict_doc = json.loads(DICT.read_text(encoding="utf-8"))
    exact = dict_doc["exact"]
    meta_path = ROOT / "dict/scenario_meta.json"
    camp_missing: list[str] = []
    camp_total = 0
    if meta_path.is_file():
        meta = json.loads(meta_path.read_text(encoding="utf-8"))
        names = sorted({n for c in meta.get("campaigns", []) for n in c.get("names", [])})
        camp = [n for n in names if not n.startswith("Local Video:")]
        camp_total = len(camp)
        camp_missing = [n for n in camp if n not in exact]
    labels = ["Resume from save", "Delete selected save",
              "Select one of the campaigns available to start a new session, "
              "or resume an existing campaign session from a save."]
    label_missing = [s for s in labels if s not in exact]
    nested_ok = (exact.get("Steam Workshop") == "创意工坊"
                 and exact.get("创意工坊 (Steam Workshop)") == "创意工坊"
                 and exact.get("创意工坊 (创意工坊 (Steam Workshop))") == "创意工坊")
    r.record("R20-static-campaign-names", "R20 战役内剧本名/窗口标签已入词典（工坊不叠译）",
             bool(camp_total and not camp_missing and not label_missing and nested_ok),
             f"战役剧本名 {camp_total - len(camp_missing)}/{camp_total}"
             + (f"，缺: {camp_missing[:5]}" if camp_missing else "")
             + (f"，窗口标签缺: {label_missing}" if label_missing else "")
             + ("" if nested_ok else "，工坊叠译未被修正"),
             "dict/CMOLocalizer.json + dict/src/campaign_ui.json")

    # R21：发版打包清单 + 统一入库检查必须存在且互相引用。
    manifest = ROOT / "docs/RELEASE_MANIFEST.md"
    rc_path = ROOT / "scripts/release_check.py"
    mtext = manifest.read_text(encoding="utf-8", errors="replace") if manifest.is_file() else ""
    rtext = rc_path.read_text(encoding="utf-8", errors="replace") if rc_path.is_file() else ""
    sop = (ROOT / "docs/RELEASE_TEST_SOP.md").read_text(encoding="utf-8", errors="replace")
    manifest_ok = ("CMO_Chinese_Setup_v<VER>.exe" in mtext and "CMO_Chinese_Pack_<VER>.zip" in mtext
                   and "禁止入库清单" in mtext and "scripts/release_check.py" in mtext)
    rc_ok = ("def check_regression_report" in rtext and "def check_artifacts" in rtext
             and "def check_pack_contents" in rtext and "RELEASE_MANIFEST.md" in rtext)
    sop_ok = "release_check.py" in sop and "RELEASE_MANIFEST.md" in sop
    r.record("R21-static-manifest", "R21 发版清单 + 统一入库检查（且 SOP 引用）",
             bool(manifest_ok and rc_ok and sop_ok),
             f"manifest={manifest_ok} release_check={rc_ok} sop={sop_ok}",
             "docs/RELEASE_MANIFEST.md + scripts/release_check.py + docs/RELEASE_TEST_SOP.md")

    # R22：字节码缓存/凭据文件不得含明文令牌。
    # 历史：scripts/__pycache__/translate_worker.cpython-312.pyc 里藏着明文 sk- 令牌，
    # 而 __pycache__/ 被 .gitignore 忽略 → 只扫 git ls-files 的密钥门禁完全看不见。
    secret_rx = re.compile(r"sk-[A-Za-z0-9]{20,}")
    caches = sorted(p for p in ROOT.glob("**/__pycache__/*.pyc") if ".git" not in p.parts)
    leaks = []
    for p in caches:
        try:
            if secret_rx.search(p.read_text(encoding="utf-8", errors="ignore")):
                leaks.append(str(p.relative_to(ROOT)))
        except Exception:
            continue
    orphan_pyc = sorted(str(p.relative_to(ROOT)) for p in caches
                        if not (p.parent.parent / (p.name.split(".")[0] + ".py")).is_file())
    r.record("R22-static-bytecode-secrets", "R22 字节码缓存不含明文令牌且无无源孤儿缓存",
             bool(not leaks and not orphan_pyc),
             f"扫描 {len(caches)} 个 .pyc"
             + (f"；命中: {leaks[:3]}" if leaks else "；无令牌命中")
             + (f"；无源孤儿: {orphan_pyc[:3]}" if orphan_pyc else "；无无源孤儿缓存"),
             "scripts/__pycache__/")

    # R26：scripts/*.py 必须全部可解析（排除 archive/ 子目录）。
    # 历史：scripts/translate_shard2.py 580 行在字典于第 82 行 "}" 闭合后又跟裸键值项、
    # 末行 580 又是孤立 "}"，ast.parse 直接 SyntaxError（unmatched '}' @580）；它从不写出结果，
    # 却被 migrate.sh 的拷贝清单以 [ -f ] 守卫方式引用。（评审 finding O-08 / COR-003）
    # 必须用 ast.parse 而不是 compileall：后者会写出 .pyc，与 R22「不得有无源孤儿缓存」冲突。
    parse_targets = sorted(p for p in (ROOT / "scripts").glob("*.py") if p.is_file())  # 非递归 → 排除 archive/
    parse_bad, parse_warn = [], []
    for p in parse_targets:
        try:
            # 捕获而非放任 SyntaxWarning 打到 stderr（历史脚本有非法转义序列）：只统计，不算解析失败。
            with warnings.catch_warnings(record=True) as caught:
                warnings.simplefilter("always")
                ast.parse(p.read_text(encoding="utf-8", errors="replace"), filename=str(p))
            if any(issubclass(w.category, SyntaxWarning) for w in caught):
                parse_warn.append(p.name)
        except SyntaxError as e:
            parse_bad.append(f"{p.name}:{e.lineno}:{e.msg}")
        except Exception as e:  # 读取/编码异常也不得静默通过
            parse_bad.append(f"{p.name}:{type(e).__name__}:{e}")
    r.record("R26-static-scripts-parse", "R26 scripts/*.py 全部可解析（排除 archive/）",
             not parse_bad,
             f"ast.parse 解析 {len(parse_targets)} 个脚本"
             + (f"；不可解析 {len(parse_bad)} 个: {parse_bad[:3]}" if parse_bad else "；全部可解析")
             + (f"；{len(parse_warn)} 个含 SyntaxWarning（非解析失败）: {parse_warn[:3]}" if parse_warn else ""),
             "scripts/*.py（不含 scripts/archive/）")

    # R24：词典必须能从源批次重建（可复现）。
    # 历史：直接跑一次 build_dict 会 ①把 LF 全写成 CRLF（+49KB 纯换行 churn，违反「保持 LF/最小 diff」）
    # ②把工坊页签值回退成非幂等的「创意工坊 (Steam Workshop)」（legacy_fixes 按字母序后加载覆盖）
    # ③丢掉长标签（被 is_script_or_schema 误判，应放 labels.json）与 offset 7916
    # （被 identifiers 过滤静默跳过，应放入人工核验过的 force_offsets.json）。
    # 现在 build_dict 重跑后与已提交词典字节一致，本用例钉住这四个前提。
    crlf = DICT.read_bytes().count(b"\r\n")
    try:
        s_legacy = json.loads((ROOT / "dict/src/legacy_fixes.json").read_text(encoding="utf-8"))
        s_force = json.loads((ROOT / "dict/src/force_offsets.json").read_text(encoding="utf-8"))
        s_labels = json.loads((ROOT / "dict/src/labels.json").read_text(encoding="utf-8"))
    except Exception:
        s_legacy, s_force, s_labels = {}, {}, {}
    long_label = ("Select one of the campaigns available to start a new session, "
                  "or resume an existing campaign session from a save.")
    btext = (ROOT / "scripts/build_dict.py").read_text(encoding="utf-8")
    repro = {
        "词典纯 LF": crlf == 0,
        "legacy_fixes 工坊幂等": s_legacy.get("Steam Workshop") == "创意工坊",
        "force_offsets 含 7916": s_force.get("7916") == "创意工坊",
        "长标签在源批次(labels)": long_label in s_labels,
        "写入器强制 LF": 'newline="\\n"' in btext,
    }
    r.record("R24-static-dict-reproducible", "R24 词典可复现（LF/工坊幂等/长标签/7916）",
             all(repro.values()),
             "；".join(f"{k}={'ok' if v else 'FAIL'}" for k, v in repro.items()),
             "dict/src/{legacy_fixes,force_offsets,labels}.json + scripts/build_dict.py")

    # R33：数据库大面积残留英文与复合字段修复（R33 门禁）。
    # 覆盖：① 描述正文 >= 16300 条；② 枚举额外库 >= 700 条；③ 记录名库 >= 9000 条；
    # ④ 关键代表串与复合规则在 CMOLocalizer.cs 与词典中生效。
    try:
        s_desc = json.loads((ROOT / "dict/src/db_descriptions.json").read_text(encoding="utf-8"))
        s_enum = json.loads((ROOT / "dict/src/db_enums_extra.json").read_text(encoding="utf-8"))
        s_rec = json.loads((ROOT / "dict/src/db_record_names.json").read_text(encoding="utf-8"))
    except Exception:
        s_desc, s_enum, s_rec = {}, {}, {}
    r33_probes = [
        "Unmanned Aerial Vehicle (UAV)", "Undefined (Use Below)",
        "Comms Monitor and Jammer", "Generic Comms Jammer Pod [Advanced]",
        "Recon radius. Cruise at optimum altitude and speed. No form-up time. 10% reserves."
    ]
    r33_miss = [p for p in r33_probes if p not in exact]
    r33_rules = ("(WRA: " in src and "Autonomy Level:" in src and "(ex:" in src and "Recon radius." in src)
    r33_ok = (len(s_desc) >= 16300 and len(s_enum) >= 700 and len(s_rec) >= 9000
              and not r33_miss and r33_rules)
    r.record("R33-static-db-records", "R33 数据库正文/枚举/记录名/复合规则覆盖",
             r33_ok,
             f"desc={len(s_desc)} enums={len(s_enum)} records={len(s_rec)} probes={len(r33_probes)-len(r33_miss)}/{len(r33_probes)} rules={r33_rules}",
             "dict/src/{db_descriptions,db_enums_extra,db_record_names}.json + src/engine/CMOLocalizer.cs")

    # R38：池级注入的形状安全（自绘面板的汉化通道 + 不得改坏游戏内部串）。
    # 背景：目标状态等**自绘面板**不经控件/WPF/WebView 通道，只读游戏字符串池，
    # 所以库值/枚举值必须池级注入；但注入到 SQL 片段/格式串/控件名上会当场改坏
    # 游戏（实例：`LongName ASC` 是 ORDER BY 片段）。
    r38 = {}
    try:
        sys.path.insert(0, str(ROOT / "scripts"))
        from inject_filter import injection_safe as _safe   # noqa: PLC0415
        _bd = (ROOT / "scripts/build_dict.py").read_text(encoding="utf-8")
        r38["新源已放行"] = all(n in _bd for n in ("pool_display_inject.json", "db_enum_displays.json"))
        _pool = json.loads((ROOT / "dict/strings_pool.json").read_text(encoding="utf-8"))
        _off_of = {v: int(k) for k, v in _pool.items() if isinstance(v, str)}
        _built = json.loads(DICT.read_text(encoding="utf-8"))["offsets"]
        _need = ["Aircraft - High manouverability fighter (F-14, F-15, F-16, MiG-29, Su-27)",
                 "Aircraft - Unspecified", "Multirole (Fighter/Attack)"]
        _injected = [s for s in _need if _off_of.get(s) is not None and str(_off_of[s]) in _built]
        r38["面板串已注入"] = len(_injected) == len(_need)
        _forbidden = ["LongName ASC", "Description ASC", "FULLNAME ASC", "{0} seconds",
                      'Get-AuthenticodeSignature "', "e.Message: {0}", "Aircraft", "Ground Unit",
                      ", Hypothetical", ", Deprecated", ", Deprecated ", "Name LIKE '%", "Description LIKE '%"]
        _leaked = [s for s in _forbidden if _off_of.get(s) is not None and str(_off_of[s]) in _built]
        r38["危险串未注入"] = not _leaked
        # 反向不变量：不能把合法的界面标签（含单位缩写）误杀——窄口径剪枝的预防针
        r38["合法标签未被误杀"] = (_off_of.get("Acoustic (1-10 kHz)") is not None
                              and str(_off_of["Acoustic (1-10 kHz)"]) in _built)
        _chk = subprocess.run([sys.executable, str(ROOT / "build/gen_pool_display_inject.py"), "--check"],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        r38["生成器与池一致"] = _chk.returncode == 0
        _d38 = (f"放行={r38['新源已放行']} 面板串={len(_injected)}/{len(_need)} "
                f"泄漏={_leaked} 误杀={not r38['合法标签未被误杀']} 生成器={(_chk.stdout or '').strip()[:40]}")
    except Exception as exc:  # noqa: BLE001
        r38["异常"] = False
        _d38 = f"异常: {type(exc).__name__}: {exc}"
    r.record("R38-static-pool-inject", "R38 池级注入：自绘面板串已注入且无危险形状",
             all(r38.values()), _d38,
             "scripts/{build_dict,inject_filter}.py + build/gen_pool_display_inject.py")

    # R41：地图瓦片图层的引擎标识符绝不得池级注入。
    # 2026-10-19 实机事故：R38 把 `Stamen Terrain - Labels ` / `Stamen Labels` 这类
    # **图层名/缓存目录查找键**当显示串注入后，标签图层解析失败，整张地图无任何文字；
    # 日志特征是从此只有 `LocalTile rejected GET /MapTiles/Labels/g`、再无 `LocalTile z=…` 渲染行。
    r41 = {}
    try:
        _ids = json.loads((ROOT / "dict/src/identifiers.json").read_text(encoding="utf-8"))
        _idlist = set(_ids.get("identifiers", [])) if isinstance(_ids, dict) else set()
        _tile_keys = ["Stamen Terrain", "Stamen Terrain ", "Stamen Terrain - Labels ",
                      "Stamen Terrain - Lines ", "Stamen Lines", "Stamen Labels",
                      "Unsupported layer for tile loading: "]
        _built41 = json.loads(DICT.read_text(encoding="utf-8"))["offsets"]
        _pool41 = json.loads((ROOT / "dict/strings_pool.json").read_text(encoding="utf-8"))
        _off41 = {v: int(k) for k, v in _pool41.items() if isinstance(v, str)}
        r41["已列入 identifiers"] = all(s in _idlist for s in _tile_keys)
        _leak41 = [s for s in _tile_keys
                   if _off41.get(s) is not None and str(_off41[s]) in _built41]
        r41["未注入 offsets"] = not _leak41
        _d41 = (f"identifiers={len(_idlist)} 图层键={len(_tile_keys)} 泄漏={_leak41}")
    except Exception as exc:  # noqa: BLE001
        r41["异常"] = False
        _d41 = f"异常: {type(exc).__name__}: {exc}"
    r.record("R41-static-tile-layer-ids", "R41 地图瓦片图层标识符不得池级注入（否则地图无文字）",
             all(r41.values()), _d41, "dict/src/identifiers.json")

    # R39：引擎翻译行为（混合前缀 + 库枚举值），用真引擎 DLL 跑 C# 宿主。
    # 背景：游戏把 "Inherited, " 与**已汉化**的值拼在一起，旧实现因「整串含中文」
    # 跳过整串，英文前缀就留在条令下拉框里（用户 2026-10-18 实测）。
    _eng_src = ENGINE.read_text(encoding="utf-8")
    r39 = {
        "helper": "FixMixedEnglishPrefix" in _eng_src,
        "字符串路径": "mixedPrefix = FixMixedEnglishPrefix(raw)" in _eng_src,
        "下拉项路径": "mixedItem = FixMixedEnglishPrefix(t)" in _eng_src,
    }
    import tempfile as _tempfile  # noqa: PLC0415
    _w39 = Path(_tempfile.mkdtemp(prefix="cmo-r39-"))
    try:
        _dll39, _exe39 = _w39 / "CMOLocalizer.dll", _w39 / "t.exe"
        _refs39 = [f"-reference:{x}" for x in ("System.dll", "System.Core.dll", "System.Data.dll",
                                               "System.Windows.Forms.dll", "System.Drawing.dll",
                                               "System.Xml.dll", "System.Xml.Linq.dll")]
        _refs39 += [f"-reference:{p}" for p in (TILE_WPF / "WindowsBase.dll",
                                                TILE_WPF / "PresentationCore.dll",
                                                TILE_WPF / "PresentationFramework.dll", TILE_XAML)]
        _b1 = subprocess.run([str(TILE_CSC), "-nologo", "-target:library", "-platform:anycpu",
                              "-optimize+", f"-out:{_dll39}", *_refs39, str(ENGINE),
                              str(ROOT / "src/shared/MapLabelRenderer.cs"),
                              str(ROOT / "src/engine/Reflect.cs")],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
        _b2 = subprocess.run([str(TILE_CSC), "-nologo", "-target:exe", "-platform:anycpu",
                              f"-out:{_exe39}", str(ROOT / "build/test_db_translation.cs")],
                             capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
        if _b1.returncode or _b2.returncode:
            r39["编译"] = False
            _d39 = "编译失败: " + (_b1.stderr or _b2.stderr or "")[:120].replace("\n", " ")
        else:
            r39["编译"] = True
            # 宿主输出是控制台编码（中文 Windows 为 GBK），按 _decode 回落链解码，
            # 否则会拿到 U+FFFD 并污染报告。
            _run39 = subprocess.run([str(_exe39), str(ROOT)], capture_output=True, timeout=180)
            _out39 = _decode(_run39.stdout or b"") + _decode(_run39.stderr or b"")
            r39["行为"] = _run39.returncode == 0
            _d39 = f"rc={_run39.returncode} 用例通过={_out39.count('--> OK')} {r39}"
    except Exception as exc:  # noqa: BLE001
        r39["异常"] = False
        _d39 = f"异常: {type(exc).__name__}: {exc}"
    finally:
        shutil.rmtree(_w39, ignore_errors=True)
    r.record("R39-static-mixed-prefix", "R39 混合串前缀修复（真引擎行为）",
             all(r39.values()), _d39, "build/test_db_translation.cs + src/engine/CMOLocalizer.cs")

    # R42：「英文前缀 + 已汉化尾」的**接线**。R39 只门禁了 TranslateString 函数本身，
    # 而条目循环在调翻译之前就 `ContainsChinese(raw)) continue`，函数再好也到不了；
    # 用户 2026-10-19 看到的就是这个空门：条令/任务编辑器里仍是 `Inherited, <中文>`。
    r42 = {}
    _d42 = ""
    try:
        def _order(fn_marker: str, fix_marker: str, guard_marker: str) -> bool:
            """函数体内 fix 必须先于 guard 出现（即先修混合前缀再走「含中文就跳过」）。"""
            start = _eng_src.find(fn_marker)
            if start < 0:
                return False
            end = _eng_src.find("\n        }", start)   # 函数体结束（同一缩进层）
            body = _eng_src[start:end if end > start else start + 4000]
            i_fix, i_guard = body.find(fix_marker), body.find(guard_marker)
            return i_fix >= 0 and i_guard >= 0 and i_fix < i_guard

        r42["下拉项先修后跳"] = _order("private static void TranslateComboItems",
                                     "TryFixMixedBeforeChineseGuard", "ContainsChinese(raw)) continue")
        r42["列表项先修后跳"] = _order("private static void LocalizeListBoxSafe",
                                     "TryFixMixedBeforeChineseGuard", "ContainsChinese(raw)) continue")
        r42["助手存在"] = "private static string TryFixMixedBeforeChineseGuard" in _eng_src

        import tempfile as _tempfile42  # noqa: PLC0415
        _w42 = Path(_tempfile42.mkdtemp(prefix="cmo-r42-"))
        try:
            _dll42, _exe42 = _w42 / "CMOLocalizer.dll", _w42 / "mixed.exe"
            _refs42 = [f"-reference:{x}" for x in ("System.dll", "System.Core.dll", "System.Data.dll",
                                                    "System.Windows.Forms.dll", "System.Drawing.dll",
                                                    "System.Xml.dll", "System.Xml.Linq.dll")]
            _refs42 += [f"-reference:{p}" for p in (TILE_WPF / "WindowsBase.dll",
                                                    TILE_WPF / "PresentationCore.dll",
                                                    TILE_WPF / "PresentationFramework.dll", TILE_XAML)]
            _b42a = subprocess.run([str(TILE_CSC), "-nologo", "-target:library", "-platform:anycpu",
                                    "-optimize+", f"-out:{_dll42}", *_refs42, str(ENGINE),
                                    str(ROOT / "src/shared/MapLabelRenderer.cs"),
                                    str(ROOT / "src/engine/Reflect.cs")],
                                   capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
            _b42b = subprocess.run([str(TILE_CSC), "-nologo", "-target:exe", "-platform:anycpu",
                                    f"-out:{_exe42}", f"-reference:{_dll42}",
                                    str(ROOT / "src/tests/MixedPrefixTests.cs")],
                                   capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=180)
            if _b42a.returncode or _b42b.returncode:
                r42["编译"] = False
                _d42 = "编译失败: " + (_b42a.stderr or _b42b.stderr or "")[:120].replace("\n", " ")
            else:
                r42["编译"] = True
                _run42 = subprocess.run([str(_exe42), str(ROOT), str(_dll42)], capture_output=True,
                                        timeout=180, cwd=str(_w42))
                _out42 = _decode(_run42.stdout or b"") + _decode(_run42.stderr or b"")
                r42["行为"] = _run42.returncode == 0 and "ALL PASS" in _out42
                _d42 = (f"rc={_run42.returncode} OK={_out42.count('--> OK')} "
                        f"FAIL={_out42.count('--> FAIL')} {r42}")
        finally:
            shutil.rmtree(_w42, ignore_errors=True)
    except Exception as exc:  # noqa: BLE001
        r42["异常"] = False
        _d42 = f"异常: {type(exc).__name__}: {exc}"
    # R44：组合翻译（结构规则 + 术语表）。R8 当年因为「按词猜译」产出中英夹生串而整块删除，
    # 所以这里必须证明：①结构规则对**没见过的**组合也能成句；②碰到生词一律放弃（宁可英文）；
    # ③术语表真的进了产物；④词典里不存在「本可组合修复却仍是半译」的条目。
    r44 = {}
    _d44 = ""
    try:
        import sys as _sys44  # noqa: PLC0415
        _sys44.path.insert(0, str(ROOT / "scripts"))
        from compose_terms import compose as _compose44, load_terms as _load_terms44, residue_words as _residue44  # noqa: PLC0415
        _terms44 = _load_terms44()
        _doc44 = json.loads((ROOT / "dict" / "CMOLocalizer.json").read_text(encoding="utf-8"))
        _exact44 = _doc44.get("exact") or {}
        _words44 = _doc44.get("words") or {}

        fixtures = {
            "151 ft": "151 英尺",
            "2500 m": "2500 米",
            "Airfield (73 units)": "机场（73 个单位）",
            "Hardened Aircraft Shelter (6x Medium Aircraft)": "加固机堡（6 个中型飞机）",
            "AvGas (780k Liter Underground Tank)": "航空汽油（780k 升地下油罐）",
            "Underground Tank": "地下油罐",
        }
        wrong = {k: _compose44(k, _terms44, _exact44) for k, v in fixtures.items() if _compose44(k, _terms44, _exact44) != v}
        r44["结构规则"] = not wrong
        # 生词必须整体放弃：宁可英文，也不能拼出中英夹生串
        r44["生词放弃"] = all(_compose44(x, _terms44, _exact44) is None
                              for x in ("Domain Kill", "Foo Bar Baz", "Some Unknown Kit (3x Thing)"))
        r44["术语入产物"] = len(_words44) >= 120 and _words44.get("Airfield") == "机场"
        unapplied = {}
        for en, zh in _exact44.items():
            if len(en) > 60 or "http" in en or '"' in en or ";" in en:
                continue
            if not _residue44(en, zh, _terms44):
                continue
            better = _compose44(en, _terms44, _exact44)
            if better and better != zh:
                unapplied[en] = better
        r44["半译已修"] = not unapplied
        _d44 = (f"术语={len(_terms44)} words={len(_words44)} "
                f"规则不符={len(wrong)} 可修未修={len(unapplied)}"
                + (f" 例:{list(unapplied)[:2]}" if unapplied else ""))
    except Exception as exc:  # noqa: BLE001
        r44["异常"] = False
        _d44 = f"异常: {type(exc).__name__}: {exc}"
    r.record("R44-static-compose-rules",
             "R44 组合翻译：结构规则成句 + 生词一律放弃 + 半译条目已自动化修复",
             all(r44.values()), _d44,
             "scripts/compose_terms.py + dict/src/terms.json + dict/CMOLocalizer.json")

    r.record("R42-static-mixed-prefix-wiring",
             "R42 混合串修正的接线：先修前缀再走「含中文就跳过」守卫",
             all(r42.values()), _d42,
             "src/tests/MixedPrefixTests.cs + src/engine/CMOLocalizer.cs")

    # R34：地图瓦片服务（并发契约 + 真跑的 7 模式回归）
    tile_contract = {
        "脱离共享池": "ThreadPool.QueueUserWorkItem(_ => HandleTileClient" not in src,
        "有界队列": "BlockingCollection<TileClient>" in src and "TILE_QUEUE_CAPACITY" in src,
        "专用工作线程": "TILE_WORKERS" in src,
        "首行总预算": "TILE_CLIENT_TIMEOUT_MS" in src and "ReadTileRequestLine" in src,
        "拒绝非法请求": "400 Bad Request" in src,
        "诊断采样上限": "TILE_REQUEST_LOG_LIMIT" in src,
    }
    r.record("R34-static-tile-contract", "R34 瓦片服务并发契约（不占用宿主线程池/有界队列/总预算）",
             all(tile_contract.values()),
             "；".join(f"{k}={'ok' if v else 'FAIL'}" for k, v in tile_contract.items()),
             str(ENGINE.relative_to(ROOT)))
    import tempfile  # noqa: PLC0415
    _tile_work = Path(tempfile.mkdtemp(prefix="cmo-r34-"))
    try:
        _tile_harness(r, _tile_work)
    except Exception as exc:  # noqa: BLE001
        r.record("R34-static-tile-harness", "R34 瓦片服务 7 模式并发回归", False,
                 f"异常: {type(exc).__name__}: {exc}", "src/tests/MapTileServerTests.cs")
    finally:
        shutil.rmtree(_tile_work, ignore_errors=True)


# ── live：需要在运行中的游戏里实测 ──────────────────────────────────────────
def _ctrl_tree(H) -> dict:
    r = H.send("DB_CTRL_TREE", timeout=20)
    return {n["name"]: n for n in r.get("tree", [])}


def _form_origin(bounds: str) -> tuple[int, int]:
    m = re.search(r"X=(-?\d+),Y=(-?\d+)", bounds or "")
    return (int(m.group(1)), int(m.group(2))) if m else (0, 0)


def _open_viewer(H, attempts: int = 10) -> bool:
    for _ in range(attempts):
        if H.send("CLICK_MENU 数据库查看器", timeout=20).get("status") == "ok":
            time.sleep(6)
            if H.send("DB_CTRL_TREE", timeout=20).get("status") == "ok":
                return True
        time.sleep(3)
    return False


def _shot(H, name: str, hwnd: int) -> Path:
    SHOTS.mkdir(parents=True, exist_ok=True)
    out = SHOTS / f"{name}.png"
    subprocess.run([str(ROOT / "build/GuiDriver.exe"), "capture", str(out), str(hwnd)],
                   capture_output=True)
    return out


def _close_cpe_banner(H) -> None:
    """关掉启动后定时弹出的 CPE 宣传窗。

    它是个独立 WinForms 窗体（CPEBannerForm），会盖住数据库查看器的列表区：
    坐标点击会落在它上面（2026-10-19 实测：8 个 R1 用例全红而真实原因是被遮挡）。
    """
    try:
        H.send("CLOSE_FORM CPEBannerForm", timeout=15)
    except Exception:  # noqa: BLE001
        pass


def _viewer_list_items(H) -> int:
    r = H.send("DUMP_LISTS", timeout=25)
    for l in r.get("lists", []):
        if l.get("name") == "ListBox1":
            return len(l.get("items", []))
    return -1


def _wait_viewer_ready(H, r: Runner, timeout: float = 180.0) -> int:
    """等数据库查看器真正就绪再驱动它 —— 用**功能性探针**而不是控件计数。

    为何不用 ListBox1.Items.Count：那个列表是 DarkListView，可能是虚拟列表，
    行能正常绘制但 Items.Count 恒为 0（2026-10-19 实测：列表已可见、计数仍为 0）。
    改用 DB_NAVIGATE_ID：能直接取到某条库记录的详情 HTML，就证明“库读 + 详情管线”
    已就绪，之后再用坐标点行才靠谱。
    背景：冷启动时查看器的筛选框/列表是异步填充的，旧实现固定 sleep 就点行，
    于是八个 R1 用例假红（而 DB_NAVIGATE_ID 当时已经正常）。
    """
    t0 = time.time()
    last = 0
    while time.time() - t0 < timeout:
        H.send("DB_NAVIGATE_ID 1", timeout=20)
        time.sleep(1.5)
        ins = H.send("DB_INSPECT", timeout=20)
        last = int(ins.get("html_len") or 0)
        if last > 0:
            return last
        _close_cpe_banner(H)
        time.sleep(2)
    return last


def run_live(r: Runner) -> None:
    print("== live 用例（实机）==", flush=True)
    import live_harness as H  # noqa: PLC0415

    pid = H.game_pid()
    if pid is None:
        r.record("L0-game", "L0 游戏进程在运行", False, "Command.exe 未运行，live 用例无法执行")
        return
    if not _open_viewer(H):
        r.record("L0-viewer", "L0 打开数据库查看器", False, "CLICK_MENU 数据库查看器 失败")
        return
    _close_cpe_banner(H)
    ready = _wait_viewer_ready(H, r)
    r.record("L0-viewer", "L0 打开数据库查看器", ready > 0,
             f"DB_CTRL_TREE 可解析；就绪探针 html_len={ready}")

    tree = _ctrl_tree(H)
    form = tree.get("InternalDBViewer")
    if not form:
        r.record("L0-form", "L0 定位查看器窗体", False, "控件树缺少 InternalDBViewer")
        return
    hwnd = None
    x, y = _form_origin(form.get("bounds", ""))
    for w in H.list_windows(H.game_pid()):
        if w["rect"]["X"] == x and w["rect"]["Y"] == y and "WindowsForms" in w["cls"]:
            hwnd = w["hwnd"]
            break

    # L1：八类对象逐个切换 + 真实点击首行 → 详情页必须生成
    for t in OBJECT_TYPE_TOKENS:
        probe = t.split()[0]              # "Ground Unit" 含空格，DB_SET_FILTER 按空格切分
        got = 0
        # 允许一次重试：冷启动时筛选框/列表是异步填充的，坐标点击会落空
        # （2026-10-19 实测八个用例假红，DB_NAVIGATE_ID 却正常）。真实破坏两次都失败。
        for attempt in (1, 2):
            H.send("DB_SET_FILTER CB_ObjectType " + probe, timeout=15)
            time.sleep(2.0)
            _wait_viewer_ready(H, r, timeout=120)
            if hwnd:
                subprocess.run([str(ROOT / "build/GuiDriver.exe"), "focus", str(hwnd)], capture_output=True)
            H.click(x + FIRST_ROW_OFFSET[0], y + FIRST_ROW_OFFSET[1])
            time.sleep(2.5)
            ins = H.send("DB_INSPECT", timeout=15)
            got = int(ins.get("html_len") or 0)
            if got > 0:
                break
            _close_cpe_banner(H)
        need = MIN_HTML_LEN.get(t, DEFAULT_MIN_HTML_LEN)
        r.record(f"R1-live-{t.replace(' ', '')}", f"R1 {t} 详情页渲染",
                 got >= need, f"html_len={got} (>= {need})")

    # L2：类型下拉必须是游戏原生 token
    tree = _ctrl_tree(H)
    token = (tree.get("CB_ObjectType") or {}).get("text", "")
    ok = token in OBJECT_TYPE_TOKENS
    r.record("R1-live-token", "R1 CB_ObjectType 保持游戏原生 token", ok, f"text={token!r}")

    # L3：无频闪（同一稳定态“文本状态”一致）
    # 为什么不再比截图 md5：游戏暗色窗口是半透明的，GuiDriver 用屏幕抓图（CopyFromScreen），
    # 窗口背后的动态地图会被一起抓进来——地图一动画两帧 md5 必不同，属于用例自身假红
    # （2026-10-17 打包候选实测：两帧 UI 文本逐字相同、差异全在窗口背后的地图上）。
    # 频闪的本质是“界面文本被来回改写”，所以直接比文本状态；截图仍保留供人复核。
    def _flicker_state() -> tuple:
        nodes = H.send("CTRL_TREE InternalDBViewer", timeout=25).get("nodes", [])
        texts = tuple(sorted({(c.get("text") or "").strip()
                              for c in nodes if (c.get("text") or "").strip()}))
        ins = H.send("DB_INSPECT", timeout=15)
        combos = tuple(sorted((c.get("name") or "", tuple(c.get("items") or []))
                              for c in H.send("DUMP_COMBOS", timeout=30).get("combos", [])
                              if c.get("form") == "InternalDBViewer"))
        return texts, (ins.get("id"), ins.get("sot"), ins.get("html_len"),
                       (ins.get("html_sample") or "")[:200]), combos

    if hwnd:
        a_state = _flicker_state()
        a = _shot(H, "flicker_a", hwnd)
        time.sleep(2.5)
        b_state = _flicker_state()
        b = _shot(H, "flicker_b", hwnd)
        if a_state == b_state:
            r.record("R4-live-flicker", "R4 界面文本无频闪（两帧文本状态一致）", True,
                     f"查看器控件文本 {len(a_state[0])} 条 + 下拉 {len(a_state[2])} 组 + "
                     f"详情页 {a_state[1][2]} 字 HTML 两帧一致",
                     str(a.relative_to(ROOT)))
        else:
            diff = []
            if a_state[0] != b_state[0]:
                gone = sorted(set(a_state[0]) - set(b_state[0]))[:3]
                new = sorted(set(b_state[0]) - set(a_state[0]))[:3]
                diff.append(f"控件文本 '{gone}'→'{new}'")
            if a_state[1] != b_state[1]:
                diff.append(f"详情页 {a_state[1]}→{b_state[1]}")
            if a_state[2] != b_state[2]:
                diff.append(f"下拉项变化 {len(a_state[2])}→{len(b_state[2])} 组")
            r.record("R4-live-flicker", "R4 界面文本无频闪（两帧文本状态一致）", False,
                     "；".join(diff), str(a.relative_to(ROOT)))
    else:
        r.record("R4-live-flicker", "R4 界面无中英频闪", False, "未找到查看器窗口句柄")

    # L4：输入保护实机探针（R19——历史闪退点必须永远不再复发）
    _input_guard_probe(H, r, pid)

    # L5：界面英文残留抽样（R27——天气/快捷键/控制台等窗口）
    _ui_residual_probe(H, r, pid)

    # L6：条令下拉项全中文 + 无新增写拉锯熔断（R28）
    _doctrine_probe(H, r, pid)
    _mixed_prefix_probe(H, r, pid)
    _panel_flicker_probe(H, r, pid)

    # L7：构建号适配哨兵（R31——游戏更新导致注入静默失效那次事故）
    _build_adaptation_probe(H, r, pid)


def _build_adaptation_probe(H, r: Runner, pid: int | None) -> None:
    """R31 实机探针：游戏内部版本必须与声明一致，偏移池注入必须真的生效。

    2026-10-17 事故：Steam 把游戏升到 Build 1900.23，混淆成员名全部重命名，
    `TryInjectCoreDecryption` 反射取字段全为 null 且没有任何日志 → 3523 条偏移池
    注入静默失效（状态栏/HUD/标尺全回英文），我们却一直不知道。本用例把三件事钉住：
    ① 引擎真的完成了注入（日志有 pre-populated、且无 not available/not found 告警）；
    ② 游戏实际构建号与仓库声明的 GAME_BUILD 一致（引擎哨兵日志）；
    ③ 状态栏/HUD 关键偏移在当前池里确实是中文（POOL_DUMP 导出后核对）。
    另抽 3 个消息日志模板断言已规则化（R31/T5）。
    """
    name = "L-build-adaptation"
    title = "R31 游戏构建号一致 + 偏移池注入生效（状态栏中文/日志模板）"
    log = H.GAME / "CMOLocalizer.log"
    tail = _log_tail(log)
    game_build = (ROOT / "GAME_BUILD").read_text(encoding="utf-8").strip()

    injected = 'Successfully pre-populated' in tail and 'offset injection disabled' not in tail
    sentinel = f'GameBuild: detected game internal build {game_build}' in tail
    warned = 'GameBuild: 警告！' in tail

    # 状态栏/HUD 关键偏移：直接读入工程维护的白名单（force_offsets.json）
    pool_hits, pool_miss = [], []
    try:
        fo = json.loads((ROOT / "dict/src/force_offsets.json").read_text(encoding="utf-8"))
    except Exception:
        fo = {}
    want = {int(k): v for k, v in fo.items() if k.isdigit() and v}
    dump = H.send("POOL_DUMP", timeout=90)
    if dump.get("status") == "ok" and dump.get("file"):
        pool = {}
        for line in Path(dump["file"]).read_text(encoding="utf-8-sig", errors="replace").splitlines():
            if "\t" in line:
                off, txt = line.split("\t", 1)
                try:
                    pool[int(off)] = txt
                except ValueError:
                    continue
        sample = {k: want[k] for k in sorted(want)[:12]}
        for off, zh in sample.items():
            got = pool.get(off, "")
            (pool_hits if got == zh else pool_miss).append(f"{off}:{got!r}≠{zh!r}" if got != zh else str(off))
    else:
        pool_miss.append(f"POOL_DUMP 失败: {dump.get('message') or dump.get('status')}")

    # 消息日志模板（R31/T5）：抽 3 个形态，断言翻出中文
    patterns = {
        "endgame": ("AIM-120C-7 AMRAAM P3I.3 #925's Endgame Report HIT", "终局报告"),
        "contact": ("New air contact (BOGEY #147)", "新空中目标接触"),
        "vanished": ("BOGEY #147 vanished", "已消失"),
    }
    batch = H.GAME / "CMOLocalizer_XlateIn.json"
    batch.write_text(json.dumps([v[0] for v in patterns.values()], ensure_ascii=False), encoding="utf-8")
    xl = H.send("XLATE_BATCH " + str(batch), timeout=90)
    got_out = {it.get("in"): it.get("string", "") for it in xl.get("items", [])}
    log_bad = [k for k, (src, need) in patterns.items() if need not in got_out.get(src, "")]

    ok = injected and sentinel and not warned and not pool_miss and not log_bad
    detail = (f"注入={'ok' if injected else 'MISSING'}，构建号哨兵={'ok' if sentinel else 'MISSING'}"
              f"（声明 {game_build}{'，已告警不一致' if warned else ''}），"
              f"池内中文 {len(pool_hits)}/{len(pool_hits) + len(pool_miss)}"
              f"，日志模板 {len(patterns) - len(log_bad)}/{len(patterns)}")
    if pool_miss:
        detail += "；池异常: " + ", ".join(pool_miss[:3])
    if log_bad:
        detail += "；模板未中: " + ", ".join(log_bad)
    r.record(name, title, ok, detail)


def _bounds_rect(bounds: str) -> tuple[int, int, int, int]:
    m = re.search(r"X=(-?\d+),Y=(-?\d+),Width=(-?\d+),Height=(-?\d+)", bounds or "")
    return (int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4))) if m else (0, 0, 0, 0)


def _log_tail(path: Path, limit: int = 400_000) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")[-limit:]
    except Exception:
        return ""


_CHILD_RE = re.compile(r"^\s*(\d+)\s+vis=(\w+)\s+rect=\[(-?\d+),(-?\d+),(\d+)x(\d+)\]\s+cls=(.*?)\s+txt=(.*)$")


def _child_windows(hwnd: int) -> list[dict]:
    """进程外枚举子窗口（Win32 句柄/类名/屏幕矩形）。

    不用引擎读 `Control.Handle`：窗口树里的 WebView2 是 WPF 托管控件，从后台命令
    线程强制创建句柄会让 WPF 在非 STA 线程初始化 InputManager 而直接搞崩游戏。
    """
    out = subprocess.run([str(ROOT / "build/GuiDriver.exe"), "children", str(hwnd)],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    kids = []
    for line in (out.stdout or "").splitlines():
        m = _CHILD_RE.match(line)
        if m:
            kids.append({"hwnd": int(m.group(1)), "visible": m.group(2) == "True",
                         "x": int(m.group(3)), "y": int(m.group(4)),
                         "w": int(m.group(5)), "h": int(m.group(6)),
                         "cls": m.group(7).strip(), "text": m.group(8)})
    return kids


def _input_guard_probe(H, r: Runner, pid: int | None) -> None:
    """R19 实机探针：往「加载想定」搜索框真实输入 `m`。

    历史事故：词典含 m→米，DarkUI 输入框不继承 TextBoxBase 被当普通标签翻译，
    形成「引擎改写 → 游戏写回 → 引擎再改写」的无限拉锯（日志 `-> 米` 每 2.5s 递增），
    最终进程以 0xc00000fd 栈溢出闪退。本用例断言：进程存活、无新增「-> 米」、
    无新崩溃日志、输入框仍为用户输入的 `m`。
    差分对照：`XLATE m` 仍返回「米」，证明词典确实含该映射，断言非空跑。
    """
    name = "L-input-guard"
    title = "L4 输入框正文不被改写且不闪退（R19 历史闪退点）"
    log, crash = H.GAME / "CMOLocalizer.log", H.GAME / "CMOLocalizer_Crash.log"
    t0 = time.time()
    driver = str(ROOT / "build/GuiDriver.exe")

    nodes = []
    for _ in range(6):
        res = H.send("SCEN_CTRL_TREE", timeout=20)
        if res.get("status") == "ok" and res.get("tree"):
            nodes = res["tree"]
            break
        H.send("CLICK_MENU TSMI_LoadScenario", timeout=20)
        time.sleep(5.0)
    if not nodes:
        r.record(name, title, False, "「加载想定」窗口打不开或 SCEN_CTRL_TREE 不可用")
        return
    box = next((n for n in nodes if n.get("vis") and "TextBox" in (n.get("type") or "")), None)
    if box is None:
        r.record(name, title, False, f"树中无可见输入控件（{len(nodes)} 个节点）")
        return

    fx, fy, fw, fh = _bounds_rect(nodes[0].get("bounds", ""))
    _, _, bw, bh = _bounds_rect(box.get("bounds", ""))
    hwnd = next((w["hwnd"] for w in H.list_windows(pid)
                 if w["visible"] and w["rect"]["X"] == fx and w["rect"]["Y"] == fy
                 and w["rect"]["Width"] == fw and w["rect"]["Height"] == fh), None)
    if hwnd is None:
        r.record(name, title, False, "未定位「加载想定」窗口（位置+尺寸双匹配失败）")
        return

    kids = _child_windows(hwnd)
    edits = [k for k in kids if "EDIT" in k["cls"].upper() and k["visible"]]
    near = [k for k in edits if abs(k["w"] - (bw - 10)) <= 24 and abs(k["h"] - (bh - 5)) <= 12]
    target = (near or edits or [None])[0]
    if target is None:
        r.record(name, title, False, f"窗口内无 EDIT 输入控件（子窗口 {len(kids)} 个）")
        return

    before = _log_tail(log)
    label = next((n for n in nodes if n.get("name") == "LblScenSearch"), None)
    label_cn = any("\u4e00" <= ch <= "\u9fff" for ch in (label or {}).get("text", ""))
    H.click(target["x"] + target["w"] // 2, target["y"] + target["h"] // 2)
    time.sleep(0.8)
    subprocess.run([driver, "postchar", str(target["hwnd"]), "m"], capture_output=True)

    def _box_text() -> str:
        cur = H.send("SCEN_CTRL_TREE", timeout=20)
        node = next((n for n in cur.get("tree", [])
                     if n.get("name") == box.get("name") and n.get("type") == box.get("type")), None)
        return (node or {}).get("text", "")

    landed = ""
    for _ in range(8):                     # 等输入落地
        time.sleep(2.0)
        landed = _box_text()
        if landed:
            break
    time.sleep(45.0)                       # 历史拉锯每 2.5s 一次，45s 足以暴露
    pid_after = H.game_pid()
    box_text = _box_text()
    after = _log_tail(log)
    grew = after.count("-> 米") - before.count("-> 米")
    crash_new = crash.is_file() and crash.stat().st_mtime > t0
    would = json.dumps(H.send("XLATE m", timeout=20), ensure_ascii=False)

    # R20 历史问题点：战役列表/战役内名称不得残留「词典里已有译文」的英文。
    try:
        exact = json.loads(DICT.read_text(encoding="utf-8")).get("exact", {})
    except Exception:
        exact = {}
    texts = [t.strip() for t in (str(n.get("text", "")) for n in nodes if n.get("d", 0) <= 2) if t.strip()]
    cn_nodes = [t for t in texts if any("\u4e00" <= ch <= "\u9fff" for ch in t)]
    residual = sorted({t for t in texts
                       if t in exact and exact[t] != t
                       and not any("\u4e00" <= ch <= "\u9fff" for ch in t)})
    r.record("L-campaign-names", "L4 战役列表名称不残留已有译文的英文（R20 历史问题点）",
             bool(cn_nodes and not residual and exact),
             f"树节点 {len(texts)} 个（中文 {len(cn_nodes)} 个）"
             + (f"；残留英文: {residual[:6]}" if residual else "；无残留英文")
             + ("" if exact else "；词典未读到"),
             "evidence/live/scan")

    ok = bool(pid_after == pid and box_text == "m" and grew <= 0 and not crash_new and label_cn)
    r.record(name, title, ok,
             f"控件 {box.get('name')}({(box.get('type') or '').split('.')[-1]}) "
             f"EDIT#{target['hwnd']} 文本={box_text!r}（期望 'm'）；进程 {pid}→{pid_after}；"
             f"新增「-> 米」{grew} 条；新崩溃日志={crash_new}；"
             f"阳性对照 同窗标签已汉化={label_cn}（翻译通道确在工作）；"
             f"差分对照 XLATE m→米={'米' in would}",
             "evidence/live/scan")

    # 收尾放在断言之后：关闭模态对话框后游戏可能自行正常退出（事件日志无错误记录），
    # 不能让它影响判定，也不能污染下一次运行。
    for _ in range(max(1, len(landed))):
        subprocess.run([driver, "postchar", str(target["hwnd"]), "\b"], capture_output=True)
        time.sleep(0.2)
    H.send("CLOSE_FORM LoadScenario", timeout=20)


def _ui_residual_probe(H, r: Runner, pid: int | None) -> None:
    """R27 实机探针：已入词典的界面文本必须显示中文（抽多个窗口比对）。

    历史：天气编辑器的 Calm/Hurricane/Level 0、快捷键窗口的 Own Units、Lua 控制台的
    Insert/RUN 都是「用户看得见的英文」——而那 6 条当时根本不在词典里。
    已知盲区：快捷键窗口的正文在 DarkRichTextBox 里（富文本，直接改 Text 会丢格式且
    碰 R19 的输入保护不变量），本用例显式跳过并报告条数，不算它失败。
    R29：「想定特性与设置」是 WPF 窗口（CTRL_TREE 看不见），必须改用 WPF_DUMP 断言——
    实测该列表无 ScrollViewer/ItemsControl，17 条特性全部 realize 且同屏可见，
    所以那 4 条英文是真缺词条，不是「WPF 虚拟化离屏项」（旧判断已纠正）。
    """
    name = "L-ui-residuals"
    title = "R27 已入词典的界面文本显示中文（多窗口抽样）"
    windows = [("WeatherToolStripMenuItem", "天气编辑器"),
               ("HotkeysToolStripMenuItem", "快捷键"),
               ("TSMI_ScriptingConsole", "Lua 脚本控制台"),
               ("OptionsToolStripMenuItem1", "推演选项"),
               ("MapSettingsToolStripMenuItem1", "地图设置窗口")]
    fixed = {"WeatherToolStripMenuItem": [("Calm", "无风"), ("Hurricane", "飓风"), ("Level 0", "层级 0")],
             "HotkeysToolStripMenuItem": [("Own Units", "己方单位")],
             "TSMI_ScriptingConsole": [("Insert", "插入"), ("RUN", "运行")],
             # R29：推演选项的 Tacview 页签曾因词条非幂等叠成「三维态势联动 (三维态势联动 (Tacview))」；
             # 未选中的页签也在控件树里，所以这里钉住显示形态（页签是否可见不影响断言）。
             "OptionsToolStripMenuItem1": [("三维态势联动 (Tacview)", "三维态势联动"),
                                           ("三维态势联动 (三维态势联动 (Tacview))", "三维态势联动")]}
    try:
        exact = json.loads(DICT.read_text(encoding="utf-8")).get("exact", {})
    except Exception:
        exact = {}
    missing: list[str] = []
    residual: list[str] = []
    rich_skipped = 0
    checked_forms = 0
    for menu, label in windows:
        H.send("CLOSE_FORM ALL", timeout=20)
        time.sleep(1.2)
        base = {(c.get("text") or "").strip()
                for c in H.send("CTRL_TREE", timeout=25).get("nodes", [])}
        if H.send("CLICK_MENU " + menu, timeout=20).get("status") != "ok":
            missing.append(f"{label}:打不开")
            continue
        time.sleep(3.2)
        nodes = H.send("CTRL_TREE", timeout=25).get("nodes", [])
        now = {(c.get("text") or "").strip() for c in nodes}
        checked_forms += 1
        # 断言“当前状态”：中文在、原英文不在。不能只看开窗差分——
        # 有些窗口（如 Lua 控制台）本来就没关，其文本不会出现在差分里（曾因此假红）。
        for en, zh in fixed.get(menu, []):
            if zh not in now:
                missing.append(f"{label}:缺{zh}")
            elif en in now:
                missing.append(f"{label}:仍显{en!r}")
        # R29：地图显示设置页签的两项只在下拉项里（控件 Text 看不见），必须查 Items。
        if menu == "OptionsToolStripMenuItem1":
            opts = {c.get("name"): (c.get("items") or [])
                    for c in H.send("DUMP_COMBOS", timeout=30).get("combos", [])
                    if c.get("form") == "Options"}
            for cname, en, zh in (("CB_SonobuoyVisibility", "Ghosted", "虚影"),
                                  ("Combo_SlugTrailFreq", "Every 5 seconds", "每 5 秒")):
                items = opts.get(cname) or []
                if zh not in items:
                    missing.append(f"{label}:{cname} 缺{zh}")
                elif en in items:
                    missing.append(f"{label}:{cname} 仍显{en!r}")
        for c in nodes:
            t = (c.get("text") or "").strip()
            if not t or t in base or len(t) > 40 or not c.get("v"):
                continue
            if "RichTextBox" in (c.get("t") or ""):
                rich_skipped += 1
                continue
            if any("\u4e00" <= ch <= "\u9fff" for ch in t):
                continue
            if exact.get(t):
                residual.append(f"{label}:{t!r}")
    # R29：想定特性与设置是 WPF，上面的 CTRL_TREE 抽不到；用 WPF_DUMP 再钉一遍。
    wpf_rows = 0
    wpf_missing: list[str] = []
    wpf_en_left: list[str] = []
    H.send("CLOSE_FORM ALL", timeout=20)
    time.sleep(1.2)
    if H.send("CLICK_MENU RealismSettingsToolStripMenuItem", timeout=20).get("status") == "ok":
        time.sleep(4.5)
        dump = H.send("WPF_DUMP", timeout=40)
        views = [w for w in dump.get("windows", []) if "Realism" in (w.get("title") or "")]
        texts = {(n.get("text") or "").strip()
                 for w in views for n in (w.get("nodes") or []) if n.get("text")}
        rows = texts - {"确定", "取消"}
        wpf_rows = len(rows)
        for en in ("Allow instant loading for landing planner", "Point To Point Unit Communication",
                   "Realistic Order Chain", "Variable burnout speed on boost-coast weapons"):
            zh = exact.get(en, "")
            if not zh:
                continue
            if zh not in texts:
                wpf_missing.append(f"想定特性:缺{zh}")
            if en in texts:
                wpf_en_left.append(en)
        wpf_en_left += [t for t in rows
                        if not any("\u4e00" <= ch <= "\u9fff" for ch in t) and exact.get(t)]
        wpf_en_left = sorted(set(wpf_en_left))
        H.send("CLICK_WPF 取消", timeout=20)
    else:
        wpf_missing.append("想定特性与设置:打不开")
    H.send("CLOSE_FORM ALL", timeout=20)
    ok = bool(exact and checked_forms >= 3 and not missing and not residual
              and wpf_rows >= 10 and not wpf_missing and not wpf_en_left)
    r.record(name, title, ok,
             f"抽查 {checked_forms}/{len(windows)} 个窗口；已入词典却仍显英文 {len(residual)} 条"
             + (f"（{residual[:4]}）" if residual else "")
             + (f"；已知盲区跳过富文本 {rich_skipped} 处" if rich_skipped else "")
             + f"；WPF 想定特性 {wpf_rows} 条（仍显英文 {len(wpf_en_left)}、缺译文 {len(wpf_missing)}）"
             + (f"；缺失: {missing + wpf_missing}" if (missing or wpf_missing) else ""),
             "evidence/live/scan")


def _mixed_prefix_probe(H, r: Runner, pid: int | None) -> None:
    """R42 实机探针：全进程下拉项不得出现「英文 Inherited 前缀 + 中文字尾」。

    这是用户 2026-10-19 报的那个形状（任务编辑器 辐射控制/武器使用授权、条令窗口）：
    值被池注入成中文后，游戏把它与英文前缀拼成 `Inherited, <中文>`，旧守卫因「整串含中文」
    整体放过，英文前缀永远留在界面上。这里不看窗口位置，直接扫全进程所有下拉项，
    因为任务编辑器/条令窗口走的是同一条 TranslateComboItems 通道。
    """
    name, title = "L-mixed-prefix", "R42 全进程下拉项没有『英文 Inherited 前缀 + 中文尾』"
    H.send("CLOSE_FORM ALL", timeout=20)
    time.sleep(1.0)
    # 任务编辑器与条令窗口都开起来，尽量让动态创建的下拉框也进入扫描范围
    H.send("CLICK_BUTTON TSB_MissionEditor", timeout=25)
    time.sleep(6.0)
    H.send("CLICK_BUTTON TSB_Doctrine", timeout=25)
    time.sleep(6.0)
    combos = H.send("DUMP_COMBOS", timeout=30).get("combos") or []
    items = [str(i) for c in combos for i in (c.get("items") or [])]
    def _mixed(s: str) -> bool:
        return (re.match(r"(?i)^\s*inherited\b", s) is not None
                and any("\u4e00" <= ch <= "\u9fff" for ch in s))
    mixed = [i for i in items if _mixed(i)]
    H.send("CLOSE_FORM ALL", timeout=20)
    ok = len(items) > 0 and not mixed
    r.record(name, title, ok,
             f"下拉框 {len(combos)} 个/条目 {len(items)} 条；英文前缀+中文尾 {len(mixed)} 条"
             + (f"（例：{mixed[:3]}）" if mixed else "（无）")
             + ("" if items else "；❌ 未采集到任何下拉项，判据无效"),
             "evidence/live/scan")


def _panel_flicker_probe(H, r: Runner, pid: int | None) -> None:
    """R44 实机探针：**运行时**的单位/编队状态面板不得中英频闪。

    用户两次报同一个症状，且强调「不运行不闪」——因为游戏每个脉冲都会重建侧边栏元素
    并把英文写回去。历史上这里靠 `AllowWrite` 的「同一目标值重复 6 次即永久拉黑」兜底，
    结果先是频闪、然后熔断生效、面板**永久停在英文**（日志 27 条 WRITE LOOP BLOCKED）。
    现在改为令牌桶＋每帧补扫 ElementHost 子树，这条用例就是它的判据：
    连续采多帧，同一元素不得在「中文/英文」之间跳，且本次进程不得新增熔断事件。
    """
    name, title = "L-panel-flicker", "R44 运行时单位状态面板不中英频闪（令牌桶 + 每帧补扫）"
    log = H.GAME / "CMOLocalizer.log"

    def _blocked_count() -> int:
        try:
            txt = log.read_text(encoding="utf-8", errors="replace")
        except OSError:
            return 0
        tail = txt.split("=== CMOLocalizer v4.0 Engine Starting ===")[-1]
        return tail.count("WRITE LOOP BLOCKED")

    before = _blocked_count()
    H.send("CLOSE_FORM ALL", timeout=20)
    time.sleep(1.0)
    H.send("CLICK_MENU TSMI_LoadScenario", timeout=20)
    time.sleep(8.0)
    H.send("SCEN_EXPAND_ALL", timeout=25)
    time.sleep(2.0)
    H.send("SCEN_SELECT Shamal", timeout=25)
    time.sleep(2.0)
    H.send("SCEN_LOAD", timeout=25)
    time.sleep(18.0)
    H.send("CLICK_BUTTON 进入想定", timeout=25)
    time.sleep(25.0)
    H.send("CLOSE_FORM ALL", timeout=20)
    H.send("CLICK_MENU TSMI_SelectNextUnit", timeout=20)
    time.sleep(2.5)
    # 必须让推演真正跑起来：暂停时面板不重建，测不出频闪（用户原话「不运行不闪」）
    H.send("CLICK_MENU StartResumeGameToolStripMenuItem", timeout=20)
    time.sleep(3.5)

    def _cjk(t: str) -> bool:
        return any("一" <= c <= "鿿" for c in t)

    def _panel() -> set:
        w = H.send("WPF_DUMP", timeout=60)
        out = set()
        for win in w.get("windows") or []:
            if "ElementHost" not in (win.get("title") or ""):
                continue
            for n in win.get("nodes") or []:
                t = (n.get("text") or "").strip()
                if t:
                    out.add((n.get("type"), n.get("name") or "", t))
        return out

    frames = []
    for _ in range(14):
        frames.append(_panel())
        time.sleep(0.3)

    flips = []
    for a, b in zip(frames, frames[1:]):
        for ty, nm, t in a - b:
            for ty2, nm2, t2 in b - a:
                if (ty, nm) == (ty2, nm2) and _cjk(t) != _cjk(t2):
                    flips.append((nm or ty, t, t2))
    after = _blocked_count()
    H.send("CLOSE_FORM ALL", timeout=20)

    ok = len(frames) >= 10 and not flips and after <= before
    r.record(name, title, ok,
             f"采样 {len(frames)} 帧；中英翻转 {len(flips)} 处"
             + (f"（例 {flips[:2]}）" if flips else "（无）")
             + f"；写拉锯熔断 {before}→{after}"
             + ("" if after <= before else "（新增熔断 = 永久停英文回归）"),
             "evidence/live/scan")


def _doctrine_probe(H, r: Runner, pid: int | None) -> None:
    """R28 实机探针：条令/交战规则窗口的下拉项必须已汉化，且不得产生新的写拉锯熔断。

    两件历史事故：① 选项值靠术语映射翻译，缺词条就中英混杂（已补 8 条，12/12 全中文）；
    ② `TranslateComboItems` 曾在入口无条件调 `AllowWrite`，把「一次都没写入」的组合框
    也永久静音（日志里 269~311 次假熔断），这是下拉项大面积英文的元凶之一。
    该窗口需先真正进入想定（否则活跃单位为 0、菜单项静默无操作），所以这里自己跑一遍
    加载想定 → 进入想定 → 选中单位。
    """
    name = "L-doctrine-combo"
    title = "R28 条令下拉项全中文且无新增写拉锯熔断"
    log = H.GAME / "CMOLocalizer.log"
    before = _log_tail(log).count("WRITE LOOP BLOCKED")
    H.send("CLOSE_FORM ALL", timeout=20)
    time.sleep(1.2)
    H.send("CLICK_MENU TSMI_LoadScenario", timeout=20)
    time.sleep(8.0)
    H.send("SCEN_EXPAND_ALL", timeout=25)
    time.sleep(2.0)
    H.send("SCEN_SELECT Shamal", timeout=25)
    time.sleep(2.0)
    H.send("SCEN_LOAD", timeout=25)
    time.sleep(18.0)
    H.send("CLICK_BUTTON 进入想定", timeout=25)
    time.sleep(25.0)
    H.send("CLOSE_FORM ALL", timeout=20)
    H.send("CLICK_MENU TSMI_SelectNextUnit", timeout=20)
    time.sleep(2.0)
    if H.send("CLICK_MENU DoctrineROEEMCONToolStripMenuItem", timeout=20).get("status") != "ok":
        r.record(name, title, False, "条令窗口打不开（想定未进入或未选中单位）")
        return
    time.sleep(4.5)
    nodes = H.send("CTRL_TREE", timeout=25).get("nodes", [])
    vals = sorted({(c.get("text") or "").strip()
                   for c in nodes if c.get("n") == "Combo_StateSelection"})
    vals = [v for v in vals if v]
    ascii_only = [v for v in vals if not any("\u4e00" <= ch <= "\u9fff" for ch in v)]
    after = _log_tail(log).count("WRITE LOOP BLOCKED")
    H.send("CLOSE_FORM ALL", timeout=20)
    ok = bool(vals) and not ascii_only and after <= before
    r.record(name, title, ok,
             f"下拉项 {len(vals)} 个去重值，纯英文 {len(ascii_only)} 个"
             + (f"（{ascii_only[:3]}）" if ascii_only else "（全中文）")
             + f"；写拉锯熔断 {before}→{after}"
             + ("" if after <= before else "（新增熔断 = 假熔断回归）"),
             "evidence/live/scan")


# ── installer：安装/还原/回退的隔离验证（临时合成游戏目录，不需要游戏） ──────
def _reg_snapshot() -> dict:
    import winreg  # noqa: PLC0415
    keys = {}
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\CMOChinese") as k:
            for name in ("GamePath", "Version", "AutoCheck", "CheckIntervalHours", "AutoStart",
                         "NotifyMode", "AutoInstall", "Mirror"):
                try:
                    keys[name] = winreg.QueryValueEx(k, name)
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        pass
    return keys


def _reg_restore(keys: dict) -> None:
    import winreg  # noqa: PLC0415
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, r"Software\CMOChinese") as k:
        for name, (value, kind) in keys.items():
            winreg.SetValueEx(k, name, 0, kind, value)


def _decode(raw: bytes) -> str:
    """安装器输出走控制台编码（中文 Windows 为 GBK）；先试 UTF-8 再回退 GBK。"""
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def _mk_game(root: Path) -> Path:
    game = root / "game"
    for d in ("DB/Templates", "Symbols/Menu", "Scenarios"):
        (game / d).mkdir(parents=True, exist_ok=True)
    (game / "Command.exe").write_bytes(b"stub")
    (game / "Command.exe.config").write_text(
        '<?xml version="1.0"?><configuration><runtime></runtime></configuration>',
        encoding="utf-8")
    return game


def run_installer(r: Runner) -> None:
    """R13/R14/R15/R17：用安装器 CLI 在临时目录验证状态/设置/回退，不碰真实游戏目录。"""
    import shutil  # noqa: PLC0415
    import tempfile  # noqa: PLC0415

    print("== installer 用例（隔离临时目录）==", flush=True)
    version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    setup = ROOT / "build" / f"CMO_Chinese_Setup_v{version}.exe"
    if not setup.is_file():
        r.record("R13-installer-setup", "R13 安装器产物存在", False,
                 f"缺少 {setup.name}，请先跑 bash scripts/build.sh")
        return

    settings = Path(os.environ["APPDATA"]) / "CMOChinese" / "settings.json"
    packages = Path(os.environ["LOCALAPPDATA"]) / "CMOChinese" / "packages"
    saved_settings = settings.read_bytes() if settings.exists() else None
    saved_reg = _reg_snapshot()
    tmp = Path(tempfile.mkdtemp(prefix="cmo-reg-installer-"))
    # 安装包缓存整个搬走再搬回：用例内部会删缓存来验证“无缓存拒绝回退”，
    # 不能把用户真实缓存的安装包（回退素材）弄丢。
    pkg_backup = Path(tempfile.mkdtemp(prefix="cmo-pkg-backup-"))
    if packages.is_dir():
        for p in packages.glob("*.exe"):
            shutil.move(str(p), str(pkg_backup / p.name))

    def run(*args: str, timeout: int = 600) -> tuple[int, str]:
        p = subprocess.run([str(setup), *args], capture_output=True, timeout=timeout)
        return p.returncode, (_decode(p.stdout or b"") + _decode(p.stderr or b"")).strip()

    try:
        # R13：安装写清单 / 还原清清单（状态不再靠注册表字符串）
        game = _mk_game(tmp / "a")
        rc, out = run("--install", str(game), "--silent")
        man = game / "CMOChinese_Installed.json"
        man_data = json.loads(man.read_text(encoding="utf-8")) if man.is_file() else {}
        dll = game / "CMOLocalizer.dll"
        dll_ok = (dll.is_file()
                  and man_data.get("files", {}).get("CMOLocalizer.dll") == hashlib.sha256(dll.read_bytes()).hexdigest())
        r.record("R13-install-manifest", "R13 安装后写清单且哈希与磁盘一致",
                 rc == 0 and man_data.get("version") == version and dll_ok,
                 f"rc={rc} version={man_data.get('version')} dll_hash_match={dll_ok}", out[:80])

        rc, out = run("--list-versions", str(game))
        r.record("R13-list-versions", "R13 状态查询显示已安装版本",
                 rc == 0 and f"installed=v{version}" in out, out.replace("\n", " | ")[:120])

        rc, out = run("--uninstall", str(game), "--silent")
        r.record("R13-uninstall-manifest", "R13 还原后清单消失（状态=未安装）",
                 rc == 0 and not man.is_file() and not dll.is_file(),
                 f"rc={rc} manifest={man.is_file()} dll={dll.is_file()}", out[:80])

        # R14：还原/重装都不得丢设置
        settings.parent.mkdir(parents=True, exist_ok=True)
        seed = {"schema": 1, "check_interval_hours": 3, "notify_mode": 0,
                "tray_on_close": False, "mirror": "https://ghproxy.net/", "auto_check": True}
        settings.write_text(json.dumps(seed, ensure_ascii=False, indent=1), encoding="utf-8")
        run("--install", str(game), "--silent")
        run("--uninstall", str(game), "--silent")
        after = json.loads(settings.read_text(encoding="utf-8")) if settings.exists() else {}
        kept = (settings.is_file() and after.get("check_interval_hours") == 3
                and after.get("notify_mode") == 0 and after.get("tray_on_close") is False)
        r.record("R14-uninstall-keeps-settings", "R14 还原后设置保留（频率/提醒/托盘）", kept,
                 f"settings_exists={settings.is_file()} interval={after.get('check_interval_hours')} "
                 f"notify={after.get('notify_mode')} tray={after.get('tray_on_close')}")
        rc, out = run("--purge-settings")
        r.record("R14-purge-explicit", "R14 仅显式清除才删设置（--purge-settings）",
                 rc == 0 and not settings.exists(), f"rc={rc} settings_exists={settings.exists()}")

        # R17：存量/绿色包自愈（有文件无清单 → adopt）
        run("--install", str(game), "--silent")
        man.unlink()
        rc, out = run("--adopt", str(game))
        adopted = json.loads(man.read_text(encoding="utf-8")) if man.is_file() else {}
        r.record("R17-adopt-selfheal", "R17 无清单时自动登记安装状态（adopt）",
                 rc == 0 and adopted.get("version") == version and adopted.get("source") == "adopted",
                 f"rc={rc} version={adopted.get('version')} source={adopted.get('source')}", out[:80])

        # R15：回退一致性（清单版本 → 目标版本；部署 dll 与缓存包内嵌 dll 一致）
        packages.mkdir(parents=True, exist_ok=True)
        cached = packages / f"CMO_Chinese_Setup_v{version}.exe"
        if not cached.is_file():
            shutil.copy(setup, cached)
        data = json.loads(man.read_text(encoding="utf-8"))
        data["version"] = "9.9.9"          # 模拟“已装了比缓存包更新的版本”
        man.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        rc, out = run("--rollback", str(game), "--silent")
        back = json.loads(man.read_text(encoding="utf-8")) if man.is_file() else {}
        # 缓存包内嵌的引擎就是刚 build 出来的 build/CMOLocalizer.dll，所以直接比它
        built_dll = ROOT / "build/CMOLocalizer.dll"
        same_dll = (dll.is_file() and built_dll.is_file()
                    and hashlib.sha256(dll.read_bytes()).hexdigest()
                    == hashlib.sha256(built_dll.read_bytes()).hexdigest())
        r.record("R15-rollback", "R15 回退到缓存版本且引擎与缓存包一致",
                 rc == 0 and back.get("version") == version and same_dll,
                 f"rc={rc} manifest=v{back.get('version')} dll==cached_pkg_dll:{same_dll}", out[:80])

        cached.unlink()
        data["version"] = "9.9.9"
        man.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        rc, out = run("--rollback", str(game), "--silent")
        unchanged = (json.loads(man.read_text(encoding="utf-8")).get("version") == "9.9.9")
        r.record("R15-rollback-requires-cache", "R15 无缓存包时拒绝回退（清单不变+报错）",
                 rc != 0 and unchanged and "9.9.9" in out,
                 f"rc={rc} manifest_unchanged={unchanged} out={out[:80]}")

        # R35：内嵌资源释放路径 + 失败文案准确性。
        # 为何要单测：R13–R17 全部从 build/ 运行安装器，而那里躺着 CMOLocalizer.dll，
        # 于是永远走「从磁盘复制」分支，内嵌资源分支从未被测到。2026-10-18 21:02 用户
        # 在**游戏运行中**安装（exe 在下载目录、旁边没有 dll），报「安装包内未找到
        # CMOLocalizer.dll 资源文件」——真实原因是覆盖被占用文件抛异常、旧实现把异常
        # 吞成「资源缺失」。下面两个用例分别锁住资源释放与错误文案。
        dist = tmp / "dist-only-exe"
        dist.mkdir()
        shutil.copyfile(setup, dist / setup.name)
        game2 = _mk_game(tmp / "r35")
        dist_setup = dist / setup.name
        p = subprocess.run([str(dist_setup), "--install", str(game2), "--silent"],
                           capture_output=True, timeout=600)
        out = (_decode(p.stdout or b"") + _decode(p.stderr or b"")).strip()
        dll2 = game2 / "CMOLocalizer.dll"
        json2 = game2 / "CMOLocalizer.json"
        src_json = ROOT / "dict/CMOLocalizer.json"
        leftovers = [q.name for q in game2.glob("*.cmo_new")]
        released = (dll2.is_file() and built_dll.is_file()
                    and hashlib.sha256(dll2.read_bytes()).hexdigest()
                    == hashlib.sha256(built_dll.read_bytes()).hexdigest())
        released_json = (json2.is_file()
                         and hashlib.sha256(json2.read_bytes()).hexdigest()
                         == hashlib.sha256(src_json.read_bytes()).hexdigest())
        r.record("R35-resource-release", "R35 从「只有 exe」的目录安装：内嵌资源释放且逐字节一致",
                 p.returncode == 0 and released and released_json and not leftovers,
                 f"rc={p.returncode} dll一致={released} json一致={released_json} 残留={leftovers}",
                 out[:80])

        # 独占锁住目标 DLL（模拟游戏已加载它）：必须如实报「写入失败」，且不得再误报资源缺失。
        import ctypes  # noqa: PLC0415
        k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        k32.CreateFileW.restype = ctypes.c_void_p
        k32.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_uint,
                                    ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p]
        handle = k32.CreateFileW(str(dll2), 0x80000000, 0, None, 3, 0, None)
        locked_rc, locked_out = -1, "独占句柄未建立"
        try:
            if handle in (None, -1, 0xFFFFFFFFFFFFFFFF):
                raise OSError(f"CreateFileW 失败 err={ctypes.get_last_error()}")
            p = subprocess.run([str(dist_setup), "--install", str(game2), "--silent"],
                               capture_output=True, timeout=600)
            locked_rc = p.returncode
            locked_out = (_decode(p.stdout or b"") + _decode(p.stderr or b"")).strip()
        except OSError as exc:
            locked_rc, locked_out = -1, str(exc)
        finally:
            if handle not in (None, -1, 0xFFFFFFFFFFFFFFFF):
                k32.CloseHandle(ctypes.c_void_p(handle))
        accurate = ("写入" in locked_out and "失败" in locked_out and "未找到" not in locked_out)
        r.record("R35-locked-error", "R35 目标被占用时如实报写入失败（不得误报资源缺失）",
                 locked_rc != 0 and accurate, f"rc={locked_rc} 文案={locked_out[:110]}",
                 "安装包内嵌资源 + 独占句柄")

        # R36：安装包必须真的把东西打进去（用户 2026-10-18 报「是不是忘记打包」）。
        # 断言两件事：① 预期资源名单齐全；② 关键载荷逐字节等于本次构建产物。
        ps1 = tmp / "dump-res.ps1"
        ps1.write_text(
            "param([string]$path)\n"
            "$asm = [System.Reflection.Assembly]::LoadFile($path)\n"
            "$sha = [System.Security.Cryptography.SHA256]::Create()\n"
            "foreach ($n in $asm.GetManifestResourceNames()) {\n"
            "  $s = $asm.GetManifestResourceStream($n)\n"
            "  $b = New-Object byte[] $s.Length\n"
            "  $s.Read($b, 0, $b.Length) | Out-Null\n"
            "  $h = [System.BitConverter]::ToString($sha.ComputeHash($b)).Replace('-','').ToLower()\n"
            "  Write-Output ($n + '|' + $s.Length + '|' + $h)\n"
            "}\n", encoding="utf-8")
        probe = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                                "-File", str(ps1), str(setup)],
                               capture_output=True, text=True, encoding="utf-8", errors="replace",
                               timeout=300)
        res = {}
        for line in (probe.stdout or "").splitlines():
            parts = line.strip().split("|")
            if len(parts) == 3:
                res[parts[0]] = (int(parts[1]), parts[2])

        def res_hash(name: str) -> str:
            hit = [h for k, (_, h) in res.items() if k.endswith(name)]
            return hit[0] if hit else ""

        exp_names = ["CMOLocalizer.dll", "CMOLocalizer.json", "UPDATER_EXE", "PLACE_SYNOPTIC",
                     "PLACE_CITIES", "LAUNCHER_ZH", "SYMBOLS_MENU_ZIP", "SCEN_HTML_ZIP",
                     "QR_WeChat", "QR_Alipay"]
        exp_names += ["TPL_" + p.name for p in sorted((ROOT / "assets/db_templates").glob("*.html"))]
        missing = [n for n in exp_names if res_hash(n) == ""]
        file_hash = lambda p: hashlib.sha256(Path(p).read_bytes()).hexdigest()  # noqa: E731
        payload = {
            "引擎 DLL": ("CMOLocalizer.dll", ROOT / "build/CMOLocalizer.dll"),
            "词典 JSON": ("CMOLocalizer.json", ROOT / "dict/CMOLocalizer.json"),
            "更新助手": ("UPDATER_EXE", ROOT / "build/CMO_Updater.exe"),
            "地名集 Synoptic": ("PLACE_SYNOPTIC", ROOT / "assets/placenames/Synoptic.wwp"),
            "地名集 Cities": ("PLACE_CITIES", ROOT / "assets/placenames/Cities.wwp"),
        }
        wrong = [k for k, (n, src) in payload.items()
                 if not src.is_file() or res_hash(n) != file_hash(src)]
        r.record("R36-installer-payload", "R36 安装包内嵌载荷齐全且与构建产物逐字节一致",
                 not missing and not wrong and len(res) >= len(exp_names),
                 f"资源={len(res)} 缺失={missing[:4]} 不一致={wrong}",
                 "powershell 反射导出安装包资源")

        # R37：游戏目录不可写（Steam 装在 C:\Program Files、未提权的典型情形）时，
        # 文案必须指向权限，而不是误报资源缺失——这是「别的电脑、没开游戏也装不上」的首要原因。
        user = os.environ.get("USERNAME", "")
        game3 = _mk_game(tmp / "r37")
        subprocess.run(["icacls", str(game3), "/deny", f"{user}:(W)"], capture_output=True)
        try:
            p = subprocess.run([str(dist_setup), "--install", str(game3), "--silent"],
                               capture_output=True, timeout=300)
            msg = (_decode(p.stdout or b"") + _decode(p.stderr or b"")).strip()
            r.record("R37-dir-not-writable", "R37 目录不可写时文案指向权限（不得误报资源缺失）",
                     p.returncode != 0 and "不可写" in msg and "管理员" in msg and "未找到" not in msg,
                     f"rc={p.returncode} 文案={msg[:110]}", "icacls deny (W)")
        finally:
            subprocess.run(["icacls", str(game3), "/remove:d", user], capture_output=True)
    finally:
        if saved_settings is None:
            if settings.exists():
                settings.unlink()
        else:
            settings.write_bytes(saved_settings)
        if packages.is_dir():
            for p in packages.glob("*.exe"):
                p.unlink()
        for p in pkg_backup.glob("*.exe"):
            packages.mkdir(parents=True, exist_ok=True)
            shutil.move(str(p), str(packages / p.name))
        shutil.rmtree(pkg_backup, ignore_errors=True)
        _reg_restore(saved_reg)
        shutil.rmtree(tmp, ignore_errors=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--static", action="store_true")
    ap.add_argument("--live", action="store_true")
    ap.add_argument("--installer", action="store_true",
                    help="安装/还原/回退的隔离验证（临时目录，不需要游戏）")
    ap.add_argument("--no-report", action="store_true",
                    help="不写 build/regression_report.json（供 package.sh 做中间门禁，"
                         "避免覆盖发版用的 live 报告）")
    args = ap.parse_args()
    any_flag = args.static or args.live or args.installer
    do_static = args.static or not any_flag
    do_live = args.live or not any_flag
    do_installer = args.installer or not any_flag

    version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    runner = Runner()
    if do_static:
        run_static(runner)
    if do_installer:
        run_installer(runner)
    if do_live:
        run_live(runner)

    failed = [x["id"] for x in runner.results if x["status"] != "pass"]
    report = {
        "version": version,
        "generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "modes": [m for m, on in (("static", do_static), ("installer", do_installer),
                                  ("live", do_live)) if on],
        "passed": not failed,
        "failed": failed,
        "results": runner.results,
    }
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    if not args.no_report:
        REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n==> 回归报告: {REPORT.relative_to(ROOT)}" + ("（--no-report，未写入）" if args.no_report else "")
          + f"  {len(runner.results) - len(failed)}/{len(runner.results)} 通过"
          + (f"，失败：{', '.join(failed)}" if failed else ""))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
