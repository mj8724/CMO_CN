"""安装器新版界面实机验证：版本更新分组渲染 + 频率/提醒方式/开机启动/回退 控件。

用法：python scripts/test_installer_ui.py
产出：evidence/design/v414_updater/live/installer_*.png 与 build/installer_ui_v414.json

注意：本脚本会真实点击安装器 UI，因此会改动「设置文件（%APPDATA%\CMOChinese\settings.json）」、
注册表 Run 键、以及（自动探测到的）游戏目录里的自适应登记清单。
运行前全部备份，结束/失败一律恢复，避免污染用户环境。
"""
import ctypes
import json
import os
import subprocess
import proc_util
proc_util.utf8_stdout()
import sys
import time
import winreg
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import live_harness as H  # noqa: E402

user32 = ctypes.windll.user32


class _RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


class _POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]


def client_origin(hwnd: int):
    """返回客户区原点（屏幕坐标，物理像素）与客户区尺寸。"""
    p = _POINT(0, 0)
    user32.ClientToScreen(wintypes.HWND(hwnd), ctypes.byref(p))
    r = _RECT()
    user32.GetClientRect(wintypes.HWND(hwnd), ctypes.byref(r))
    return (p.x, p.y, r.right, r.bottom)


VER = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
SETUP = ROOT / "build" / f"CMO_Chinese_Setup_v{VER}.exe"
SETUP_NAME = SETUP.name
BASE = ROOT / "evidence" / "design" / "v414_updater"
REG = r"Software\CMOChinese"
RUNKEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
MANIFEST_NAME = "CMOChinese_Installed.json"
DEFAULT_GAME = Path(r"G:\soft\steam\steamapps\common\Command - Modern Operations")

# 控件在客户区内的坐标（与 CMO_Chinese_Setup.cs 的 Location 对应；grpUpdate 原点 = (22, 320)）
GRP = (22, 320)
BTN_CHECK = (GRP[0] + 174 + 50, GRP[1] + 146 + 15)     # 检查升级
BTN_NEVER = (GRP[0] + 284 + 50, GRP[1] + 146 + 15)     # 不再提醒
BTN_ROLLBACK = (GRP[0] + 394 + 90, GRP[1] + 146 + 15)  # 回退到上一版本
CHK_START = (GRP[0] + 14 + 8, GRP[1] + 112 + 8)        # 随 Windows 启动并在后台自动检查更新
CB_INTERVAL = (GRP[0] + 120 + 95, GRP[1] + 75 + 13)    # 自动检查频率下拉
CB_NOTIFY = (GRP[0] + 408 + 80, GRP[1] + 75 + 13)      # 提醒方式下拉

SETTINGS_PATH = Path(os.environ["APPDATA"]) / "CMOChinese" / "settings.json"


def settings_text() -> str:
    return SETTINGS_PATH.read_text(encoding="utf-8") if SETTINGS_PATH.exists() else ""


def settings_json() -> dict:
    try:
        return json.loads(settings_text()) if settings_text() else {}
    except Exception:
        return {}


def reg_get(name, hive=REG):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, hive) as k:
            return winreg.QueryValueEx(k, name)[0]
    except FileNotFoundError:
        return None


def reg_set_str(name, value, hive=REG):
    with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, hive) as k:
        winreg.SetValueEx(k, name, 0, winreg.REG_SZ, value)


def reg_del(name, hive=REG):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, hive, 0, winreg.KEY_SET_VALUE) as k:
            winreg.DeleteValue(k, name)
    except FileNotFoundError:
        pass


def run_key() -> str | None:
    return reg_get("CMOChineseUpdater", RUNKEY)


def modal_dialogs(pid: int):
    return [w for w in H.list_windows() if w["pid"] == pid and w["cls"] == "#32770" and w["visible"]]


