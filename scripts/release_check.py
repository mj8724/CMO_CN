#!/usr/bin/env python3
"""发版统一入库检查（Release intake check）。

一条命令把「要发版的东西」全部过一遍，逐项打印 PASS/FAIL 并给出退出码：

    1  版本一致性（VERSION / 安装器常量 / 安装器标题 / 助手常量 / 词典 _meta.version）
    2  词库、菜单、引擎解析、想定元数据（标题 + 设置 + 战役剧本名）、简报、地名集、DB 模板
    3  密钥门禁、工作区干净
    4  必需的源文件与汉化资产齐全（清单见 docs/RELEASE_MANIFEST.md §2）
    5  回归：只读校验 schema2、独立 static/installer/live 证据；不在发布门禁里重新运行安装器
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
import io
import json
import re
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
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
REPORT_MODES = ("static", "installer", "live")
# Minimal real-case anchors prevent relabelling a live-only list as static/installer evidence.
MODE_REQUIRED_RESULTS = {
    "static": {"R0-static-env", "R21-static-manifest", "R24-static-dict-reproducible", "R26-static-scripts-parse"},
    "installer": {"R13-install-manifest", "R14-uninstall-keeps-settings", "R15-rollback",
                  "R17-adopt-selfheal", "R35-resource-release", "R36-installer-payload"},
    "live": {"L0-viewer", "R1-live-token", "L-build-adaptation"},
}
INPUT_KEYS = ("source_sha256", "dictionary_sha256", "engine_sha256", "setup_sha256")
# Known published history remains frozen even in a shallow clone without tags.
FROZEN_THROUGH = (4, 2, 2)

results: list[tuple[str, bool, str]] = []


def record(name: str, title: str, ok: bool, detail: str) -> None:
    results.append((name, ok, f"{title} — {detail}" if detail else title))


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


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


def release_inputs(version: str, *, root: Path | None = None,
                   build_dir: Path | None = None) -> dict[str, str]:
    """Use the report writer's single source-scope authority; never hash a second scope here."""
    import verify_regressions

    root = ROOT if root is None else Path(root)
    build = BUILD if build_dir is None else Path(build_dir)
    return verify_regressions.release_inputs(
        root, engine_path=build / "CMOLocalizer.dll",
        setup_path=build / f"CMO_Chinese_Setup_v{version}.exe")


def _stamp(value: object, label: str, now: datetime,
           problems: list[str]) -> datetime | None:
    try:
        if not isinstance(value, str):
            raise ValueError("not a string")
        stamp = datetime.fromisoformat(value)
        if stamp.tzinfo is None or stamp.utcoffset() is None:
            raise ValueError("timezone required")
        age = (now - stamp).total_seconds()
        if age < 0:
            problems.append(f"{label} 位于未来")
        elif age > 24 * 3600:
            problems.append(f"{label} 已过期（{age / 3600:.1f} 小时前）")
        return stamp
    except (ValueError, TypeError, OverflowError):
        problems.append(f"{label} 缺少带时区的 generated_at")
        return None


def _result_map(value: object, label: str, problems: list[str]) -> dict[str, dict]:
    if not isinstance(value, list) or not value:
        problems.append(f"{label} results 必须是非空真实用例列表")
        return {}
    found: dict[str, dict] = {}
    for item in value:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip():
            problems.append(f"{label} 存在无有效 id 的用例")
            continue
        cid = item["id"]
        if cid in found:
            problems.append(f"{label} 重复用例 {cid}")
        found[cid] = item
        if item.get("status") != "pass":
            problems.append(f"{label} 用例未通过: {cid}")
    return found


