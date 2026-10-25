#!/usr/bin/env python3
"""Open, screenshot, and inspect all 7 core CMO subwindows."""
from __future__ import annotations

import json
import subprocess
import proc_util
proc_util.utf8_stdout()
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAME = Path("G:/soft/steam/steamapps/common/Command - Modern Operations")
DRIVER = ROOT / "build/GuiDriver.exe"
OUT_DIR = ROOT / "evidence/live/dialogs"
OUT_DIR.mkdir(parents=True, exist_ok=True)

SUBWINDOWS = [
    ("数据库查看器", "01_database_viewer", 4.0),
    ("阵营条令、辐射控制与武器投射权限", "02_doctrine_window", 3.0),
    ("任务编辑器", "03_mission_editor", 3.0),
    ("添加/编辑阵营...", "04_edit_sides", 3.0),
    ("作战序列 (OoB)", "05_order_of_battle", 3.0),
    ("事件编辑器", "06_event_editor", 3.0),
    ("推演选项", "07_game_options", 3.0),
]


def send_cmd(cmd_text: str, timeout: float = 8.0) -> dict:
    cmd_file = GAME / "CMOLocalizer_Cmd.txt"
    res_file = GAME / "CMOLocalizer_CmdResult.json"
    if res_file.exists():
        try:
            res_file.unlink()
        except Exception:
            pass
    cmd_file.write_text(cmd_text, encoding="utf-8")
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.1)
        if res_file.exists():
            try:
                content = res_file.read_text(encoding="utf-8")
                if content.strip():
                    return json.loads(content)
            except Exception:
                pass
    return {"status": "timeout", "cmd": cmd_text}


def get_top_dialog_hwnd(pid: int) -> int | None:
    res = proc_util.run([str(DRIVER), "list", str(pid)],
                         capture_output=True, )
    for line in res.stdout.splitlines():
        if "vis=True" in line and "rect=" in line:
            parts = line.split()
            hwnd = int(parts[0])
            cls = line.split("cls=", 1)[1].split()[0] if "cls=" in line else ""
            if "WindowsForms10" in cls:
                # If it is not MainForm (1936x1056)
                if "1936x1056" not in line and "-8,-8" not in line:
                    return hwnd
    return None


def main() -> int:
    pid_res = proc_util.run(["tasklist"], capture_output=True)
    cmo_pid = None
    for line in pid_res.stdout.splitlines():
        if line.lower().startswith("command.exe"):
            cmo_pid = int(line.split()[1])
            break

    if not cmo_pid:
        print("ERROR: Command.exe is not running")
        return 1

    print(f"Testing subwindows on Command.exe (PID {cmo_pid})...")
    for menu_cmd, out_prefix, wait_s in SUBWINDOWS:
        print(f"\n---> Opening [{menu_cmd}]...")
        res = send_cmd(f"CLICK_MENU {menu_cmd}")
        print("     CMD result:", res)
        time.sleep(wait_s)

        # Dump open forms
        forms_info = send_cmd("DUMP_OPEN_FORMS")
        out_json = OUT_DIR / f"{out_prefix}_controls.json"
        out_json.write_text(json.dumps(forms_info, ensure_ascii=False, indent=2),
                            encoding="utf-8")

        # Capture dialog or screen
        diag_hwnd = get_top_dialog_hwnd(cmo_pid)
        out_png = OUT_DIR / f"{out_prefix}.png"
        if diag_hwnd:
            print(f"     Found dialog HWND={diag_hwnd}, capturing...")
            proc_util.run([str(DRIVER), "capture", str(out_png), str(diag_hwnd)],
                           check=True)
        else:
            print("     Capturing full screen...")
            proc_util.run([str(DRIVER), "capture", str(out_png)], check=True)
        print(f"     Saved: {out_png.name}")

        # Close the dialog
        time.sleep(0.5)
        send_cmd("CLOSE_FORM ALL")
        # Also press Escape as fallback
        proc_util.run([str(DRIVER), "key", "{ESC}"], check=True)
        time.sleep(1.0)

    print(f"\nAll 7 subwindows tested and saved to {OUT_DIR}!")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
