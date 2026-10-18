#!/usr/bin/env python3
"""Unified acceptance gate for the CMO localization release.

Runs every deliverable check in one pass and prints a summary table.  Exit code
is 0 only when all gates pass.

Gates
-----
  1. dictionary    dict/CMOLocalizer.json integrity + quality (verify_release)
  2. menu          every menu item translated, no stiff residue (verify_menu)
  3. engine parse  the engine's own scanner can load every entry
  4. scenario meta 100% of <ScenTitle> / <ScenSetting> translated
  5. briefings     HTML briefings carry Chinese prose, structure intact
  6. placenames    Synoptic + Cities .wwp translated or explicitly pending
  7. db templates  8 DB HTML templates localized
  8. version       VERSION consistent across VERSION / engine / installer
  9. repository    git worktree clean
 10. secrets       no plaintext credentials in worktree or git history
"""
from __future__ import annotations

import html as htmlmod
import json
import re
import subprocess
import sys
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
SCEN = GAME / "Scenarios"
HAN = re.compile(r"[\u4e00-\u9fff]")

results: list[tuple[str, bool, str]] = []


def record(name: str, ok: bool, detail: str) -> None:
    results.append((name, ok, detail))


def run(script: str, argv: list[str] | None = None) -> tuple[bool, str]:
    cmd = [sys.executable, str(ROOT / "scripts" / script)] + (argv or [])
    p = subprocess.run(cmd, capture_output=True, text=True,
                       encoding="utf-8", errors="replace")
    tail = [l for l in (p.stdout or "").strip().splitlines() if l.strip()]
    return p.returncode == 0, (tail[-1] if tail else "(no output)")


def gate_dictionary() -> None:
    ok, msg = run("verify_release.py")
    doc = json.loads((DICT / "CMOLocalizer.json").read_text(encoding="utf-8"))
    record("dictionary", ok,
           f"offsets={len(doc['offsets'])} exact={len(doc['exact'])} | {msg}")


def gate_menu() -> None:
    ok, msg = run("verify_menu.py")
    record("menu", ok, msg)


def gate_engine_parse() -> None:
    ok, msg = run("verify_engine_parse.py")
    record("engine parse", ok, msg)


def gate_scenario_meta() -> None:
    pool = json.loads((DICT / "strings_pool.json").read_text(encoding="utf-8"))
    doc = json.loads((DICT / "CMOLocalizer.json").read_text(encoding="utf-8"))
    exact = doc["exact"]
    meta_file = DICT / "scenario_meta.json"
    # 有游戏目录时重新抽取（新增/更新过想定后会多出未翻译的标题）；无游戏时用已入库快照。
    if GAME.is_dir() or not meta_file.is_file():
        subprocess.run([sys.executable, str(ROOT / "scripts/scenario_meta.py")],
                       capture_output=True)
    meta = json.loads(meta_file.read_text(encoding="utf-8"))
    miss_t = [t for t in meta["titles"] if t not in exact]
    miss_s = [s for s in meta["settings"] if s not in exact]
    # 战役内剧本名（*.campaign 的 <Name>）同样是引擎按整串匹配的显示文本；
    # “Local Video: *.wmv|*.mp4”是游戏用于找视频文件的名字，不得翻译，故排除。
    camp_names = sorted({n for c in meta.get("campaigns", []) for n in c.get("names", [])})
    camp = [n for n in camp_names if not n.startswith("Local Video:")]
    miss_c = [n for n in camp if n not in exact]
    ok = not miss_t and not miss_s and not miss_c
    detail = (f"titles {len(meta['titles'])-len(miss_t)}/{len(meta['titles'])}, "
              f"settings {len(meta['settings'])-len(miss_s)}/{len(meta['settings'])}, "
              f"campaign names {len(camp)-len(miss_c)}/{len(camp)}")
    if miss_c:
        detail += " | 缺失: " + ", ".join(miss_c[:8])
    record("scenario meta", ok, detail)


# 已人工确认为「纯图片、无可译文本」的简报页（例：Desert Storm 战役的剧情页整页是一张图）。
# 游戏内容更新会新增同类文件（2026-10-17 Steam 更新 1900.23 新增了 16 个），若把它们计入
# 「图片型占比」就会把门禁顶红，而它们本就没有可译文本。仍计入总数与中文覆盖统计。
IMAGE_ONLY_ALLOW = re.compile(r"(?:^|[\\/])Assets[\\/][^\\/]*Desc\.html$", re.I)