def check_regression_report(version: str | None = None, report_path: Path | None = None,
                            *, expected_inputs: dict[str, str] | None = None,
                            root: Path | None = None, build_dir: Path | None = None,
                            now: datetime | None = None) -> list[str]:
    """Fail closed on schema2, all three independently fresh modes, and current source/payload identity."""
    version = VERSION if version is None else version
    report = REPORT if report_path is None else Path(report_path)
    now = datetime.now(timezone.utc) if now is None else now
    problems: list[str] = []
    if not report.is_file():
        return [f"缺少 {report.name}（先分开跑 static/installer 与 live）"]
    try:
        data = json.loads(report.read_text(encoding="utf-8"))
    except (OSError, ValueError) as exc:
        return [f"报告无法解析: {exc}"]
    if not isinstance(data, dict):
        return ["报告必须是 JSON object"]
    if type(data.get("schema")) is not int or data["schema"] != 2:
        problems.append("报告 schema 必须为 2（旧汇总报告不能授权发布）")
    if data.get("version") != version:
        problems.append(f"报告版本 {data.get('version')} != VERSION {version}")
    if data.get("passed") is not True or data.get("failed"):
        problems.append("回归汇总未明确全通过")
    inputs = data.get("inputs")
    if not isinstance(inputs, dict) or set(inputs) != set(INPUT_KEYS) or any(
            not isinstance(inputs.get(k), str) or not re.fullmatch(r"[0-9a-f]{64}", inputs[k])
            for k in INPUT_KEYS):
        problems.append("报告 inputs 缺少四个有效 SHA256")
    if expected_inputs is None:
        try:
            expected_inputs = release_inputs(version, root=root, build_dir=build_dir)
        except Exception as exc:  # helper failures must not become a successful gate
            problems.append(f"无法取得当前发布输入: {exc}")
    if expected_inputs is not None:
        if set(expected_inputs) != set(INPUT_KEYS) or any(
                not isinstance(expected_inputs.get(k), str) or
                not re.fullmatch(r"[0-9a-f]{64}", expected_inputs[k]) for k in INPUT_KEYS):
            problems.append("当前发布输入不完整")
        elif inputs != expected_inputs:
            problems.append("报告 inputs 与当前源码/词库/引擎/安装器不一致，必须重跑分层回归")
    stamp = _stamp(data.get("generated_at"), "报告", now, problems)
    modes = data.get("modes")
    if not isinstance(modes, list) or len(modes) != len(REPORT_MODES) or any(
            not isinstance(m, str) for m in modes) or set(modes) != set(REPORT_MODES):
        problems.append("报告 modes 必须完整包含 static/installer/live，且不能重复或未知")
    layers = data.get("mode_reports")
    if not isinstance(layers, dict) or set(layers) != set(REPORT_MODES):
        problems.append("缺少完整的独立 mode_reports")
        layers = layers if isinstance(layers, dict) else {}
    merged: dict[str, dict] = {}
    stamps: dict[str, datetime] = {}
    for mode in REPORT_MODES:
        layer = layers.get(mode)
        if not isinstance(layer, dict):
            problems.append(f"缺少 {mode} 分层报告")
            continue
        if layer.get("passed") is not True:
            problems.append(f"{mode} 分层未明确全通过")
        layer_stamp = _stamp(layer.get("generated_at"), mode, now, problems)
        if layer_stamp is not None:
            stamps[mode] = layer_stamp
            if stamp is not None and layer_stamp > stamp:
                problems.append(f"{mode} 时间晚于报告汇总时间")
        found = _result_map(layer.get("results"), mode, problems)
        missing = MODE_REQUIRED_RESULTS[mode] - set(found)
        if missing:
            problems.append(f"{mode} 缺少真实回归用例: {', '.join(sorted(missing))}")
        for cid, item in found.items():
            if cid in merged:
                problems.append(f"分层用例重复归属: {cid}")
            merged[cid] = item
    aggregate = _result_map(data.get("results"), "汇总", problems)
    if aggregate != merged:
        problems.append("汇总 results 与三个独立分层不一致")
    if "live" in stamps and any(stamps[m] > stamps["live"] for m in ("static", "installer") if m in stamps):
        problems.append("最后一轮分层回归不是 live（installer/live 必须分开运行）")
    return problems


def artifact_names(version: str) -> tuple[str, ...]:
    setup = f"CMO_Chinese_Setup_v{version}.exe"
    pack = f"CMO_Chinese_Pack_{version}.zip"
    return setup, setup + ".sha256", pack, pack + ".sha256"


