#!/usr/bin/env python3
"""Reviewed-corpus audit checks; mutations use memory copies, never game files."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import audit_richtext as tool


class RichTextAuditTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.inventory = tool.load_json(tool.ROOT / "docs/RICHTEXT_INVENTORY.json")
        cls.source = tool.load_json(tool.ROOT / "dict/src/richtext_help.json")
        cls.exact = tool.load_json(tool.ROOT / "dict/CMOLocalizer.json")["exact"]
        cls.pool = tool.load_json(tool.ROOT / "dict/strings_pool.json")
        cls.texts, _ = tool.recover_texts(cls.inventory, cls.source)
        cls.extra = tool.recover_records(cls.inventory["resourceHelp"], cls.source)

    def run_audit(self, *, exact=None, resources=None) -> dict:
        return tool.audit(self.inventory, self.source, self.exact if exact is None else exact,
                          self.pool, resources)

    def resource_report(self) -> dict:
        return {"sha256": self.inventory["sourceAssembly"]["sha256"], "unknownResources": [],
                "rows": [{"resource": record["source"]["resource"],
                          "key": record["source"]["key"], "text": self.texts[name]}
                         for name, record in self.inventory["controls"].items()
                         if record["source"]["kind"] == "manifest-resource"]}

    def full_resource_report(self) -> dict:
        report = self.resource_report()
        report["resourceFilter"] = ""
        report["rows"].extend({"resource": record["source"]["resource"],
                               "key": record["source"]["key"], "text": self.extra[name]}
                              for name, record in self.inventory["resourceHelp"].items())
        return report

    def filtered_resource_report(self, resource_filter: str) -> dict:
        report = self.full_resource_report()
        report["resourceFilter"] = resource_filter
        report["rows"] = [row for row in report["rows"]
                          if resource_filter.lower() in row["resource"].lower()]
        return report

    def test_reviewed_sources_are_complete_without_game_or_build_evidence(self) -> None:
        result = self.run_audit()
        self.assertTrue(result["ok"], result["errors"] + result["unknown"])
        self.assertEqual((result["scope"], result["controlCount"], result["lineCount"], len(result["prose"])),
                         ("reviewed-corpus", 9, 89, 7))
        self.assertEqual((result["resourceHelpCount"], result["resourceHelpLineCount"], result["reviewedBodyCount"]),
                         (13, 39, 29))

    def test_resource_report_recovers_five_resources_and_four_snapshot_bodies(self) -> None:
        result = self.run_audit(resources=self.resource_report())
        self.assertTrue(result["ok"], result["errors"] + result["unknown"])
        self.assertEqual(result["lineCount"], 89)

    def test_debugtool_filter_uses_reviewed_hotkeys_and_reports_correct_provenance(self) -> None:
        report = self.filtered_resource_report("dEbUgToOl")
        self.assertEqual(len(report["rows"]), 1)
        result = self.run_audit(resources=report)
        self.assertTrue(result["ok"], result["errors"] + result["unknown"])
        self.assertEqual((result["controlCount"], result["lineCount"]), (9, 89))
        for control in result["controls"]:
            expected = ("reviewed-source" if control["source"]["kind"] == "manifest-resource"
                        else "string-pool-snapshot")
            self.assertEqual(control["inputSource"], expected)
        for help_row in result["resourceHelp"]:
            expected = ("resource-report" if "debugtool" in help_row["source"]["resource"].lower()
                        else "reviewed-source")
            self.assertEqual(help_row["inputSource"], expected)

    def test_filter_without_reviewed_resources_uses_reviewed_hotkeys(self) -> None:
        result = self.run_audit(resources=self.filtered_resource_report("NoReviewedResource"))
        self.assertTrue(result["ok"], result["errors"] + result["unknown"])
        self.assertEqual(result["lineCount"], 89)
        self.assertNotIn("resource-report", [row["inputSource"] for row in result["controls"]])

    def test_missing_resource_is_unknown_and_fails(self) -> None:
        for resource_filter in (None, "hOtKeYs", ""):
            with self.subTest(resource_filter=resource_filter):
                report = (self.resource_report() if resource_filter is None
                          else self.filtered_resource_report(resource_filter))
                missing = report["rows"].pop(0)["key"][:-5]
                result = self.run_audit(resources=report)
                self.assertFalse(result["ok"])
                self.assertIn("Missing resource control: " + missing, result["unknown"])

    def test_debugtool_filter_still_requires_its_selected_help(self) -> None:
        report = self.filtered_resource_report("DebugTool")
        missing = report["rows"].pop()
        result = self.run_audit(resources=report)
        self.assertFalse(result["ok"])
        self.assertIn("Missing help resource: " + missing["resource"] + ":" + missing["key"],
                      result["unknown"])

    def test_new_or_unparsed_resources_are_unknown(self) -> None:
        for change in ("new", "unparsed", "drift", "unresolved"):
            with self.subTest(change=change):
                report = self.resource_report()
                if change == "new":
                    report["rows"].append({"resource": "Command.Hotkeys.resources",
                                           "key": "RichTextBox9.Text", "text": "X\tNew help"})
                elif change == "unparsed":
                    report["rows"][0]["text"] = None
                elif change == "drift":
                    report["rows"][0]["text"] += "\r\nX\tNew help"
                else:
                    report["unknownResources"].append({"resource": "encrypted", "reason": "resource-read-failed"})
                self.assertFalse(self.run_audit(resources=report)["ok"])

    def test_missing_description_translation_fails_even_with_existing_body(self) -> None:
        exact = self.exact.copy()
        del exact["Start / Resume / Pause Game"]
        result = self.run_audit(exact=exact)
        self.assertFalse(result["ok"])
        self.assertTrue(any("missing Chinese description" in item for item in result["errors"]))

    def test_tab_shortcut_and_newline_mutations_fail(self) -> None:
        raw = self.texts["RichTextBox1"]
        translated = self.exact[raw]
        for corrupted in (translated.replace("\t", " ", 1),
                          translated.replace("F1\t", "F2\t", 1),
                          translated.replace("\r\n", "\n", 1)):
            with self.subTest(corrupted=corrupted[:80]):
                exact = self.exact.copy()
                exact[raw] = corrupted
                result = self.run_audit(exact=exact)
                self.assertFalse(result["ok"])
                self.assertTrue(any("changed" in item for item in result["errors"]))

    def test_missing_reviewed_source_hash_fails_fast(self) -> None:
        source = self.source.copy()
        del source[self.texts["RichTextBox1"]]
        with self.assertRaisesRegex(ValueError, "Reviewed source hash missing"):
            tool.audit(self.inventory, source, self.exact, self.pool)

    def test_prose_file_and_url_tokens_cannot_change(self) -> None:
        _, prose = tool.recover_texts(self.inventory, self.source)
        for offset, token in (("601496", "Autosave.scen"), ("809922", "http://www.freesfx.co.uk")):
            with self.subTest(offset=offset):
                exact = self.exact.copy()
                exact[prose[offset]] = exact[prose[offset]].replace(token, "changed")
                result = self.run_audit(exact=exact)
                self.assertFalse(result["ok"])
                self.assertTrue(any("URL/path/format" in item for item in result["errors"]))

    def test_full_resource_scope_keeps_containers_and_templates_outside(self) -> None:
        report = self.full_resource_report()
        report["unknownResources"] = [
            {"resource": "opaque.container", "reason": "not-a-standard-resource-set"},
            {"resource": "Command_Core.slaxml.lua", "reason": "not-a-standard-resource-set"},
            {"resource": "Command_Core.NavalFormationEditorTemplate.txt", "reason": "not-a-standard-resource-set"}]
        result = self.run_audit(resources=report)
        self.assertTrue(result["ok"], result["errors"] + result["unknown"])
        self.assertEqual(result["outsideScopeUnknownCount"], 3)

    def test_extra_resource_loss_and_new_multiline_help_fail(self) -> None:
        report = self.full_resource_report()
        report["rows"].pop()
        result = self.run_audit(resources=report)
        self.assertFalse(result["ok"])
        self.assertTrue(any("Missing help resource" in item for item in result["unknown"]))
        report = self.full_resource_report()
        report["rows"].append({"resource": "Command.NewHelp.resources", "key": "Info.Text",
                               "text": "New information\r\nMore information"})
        result = self.run_audit(resources=report)
        self.assertFalse(result["ok"])
        self.assertTrue(any("Unreviewed UI help" in item for item in result["unknown"]))

    def test_additional_help_translation_and_code_examples_are_checked(self) -> None:
        raw = next(text for name, text in self.extra.items() if name.endswith(":TB_Examples.Text"))
        self.assertTrue(self.run_audit()["ok"])
        self.assertIn("#aircraft;2846;Drone.png;40;rot", self.exact[raw])
        exact = self.exact.copy()
        del exact[raw]
        self.assertFalse(self.run_audit(exact=exact)["ok"])
        exact[raw] = self.exact[raw].replace("#aircraft;2846", "#aircraft;2847")
        result = self.run_audit(exact=exact)
        self.assertFalse(result["ok"])
        self.assertTrue(any("code example changed" in item for item in result["errors"]))

    def test_extra_help_needs_individual_lines_even_when_the_full_body_exists(self) -> None:
        raw = next(text for name, text in self.extra.items()
                   if "LuaEditorOperationPlanner.resources:" in name)
        description = "-The script MUST return a boolean."
        self.assertNotEqual(raw, description)
        self.assertIn(raw, self.exact)
        exact = self.exact.copy()
        del exact[description]
        result = self.run_audit(exact=exact)
        self.assertFalse(result["ok"])
        self.assertTrue(any("missing Chinese description: " + description in item
                            for item in result["errors"]))

    def test_verified_designer_placeholder_is_excluded(self) -> None:
        report = self.full_resource_report()
        inventory = json.loads(json.dumps(self.inventory))
        placeholder = "The quick brown fox jumps over the lazy dog.\r\nPlaceholder"
        excluded = inventory["excludedResources"][0]
        excluded["sha256"] = tool.text_hash(placeholder)
        report["rows"].append({"resource": excluded["resource"], "key": excluded["key"], "text": placeholder})
        result = tool.audit(inventory, self.source, self.exact, self.pool, report)
        self.assertTrue(result["ok"], result["unknown"])
        report["rows"][-1]["text"] = "New real instructions\r\nMust be reviewed"
        self.assertFalse(tool.audit(inventory, self.source, self.exact, self.pool, report)["ok"])

    def test_dictionary_argument_audits_the_selected_file(self) -> None:
        build = tool.ROOT / "build"
        build.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=build, prefix="richtext-audit-") as temporary:
            root = Path(temporary)
            dictionary, output = root / "dictionary.json", root / "corpus.json"
            dictionary.write_text(json.dumps({"exact": self.exact}, ensure_ascii=False), encoding="utf-8")
            command = [sys.executable, "-X", "utf8", str(tool.ROOT / "scripts/audit_richtext.py"),
                       "--dictionary", str(dictionary), "--output", str(output)]
            result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=10)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual(tool.load_json(output)["resourceHelpCount"], 13)
            dictionary.write_text('{"exact": {}}', encoding="utf-8")
            result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=10)
            self.assertEqual(result.returncode, 1)


if __name__ == "__main__":
    unittest.main()