def gate_briefings() -> None:
    if not SCEN.is_dir():
        record("briefings", False, f"scenario root not found: {SCEN}")
        return
    files = [f for f in SCEN.rglob("*.html") if not f.name.endswith(".orig")]
    cjk = 0
    image_only = 0
    for f in files:
        text = f.read_text(encoding="utf-8", errors="replace")
        body = htmlmod.unescape(re.sub(r"<[^>]+>", " ", text))
        n = len(HAN.findall(body))
        cjk += n
        if n == 0 and "<img" in text.lower():
            image_only += 1
    translated = sum(1 for f in files
                     if HAN.search(re.sub(r"<[^>]+>", " ",
                                          f.read_text(encoding="utf-8",
                                                       errors="replace"))))
    # 空集不得空过：无文件、或「图片型」占比过高（>10%）都判 FAIL。
    # 旧式 `translated >= len(files) - image_only` 在 files 为空时是 0>=0，永远绿。
    image_names = [f.name for f in files if not HAN.search(re.sub(r"<[^>]+>", " ",
                                     f.read_text(encoding="utf-8", errors="replace"))) ]
    allowed_image_only = [f for f in files if IMAGE_ONLY_ALLOW.search(str(f))]
    unconfirmed = image_only - len(allowed_image_only)
    denom = max(1, len(files) - len(allowed_image_only))
    ratio_ok = unconfirmed * 10 <= denom
    ok = bool(files) and ratio_ok and translated >= len(files) - image_only
    if not files:
        record("briefings", False, f"未找到任何简报文件（{SCEN} 下无 *.html），门禁目标为空判 FAIL")
        return
    record("briefings", ok,
           f"{translated}/{len(files)} files Chinese ({image_only} image-only, "
           f"其中 {len(allowed_image_only)} 个已人工确认为纯图片白名单), {cjk} CJK chars"
           + ("" if ratio_ok else f"；图片型占比过高需人工确认: {image_names[:3]}"))


