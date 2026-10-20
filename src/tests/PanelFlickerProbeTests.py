#!/usr/bin/env python3
"""Synthetic regression inputs only; never send commands to a game process."""
from __future__ import annotations

import contextlib
import copy
import io
import json
import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import verify_regressions as regressions  # noqa: E402


def frame(selected: str = "UnitStatus_WPF") -> dict:
    return {"status": "ok", "windows": [
        {"title": "ElementHost:ElementHost_RightColumnWPF", "nodes": [
            {"d": 0, "type": selected, "name": "", "text": ""},
            {"d": 1, "type": "TextBlock", "name": "Status", "text": "状态：正常"}]}]}


def samples(selected: str = "UnitStatus_WPF") -> list[dict]:
    return [frame(selected) for _ in range(14)]


class Harness:
    def __init__(self, game: Path, responses: list[dict], block_after: bool = False,
                 sim_times: tuple[int | None, int | None] = (100, 101),
                 lua_status: str = "ok", nonce_matches: bool = True) -> None:
        self.GAME = game
        self.responses = iter(responses)
        self.sim_times = iter(sim_times)
        self.frames = 0
        self.block_after = block_after
        self.lua_status = lua_status
        self.nonce_matches = nonce_matches

    def send(self, command: str, timeout: float) -> dict:
        if command.startswith("RUN_LUA "):
            value = next(self.sim_times)
            tag = re.search(r'print\("([^"]+)"', command).group(1)
            if not self.nonce_matches:
                tag = "CMO_TEST_TIME_another_nonce_"
            if value is not None:
                logs = self.GAME / "Logs"
                logs.mkdir(exist_ok=True)
                with (logs / "LuaHistory_test.txt").open("a", encoding="utf-8", newline="\n") as stream:
                    stream.write(f"{tag}{value}\n")
            return {"status": self.lua_status}
        if command != "WPF_DUMP":
            return {"status": "ok"}
        self.frames += 1
        if self.block_after and self.frames == 14:
            (self.GAME / "CMOLocalizer.log").write_text("WRITE LOOP BLOCKED\n", encoding="utf-8")
        return next(self.responses)


