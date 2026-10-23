#!/usr/bin/env python3
"""Negative controls for the in-place performance probe; never operate a game."""
from __future__ import annotations

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import perf_probe_large as probe
import verify_regressions as regressions


def counters() -> dict:
    metric = {"posted": 1, "started": 1, "completed": 1, "pending": 0, "peakPending": 1,
              "active": 0, "totalMs": 1.0, "maxMs": 1.0, "maxQueueMs": 1.0, "over16ms": 0}
    return {"status": "ok", "renderTicks": 60, "elementHostScans": 5, "sweepCalls": 5,
            "uiPerfVersion": 1, "uiPerfEpoch": 1, "uiPerf": {"repaint": metric}}


def healthy_row() -> dict:
    metric = counters()["uiPerf"]["repaint"]
    return {"rate": {"scan_hz": 5, "ui": {"repaint": metric}},
            "health": {"combo_mixed": [], "residual_en": [], "zombies": {},
                       "write_loop": 0, "sweep_errors": 0, "nodes": 100}}


class PerformanceProbeTests(unittest.TestCase):
    def test_timeout_and_missing_execution_metrics_fail(self):
        for response in ({"status": "timeout"}, {"status": "ok"},
                         {**counters(), "uiPerf": {}}, {**counters(), "uiPerfVersion": 0}):
            with self.subTest(response=response), patch.object(probe.H, "send", return_value=response):
                with self.assertRaises(probe.ProbeError):
                    probe._counters()

    def test_invalid_numeric_metric_fails(self):
        for invalid in (-1, True, float("nan"), None):
            response = counters()
            response["uiPerf"]["repaint"]["completed"] = invalid
            with self.subTest(value=invalid), patch.object(probe.H, "send", return_value=response):
                with self.assertRaises(probe.ProbeError):
                    probe._counters()

    def test_zero_work_cannot_pass(self):
        with patch.object(probe, "_counters", side_effect=[counters(), counters()]), \
             patch.object(probe.time, "sleep"), patch.object(probe.time, "monotonic", side_effect=[0, 1]):
            with self.assertRaises(probe.ProbeError):
                probe._rate(1)

    def test_actual_completed_work_is_measured(self):
        before, after = counters(), counters()
        after["elementHostScans"] += 5
        after["renderTicks"] += 60
        after["sweepCalls"] += 5
        after["uiPerf"]["repaint"]["completed"] += 5
        after["uiPerf"]["repaint"]["totalMs"] += 10
        with patch.object(probe, "_counters", side_effect=[before, after]), \
             patch.object(probe.time, "sleep"), patch.object(probe.time, "monotonic", side_effect=[0, 2]):
            result = probe._rate(1)
        self.assertEqual(2.5, result["scan_hz"])
        self.assertEqual(2, result["ui"]["repaint"]["sampleAvgMs"])

    def test_metric_reset_during_sample_fails(self):
        after = counters()
        after["uiPerfEpoch"] += 1
        with patch.object(probe, "_counters", side_effect=[counters(), after]), \
             patch.object(probe.time, "sleep"), patch.object(probe.time, "monotonic", side_effect=[0, 1]):
            with self.assertRaises(probe.ProbeError):
                probe._rate(1)

    def test_all_health_failures_apply_to_soak(self):
        self.assertEqual([], probe._problems(healthy_row()))
        for field, value in (("combo_mixed", ["Inherited 中文"]), ("residual_en", ["No unit selected"]),
                             ("zombies", {"old window": 2}), ("write_loop", 1), ("sweep_errors", 100)):
            row = healthy_row()
            row["health"][field] = value
            with self.subTest(field=field):
                self.assertTrue(probe._problems(row))

    def test_isolated_single_write_is_accepted(self):
        # The user accepted one-off panel re-layout writes (measured 24.6ms).
        row = healthy_row()
        row["rate"]["ui"]["repaint"]["maxMs"] = 24.6
        row["rate"]["ui"]["repaint"]["over16ms"] = 1
        row["rate"]["ui"]["repaint"]["sampleOver16"] = 1
        row["elapsed_secs"] = 45
        self.assertEqual([], probe._problems(row))

    def test_repeated_over_frame_work_fails(self):
        row = healthy_row()
        row["rate"]["ui"]["repaint"]["sampleOver16"] = 5
        self.assertTrue(probe._problems(row))

    def test_multi_frame_outlier_fails(self):
        row = healthy_row()
        row["rate"]["ui"]["repaint"]["maxMs"] = 60
        self.assertTrue(probe._problems(row))

    def test_queue_starvation_fails(self):
        row = healthy_row()
        row["rate"]["ui"]["repaint"]["maxQueueMs"] = 5000
        self.assertTrue(probe._problems(row))

    def test_sustained_over_frame_rate_fails(self):
        row = healthy_row()
        row["rate"]["ui"]["repaint"]["over16ms"] = 30
        row["elapsed_secs"] = 60
        self.assertTrue(probe._problems(row))

    def test_paused_simulation_fails(self):
        identity = {"pid": 123, "title": "Blue Dawn", "simulation_utc": 10}
        # A paused game may be resumed once, but it still must not pass while
        # the scenario clock does not advance.
        with patch.object(probe, "_identity", return_value=identity),              patch.object(probe, "_dismiss_message_forms", return_value=0),              patch.object(probe, "_ensure_running", return_value=(10, 1)),              patch.object(probe, "_rate", return_value={}):
            with self.assertRaises(probe.ProbeError):
                probe._sample("Blue Dawn", 1, identity)

    def test_message_dialog_is_closed_and_recorded(self):
        identity = {"pid": 123, "title": "Blue Dawn", "simulation_utc": 10}
        advanced = {"pid": 123, "title": "Blue Dawn", "simulation_utc": 40}
        with patch.object(probe, "_identity", side_effect=[identity, advanced]),              patch.object(probe, "_dismiss_message_forms", return_value=2),              patch.object(probe, "_rate", return_value={"scan_hz": 5, "ui": {}}),              patch.object(probe, "_inplace_health", return_value={}),              patch.object(probe, "_process_metrics", return_value={}):
            row = probe._sample("Blue Dawn", 1, identity)
        self.assertEqual(2, row["messages_closed"])
        self.assertEqual(40, row["identity_after"]["simulation_utc"])


    def test_changed_scenario_fails(self):
        original = {"pid": 123, "title": "Blue Dawn", "simulation_utc": 10}
        with patch.object(probe, "_identity", return_value={**original, "title": "Shamal"}):
            with self.assertRaises(probe.ProbeError):
                probe._sample("Blue Dawn", 1, original)

    def test_chinese_combo_is_not_a_name_error(self):
        responses = [
            {"status": "ok", "combos": [{"items": ["就绪"]}]},
            {"status": "ok", "nodes": [{"text": "状态"}]},
            {"status": "ok", "windows": [{"title": "ElementHost:status", "nodes": [{"text": "正常"}]}]},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            (path / "dict").mkdir()
            (path / "dict/CMOLocalizer.json").write_text('{"exact":{}}', encoding="utf-8")
            (path / "CMOLocalizer.log").write_text("=== CMOLocalizer test ===", encoding="utf-8")
            with patch.object(probe.H, "ROOT", path), patch.object(probe.H, "GAME", path), \
                 patch.object(probe.H, "send", side_effect=responses):
                result = probe._inplace_health()
        self.assertEqual([], result["combo_mixed"])
        self.assertEqual(1, result["combo_items"])

    def test_empty_health_sample_fails(self):
        with patch.object(probe.H, "send", return_value={"status": "ok", "combos": [], "nodes": [], "windows": []}):
            with self.assertRaises(probe.ProbeError):
                probe._inplace_health()

    def test_load_is_processed_before_soak(self):
        events = []
        with patch.object(probe, "_load", side_effect=lambda target: events.append("load")), \
             patch.object(probe, "_identity", side_effect=probe.ProbeError("fixture stops after load")), \
             contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            code = probe.main(["--load", "Blue Dawn", "--soak", "20"])
        self.assertEqual(["load"], events)
        self.assertEqual(1, code)


class RegressionReportTests(unittest.TestCase):
    def run_modes(self, directory: Path, arguments: list[str], *, failing: bool = False, inputs=None):
        identity = inputs or {"source_sha256": "a" * 64, "dictionary_sha256": "b" * 64,
                              "engine_sha256": "c" * 64, "setup_sha256": "d" * 64}
        def run(runner):
            if failing:
                raise RuntimeError("injected failure")
            runner.record("fixture-" + arguments[0], "fixture only", True, "ok")
        with patch.object(regressions, "ROOT", directory), patch.object(regressions, "REPORT", directory / "build/regression_report.json"), \
             patch.object(regressions, "release_inputs", return_value=identity), patch.object(regressions, "run_static", side_effect=run), \
             patch.object(regressions, "run_live", side_effect=run), patch.object(regressions, "run_installer", side_effect=run), \
             contextlib.redirect_stdout(io.StringIO()):
            code = regressions.main(arguments)
        return code, json.loads((directory / "build/regression_report.json").read_text(encoding="utf-8"))

    def test_exception_replaces_old_green(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory / "VERSION").write_text("4.2.2", encoding="utf-8")
            self.run_modes(directory, ["--live"])
            code, report = self.run_modes(directory, ["--live"], failing=True)
        self.assertEqual(1, code)
        self.assertFalse(report["passed"])
        self.assertEqual("live-exception", report["results"][0]["id"])

    def test_same_inputs_preserve_separate_mode_evidence(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory / "VERSION").write_text("4.2.2", encoding="utf-8")
            self.run_modes(directory, ["--static", "--installer"])
            code, report = self.run_modes(directory, ["--live"])
        self.assertEqual(0, code)
        self.assertEqual({"static", "installer", "live"}, set(report["mode_reports"]))
        self.assertTrue(report["passed"])

    def test_changed_inputs_discard_previous_modes(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory / "VERSION").write_text("4.2.2", encoding="utf-8")
            self.run_modes(directory, ["--static", "--installer"])
            _, report = self.run_modes(directory, ["--live"], inputs={"source_sha256": "new"})
        self.assertEqual(["live"], report["modes"])

    def test_installer_and_live_cannot_share_run(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                regressions.main(["--installer", "--live"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
