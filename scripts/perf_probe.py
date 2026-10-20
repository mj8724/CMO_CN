#!/usr/bin/env python3
"""R47 性能探针：任务编辑器开关是否明显拖慢界面。

**为什么单独做成脚本而不是并入门禁**：卡顿是"手感"问题，判据不稳定，
硬塞进门禁会频繁假红。这里做成可反复运行的观测工具，用趋势判断退化。

三条判据（都很关键，缺一条就会误判）：

1. **响应延迟用"最小值"而不是中位数**
   引擎命令走 1500ms 轮询，中位数几乎恒定在 1415ms，完全看不出 UI 忙不忙。
   最小值消除了轮询相位差，才反映真实处理耗时。

2. **必须和"编辑器关闭"状态对照**
   这台机器上 Command 进程本身就是 110-160% CPU（纯英文模式 112.7% 也一样），
   绝对数值没有意义，只有 A/B 比值有意义。

3. **必须确认翻译通道是否在忙**
   如果引擎 8 秒内没有任何 SET/WRITE 事件，说明卡顿不是汉化层造成的——
   这条排除项比"CPU 数字"更有说服力。

用法：
    python scripts/perf_probe.py                  # 完整流程（需要游戏已在想定里）
    python scripts/perf_probe.py --open-editor     # 先打开任务编辑器再测
"""

from __future__ import annotations

import argparse
import re
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import live_harness as H  # noqa: E402


def _min_latency(n: int = 8) -> tuple[int, int]:
    """返回 (最小, 中位) 毫秒。最小值才反映真实 UI 处理耗时。"""
    xs = []
    for _ in range(n):
        t0 = time.time()
        H.send("DUMP_OPEN_FORMS", timeout=120)
        xs.append((time.time() - t0) * 1000)
        time.sleep(0.15)
    xs.sort()
    return int(xs[0]), int(xs[len(xs) // 2])


def _scan_rate(secs: int = 8) -> float:
    d1 = H.send("TRACE_DUMP", timeout=60)
    s1 = d1.get("elementHostScans") or 0
    time.sleep(secs)
    d2 = H.send("TRACE_DUMP", timeout=60)
    s2 = d2.get("elementHostScans") or 0
    return (s2 - s1) / max(1, secs)


def _translate_events(secs: int = 8) -> dict:
    """统计翻译通道事件：全 0 说明汉化层不忙，卡顿与汉化无关。"""
    H.send("TRACE_START", timeout=20)
    time.sleep(secs)
    H.send("TRACE_STOP", timeout=20)
    d = H.send("TRACE_DUMP", timeout=60)
    cnt: dict[str, int] = {}
    for ln in d.get("lines") or []:
        m = re.match(r"^\S+ (\w+) ", ln or "")
        if m:
            cnt[m.group(1)] = cnt.get(m.group(1), 0) + 1
    return cnt


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--open-editor", action="store_true", help="先打开任务编辑器再测")
    ap.add_argument("--secs", type=int, default=8)
    args = ap.parse_args()

    pid = H.game_pid()
    if not pid:
        print("ERROR: 游戏未运行。先进入一个想定再跑本探针。", file=sys.stderr)
        return 2
    print(f"游戏进程: {pid}")

    if args.open_editor:
        H.send("CLICK_BUTTON TSB_MissionEditor", timeout=180)
        time.sleep(4)

    closed_min, closed_med = _min_latency()
    print(f"编辑器关闭: 最小 {closed_min}ms / 中位 {closed_med}ms")

    H.send("CLICK_BUTTON TSB_MissionEditor", timeout=180)
    time.sleep(4)
    open_min, open_med = _min_latency()
    print(f"编辑器打开: 最小 {open_min}ms / 中位 {open_med}ms")

    ratio = open_min / max(1, closed_min)
    print(f"→ 真实响应差异: {ratio:.2f}x")

    rate_open = _scan_rate(args.secs)
    print(f"补扫频率(编辑器打开): {rate_open:.1f} 次/秒")
    events = _translate_events(args.secs)
    print(f"翻译通道事件({args.secs}s): {events or '无'}")

    # 判据
    #
    # 只用两条**可复现**的硬指标：
    #   ① 补扫频率必须接近设计值（约 5 次/秒 = 200ms 退避周期）。这条是确定性的：
    #      它直接反映"我们往 UI 线程丢了多少工作"，超了就是我们的责任。
    #   ② 翻译通道在静止界面下应当没有事件。有事件说明汉化层在不停改写控件
    #      （R43/R45/R46 那类问题），那才是真卡顿来源。
    #
    # 刻意**不用**"响应延迟比值"做判据：实测同一台机器同一状态下在 0.45x ~ 2.98x 之间
    # 剧烈波动（引擎命令走 1500ms 轮询，相位差主导测量），拿它当门禁会频繁假红。
    # 它只作为**观测值**打印出来，供人工判断趋势。
    problems = []
    if rate_open > 12:
        problems.append(f"补扫频率 {rate_open:.1f}/s 超出设计上限（应约 5/s）")
    if events:
        problems.append(f"静止界面仍有翻译事件 {events}（汉化层在反复改写）")

    if problems:
        print("FAIL: " + "；".join(problems))
        return 1
    print("PASS: 补扫频率正常、静止界面无翻译事件 ⇒ 未发现汉化层引起的卡顿")


if __name__ == "__main__":
    raise SystemExit(main())