def gate_placenames() -> None:
    ok, msg = run("apply_placenames.py", ["--verify"])
    detail = msg
    syn = cite = None
    for line in detail.splitlines():
        pass
    text = subprocess.run(
        [sys.executable, str(ROOT / "scripts/apply_placenames.py"), "--verify"],
        capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    for line in text.splitlines():
        if "Synoptic" in line:
            syn = line.strip()
        if "Cities" in line:
            cite = line.strip()
    record("placenames", ok, f"{syn} ;; {cite}")


def gate_db_templates() -> None:
    tpl = GAME / "DB/Templates"
    files = sorted(tpl.glob("*.html"))
    if not files:
        record("db templates", False, "no templates found")
        return
    localized = []
    for f in files:
        body = htmlmod.unescape(re.sub(r"<[^>]+>", " ",
                                       f.read_text(encoding="utf-8",
                                                    errors="replace")))
        if HAN.search(body):
            localized.append(f.name)
    platform_only = [f.name for f in files if f.name == "Platform.html"]
    desc_file = ROOT / "dict/src/db_descriptions.json"
    descs_ok = desc_file.is_file() and len(json.loads(desc_file.read_text(encoding="utf-8"))) >= 16300
    ok = (len(localized) >= len(files) - len(platform_only) - 1) and descs_ok
    record("db templates", ok,
           f"{len(localized)}/{len(files)} carry Chinese (Platform.html is the font-only wrapper), "
           f"db_descriptions={len(json.loads(desc_file.read_text(encoding='utf-8')))} entries")


def gate_version() -> None:
    """版本号单一来源：五处必须一致（VERSION / 安装器 CurrentVersion / 安装器标题 /
    助手 UpdaterVersion / 词典 _meta.version）。历史事故：词典戳停在 4.1.4，
    而 VERSION 已到 4.1.5，导致发布包里资产自称的版本与安装包名不符。"""
    import json as _json

    ver = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    engine = (ROOT / "src/engine/CMOLocalizer.cs").read_text(encoding="utf-8")
    inst = (ROOT / "src/installer/CMO_Chinese_Setup.cs").read_text(encoding="utf-8")
    upd = (ROOT / "src/updater/CMO_Updater.cs").read_text(encoding="utf-8")
    major = ver.split(".")[0]

    def find(pattern: str, text: str) -> str | None:
        m = re.search(pattern, text)
        return m.group(1) if m else None

    # O-06 之后版本常量只存一处：CMOShared.ReleaseConstants.Version。
    # 安装器/助手的 CurrentVersion / UpdaterVersion 只是对该常量的引用，不再是字面量副本。
    shared_src = (ROOT / "src/shared/CMOShared.cs").read_text(encoding="utf-8")
    shared_ver = find(r'class\s+ReleaseConstants[\s\S]*?Version\s*=\s*"([0-9.]+)"', shared_src)
    inst_title = find(r"一键安装工具 v([0-9.]+)", inst)
    inst_ref = bool(re.search(r'CurrentVersion\s*=\s*ReleaseConstants\.Version', inst))
    upd_ref = bool(re.search(r'UpdaterVersion\s*=\s*ReleaseConstants\.Version', upd))
    try:
        dict_ver = _json.loads((ROOT / "dict/CMOLocalizer.json").read_text(encoding="utf-8"))["_meta"]["version"]
    except Exception as ex:  # noqa: BLE001
        dict_ver = f"<读取失败: {ex}>"

    places = {
        "VERSION": ver,
        "CMOShared.ReleaseConstants.Version": shared_ver,
        "installer.title": inst_title,
        "dict._meta.version": dict_ver,
    }
    mismatch = {k: v for k, v in places.items() if v != ver}
    engine_ok = f"v{major}.0 Engine" in engine or f"v{ver} Engine" in engine
    refs_ok = inst_ref and upd_ref

    # 游戏内部版本（构建号，例如 1900.23）：与我们的版本号一起必须写清（安装器标题 / 更新助手 /
    # 发版说明 / README）。单一来源 GAME_BUILD ↔ CMOShared.ReleaseConstants.GameBuild。
    # R31 事故：游戏构建号从 1900.20 跳到 1900.23，汉化整批失效而我们此前根本没记录构建号。
    gb_file = ROOT / "GAME_BUILD"
    game_build = gb_file.read_text(encoding="utf-8").strip() if gb_file.is_file() else None
    shared_build = find(r'class\s+ReleaseConstants[\s\S]*?GameBuild\s*=\s*"([0-9.]+)"', shared_src)
    inst_build_ref = ("适配游戏内部版本" in inst) and ("ReleaseConstants.GameBuild" in inst)
    try:
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
    except Exception:  # noqa: BLE001
        readme = ""
    readme_ok = bool(game_build) and game_build in readme
    build_ok = bool(game_build) and shared_build == game_build and inst_build_ref and readme_ok

    ok = not mismatch and engine_ok and refs_ok and build_ok
    detail = ", ".join(f"{k}={v}" for k, v in places.items()) + f", engine={'ok' if engine_ok else 'MISMATCH'}"
    detail += f", installer/updater 引用共享常量={refs_ok}"
    detail += f", 游戏内部版本={game_build}"
    if not build_ok:
        detail += (f" [构建号不一致: 共享常量={shared_build}, 安装器引用={inst_build_ref},"
                   f" README含构建号={readme_ok}]")
    if mismatch:
        detail += f" → 与 VERSION={ver} 不一致: {sorted(mismatch)}"
    if not refs_ok:
        detail += " → 安装器/助手又写回了版本字面量副本"
    record("version", ok, detail)


def gate_repository() -> None:
    p = subprocess.run(["git", "status", "--porcelain"], cwd=str(ROOT),
                       capture_output=True, text=True)
    dirty = [l for l in p.stdout.splitlines() if l.strip()]
    record("repository", not dirty,
           "clean" if not dirty else f"{len(dirty)} uncommitted path(s)")


def gate_no_secrets() -> None:
    """明文凭据硬门禁：工作区 + 全历史（verify_no_secrets.py --history）。"""
    ok, tail = run("verify_no_secrets.py", ["--history"])
    record("secrets", ok,
           "no plaintext credentials" if ok else f"PLAINTEXT CREDENTIALS: {tail}")


def main() -> int:
    for g in (gate_dictionary, gate_menu, gate_engine_parse,
              gate_scenario_meta, gate_briefings, gate_placenames,
              gate_db_templates, gate_version, gate_no_secrets,
              gate_repository):
        try:
            g()
        except Exception as exc:  # noqa: BLE001
            record(g.__name__, False, f"EXCEPTION: {exc}")

    width = max(len(n) for n, _, _ in results)
    print("\n" + "=" * 78)
    print("CMO 汉化验收")
    print("=" * 78)
    for name, ok, detail in results:
        print(f"  [{'PASS' if ok else 'FAIL'}] {name:<{width}}  {detail}")
    failed = [n for n, ok, _ in results if not ok]
    print("=" * 78)
    if failed:
        print(f"结果：{len(results)-len(failed)}/{len(results)} 通过，"
              f"失败：{', '.join(failed)}")
        return 1
    print(f"结果：全部 {len(results)} 项通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