def dismiss_all(pid: int, tries: int = 6, wait: float = 1.2) -> bool:
    """关闭本进程的模态框：先按回车，仍不消失就点「确定」按钮。"""
    for _ in range(tries):
        d = modal_dialogs(pid)
        if not d:
            return True
        H.focus(d[0]["hwnd"])
        time.sleep(0.3)
        H.keys("{ENTER}")
        time.sleep(wait)
        left = modal_dialogs(pid)
        if left:
            r = left[0]["rect"]
            H.click(r["X"] + r["Width"] // 2, r["Y"] + r["Height"] - 26)
            time.sleep(wait)
    return not modal_dialogs(pid)


def game_dir() -> Path:
    gp = reg_get("GamePath")
    return Path(gp) if gp else DEFAULT_GAME


def main() -> int:
    result = {"steps": [], "version": VER}
    saved_settings = SETTINGS_PATH.read_bytes() if SETTINGS_PATH.exists() else None
    man = game_dir() / MANIFEST_NAME
    saved_manifest = man.read_bytes() if man.exists() else None
    saved_version_reg = reg_get("Version")
    saved_run = run_key()
    saved_autocheck = reg_get("AutoCheck")

    def restore():
        try:
            if saved_settings is None:
                if SETTINGS_PATH.exists():
                    SETTINGS_PATH.unlink()
            else:
                SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
                SETTINGS_PATH.write_bytes(saved_settings)
        except Exception as ex:
            print(f"恢复设置失败: {ex}")
        try:
            if saved_manifest is None:
                if man.exists():
                    man.unlink()
            else:
                man.write_bytes(saved_manifest)
        except Exception as ex:
            print(f"恢复安装清单失败: {ex}")
        try:
            if saved_version_reg is None:
                reg_del("Version")
            else:
                reg_set_str("Version", str(saved_version_reg))
            if saved_run is None:
                reg_del("CMOChineseUpdater", RUNKEY)
            else:
                reg_set_str("CMOChineseUpdater", str(saved_run), RUNKEY)
            if saved_autocheck is not None:
                with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, REG) as k:
                    winreg.SetValueEx(k, "AutoCheck", 0, winreg.REG_DWORD, int(saved_autocheck))
        except Exception as ex:
            print(f"恢复注册表失败: {ex}")

    for line in proc_util.run(["tasklist", "/FI", f"IMAGENAME eq {SETUP_NAME}"],
                               capture_output=True).stdout.splitlines():
        if "CMO_Chinese_Setup" in line:
            proc_util.run(["taskkill", "/IM", SETUP_NAME, "/F"], capture_output=True)

    try:
        proc = subprocess.Popen([str(SETUP)])
        time.sleep(5)
        wins = [w for w in H.list_windows() if w["pid"] == proc.pid and w["visible"] and w["rect"]["Width"] > 500]
        if not wins:
            print("未找到安装器窗口")
            return 1
        win = wins[0]
        r = win["rect"]
        ox, oy, cw, ch = client_origin(win["hwnd"])
        print(f"窗口 rect={r} 客户区原点=({ox},{oy}) 客户区={cw}x{ch}")
        result["window"] = {"rect": r, "client": [cw, ch],
                            "shot": str(H.shot("live/installer_ui", hwnd=win["hwnd"], out_dir=BASE))}

        def to_screen(p):
            return (ox + p[0], oy + p[1])

        def click_ui(p, label, settle=1.6):
            H.focus(win["hwnd"])
            time.sleep(0.3)
            H.click(*to_screen(p))
            time.sleep(settle)
            print(f"[{label}] 点击完成")

        # ① 检查升级
        click_ui(BTN_CHECK, "检查升级", settle=9.0)
        dlgs = [w for w in H.list_windows() if w["visible"] and not w["title"].startswith("GDI+")
                and w["rect"]["Width"] in (560, 253) and w["pid"] != proc.pid]
        result["steps"].append({"step": "检查升级",
                                "dialogs": [{"cls": d["cls"], "rect": d["rect"], "pid": d["pid"]} for d in dlgs]})
        if dlgs:
            result["steps"][-1]["shot"] = str(H.shot("live/installer_checkupdate", hwnd=dlgs[0]["hwnd"], out_dir=BASE))
        dismiss_all(proc.pid)
        proc_util.run(["taskkill", "/IM", "CMO_Updater.exe", "/F"], capture_output=True)
        time.sleep(0.8)

        # ② 不再提醒（关）→ ③ 再点一次恢复
        before = reg_get("AutoCheck")
        click_ui(BTN_NEVER, "不再提醒", settle=2.0)
        dismiss_all(proc.pid)
        after = reg_get("AutoCheck")
        click_ui(BTN_NEVER, "恢复提醒", settle=2.0)
        dismiss_all(proc.pid)
        time.sleep(1.0)
        result["steps"].append({"step": "不再提醒/恢复", "AutoCheck_before": before, "AutoCheck_off": after,
                                "AutoCheck_on": reg_get("AutoCheck")})
        result["steps"][-1]["shot"] = str(H.shot("live/installer_neverremind", hwnd=win["hwnd"], out_dir=BASE))
        print(f"不再提醒: {before} -> {after} -> {reg_get('AutoCheck')}")

        # ④ 开机启动开关
        run_before = run_key()
        click_ui(CHK_START, "开机启动-关", settle=2.0)
        dismiss_all(proc.pid)
        run_off = run_key()
        click_ui(CHK_START, "开机启动-开", settle=2.0)
        dismiss_all(proc.pid)
        run_on = run_key()
        result["steps"].append({"step": "开机启动开关", "before": run_before, "after_off": run_off, "after_on": run_on})
        print(f"开机启动: 关={run_off} 开={run_on}")

        # ⑤ 自动检查频率下拉（用户反馈「更新频率也没有了」→ 现在必须能直接改）
        before_iv = settings_json().get("check_interval_hours")
        click_ui(CB_INTERVAL, "频率下拉", settle=1.0)
        H.keys("{DOWN}")
        H.keys("{ENTER}")
        time.sleep(1.5)
        after_iv = settings_json().get("check_interval_hours")
        result["steps"].append({"step": "检查频率下拉", "before": before_iv, "after": after_iv})
        print(f"检查频率: {before_iv} -> {after_iv}")
        result["steps"][-1]["shot"] = str(H.shot("live/installer_interval", hwnd=win["hwnd"], out_dir=BASE))

        # ⑥ 提醒方式下拉
        before_nm = settings_json().get("notify_mode")
        click_ui(CB_NOTIFY, "提醒方式下拉", settle=1.0)
        H.keys("{DOWN}")
        H.keys("{ENTER}")
        time.sleep(1.5)
        after_nm = settings_json().get("notify_mode")
        result["steps"].append({"step": "提醒方式下拉", "before": before_nm, "after": after_nm})
        print(f"提醒方式: {before_nm} -> {after_nm}")

        # ⑦ 回退按钮（本地无更旧缓存时应为禁用；有缓存时会弹确认框）
        click_ui(BTN_ROLLBACK, "回退按钮", settle=1.5)
        dlg = modal_dialogs(proc.pid)
        result["steps"].append({"step": "回退按钮", "dialogs": len(dlg),
                                "text": "弹出确认框" if dlg else "禁用(无更旧缓存搜索)"})
        print(f"回退按钮: {result['steps'][-1]['text']}")
        dismiss_all(proc.pid)
        time.sleep(0.8)

        result["settings_final"] = settings_text()
        result["final_shot"] = str(H.shot("live/installer_ui_final", hwnd=win["hwnd"], out_dir=BASE))
        return 0
    finally:
        proc_util.run(["taskkill", "/IM", SETUP_NAME, "/F"], capture_output=True)
        restore()
        (ROOT / "build" / "installer_ui_v414.json").write_text(
            json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"saved build/installer_ui_v414.json  频率={settings_json().get('check_interval_hours')}")


if __name__ == "__main__":
    raise SystemExit(main())
