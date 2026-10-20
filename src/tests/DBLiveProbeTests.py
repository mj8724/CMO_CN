"""Synthetic DB readiness checks; no game process or command channel is used."""
from __future__ import annotations

import html
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import verify_regressions as gate
import live_harness as live


class DBLiveProbeTests(unittest.TestCase):
    def probe(self, kind: str = "Ship") -> dict:
        return {"type": kind, "id": 3, "name": kind + " fixture", "database": "DB3K_519"}

    def page(self, name: str, kind: str = "Ship") -> str:
        minimum = gate.MIN_HTML_LEN.get(kind, gate.DEFAULT_MIN_HTML_LEN)
        return '<h4 class="section">' + html.escape(name) + "</h4><p>" + "x" * minimum + "</p>"

    def inspection(self, probe: dict, page: str) -> dict:
        return {"status": "ok", "sot": probe["type"], "id": probe["id"],
                "name": probe["name"], "html_len": len(page)}

    def test_stale_long_page_is_not_ready(self) -> None:
        probe = self.probe("Submarine")
        page = self.page("old Ship fixture")
        old = {"status": "ok", "sot": "Ship", "id": 2625, "name": "old Ship fixture", "html_len": 45000}
        self.assertGreater(old["html_len"], gate.DEFAULT_MIN_HTML_LEN)  # The old length-only gate passed.
        self.assertTrue(gate._db_probe_errors(old, page, probe, {}))

    def test_wrong_type_id_and_name_are_rejected_independently(self) -> None:
        probe = self.probe()
        page = self.page(probe["name"])
        for field, wrong in (("sot", "Submarine"), ("id", 999), ("name", "old name")):
            with self.subTest(field=field):
                inspection = self.inspection(probe, page)
                inspection[field] = wrong
                self.assertTrue(gate._db_probe_errors(inspection, page, probe, {}))

    def test_empty_optional_name_uses_the_verified_current_title(self) -> None:
        probe = self.probe()
        page = self.page(probe["name"])
        inspection = self.inspection(probe, page)
        inspection["name"] = ""
        self.assertEqual([], gate._db_probe_errors(inspection, page, probe, {}))

    def test_wrong_nonempty_metadata_name_is_rejected_with_a_correct_title(self) -> None:
        probe = self.probe()
        page = self.page(probe["name"])
        inspection = self.inspection(probe, page)
        inspection["name"] = "Different record"
        self.assertTrue(gate._db_probe_errors(inspection, page, probe, {}))

    def test_old_html_is_rejected_even_when_selection_metadata_is_new(self) -> None:
        probe = self.probe("Submarine")
        old = self.page("Ship fixture", "Submarine")
        inspection = self.inspection(probe, old)
        self.assertTrue(gate._db_probe_errors(inspection, old, probe, {}, previous_html=old))

    def test_record_name_in_body_does_not_validate_an_old_title(self) -> None:
        probe = self.probe()
        page = self.page("Old ship") + "<p>Related item: " + probe["name"] + "</p>"
        self.assertTrue(gate._db_probe_errors(self.inspection(probe, page), page, probe, {}))

    def test_correct_pages_for_all_eight_types(self) -> None:
        for kind in gate.OBJECT_TYPE_TOKENS:
            with self.subTest(kind=kind):
                probe = self.probe(kind)
                page = self.page(probe["name"], kind)
                self.assertEqual([], gate._db_probe_errors(self.inspection(probe, page), page, probe, {}))

    def test_translated_and_html_escaped_titles_are_accepted(self) -> None:
        probe = self.probe()
        probe["name"] = "Ship A & B"
        for title, exact in ((probe["name"], {}), ("fixture translation", {probe["name"]: "fixture translation"})):
            with self.subTest(title=title):
                page = self.page(title)
                self.assertEqual([], gate._db_probe_errors(self.inspection(probe, page), page, probe, exact))

    def test_actual_html_must_meet_the_existing_size_floor(self) -> None:
        probe = self.probe("Aircraft")
        page = "<h4>Aircraft fixture</h4>"
        inspection = self.inspection(probe, page)
        inspection["html_len"] = 45000
        self.assertTrue(gate._db_probe_errors(inspection, page, probe, {}))

    def test_active_database_from_tree_or_window_title(self) -> None:
        tree = {"InternalDBViewer": {"text": "Database Viewer [DB3K_519]"}}
        self.assertEqual("DB3K_519", gate._active_viewer_database(tree, []))
        self.assertEqual("CWDB_519", gate._active_viewer_database({}, ["Viewer - CWDB_519.db3"]))
        with self.assertRaises(ValueError):
            gate._active_viewer_database(tree, ["Other database CWDB_519"])
        with self.assertRaises(ValueError):
            gate._active_viewer_database({}, ["Database Viewer"])

    def test_probes_are_selected_from_read_only_current_database(self) -> None:
        fixture_root = ROOT / "build/db-probe-tests"
        fixture_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=fixture_root) as directory:
            game = Path(directory)
            (game / "DB").mkdir()
            database = game / "DB/DB3K_519.db3"
            with sqlite3.connect(database) as connection:
                for kind, table in gate.DB_TYPE_TABLES.items():
                    connection.execute(f'CREATE TABLE "{table}" (ID INTEGER PRIMARY KEY, Name TEXT)')
                    connection.executemany(f'INSERT INTO "{table}" VALUES (?, ?)', [(9, kind + " later"), (3, kind + " first")])
            connection.close()
            original_connect = sqlite3.connect
            with patch.object(gate.sqlite3, "connect", wraps=original_connect) as connect:
                probes = gate._database_probes(game, "DB3K_519")
            self.assertIn("mode=ro", connect.call_args.args[0])
            self.assertTrue(connect.call_args.kwargs["uri"])
            self.assertEqual(set(gate.OBJECT_TYPE_TOKENS), set(probes))
            for kind, probe in probes.items():
                self.assertEqual((kind, 3, kind + " first"), (probe["type"], probe["id"], probe["name"]))

    def test_wait_sends_explicit_type_and_id(self) -> None:
        probe = self.probe("Ship")
        page = self.page(probe["name"])
        inspection = self.inspection(probe, page)

        class Harness:
            sent: list[str] = []

            def send(self, command: str, timeout: float = 0) -> dict:
                self.sent.append(command)
                return inspection if command == "DB_INSPECT" else {"status": "ok"}

        harness = Harness()
        with patch.object(gate.time, "sleep"), patch.object(gate, "_read_viewer_html", return_value=(page, Path("fixture.html"))):
            result = gate._wait_viewer_ready(harness, gate.Runner(), probe, {}, timeout=0.1)
        self.assertTrue(result["ok"])
        self.assertEqual("DB_NAVIGATE_ID Ship 3", harness.sent[0])
        self.assertNotIn("DB_NAVIGATE_ID 1", harness.sent)

    def ground_result(self, *, selected_id: int = 362, stale: bool = False,
                      visible_count: int = 1, focus_ok: bool = True) -> tuple[dict, list]:
        fixture_root = ROOT / "build/db-probe-tests"
        fixture_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=fixture_root) as directory:
            game = Path(directory)
            (game / "DB").mkdir()
            connection = sqlite3.connect(game / "DB/DB3K_519.db3")
            try:
                connection.execute("CREATE TABLE DataGroundUnit (ID INTEGER PRIMARY KEY, Name TEXT)")
                connection.execute("INSERT INTO DataGroundUnit VALUES (362, '100mm MT-12R [2A29R Ruta]')")
                connection.commit()
            finally:
                connection.close()
            probe = self.probe("Ground Unit")
            page = self.page("Old Facility" if stale else "100mm MT-12R [2A29R Ruta]")
            actions = []

            class Harness:
                GAME = game

                def game_pid(self):
                    return 123

                def list_windows(self, pid):
                    return [{"visible": True, "title": "Database Viewer DB3K_519", "hwnd": i,
                             "rect": {"X": 10, "Y": 20}} for i in range(visible_count)]

                def focus(self, hwnd):
                    actions.append(("focus", hwnd))
                    return focus_ok

                def click(self, x, y):
                    actions.append(("click", x, y))

                def keys(self, keys):
                    actions.append(("keys", keys))

                def send(self, command, timeout=0):
                    actions.append(("send", command))
                    if command != "DB_INSPECT":
                        raise AssertionError("Ground must use native list selection, not " + command)
                    return {"status": "ok", "sot": "Ground Unit", "id": selected_id,
                            "name": "", "html_len": len(page)}

            with patch.object(gate.time, "sleep"), patch.object(gate, "_read_viewer_html", return_value=(page, Path("fixture.html"))):
                result = gate._wait_viewer_ready(Harness(), gate.Runner(), probe, {}, timeout=0.1,
                                                previous_html=page if stale else None)
            return result, actions

    def test_ground_uses_native_selection_without_db_id_navigation(self) -> None:
        result, actions = self.ground_result()
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(362, result["probe"]["id"])
        self.assertEqual("100mm MT-12R [2A29R Ruta]", result["probe"]["name"])
        self.assertIn(("keys", "{HOME}{DOWN 4}{ENTER}"), actions)
        self.assertIn(("click", 10 + gate.FIRST_ROW_OFFSET[0], 20 + gate.FIRST_ROW_OFFSET[1]), actions)
        self.assertFalse(any(action[0] == "send" and "DB_NAVIGATE_ID" in action[1] for action in actions))

    def test_ground_rejects_the_previous_html(self) -> None:
        result, _ = self.ground_result(stale=True)
        self.assertFalse(result["ok"])

    def test_ground_rejects_an_id_missing_from_the_current_database(self) -> None:
        result, _ = self.ground_result(selected_id=999)
        self.assertFalse(result["ok"])

    def test_ground_requires_one_visible_current_database_viewer(self) -> None:
        for count in (0, 2):
            with self.subTest(visible_count=count):
                result, actions = self.ground_result(visible_count=count)
                self.assertFalse(result["ok"])
                self.assertEqual([], actions)

    def test_ground_failed_focus_sends_no_global_input(self) -> None:
        result, actions = self.ground_result(focus_ok=False)
        self.assertFalse(result["ok"])
        self.assertEqual([("focus", 0)], actions)

    def test_focus_requires_explicit_success_from_the_driver(self) -> None:
        for code, output, expected in ((0, "Focus result: True\n", True),
                                       (0, "Focus result: False\n", False),
                                       (1, "Focus result: True\n", False), (0, "", False)):
            with self.subTest(code=code, output=output):
                result = subprocess.CompletedProcess([], code, stdout=output)
                with patch.object(live.subprocess, "run", return_value=result):
                    self.assertEqual(expected, live.focus(456))

    def test_failed_dump_cannot_reuse_a_prior_file(self) -> None:
        fixture_root = ROOT / "build/db-probe-tests"
        fixture_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=fixture_root) as directory:
            game = Path(directory)
            dump = game / "CMOLocalizer_DBHTML.html"
            dump.write_text("old generated page", encoding="utf-8")

            class Harness:
                GAME = game

                def send(self, command: str, timeout: float = 0) -> dict:
                    # The engine can acknowledge despite a swallowed write error.
                    return {"status": "ok", "action": "DB_HTML_DUMP", "len": 9000}

            with patch.object(gate, "ROOT", game):
                text, _ = gate._read_viewer_html(Harness(), self.probe())
            self.assertEqual("", text)

    def test_successful_dump_requires_a_new_generated_file(self) -> None:
        fixture_root = ROOT / "build/db-probe-tests"
        fixture_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=fixture_root) as directory:
            game = Path(directory)
            dump = game / "CMOLocalizer_DBHTML.html"
            dump.write_text("old generated page", encoding="utf-8")
            fresh = self.page(self.probe()["name"])

            class Harness:
                GAME = game

                def send(self, command: str, timeout: float = 0) -> dict:
                    dump.write_text(fresh, encoding="utf-8")
                    return {"status": "ok", "action": "DB_HTML_DUMP", "len": len(fresh)}

            with patch.object(gate, "ROOT", game):
                text, evidence = gate._read_viewer_html(Harness(), self.probe())
            self.assertEqual(fresh, text)
            self.assertEqual(fresh, evidence.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