def read_artifacts(version: str, build_dir: Path | None = None) -> tuple[dict[str, bytes], list[str]]:
    """Snapshot all four assets before a network write; SHA files must bind the exact filename."""
    build = BUILD if build_dir is None else Path(build_dir)
    files: dict[str, bytes] = {}
    problems: list[str] = []
    for name in artifact_names(version):
        path = build / name
        try:
            files[name] = path.read_bytes()
            if not files[name]:
                problems.append(f"空产物 {name}")
        except OSError:
            problems.append(f"缺少或无法读取产物 {name}")
    for name in artifact_names(version)[::2]:
        if name not in files or name + ".sha256" not in files:
            continue
        try:
            text = files[name + ".sha256"].decode("ascii").strip()
            match = re.fullmatch(r"([0-9a-fA-F]{64}) [ *]" + re.escape(name), text)
            if match is None:
                problems.append(f"{name}.sha256 必须是单条 SHA256 + 精确文件名")
            elif match[1].lower() != hashlib.sha256(files[name]).hexdigest():
                problems.append(f"{name} 哈希与 .sha256 不符")
        except UnicodeError:
            problems.append(f"{name}.sha256 不是 ASCII 摘要")
    return files, problems


def embedded_engine_hashes(setup: Path) -> dict[str, str]:
    """Read real CLR manifest resources with ReflectionOnlyLoad; never execute the installer."""
    literal = str(setup.resolve()).replace("'", "''")
    code = """$ErrorActionPreference = 'Stop'
$a = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes('%s'))
$sha = [Security.Cryptography.SHA256]::Create()
try {
  foreach ($name in @('CMOLocalizer.dll', 'CMOLocalizer.json')) {
    $s = $a.GetManifestResourceStream($name)
    if ($null -eq $s -or $s.Length -eq 0) { throw ('missing embedded resource: ' + $name) }
    try { $h = [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-', '').ToLowerInvariant() }
    finally { $s.Dispose() }
    Write-Output ($name + '|' + $h)
  }
} finally { $sha.Dispose() }
""" % literal
    p = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", code],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
    if p.returncode:
        raise ValueError("安装器的 CLR 内嵌资源不可读取（ReflectionOnlyLoad）")
    found: dict[str, str] = {}
    for line in p.stdout.splitlines():
        name, sep, digest = line.strip().partition("|")
        if sep and re.fullmatch(r"[0-9a-f]{64}", digest):
            found[name] = digest
    if set(found) != {"CMOLocalizer.dll", "CMOLocalizer.json"}:
        raise ValueError("安装器缺少可核验的内嵌 dll/json")
    return found


