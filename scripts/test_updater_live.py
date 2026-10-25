"""更新助手实机验证（v4.1.6 起）：--check 四条路径 + 托盘常驻与设置窗。

用法：python scripts/test_updater_live.py [check|tray|all]

与旧版的区别：助手的状态真相改为「游戏目录内的安装清单」（%APPDATA%\\CMOChinese\\settings.json
保存设置），因此本脚本在**临时游戏目录**里用安装器 CLI 建立真实清单，再改写清单版本号来模拟
「已是最新 / 有更新 / 可回退（有缓存）/ 不提供回退（无缓存）」四条路径，不再改注册表 Version，
也不再碰真实游戏目录。

判定方式不依赖中文文本（控制台编码会破坏比对）：
  - 更新/回退提示窗是 WinForms 窗体（cls 以 WindowsForms10 开头，宽 560）
  - 纯提示是 MessageBox（cls #32770）
两种窗口类型可以直接区分，另外每条路径都留截图作为证据。
"""
import json
import os
import shutil
import subprocess
import proc_util
proc_util.utf8_stdout()
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import live_harness as H  # noqa: E402

VER = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
SETUP = ROOT / "build" / f"CMO_Chinese_Setup_v{VER}.exe"
UPD = ROOT / "build" / "CMO_Updater.exe"
BASE = ROOT / "evidence" / "design" / "v414_updater"
SETTINGS = Path(os.environ["APPDATA"]) / "CMOChinese" / "settings.json"
PACKAGES = Path(os.environ["LOCALAPPDATA"]) / "CMOChinese" / "packages"
MANIFEST = "CMOChinese_Installed.json"


def make_game(tmp: Path, version: str) -> Path:
    """用安装器 CLI 在临时目录装一份（产生真实清单与哈希），再把清单版本改成 version。"""
    game = tmp / "game"
    for d in ("DB/Templates", "Symbols/Menu", "Scenarios"):
        (game / d).mkdir(parents=True, exist_ok=True)
    (game / "Command.exe").write_bytes(b"stub")
    (game / "Command.exe.config").write_text(
        '<?xml version="1.0"?><configuration><runtime></runtime></configuration>', encoding="utf-8")
    proc_util.run([sys.executable, "-c", "import sys; sys.exit(0)"], capture_output=True)
    rc = proc_util.run([str(SETUP), "--install", str(game), "--silent"],
                        capture_output=True, )
    man = game / MANIFEST
    data = json.loads(man.read_text(encoding="utf-8"))
    data["version"] = version
    man.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    return game


def seed_settings(game: Path) -> None:
    SETTINGS.parent.mkdir(parents=True, exist_ok=True)
    SETTINGS.write_text(json.dumps({
        "schema": 1, "check_interval_hours": 3, "notify_mode": 0, "tray_on_close": False,
        "auto_check": True, "auto_install": True, "mirror": "https://ghproxy.net/",
        "game_path": str(game),
    }, ensure_ascii=False, indent=1), encoding="utf-8")


def run_check(tag: str, wait: float = 9.0) -> dict:
    p = subprocess.Popen([str(UPD), "--check"])
    time.sleep(wait)
    wins = [w for w in H.list_windows()
            if w["pid"] == p.pid and w["visible"] and w["cls"] != "GDI+ Window" and w["rect"]["Width"] > 200]
    out = {"tag": tag, "windows": [], "kind": None}
    if wins:
        w = sorted(wins, key=lambda x: -x["rect"]["Width"])[0]
        is_form = str(w["cls"]).startswith("WindowsForms10")
        out["windows"].append({"cls": w["cls"], "rect": w["rect"], "title": w["title"]})
        out["kind"] = "UpdateForm" if is_form else "MessageBox"
        out["shot"] = str(H.shot(f"live/{tag}", hwnd=w["hwnd"], out_dir=BASE))
        print(f"[{tag}] {out['kind']} cls={w['cls']} -> {out['shot']}")
    else:
        print(f"[{tag}] 没有出现窗口")
    H.keys("{ENTER}")
    time.sleep(1.0)
    try:
        p.wait(timeout=6)
        out["exit"] = p.returncode
    except Exception:
        p.kill()
        out["exit"] = None
    return out


def check_tray(tag: str = "tray") -> dict:
    p = subprocess.Popen([str(UPD), "--restarted"])
    time.sleep(6)
    procs = proc_util.run(["tasklist", "/FI", "IMAGENAME eq CMO_Updater.exe"],
                           capture_output=True).stdout
    f = H.shot(f"live/{tag}_tray", out_dir=BASE)
    running = "CMO_Updater.exe" in procs
    print(f"[{tag}] 托盘进程: {'运行中' if running else '未运行'} -> {f}")
    proc_util.run(["taskkill", "/IM", "CMO_Updater.exe", "/F"], capture_output=True)
    time.sleep(1)
    return {"tag": tag, "running": running, "shot": str(f)}


