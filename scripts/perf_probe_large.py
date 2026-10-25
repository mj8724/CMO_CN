#!/usr/bin/env python3
"""In-place performance evidence for a running scenario; missing data fails closed.

Use --expect "Blue Dawn" --soak 20 after entering the campaign. The command
channel is a polling transport, so its latency is not an input-response metric.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
import subprocess
import proc_util
proc_util.utf8_stdout()
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import live_harness as H
from verify_regressions import _read_simulation_time


class ProbeError(RuntimeError):
    pass


def _send(command: str, timeout: float = 30) -> dict:
    response = H.send(command, timeout=timeout)
    if not isinstance(response, dict) or response.get("status") != "ok":
        raise ProbeError(f"{command}: unsuccessful response {response!r}")
    return response


def _number(value, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ProbeError(f"invalid {name}: {value!r}")
    return value


def _reset_ui_perf(timeout: float = 60.0) -> None:
    """Reset execution metrics, tolerating the engine's transient "busy" reply.

    The engine refuses a reset while a slice is in flight; that is correct
    behaviour, so poll briefly instead of failing the whole soak on it.
    """
    deadline = time.monotonic() + timeout
    while True:
        response = H.send("UI_PERF_RESET", timeout=20)
        status = response.get("status") if isinstance(response, dict) else None
        if status == "ok":
            return
        if status != "busy" or time.monotonic() >= deadline:
            raise ProbeError(f"UI_PERF_RESET: unsuccessful response {response!r}")
        time.sleep(0.5)


def _counters() -> dict:
    response = _send("TRACE_DUMP")
    if response.get("uiPerfVersion") != 1 or not isinstance(response.get("uiPerf"), dict) or not response["uiPerf"]:
        raise ProbeError("actual UI execution metrics are unavailable; rebuild/deploy the current engine")
    for name in ("renderTicks", "elementHostScans", "sweepCalls", "uiPerfEpoch"):
        _number(response.get(name), name)
    for kind, metric in response["uiPerf"].items():
        if not isinstance(metric, dict):
            raise ProbeError(f"invalid metric {kind}")
        for field in ("posted", "started", "completed", "pending", "peakPending", "active", "totalMs", "maxMs", "maxQueueMs", "over16ms"):
            _number(metric.get(field), f"{kind}.{field}")
        if metric["completed"] > metric["started"] or metric["pending"] > metric["peakPending"]:
            raise ProbeError(f"inconsistent metric {kind}")
    return response


def _rate(secs: float) -> dict:
    before = _counters()
    started = time.monotonic()
    time.sleep(secs)
    after = _counters()
    elapsed = time.monotonic() - started
    if elapsed <= 0 or before["uiPerfEpoch"] != after["uiPerfEpoch"]:
        raise ProbeError("sampling interval is invalid or UI metrics reset during sampling")
    delta = {}
    for name in ("renderTicks", "elementHostScans", "sweepCalls"):
        delta[name] = after[name] - before[name]
        if delta[name] < 0:
            raise ProbeError(f"counter moved backwards: {name}")
    work = 0
    metrics = {}
    for kind, metric in after["uiPerf"].items():
        previous = before["uiPerf"].get(kind)
        completed = metric["completed"] - (previous["completed"] if previous else 0)
        total = metric["totalMs"] - (previous["totalMs"] if previous else 0)
        if completed < 0 or total < 0:
            raise ProbeError(f"execution metric moved backwards: {kind}")
        work += completed
        previous_over = (previous or {}).get("over16ms", 0)
        metrics[kind] = {**metric, "sampleCompleted": completed,
                         "sampleOver16": metric["over16ms"] - previous_over,
                         "sampleAvgMs": total / completed if completed else None}
    if delta["elementHostScans"] <= 0 or work <= 0:
        raise ProbeError("no expected UI execution occurred; zero work is not a performance pass")
    return {"elapsed_secs": elapsed, "render_hz": delta["renderTicks"] / elapsed,
            "scan_hz": delta["elementHostScans"] / elapsed, "ui": metrics,
            "before": before, "after": after}


def _dismiss_message_forms() -> int:
    """Close scenario message dialogs the way a player would.

    Blue Dawn pauses the simulation behind "new message" windows. A soak that
    ignored them would just measure a paused game, so dismissal is recorded as
    part of the evidence instead of being silently skipped.
    """
    forms = H.send("DUMP_OPEN_FORMS", timeout=20).get("forms")
    if not isinstance(forms, list) or not forms:
        raise ProbeError("form sample is empty or missing")
    closed = 0
    for form in forms:
        name = form.get("name") or ""
        if name == "MainForm" or "message" not in name.casefold():
            continue
        if H.send("CLOSE_FORM " + name, timeout=20).get("status") != "ok":
            raise ProbeError("could not close message form " + name)
        closed += 1
        time.sleep(1.0)
    return closed


def _ensure_running(before: int) -> tuple[int, int]:
    """Resume a paused simulation; returns (after, resumes)."""
    H.send("CLICK_TOOLBAR TSB_StartGame", timeout=30)
    time.sleep(4)
    return _read_simulation_time(H), 1


def _identity(expected: str = "") -> dict:
    pid = H.game_pid()
    if not pid:
        raise ProbeError("game is not running")
    forms = _send("DUMP_OPEN_FORMS").get("forms")
    if not isinstance(forms, list):
        raise ProbeError("form sample is missing")
    main = [form for form in forms if form.get("name") == "MainForm"]
    if len(main) != 1 or not main[0].get("text"):
        raise ProbeError("one identifiable MainForm is required")
    title = main[0]["text"]
    if expected and expected.casefold() not in title.casefold():
        raise ProbeError(f"expected {expected!r}, observed {title!r}")
    simulation = _read_simulation_time(H)
    if simulation is None:
        raise ProbeError("simulation UTC time could not be read")
    return {"pid": pid, "title": title, "simulation_utc": simulation}


def _process_metrics(pid: int) -> dict:
    command = f"Get-Process -Id {int(pid)} -ErrorAction Stop | Select-Object Id,WorkingSet64,CPU | ConvertTo-Json -Compress"
    process = proc_util.run(["powershell.exe", "-NoProfile", "-Command", command],
                             capture_output=True, timeout=15, check=True)
    result = json.loads(process.stdout)
    if result.get("Id") != pid or not isinstance(result.get("WorkingSet64"), int) or result["WorkingSet64"] <= 0:
        raise ProbeError("valid process memory sample is required")
    return {"rss_mb": result["WorkingSet64"] / 1048576, "cpu_seconds": result.get("CPU")}


def _inplace_health() -> dict:
    combos = _send("DUMP_COMBOS", 60).get("combos")
    control_nodes = _send("CTRL_TREE", 60).get("nodes")
    windows = _send("WPF_DUMP", 90).get("windows")
    # Combos are often absent during play (no dialog open); require the sample to
    # be readable, and require real control/window text so the check cannot pass
    # on an empty observation.
    if not isinstance(combos, list) or not isinstance(control_nodes, list) or not control_nodes:
        raise ProbeError("control sample is empty or missing")
    if not isinstance(windows, list) or not windows:
        raise ProbeError("WPF window sample is empty or missing")
    mixed = []
    items = 0
    for combo in combos:
        values = combo.get("items")
        if not isinstance(values, list):
            raise ProbeError("combobox item sample is missing")
        items += len(values)
        for value in values:
            if not isinstance(value, str):
                raise ProbeError("combobox item is not text")
            if re.match(r"^[A-Za-z][A-Za-z \-/]{2,}\s*[\u4e00-\u9fff]", value.strip()):
                mixed.append(value)
    if items < 0:
        raise ProbeError("invalid combobox item count")
    texts = {node.get("text", "").strip() for node in control_nodes}
    titles = {}
    nodes = 0
    for window in windows:
        values = window.get("nodes")
        if not isinstance(values, list) or not values:
            raise ProbeError("WPF subtree is empty or missing")
        title = window.get("title", "").strip()
        titles[title] = titles.get(title, 0) + 1
        nodes += len(values)
        texts.update(node.get("text", "").strip() for node in values)
    exact = json.loads((H.ROOT / "dict/CMOLocalizer.json").read_text(encoding="utf-8"))["exact"]
    def scan_residual(values):
        return sorted(text for text in values if text and not re.search(r"[一-鿿]", text)
                      and text in exact and exact[text] != text)

    residual = scan_residual(texts)
    rechecked = 0
    if residual:
        # Panels refresh about every 200ms; a node sampled mid-update is English
        # for a fraction of a second. Re-sample once before calling it a gap.
        rechecked = 1
        time.sleep(1.2)
        retry = set()
        for node in H.send("CTRL_TREE", 60).get("nodes") or []:
            value = (node.get("text") or "").strip()
            if value:
                retry.add(value)
        for window in H.send("WPF_DUMP", 90).get("windows") or []:
            for node in window.get("nodes") or []:
                value = (node.get("text") or "").strip()
                if value:
                    retry.add(value)
        residual = scan_residual(retry)
    log = (H.GAME / "CMOLocalizer.log").read_text(encoding="utf-8-sig", errors="replace")
    start = log.rfind("=== CMOLocalizer")
    if start < 0:
        raise ProbeError("current engine startup marker is missing")
    session = log[start:]
    zombies = {title: count for title, count in titles.items()
               if count > 1 and title and not title.startswith("ElementHost:")}
    # A moving map is not a flicker failure. Text stability is checked on actual
    # UI state; screen captures remain visual evidence, not an all-pixel gate.
    if not texts or not any(texts):
        raise ProbeError("no on-screen text was sampled")
    return {"combo_items": items, "combo_mixed": sorted(set(mixed)), "residual_en": residual,
            "residual_recheck": rechecked,
            "sampled_texts": len(texts), "windows": len(windows), "nodes": nodes,
            "zombies": zombies, "write_loop": session.count("WRITE LOOP BLOCKED"),
            "sweep_errors": sum(session.count(marker) for marker in
                                ("WPF sweep item error", "TranslateWpfWindows outer error", "Queued UI node translation failed")),
            "raw_combos": combos, "raw_controls": control_nodes, "raw_wpf": windows}


# Accepted by the user (2026-10-23): an isolated single write that makes the
# game's own WPF panel re-layout once (~20-25ms) is not perceived as lag. What
# still fails is *repetition* (a stall) or a clear multi-frame outlier.
OUTLIER_MS = 50.0
REPEATS_PER_SAMPLE = 3
STARVATION_MS = 3000.0


def _problems(row: dict) -> list[str]:
    issues = []
    if row["rate"]["scan_hz"] > 15:
        issues.append(f"repaint frequency {row['rate']['scan_hz']:.1f}/s exceeds 15/s")
    elapsed = max(1.0, float(row.get("elapsed_secs") or 1.0))
    for kind, metric in row["rate"]["ui"].items():
        if metric["maxMs"] > OUTLIER_MS:
            issues.append(f"{kind} peak {metric['maxMs']:.1f}ms exceeds the {OUTLIER_MS:.0f}ms outlier limit")
        if metric.get("sampleOver16", 0) > REPEATS_PER_SAMPLE:
            issues.append(f"{kind} repeated over-frame work ({metric['sampleOver16']} events in this sample)")
        if metric["completed"] and metric["over16ms"] / elapsed > 0.1:
            issues.append(f"{kind} sustained over-frame rate ({metric['over16ms']} events in {elapsed:.0f}s)")
        if metric["maxQueueMs"] > STARVATION_MS:
            issues.append(f"{kind} queued {metric['maxQueueMs']:.0f}ms before running (starvation)")
    health = row["health"]
    for field in ("combo_mixed", "residual_en", "zombies", "write_loop", "sweep_errors"):
        if health[field]:
            issues.append(f"{field}: {health[field]!r}")
    if health["nodes"] > 3000:
        issues.append(f"WPF tree has {health['nodes']} nodes (limit 3000)")
    return issues


def _sample(expected: str, secs: float, original: dict) -> dict:
    messages = _dismiss_message_forms()
    resumes = 0
    before = _identity(expected)
    if before["pid"] != original["pid"] or before["title"] != original["title"]:
        raise ProbeError("game process or scenario changed")
    rate = _rate(secs)
    after = _identity(expected)
    if after["pid"] != before["pid"] or after["title"] != before["title"]:
        raise ProbeError("game process or scenario changed while sampling")
    if after["simulation_utc"] <= before["simulation_utc"]:
        resumed, resumes = _ensure_running(before["simulation_utc"])
        if resumed is None or resumed <= before["simulation_utc"]:
            raise ProbeError("simulation did not advance even after resuming; the game is paused or blocked")
        after = _identity(expected)
        after["simulation_utc"] = resumed
    return {"utc": datetime.now(timezone.utc).isoformat(), "identity_before": before,
            "identity_after": after, "rate": rate, "health": _inplace_health(),
            "process": _process_metrics(after["pid"]),
            "messages_closed": messages, "resumes": resumes}


def _load(target: str) -> None:
    if "blue dawn" in target.casefold():
        import live_load_blue_dawn
        if live_load_blue_dawn.main() != 0:
            raise ProbeError("Blue Dawn campaign entry failed")
        return
    raise ProbeError("automatic loading is limited to the Blue Dawn campaign; enter other scenarios first")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expect", default="Blue Dawn")
    parser.add_argument("--load", default="")
    parser.add_argument("--secs", type=float, default=10.0)
    parser.add_argument("--soak", type=float, default=0.0, help="minimum real running minutes")
    parser.add_argument("--report", type=Path, help="explicit destination for original evidence")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    if not math.isfinite(args.secs) or args.secs <= 0 or not math.isfinite(args.soak) or args.soak < 0:
        parser.error("--secs must be positive and --soak nonnegative")
    evidence = {"schema": 2, "expected": args.expect, "required_minutes": args.soak,
                "rows": [], "passed": False, "errors": []}
    started = time.monotonic()
    try:
        if args.load:
            _load(args.load)
        original = _identity(args.expect)
        evidence["identity"] = original
        evidence["dictionary_sha256"] = hashlib.sha256((H.ROOT / "dict/CMOLocalizer.json").read_bytes()).hexdigest()
        evidence["deployed_engine_sha256"] = hashlib.sha256((H.GAME / "CMOLocalizer.dll").read_bytes()).hexdigest()
        evidence["game_build"] = (H.ROOT / "GAME_BUILD").read_text(encoding="utf-8").strip()
        if evidence["game_build"] not in original["title"]:
            raise ProbeError("game title does not match the declared game build")
        _reset_ui_perf()
        started = time.monotonic()
        target_seconds = args.soak * 60
        while True:
            row = _sample(args.expect, args.secs, original)
            row["elapsed_secs"] = time.monotonic() - started
            evidence["rows"].append(row)
            issues = _problems(row)
            print(f"t={row['elapsed_secs']:.1f}s scan={row['rate']['scan_hz']:.2f}/s "
                  f"sim={row['identity_before']['simulation_utc']}->{row['identity_after']['simulation_utc']} "
                  f"nodes={row['health']['nodes']} RSS={row['process']['rss_mb']:.1f}MB", flush=True)
            if issues:
                raise ProbeError("; ".join(issues))
            first = evidence["rows"][0]
            if row["health"]["nodes"] > first["health"]["nodes"] * 1.5 + 200:
                raise ProbeError("visual tree grew persistently during the soak")
            if row["process"]["rss_mb"] > first["process"]["rss_mb"] * 1.3 + 200:
                raise ProbeError("resident memory grew persistently during the soak")
            if target_seconds == 0 or time.monotonic() - started >= target_seconds:
                break
            time.sleep(min(45, max(0, target_seconds - (time.monotonic() - started))))
        evidence["passed"] = True
    except (ProbeError, OSError, ValueError, subprocess.SubprocessError) as error:
        evidence["errors"].append(str(error))
    finally:
        evidence["elapsed_secs"] = time.monotonic() - started
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(evidence, ensure_ascii=False, indent=1) + "\n", encoding="utf-8", newline="\n")
    if args.json:
        print(json.dumps(evidence, ensure_ascii=False, indent=1))
    if not evidence["passed"]:
        print("FAIL: " + "; ".join(evidence["errors"]), file=sys.stderr)
        return 1
    print("PASS: actual UI execution and advancing target scenario samples passed; gameplay operations need separate evidence")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