def pack_problems(pack_bytes: bytes, version: str, *, setup_bytes: bytes | None = None,
                  expected_inputs: dict[str, str] | None = None) -> list[str]:
    """Portable activation delegates to the same installer, not a copied official config/backup."""
    problems: list[str] = []
    try:
        with zipfile.ZipFile(io.BytesIO(pack_bytes)) as z:
            names = [n for n in z.namelist() if not n.endswith("/")]
            if len({n.casefold() for n in names}) != len(names):
                problems.append("绿色包包含重复或大小写冲突条目")
            for name in names:
                if ("\\" in name or name.startswith("/") or re.match(r"^[A-Za-z]:", name)
                        or any(p in (".", "..") for p in name.split("/"))):
                    problems.append(f"绿色包路径不安全: {name}")
                lower = name.casefold()
                if (lower.endswith((".orig", ".cmo_bak", ".bak")) or
                        lower.rsplit("/", 1)[-1] in ("command.exe.config", "command.exe.config.patch") or
                        any(bad.casefold() in lower for bad in FORBIDDEN_IN_PACK)):
                    problems.append(f"不该入包的配置/备份/过程条目: {name}")
            setup_name = artifact_names(version)[0]
            required = ["汉化说明.md", "CMOLocalizer.dll", "CMOLocalizer.json", "CMO_Updater.exe", setup_name,
                        "WW/PlacenameSets/Synoptic/0000_0000.wwp", "WW/PlacenameSets/Cities/0000_0000.wwp",
                        "Launcher/Localization/stringsChinese.resx"]
            required += [f"DB/Templates/{t}.html" for t in DB_TEMPLATES]
            for need in required:
                if need not in names:
                    problems.append(f"绿色包缺少条目 {need}")
            if sum(n.startswith("Symbols/Menu/") and n.lower().endswith(".png") for n in names) < 28:
                problems.append("绿色包菜单按钮图不足 28")
            if sum(n.startswith("Scenarios/") and n.lower().endswith(".html") for n in names) < 300:
                problems.append("绿色包 Scenarios HTML 不足 300（不能用备份凑条数）")
            if setup_bytes is not None and setup_name in names and z.read(setup_name) != setup_bytes:
                problems.append("绿色包的版本化安装器与上传安装器不是同一份字节")
            if "汉化说明.md" in names:
                text = z.read("汉化说明.md").decode("utf-8-sig")
                if not all(token in text for token in (setup_name, "--install", "--silent", "--adopt")):
                    problems.append("绿色包说明缺少同版本安装器的首次 apply/--install --silent 与 --adopt 说明")
            if "CMOLocalizer.json" in names:
                dictionary = json.loads(z.read("CMOLocalizer.json"))
                if not isinstance(dictionary, dict) or dictionary.get("_meta", {}).get("version") != version:
                    problems.append("绿色包内词库版本与 VERSION 不符")
            if expected_inputs is not None:
                for name, key in (("CMOLocalizer.dll", "engine_sha256"), ("CMOLocalizer.json", "dictionary_sha256")):
                    if name in names and hashlib.sha256(z.read(name)).hexdigest() != expected_inputs.get(key):
                        problems.append(f"绿色包 {name} 与报告/当前输入不一致")
    except (OSError, ValueError, KeyError, TypeError, AttributeError, zipfile.BadZipFile, RuntimeError) as exc:
        problems.append(f"绿色包无法解析: {exc}")
    return problems


def check_upload_preflight(version: str, *, root: Path | None = None,
                           build_dir: Path | None = None,
                           report_path: Path | None = None) -> tuple[dict[str, bytes], list[str]]:
    """Shared mandatory preflight. No credentials, installation, report writes, or remote writes."""
    root = ROOT if root is None else Path(root)
    build = BUILD if build_dir is None else Path(build_dir)
    files, problems = read_artifacts(version, build)
    inputs = None
    try:
        if (root / "VERSION").read_text(encoding="utf-8").strip() != version:
            problems.append("发布版本必须精确等于 VERSION")
        inputs = release_inputs(version, root=root, build_dir=build)
    except Exception as exc:
        problems.append(f"当前发布输入无法核验: {exc}")
    problems += check_regression_report(version, report_path or build / "regression_report.json",
                                        expected_inputs=inputs, root=root, build_dir=build)
    if problems:
        return files, problems
    setup_name, _, pack_name, _ = artifact_names(version)
    if inputs is not None:
        if setup_name in files and hashlib.sha256(files[setup_name]).hexdigest() != inputs.get("setup_sha256"):
            problems.append("上传安装器与当前报告 inputs 不一致")
        if pack_name in files:
            problems += pack_problems(files[pack_name], version, setup_bytes=files.get(setup_name),
                                      expected_inputs=inputs)
        if setup_name in files:
            try:
                embedded = embedded_engine_hashes(build / setup_name)
                for name, key in (("CMOLocalizer.dll", "engine_sha256"), ("CMOLocalizer.json", "dictionary_sha256")):
                    if embedded[name] != inputs.get(key):
                        problems.append(f"安装器内嵌 {name} 与当前输入/报告/绿色包不一致")
                if sha256(build / setup_name) != hashlib.sha256(files[setup_name]).hexdigest():
                    problems.append("安装器在预检期间发生变化")
            except Exception as exc:
                problems.append(f"安装器内嵌载荷核验失败: {exc}")
    # File bytes are retained for upload so a later disk change cannot replace the validated asset.
    return files, problems