def check_settings_writeback(tag: str = "settings_writeback") -> dict:
    """--check 之后设置文件必须保留种子值并回写 last_check_utc（证明设置以文件为准）。"""
    before = json.loads(SETTINGS.read_text(encoding="utf-8"))
    p = subprocess.Popen([str(UPD), "--check"])
    time.sleep(9)
    H.keys("{ENTER}")
    time.sleep(1)
    try:
        p.wait(timeout=6)
    except Exception:
        p.kill()
    after = json.loads(SETTINGS.read_text(encoding="utf-8"))
    kept = (after.get("check_interval_hours") == 3 and after.get("notify_mode") == 0
            and after.get("tray_on_close") is False and after.get("game_path") == before.get("game_path"))
    updated = bool(after.get("last_check_utc"))
    print(f"[{tag}] 设置保留={kept} 回写时间={updated} interval={after.get('check_interval_hours')} "
          f"tray_on_close={after.get('tray_on_close')}")
    return {"tag": tag, "settings_kept": kept, "last_check_utc_written": updated, "after": after}


def check_settings_window(tag: str = "settings_window") -> dict:
    """打开设置窗（含新增的「关闭窗口时最小化到托盘」）并截图。"""
    p = subprocess.Popen([str(UPD), "--settings"])
    time.sleep(6)
    wins = [w for w in H.list_windows()
            if w["pid"] == p.pid and w["visible"] and str(w["cls"]).startswith("WindowsForms10")]
    out = {"tag": tag, "found": bool(wins)}
    if wins:
        w = sorted(wins, key=lambda x: -x["rect"]["Width"])[0]
        out["shot"] = str(H.shot(f"live/{tag}", hwnd=w["hwnd"], out_dir=BASE))
        out["title"] = w["title"]
        print(f"[{tag}] {w['title']} -> {out['shot']}")
    else:
        print(f"[{tag}] 未找到设置窗")
    p.kill()
    time.sleep(0.5)
    return out


def main() -> int:
    what = sys.argv[1] if len(sys.argv) > 1 else "all"
    saved_settings = SETTINGS.read_bytes() if SETTINGS.exists() else None
    had_pkg = PACKAGES.exists()
    result = []
    tmp = Path(tempfile.mkdtemp(prefix="cmo-upd-"))
    try:
        if what in ("all", "check"):
            game = make_game(tmp / "a", "4.1.5")
            seed_settings(game)
            result.append(run_check("check_uptodate"))          # 与线上最新一致 → MessageBox「已是最新」
            result.append(check_settings_writeback())

            game2 = make_game(tmp / "b", "4.1.2")
            seed_settings(game2)
            result.append(run_check("check_newver"))            # 低于线上 → UpdateForm「发现新版本」

            game3 = make_game(tmp / "c", "4.2.0")
            seed_settings(game3)
            # 确保缓存里有线上最新版安装包 → 才允许提示"可回退"
            PACKAGES.mkdir(parents=True, exist_ok=True)
            cached = PACKAGES / f"CMO_Chinese_Setup_v{VER}.exe"
            if not cached.exists():
                shutil.copy(SETUP, cached)
            result.append(run_check("check_rollback_offer"))    # 本地高于线上 + 有缓存 → UpdateForm「可回退」

            for f in PACKAGES.glob("*.exe"):
                f.unlink()
            result.append(run_check("check_rollback_gated"))    # 本地高于线上 + 无缓存 → MessageBox（不提供回退）
        if what in ("all", "tray"):
            game = make_game(tmp / "t", VER)
            seed_settings(game)
            result.append(check_tray())
            result.append(check_settings_window())
    finally:
        if saved_settings is None:
            if SETTINGS.exists():
                SETTINGS.unlink()
        else:
            SETTINGS.write_bytes(saved_settings)
        if not had_pkg:
            shutil.rmtree(PACKAGES, ignore_errors=True)
        shutil.rmtree(tmp, ignore_errors=True)

    (ROOT / "build" / "updater_live_v414.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
    kinds = {r["tag"]: r.get("kind") for r in result if r.get("kind")}
    expect = {"check_uptodate": "MessageBox", "check_newver": "UpdateForm",
              "check_rollback_offer": "UpdateForm", "check_rollback_gated": "MessageBox"}
    problems = [f"{k}: {kinds.get(k)}" for k, v in expect.items() if k in kinds and kinds.get(k) != v]
    if "settings_writeback" in [r["tag"] for r in result]:
        sw = [r for r in result if r["tag"] == "settings_writeback"][0]
        if not (sw.get("settings_kept") and sw.get("last_check_utc_written")):
            problems.append("settings_writeback")
    if "tray" in [r["tag"] for r in result] and not [r for r in result if r["tag"] == "tray"][0].get("running"):
        problems.append("tray-not-running")
    print(f"==> 路径判定: {kinds}")
    print("==> " + ("OK" if not problems else "FAIL: " + ", ".join(problems)))
    print("saved build/updater_live_v414.json")
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(main())
