#!/usr/bin/env python3
"""Live in-game driver helpers for CMO localization audits.

Provides: restart the game on a scenario, talk to the in-process command
channel (CMOLocalizer_Cmd.txt -> CMOLocalizer_CmdResult.json), enumerate
windows and capture screenshots through build/GuiDriver.exe.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parent.parent
GAME = Path(os.environ.get("GAME_DIR", "G:/soft/steam/steamapps/common/Command - Modern Operations"))
DRIVER = ROOT / "build/GuiDriver.exe"
CMD_FILE = GAME / "CMOLocalizer_Cmd.txt"
RES_FILE = GAME / "CMOLocalizer_CmdResult.json"

_WIN_RE = re.compile(
    r"^(?P<hwnd>\d+)\s+pid=(?P<pid>\d+)\s+vis=(?P<vis>\w+)\s+"
    r"rect=\[(?P<x>-?\d+),(?P<y>-?\d+),(?P<w>-?\d+)x(?P<h>-?\d+)\]\s+"
    r"cls=(?P<cls>.*?)\s+txt=(?P<txt>.*)$")


def game_pid() -> int | None:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "(Get-Process Command -ErrorAction SilentlyContinue | Select-Object -First 1).Id"],
        capture_output=True, text=True)
    txt = (out.stdout or "").strip()
    return int(txt) if txt.isdigit() else None


def send(cmd: str, timeout: float = 15.0) -> dict:
    """Send one command to the in-process localization engine."""
    try:
        if RES_FILE.exists():
            RES_FILE.unlink()
    except Exception:
        pass
    CMD_FILE.write_text(cmd, encoding="utf-8")
    t0 = time.time()
    while time.time() - t0 < timeout:
        time.sleep(0.1)
        if RES_FILE.exists():
            try:
                txt = RES_FILE.read_text(encoding="utf-8-sig").strip()
                if txt:
                    return json.loads(txt)
            except Exception:
                pass
    return {"status": "timeout", "cmd": cmd}


def wait_ready(timeout: float = 60.0) -> bool:
    t0 = time.time()
    while time.time() - t0 < timeout:
        if send("PING", timeout=3.0).get("status") != "timeout":
            return True
        time.sleep(1.0)
    return False


def restart_game(scen: str | None = None, wait: float = 60.0) -> int:
    """Kill any running Command.exe and relaunch, optionally on a scenario."""
    subprocess.run(["taskkill", "/F", "/IM", "Command.exe"], capture_output=True)
    time.sleep(2.0)
    args = [str(GAME / "Command.exe")]
    if scen:
        args.append(str(GAME / scen))
    subprocess.Popen(args, cwd=str(GAME))
    t0 = time.time()
    pid = None
    while time.time() - t0 < wait:
        time.sleep(1.0)
        pid = game_pid()
        if pid and wait_ready(timeout=4.0):
            time.sleep(2.0)
            return pid
    if pid is None:
        raise RuntimeError("Command.exe did not start")
    return pid


def list_windows(pid: int | None = None) -> list[dict]:
    out = subprocess.run([str(DRIVER), "list", str(pid or 0)],
                         capture_output=True, text=True, encoding="utf-8", errors="replace")
    wins = []
    for line in (out.stdout or "").splitlines():
        m = _WIN_RE.match(line.strip())
        if not m:
            continue
        d = m.groupdict()
        wins.append({
            "hwnd": int(d["hwnd"]),
            "pid": int(d["pid"]),
            "visible": d["vis"].lower() == "true",
            "rect": {"X": int(d["x"]), "Y": int(d["y"]), "Width": int(d["w"]), "Height": int(d["h"])},
            "cls": d["cls"],
            "title": d["txt"],
        })
    return wins


def big_visible_windows(pid: int, min_w: int = 260, min_h: int = 140) -> list[dict]:
    wins = [w for w in list_windows(pid)
            if w["visible"] and w["rect"]["Width"] >= min_w and w["rect"]["Height"] >= min_h
            and w["rect"]["X"] > -30000]
    wins.sort(key=lambda w: w["rect"]["Width"] * w["rect"]["Height"], reverse=True)
    return wins


def shot(name: str, hwnd: int | None = None, out_dir: Path | None = None) -> Path | None:
    out_dir = out_dir or (ROOT / "evidence/live/v413")
    out_dir.mkdir(parents=True, exist_ok=True)
    dest = out_dir / (name if name.endswith(".png") else name + ".png")
    args = [str(DRIVER), "capture", str(dest)] + ([str(hwnd)] if hwnd else [])
    subprocess.run(args, capture_output=True, text=True)
    return dest if dest.exists() else None


def click(x: int, y: int, how: str | None = None) -> None:
    args = [str(DRIVER), "click", str(x), str(y)] + ([how] if how else [])
    subprocess.run(args, capture_output=True, text=True)


def focus(hwnd: int) -> bool:
    """把目标窗口拉到前台（否则后续鼠标点击会落到盖在上面的窗口上）。"""
    result = subprocess.run([str(DRIVER), "focus", str(hwnd)], capture_output=True, text=True)
    return result.returncode == 0 and any(line.strip() == "Focus result: True"
                                         for line in (result.stdout or "").splitlines())


def keys(combo: str) -> None:
    subprocess.run([str(DRIVER), "key", combo], capture_output=True, text=True)


if __name__ == "__main__":
    import sys
    print(json.dumps(send(" ".join(sys.argv[1:])), ensure_ascii=False)[:3000])