def frozen_version_problems(version: str, *, root: Path | None = None,
                            build_dir: Path | None = None, operation: str = "build") -> list[str]:
    root = ROOT if root is None else Path(root)
    build = BUILD if build_dir is None else Path(build_dir)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version):
        return ["VERSION 必须为 x.y.z"]
    problems = []
    if tuple(map(int, version.split("."))) <= FROZEN_THROUGH:
        problems.append(f"v{version} 已冻结（历史至少到 v4.2.2），禁止重新构建/打包")
    try:
        p = subprocess.run(["git", "tag", "--list", f"v{version}"], cwd=root,
                           capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=15)
        if p.returncode:
            problems.append("无法只读核验本地 tag，拒绝构建/打包")
        elif p.stdout.strip():
            problems.append(f"已有 tag v{version}，禁止重建/重打包")
    except (OSError, subprocess.TimeoutExpired):
        problems.append("无法只读核验本地 tag，拒绝构建/打包")
    names = artifact_names(version)
    # Packaging reuses the exact already-tested EXE; it must not compile it again.
    protected = names if operation == "build" else names[1:]
    for name in protected:
        if (build / name).exists():
            problems.append(f"拒绝覆盖已有资产 {name}")
    return problems


def check_artifacts() -> None:
    files, problems = read_artifacts(VERSION)
    record("artifacts", "安装包/绿色包 + 两个 SHA256（精确文件名与字节）", not problems,
           "; ".join(problems) if problems else f"全部 {len(files)} 个资产已核验")


def check_pack_contents() -> None:
    try:
        problems = pack_problems(PACK.read_bytes(), VERSION, setup_bytes=SETUP.read_bytes(),
                                 expected_inputs=release_inputs(VERSION))
    except Exception as exc:
        problems = [f"绿色包/当前输入无法读取: {exc}"]
    record("pack-contents", "绿色包同安装器单路径启用且无覆盖官方配置/旧备份", not problems,
           "; ".join(problems) if problems else "同版本安装器/dll/json/汉化资产全部匹配")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="发版统一入库检查", allow_abbrev=False)

    ap.add_argument("--pre", action="store_true",
                    help="打包前预检：跳过产物与绿色包内容检查")
    ap.add_argument("--json", action="store_true", help="输出机器可读汇总")
    ap.add_argument("--allow-dirty", action="store_true", help="允许工作区未提交改动（开发用）")
    ap.add_argument("--check-frozen-version", metavar="VERSION",
                    help="仅执行只读冻结版本/已有资产保护，供 build.sh/package.sh 在任何写入前调用")
    ap.add_argument("--operation", choices=("build", "package"), default="build")
    args = ap.parse_args(argv)
    if args.check_frozen_version:
        if args.pre or args.allow_dirty:
            ap.error("冻结保护不能与 --pre/--allow-dirty 混用")
        problems = frozen_version_problems(args.check_frozen_version, operation=args.operation)
        for problem in problems:
            print("FAIL: " + problem, file=sys.stderr)
        if not problems:
            print(f"PASS: v{args.check_frozen_version} 无冻结 tag/已有受保护资产")
        return 1 if problems else 0
    if args.operation != "build":
        ap.error("--operation 只能与 --check-frozen-version 一起使用")
    results.clear()
    VA.results.clear()

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
    # Do not rerun installer here: it can close the game and invalidate the final live phase.
    problems = check_regression_report()
    record("regression-report", "schema2 发版回归报告（当前输入/三个独立分层/非空/非未来/24h）", not problems,
           "; ".join(problems) if problems else "static/installer/live 各自证据有效")
    if args.pre:
        print("  [SKIP] artifacts / pack-contents  (--pre：尚未打包)")
    else:
        check_artifacts()
        check_pack_contents()
        _, problems = check_upload_preflight(VERSION)
        record("payload-binding", "四资产与报告/内嵌 dll/json 绑定", not problems,
               "; ".join(problems) if problems else "内嵌载荷与报告/当前引擎/词库/绿色包一致")

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
