#!/usr/bin/env python3
"""发版统一入库检查（Release intake check）。

一条命令把「要发版的东西」全部过一遍，逐项打印 PASS/FAIL 并给出退出码：

    1  版本一致性（VERSION / 安装器常量 / 安装器标题 / 助手常量 / 词典 _meta.version）
    2  词库、菜单、引擎解析、想定元数据（标题 + 设置 + 战役剧本名）、简报、地名集、DB 模板
    3  密钥门禁、工作区干净
    4  必需的源文件与汉化资产齐全（清单见 docs/RELEASE_MANIFEST.md §2）
    5  回归：static + installer 现跑一遍；发版报告须同版本、含 live、24h 内、全通过
    6  产物：安装包/绿色包 + 各自 .sha256 且哈希相符；绿色包内容与禁入项

用法：
    python scripts/release_check.py            # 完整入库检查（发版前必须全绿）
    python scripts/release_check.py --pre      # 打包前的预检：跳过产物/绿色包内容（尚未 package）
    python scripts/release_check.py --json     # 额外输出机器可读汇总
    python scripts/release_check.py --allow-dirty   # 允许工作区有未提交改动（仅开发时用）
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

import verify_all as VA  # noqa: E402  （复用其门禁实现，保证与 SOP 一致）

BUILD = ROOT / "build"
VERSION = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
SETUP = BUILD / f"CMO_Chinese_Setup_v{VERSION}.exe"
PACK = BUILD / f"CMO_Chinese_Pack_{VERSION}.zip"
REPORT = BUILD / "regression_report.json"

# 入库必需项（docs/RELEASE_MANIFEST.md §2）
REQUIRED_SOURCES = [
    "src/engine/CMOLocalizer.cs",
    "src/engine/Reflect.cs",
    "src/installer/CMO_Chinese_Setup.cs",
    "src/updater/CMO_Updater.cs",
    "src/shared/CMOShared.cs",
    "src/shared/CMOSharedTest.cs",
    "src/shared/MapLabelRenderer.cs",
    "scripts/build.sh",
    "scripts/build.ps1",
    "scripts/localize_html_files.py",
    "scripts/deploy.sh",
    "scripts/package.sh",
    "scripts/publish_release.py",
    "scripts/release_check.py",
    "scripts/verify_all.py",
    "scripts/verify_regressions.py",
    "scripts/verify_no_secrets.py",
    "dict/CMOLocalizer.json",
    "dict/src/campaign_ui.json",
    "dict/strings_pool.json",
    "VERSION",
    "README.md",
    "docs/BUG_REGISTRY.md",
    "docs/CHANGELOG.md",
    "docs/RELEASE_TEST_SOP.md",
    "docs/RELEASE_MANIFEST.md",
    "docs/AUTO_UPDATE_DESIGN.md",
]
REQUIRED_ASSETS = [
    "assets/placenames/Synoptic.wwp",
    "assets/placenames/Cities.wwp",
    "assets/launcher/stringsChinese.resx",
]
DB_TEMPLATES = ["Aircraft", "Facility", "GroundUnit", "Platform", "Satellite", "Ship", "Submarine", "Weapon"]
FORBIDDEN_IN_PACK = [".workflow/", "evidence/", ".env", "regression_report.json",
                     "CMOSharedTest.exe", "GuiDriver.exe", "_res_probe"]

results: list[tuple[str, bool, str]] = []


def record(name: str, title: str, ok: bool, detail: str) -> None:
    results.append((name, ok, f"{title} — {detail}" if detail else title))


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_sources() -> None:
    missing = [p for p in REQUIRED_SOURCES if not (ROOT / p).is_file()]
    record("sources", "必需源文件与文档齐全", not missing,
           "全部就位" if not missing else "缺少: " + ", ".join(missing))


def check_assets() -> None:
    missing = [p for p in REQUIRED_ASSETS if not (ROOT / p).is_file()]
    missing += [f"assets/db_templates/{t}.html" for t in DB_TEMPLATES
                if not (ROOT / f"assets/db_templates/{t}.html").is_file()]
    symbols = list((ROOT / "assets/symbols_menu").glob("*")) if (ROOT / "assets/symbols_menu").is_dir() else []
    ok = not missing and len(symbols) >= 28
    detail = f"DB 模板 {len(DB_TEMPLATES) - len([m for m in missing if 'db_templates' in m])}/{len(DB_TEMPLATES)}，" \
             f"菜单按钮图 {len(symbols)} 个"
    if missing:
        detail += " | 缺少: " + ", ".join(missing[:6])
    if len(symbols) < 28:
        detail += " | 菜单按钮图不足 28"
    record("assets", "汉化资产齐全（DB 模板/地名集/按钮图/启动器）", ok, detail)


def check_regression_static_installer() -> None:
    p = subprocess.run([sys.executable, str(ROOT / "scripts/verify_regressions.py"),
                        "--static", "--installer", "--no-report"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    tail = [l for l in (p.stdout or "").strip().splitlines() if l.strip()]
    record("regression-static-installer", "static + installer 回归现跑全绿",
           p.returncode == 0, tail[-1] if tail else "(no output)")


def check_regression_report() -> list[str]:
    """发版报告必须：存在、可解析、版本一致、含 live、全通过、24h 内。返回问题列表。"""
    problems: list[str] = []
    data = None
    if not REPORT.is_file():
        return ["缺少 build/regression_report.json（先跑 verify_regressions.py 带 --live）"]
    try:
        data = json.loads(REPORT.read_text(encoding="utf-8"))
    except Exception as exc:  # noqa: BLE001
        return [f"报告无法解析: {exc}"]
    if str(data.get("version")) != VERSION:
        problems.append(f"报告版本 {data.get('version')} != VERSION {VERSION}")
    if "live" not in (data.get("modes") or []):
        problems.append("报告缺少 live 用例（发布前须带 --live 跑一次回归）")
    if not data.get("passed"):
        problems.append(f"回归失败用例: {', '.join(data.get('failed') or [])}")
    try:
        # 报告用带时区的 ISO 时间，timestamp() 两端对齐后再相减，避免 naive/aware 混用报错
        age = datetime.now().timestamp() - datetime.fromisoformat(data["generated_at"]).timestamp()
        if age > 24 * 3600:
            problems.append(f"报告已过期（{age / 3600:.1f} 小时前）")
    except Exception:  # noqa: BLE001
        problems.append("报告缺少可解析的 generated_at")
    return problems


def check_artifacts() -> None:
    problems: list[str] = []
    for art in (SETUP, PACK):
        if not art.is_file():
            problems.append(f"缺少产物 {art.relative_to(ROOT)}")
            continue
        sha_file = art.with_suffix(art.suffix + ".sha256")
        if not sha_file.is_file():
            problems.append(f"缺少校验值 {sha_file.relative_to(ROOT)}")
            continue
        expect = sha_file.read_text(encoding="utf-8").strip().split()[0]
        actual = sha256(art)
        if expect != actual:
            problems.append(f"{art.name} 哈希不符（文件 {actual[:16]}… vs .sha256 {expect[:16]}…）")
    record("artifacts", "安装包/绿色包 + .sha256 且哈希相符", not problems,
           "; ".join(problems) if problems
           else f"{SETUP.name} {SETUP.stat().st_size:,d}B / {PACK.name} {PACK.stat().st_size:,d}B")


def check_pack_contents() -> None:
    if not PACK.is_file():
        record("pack-contents", "绿色包内容与禁入项", False, f"缺少 {PACK.name}")
        return
    problems: list[str] = []
    with zipfile.ZipFile(PACK) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]
        for need in ("汉化说明.md", "CMOLocalizer.dll", "CMOLocalizer.json", "CMO_Updater.exe"):
            if need not in names:
                problems.append(f"缺少条目 {need}")
        if len(names) < 600:
            problems.append(f"条目数偏少（{len(names)}）")
        for bad in FORBIDDEN_IN_PACK:
            hit = [n for n in names if bad in n]
            if hit:
                problems.append(f"不该入包的条目: {hit[:2]}")
    record("pack-contents", "绿色包含全部汉化资产且无禁入项", not problems,
           "; ".join(problems) if problems else f"{len(names)} 条目（含 汉化说明.md/dll/json/助手）")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="发版统一入库检查")
    ap.add_argument("--pre", action="store_true",
                    help="打包前预检：跳过产物与绿色包内容检查")
    ap.add_argument("--json", action="store_true", help="输出机器可读汇总")
    ap.add_argument("--allow-dirty", action="store_true", help="允许工作区未提交改动（开发用）")
    args = ap.parse_args(argv)

    print(f"发版统一入库检查  version={VERSION}  mode={'pre' if args.pre else 'release'}")
    print("=" * 78)

    VA.gate_version()
    VA.gate_dictionary()
    VA.gate_menu()
    VA.gate_engine_parse()
    VA.gate_scenario_meta()
    VA.gate_briefings()
    VA.gate_placenames()
    VA.gate_db_templates()
    VA.gate_no_secrets()
    VA.gate_readme_downloads()
    VA.gate_routing()
    if args.allow_dirty:
        print("  [SKIP] repository   (--allow-dirty)")
    else:
        VA.gate_repository()

    check_sources()
    check_assets()
    check_regression_static_installer()
    problems = check_regression_report()
    record("regression-report", "发版回归报告（同版本/含 live/24h 内/全绿）", not problems,
           "; ".join(problems) if problems
           else f"{REPORT.name} modes={json.loads(REPORT.read_text(encoding='utf-8')).get('modes')}")
    if args.pre:
        print("  [SKIP] artifacts / pack-contents  (--pre：尚未打包)")
    else:
        check_artifacts()
        check_pack_contents()

    combined = [(n, ok, d) for n, ok, d in results] + [(n, ok, d) for n, ok, d in VA.results]
    width = max(len(n) for n, _, _ in combined)
    failed = [n for n, ok, _ in combined if not ok]
    print("-" * 78)
    for name, ok, detail in combined:
        print(f"  [{'PASS' if ok else 'FAIL'}] {name:<{width}} {detail}")
    print("=" * 78)
    if failed:
        print(f"结果：{len(combined) - len(failed)}/{len(combined)} 通过，失败 {len(failed)} 项：{', '.join(failed)}")
    else:
        print(f"结果：全部 {len(combined)} 项通过 —— 可以打包/发布"
              if not args.pre else f"结果：全部 {len(combined)} 项通过（预检模式，产物检查已跳过）")
    if args.json:
        print(json.dumps({"version": VERSION, "pre": args.pre,
                          "passed": not failed,
                          "failed": failed,
                          "results": [{"id": n, "ok": ok, "detail": d} for n, ok, d in combined]},
                         ensure_ascii=False))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