class PanelFlickerProbeTests(unittest.TestCase):
    def probe(self, responses: list[dict], **options) -> tuple[dict, dict | None]:
        with tempfile.TemporaryDirectory(prefix="panel-probe-", dir=ROOT / "build") as temporary:
            target = Path(temporary)
            runner = regressions.Runner()
            with patch.object(regressions.time, "sleep"), patch.object(regressions, "SHOTS", target), \
                    contextlib.redirect_stdout(io.StringIO()):
                regressions._panel_flicker_probe(Harness(target, responses, **options), runner, 123)
            evidence = target / "panel_flicker_samples.json"
            saved = json.loads(evidence.read_text(encoding="utf-8")) if evidence.is_file() else None
            return runner.results[-1], saved

    def analyze(self, responses: list[dict], before: int | None = 100,
                after: int | None = 101) -> dict:
        helper = getattr(regressions, "_analyze_panel_samples", None)
        self.assertIsNotNone(helper, "pure panel-sample analyzer is missing")
        self.assertTrue(hasattr(regressions, "_read_simulation_time"), "Lua simulation-time reader is missing")
        return helper(responses, before, after)

    def test_empty_frames_fail(self) -> None:
        result, _ = self.probe([{"status": "ok", "windows": []} for _ in range(14)])
        self.assertEqual(result["status"], "fail")

    def test_no_selected_unit_fails(self) -> None:
        result, _ = self.probe(samples("StackPanel"))
        self.assertEqual(result["status"], "fail")

    def test_stationary_simulation_time_fails(self) -> None:
        result, _ = self.probe(samples(), sim_times=(100, 100))
        self.assertEqual(result["status"], "fail")

    def test_running_unit_and_group_samples_pass(self) -> None:
        for selected in ("UnitStatus_WPF", "GroupStatus_WPF"):
            with self.subTest(selected=selected):
                result = self.analyze(samples(selected))
                self.assertTrue(result["ok"], result)
                self.assertEqual(result["valid_frames"], 14)

    def test_timeout_and_missing_simulation_time_fail(self) -> None:
        responses = samples()
        responses[5] = {"status": "timeout", "cmd": "WPF_DUMP"}
        self.assertFalse(self.analyze(responses)["ok"])
        for before, after in ((None, 101), (100, None), (None, None), (100, 99)):
            with self.subTest(before=before, after=after):
                self.assertFalse(self.analyze(samples(), before, after)["ok"])

    def test_single_player_without_wpf_clock_passes(self) -> None:
        result, saved = self.probe(samples())
        self.assertEqual(result["status"], "pass")
        self.assertEqual(saved["simulation_time_before"], 100)
        self.assertEqual(saved["simulation_time_after"], 101)

    def test_lua_failure_and_wrong_nonce_fail(self) -> None:
        for options in ({"lua_status": "timeout"}, {"nonce_matches": False},
                        {"sim_times": (None, 101)}, {"sim_times": (100, None)}):
            with self.subTest(options=options):
                result, _ = self.probe(samples(), **options)
                self.assertEqual(result["status"], "fail")

    def test_selected_structure_without_text_fails(self) -> None:
        responses = samples()
        for response in responses:
            response["windows"][0]["nodes"][-1]["text"] = ""
            response["windows"][0]["nodes"].append(
                {"d": 0, "type": "TextBlock", "name": "Header", "text": "单位状态"})
        self.assertFalse(self.analyze(responses)["ok"])

    def test_language_flip_fails(self) -> None:
        responses = samples()
        responses[-1]["windows"][0]["nodes"][-1]["text"] = "Status: Normal"
        self.assertFalse(self.analyze(responses)["ok"])

    def test_duplicate_anonymous_text_cannot_hide_a_language_flip(self) -> None:
        for location in ("sibling", "branch", "host"):
            with self.subTest(location=location):
                first = frame()
                nodes = first["windows"][0]["nodes"]
                nodes[1].update(name="", text="正常")
                duplicate = {"d": 1, "type": "TextBlock", "name": "", "text": "Normal"}
                if location == "sibling":
                    nodes.append(duplicate)
                elif location == "branch":
                    nodes.extend([{"d": 1, "type": "StackPanel", "name": "Details", "text": ""},
                                  {**duplicate, "d": 2}])
                else:
                    first["windows"].append({"title": "ElementHost:Details", "nodes": [duplicate]})
                responses = [copy.deepcopy(first) for _ in range(14)]
                responses[-1]["windows"][0]["nodes"][1]["text"] = "Normal"
                result = self.analyze(responses)
                self.assertFalse(result["ok"], result)
                self.assertTrue(result["flips"])

    def test_identical_anonymous_nodes_keep_distinct_identities(self) -> None:
        responses = samples()
        for response in responses:
            nodes = response["windows"][0]["nodes"]
            nodes[1]["name"] = ""
            nodes.append(dict(nodes[1]))
        result = self.analyze(responses)
        self.assertTrue(result["ok"], result)
        self.assertEqual(2, len(result["parsed_frames"][0].get("node_texts", {})))

    def test_too_few_frames_fail(self) -> None:
        self.assertFalse(self.analyze(samples()[:9])["ok"])

    def test_new_write_block_fails(self) -> None:
        result, _ = self.probe(samples(), block_after=True)
        self.assertEqual(result["status"], "fail")

    def test_probe_saves_all_raw_frames(self) -> None:
        responses = samples()
        result, saved = self.probe(responses)
        self.assertEqual(result["status"], "pass")
        self.assertIsNotNone(saved)
        self.assertEqual(saved["samples"], responses)
        self.assertTrue(saved["analysis"]["simulation_advanced"])


if __name__ == "__main__":
    (ROOT / "build").mkdir(exist_ok=True)
    unittest.main(verbosity=2)
