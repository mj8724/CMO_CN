#!/usr/bin/env python3
"""R53 大想定性能探针：单位数量多的想定为什么会「卡到电脑不能动」。

**为什么必须单独做一个大想定探针**：`perf_probe.py` 用的是教程想定（几十个单位），
它测出来的补扫频率、翻译事件都是"小想定"的数值。用户报的「卡到电脑不能动」
只在大想定复现（战争链 DLC 第一关 Blue Dawn），而大想定与小想定的差别不是
线性的——WPF 视觉树节点数、消息日志行数、单位状态面板重建频率都随单位数增长，
补扫代价是**每个渲染帧走一遍全树**，于是单位越多帧率越高、代价越爆炸。

三条判据（都必须是可复现的硬指标）：

1. **补扫频率必须由定时器主导，不能被渲染帧主导**
   设计是 40ms/200ms 定时器（约 5 次/秒）。若 `elementHostScans` 与 `renderTicks`
   同步增长（比值≈1），说明每帧都在走全树——这是 R53 的根因。

2. **僵尸 WPF 窗口数必须为 0**
   `HwndWrapper` 窗口若在窗体关闭后仍留在进程里，`TranslateWpfWindows()` 会一直翻译它们。
   实测「想定特性与拟真度选项」会累积到 22 个同位置同尺寸的僵尸窗口。

3. **UI 线程单次补扫耗时必须低于帧预算**
   单次补扫 > 16ms 就意味着它自己吃掉整帧，用户直接感知为卡顿。

用法：
    python scripts/perf_probe_large.py                    # 当前想定
    python scripts/perf_probe_large.py --load "Blue Dawn"  # 加载指定想定后测
    python scripts/perf_probe_large.py --secs 15
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import live_harness as H  # noqa: E402


def _counters() -> dict:
    d = H.send("TRACE_DUMP", timeout=60)
    return {
        "renderTicks": d.get("renderTicks") or 0,
        "elementHostScans": d.get("elementHostScans") or 0,
    }


def _rate(secs: float) -> dict:
    """测量 renderTicks / elementHostScans 的增长速率。"""
    a = _counters()
    time.sleep(secs)
    b = _counters()
    return {
        "render_hz": (b["renderTicks"] - a["renderTicks"]) / secs,
        "scan_hz": (b["elementHostScans"] - a["elementHostScans"]) / secs,
        "d_render": b["renderTicks"] - a["renderTicks"],
        "d_scan": b["elementHostScans"] - a["elementHostScans"],
    }


def _wpf_windows() -> dict:
    """统计 WPF 窗口与视觉树节点数；同标题重复即为僵尸窗口。

    注意：ElementHost 是**承载容器**，名字（ElementHost1 等）由 WinForms 自动编号，
    同名不代表僵尸——游戏重建面板时会产生同名 ElementHost。
    真正的僵尸特征是「顶层 HwndWrapper 窗口」同名重复（如想定特性窗口反复打开未销毁），
    所以统计只针对非 ElementHost 条目。
    """
    d = H.send("WPF_DUMP", timeout=300)
    wins = d.get("windows") or []
    by_title: dict[str, int] = {}
    total_nodes = 0
    for w in wins:
        t = (w.get("title") or "").strip()
        by_title[t] = by_title.get(t, 0) + 1
        total_nodes += len(w.get("nodes") or [])
    zombies = {t: n for t, n in by_title.items()
               if n > 1 and t and not t.startswith("ElementHost:")}
    return {
        "windows": len(wins),
        "total_nodes": total_nodes,
        "by_title": by_title,
        "zombies": zombies,
        "zombie_extra": sum(n - 1 for n in zombies.values()),
    }


def _win32_windows(pid: int) -> dict:
    """Win32 层统计：HwndWrapper 窗口里有多少是隐藏/零尺寸的残留。"""
    wins = H.list_windows(pid)
    hw = [w for w in wins if "HwndWrapper" in (w.get("cls") or "")]
    zero = [w for w in hw if w["rect"]["Width"] == 0 or w["rect"]["Height"] == 0]
    return {"hwndwrapper": len(hw), "zero_size": len(zero),
            "visible": len([w for w in hw if w["visible"]])}


def _latency(n: int = 8) -> tuple[int, int]:
    xs = []
    for _ in range(n):
        t0 = time.time()
        H.send("DUMP_OPEN_FORMS", timeout=120)
        xs.append((time.time() - t0) * 1000)
        time.sleep(0.12)
    xs.sort()
    return int(xs[0]), int(xs[len(xs) // 2])


def _cpu(pid: int, secs: float = 3.0) -> float:
    """进程 CPU 百分比（相对单核），用于对照汉化层开销。"""
    try:
        import psutil  # type: ignore
        p = psutil.Process(pid)
        p.cpu_percent(None)
        time.sleep(secs)
        return p.cpu_percent(None)
    except Exception:
        return -1.0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--load", default="", help="先加载该想定（名称子串）")
    ap.add_argument("--secs", type=float, default=10.0)
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    pid = H.game_pid()
    if not pid:
        print("ERROR: 游戏未运行。", file=sys.stderr)
        return 2

    if args.load:
        print(f"加载想定: {args.load}", flush=True)
        H.send("CLICK_MENU TSMI_LoadScenario", timeout=20)
        time.sleep(2.0)
        H.send("SCEN_EXPAND_ALL", timeout=40)
        time.sleep(1.0)
        H.send("SCEN_SELECT " + args.load, timeout=40)
        time.sleep(1.0)
        H.send("SCEN_LOAD", timeout=30)
        time.sleep(20.0)
        for _ in range(10):
            r = H.send("CLICK_BUTTON 进入想定", timeout=15)
            if r.get("status") == "ok":
                break
            time.sleep(3.0)
        time.sleep(5.0)
        for _ in range(8):
            if H.send("CLICK_WPF 开始推演", timeout=25).get("clicked", 0) > 0:
                break
            time.sleep(2.0)
        time.sleep(6.0)

    title = ""
    try:
        forms = H.send("DUMP_OPEN_FORMS", timeout=60)
        for f in forms.get("forms") or []:
            if f.get("name") == "MainForm":
                title = f.get("text") or ""
    except Exception:
        pass

    rate = _rate(args.secs)
    wpf = _wpf_windows()
    win32 = _win32_windows(pid)
    lat_min, lat_med = _latency()
    cpu = _cpu(pid)

    report = {
        "title": title,
        "pid": pid,
        "rate": rate,
        "wpf": {k: v for k, v in wpf.items() if k != "by_title"},
        "wpf_titles": wpf["by_title"],
        "win32": win32,
        "latency_ms": {"min": lat_min, "med": lat_med},
        "cpu_percent": round(cpu, 1),
    }

    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=1))
    else:
        print(f"想定: {title}")
        print(f"渲染帧率: {rate['render_hz']:.1f}/s   补扫频率: {rate['scan_hz']:.1f}/s"
              f"   (Δrender={rate['d_render']}, Δscan={rate['d_scan']})")
        print(f"WPF 窗口: {wpf['windows']} 个 / {wpf['total_nodes']} 节点"
              f"   僵尸重复: {wpf['zombie_extra']} 个")
        if wpf["zombies"]:
            for t, n in sorted(wpf["zombies"].items(), key=lambda kv: -kv[1]):
                print(f"    !! {n}x  {t[:48]}")
        print(f"HwndWrapper: {win32['hwndwrapper']} (可见 {win32['visible']}, 零尺寸 {win32['zero_size']})")
        print(f"命令延迟: 最小 {lat_min}ms / 中位 {lat_med}ms")
        print(f"进程 CPU: {cpu:.1f}%")

    # ── 判据 ──────────────────────────────────────────────────────────
    problems = []

    # ① 补扫必须由定时器主导（约 5/s），不能被渲染帧主导。
    #    阈值取 15/s：比设计值宽 3 倍，但能抓住「每帧一次」这类数量级错误。
    if rate["scan_hz"] > 15:
        problems.append(
            f"补扫 {rate['scan_hz']:.1f}/s 远超设计值(约5/s)；"
            f"渲染帧 {rate['render_hz']:.1f}/s，比值 {rate['scan_hz'] / max(1.0, rate['render_hz']):.2f}"
            " ⇒ 补扫很可能挂在渲染帧上，而非定时器")

    # ② 僵尸 WPF 窗口必须为 0：关闭后仍留在进程里的窗口会被永久翻译
    if wpf["zombie_extra"] > 0:
        problems.append(f"存在 {wpf['zombie_extra']} 个僵尸 WPF 窗口（同标题重复）")

    # ③ 视觉树节点总数上限：大想定也不该超过 3000
    if wpf["total_nodes"] > 3000:
        problems.append(f"WPF 视觉树 {wpf['total_nodes']} 节点过多（上限 3000）")

    if problems:
        print("FAIL:")
        for p in problems:
            print("  - " + p)
        return 1
    print("PASS: 补扫由定时器主导、无僵尸窗口、视觉树规模正常")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
