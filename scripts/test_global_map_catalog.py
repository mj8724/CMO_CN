#!/usr/bin/env python3
"""Global map catalog gates must reject incomplete sources and translations."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
LAYERS = ("city", "admin0", "admin1", "marine", "water", "park", "peak", "region")


class GlobalMapCatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="global-map-catalog-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.records = [dict(id="city:1", source_id="fixture", name="Original City",
                             kind="city", lat=30.0, lon=110.0,
                             min_zoom=4, max_zoom=10, priority=10)]
        self.translations = [dict(id="city:1", zh="核准城市", status="approved",
                                  source="https://example.com/translation")]
        self.manifest = dict(schema_version=1, min_zoom=3, max_zoom=10,
                             snapshot_verified=True, parity_verified=True,
                             sources=[dict(id="fixture", url="https://example.com/source",
                                           sha256="a" * 64, license="Public domain")],
                             layers={kind: dict(complete=True, source_ids=["fixture"],
                                                evidence="https://example.com/layer")
                                     for kind in LAYERS})

    def write_json(self, name, value):
        path = self.base / name
        path.write_text(json.dumps(value, ensure_ascii=False) + "\n", encoding="utf-8")
        return path

    def run_gate(self, build=False, reviews=None):
        inventory = self.base / "inventory.jsonl"
        inventory.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n"
                                     for r in self.records), encoding="utf-8")
        translations = self.base / "translations.jsonl"
        by_id = {record["id"]: record for record in self.records}
        for translation in self.translations:
            if "record_sha256" not in translation and translation["id"] in by_id:
                wire = json.dumps(by_id[translation["id"]], sort_keys=True,
                                  separators=(",", ":"), ensure_ascii=False)
                translation["record_sha256"] = hashlib.sha256(wire.encode("utf-8")).hexdigest()
        translations.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n"
                                        for r in self.translations), encoding="utf-8")
        self.manifest["inventory_sha256"] = hashlib.sha256(inventory.read_bytes()).hexdigest()
        manifest = self.write_json("manifest.json", self.manifest)
        cmd = [sys.executable, "-B", str(ROOT / "scripts/global_map_catalog.py"),
               "--manifest", str(manifest), "--inventory", str(inventory),
               "--translations", str(translations), "--report", str(self.base / "report.json")]
        if build:
            cmd += ["--output", str(self.base / "map.dat")]
        if reviews is not None:
            path = self.base / "reviews.jsonl"
            path.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in reviews),
                            encoding="utf-8")
            cmd += ["--reviews", str(path)]
        return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")

    def success(self, result):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def failure(self, result):
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)

    def report(self):
        path = self.base / "report.json"
        self.assertTrue(path.is_file(), "audit did not produce its report")
        return json.loads(path.read_text(encoding="utf-8"))

    def test_complete_catalog_is_deterministic_and_keeps_source_zoom(self):
        self.success(self.run_gate(build=True))
        wire = (self.base / "map.dat").read_bytes()
        self.assertEqual(b"CMOMAP01", wire[:8])
        # A SHA256 string has a one-byte .NET UTF8 length prefix.
        self.assertEqual(64, wire[8])
        self.assertEqual(hashlib.sha256(wire[73:]).hexdigest(), wire[9:73].decode("ascii"))
        self.assertEqual(1, struct.unpack_from("<i", wire, 73)[0])
        self.assertIn("核准城市".encode("utf-8"), wire)
        self.assertEqual((4, 10, 10, 1), struct.unpack_from("<iiii", wire, len(wire) - 16))
        self.success(self.run_gate(build=True))
        self.assertEqual(wire, (self.base / "map.dat").read_bytes())

    def test_missing_translation_stays_in_denominator_and_preserves_old_output(self):
        self.records.append(dict(self.records[0], id="city:2", name="Untranslated City"))
        (self.base / "map.dat").write_bytes(b"previous-good-catalog")
        self.failure(self.run_gate(build=True))
        report = self.report()
        self.assertEqual(2, report["inventory_count"])
        self.assertEqual(1, report["approved_count"])
        self.assertEqual(["city:2"], [r["id"] for r in report["translation_gaps"]])
        self.assertEqual(b"previous-good-catalog", (self.base / "map.dat").read_bytes())

    def test_english_half_translation_and_unreviewed_candidates_are_rejected(self):
        for zh, status in [("Original City", "approved"), ("原名City", "approved"),
                           ("原名Сити", "approved"), ("原名", "source_candidate"),
                           ("原名\U0002fffe", "approved"), ("原名\ufa6e", "approved"),
                           ("第\U0001fbf0城", "approved"), ("名\U0001e95e城", "approved"),
                           ("原名", "approved_but_ambiguous")]:
            with self.subTest(zh=zh, status=status):
                (self.base / "map.dat").unlink(missing_ok=True)
                self.translations[0].update(zh=zh, status=status)
                self.failure(self.run_gate(build=True))
                self.assertEqual(0, self.report()["approved_count"])
                self.assertFalse((self.base / "map.dat").exists())

    def test_all_chinese_does_not_hide_incomplete_source_or_visual_parity(self):
        for field in ("snapshot_verified", "parity_verified"):
            with self.subTest(field=field):
                self.manifest[field] = False
                self.failure(self.run_gate(build=True))
                self.assertEqual(1, self.report()["approved_count"])
                self.assertFalse((self.base / "map.dat").exists())
                self.manifest[field] = True
        self.manifest["layers"]["water"]["complete"] = False
        self.failure(self.run_gate(build=True))
        self.assertIn("water", " ".join(self.report()["source_gaps"]))
        del self.manifest["layers"]["park"]
        self.failure(self.run_gate())

    def test_global_attestation_does_not_override_explicit_unresolved_records(self):
        self.manifest["counts"] = {"unresolved_source_records": 1}
        self.failure(self.run_gate(build=True))
        self.assertFalse((self.base / "map.dat").exists())
        self.manifest["counts"]["unresolved_source_records"] = 0
        self.records[0]["anchor_verified"] = False
        self.translations[0].pop("record_sha256", None)
        self.failure(self.run_gate(build=True))
        self.assertFalse((self.base / "map.dat").exists())
        self.assertIn("anchor_verified", " ".join(self.report()["source_gaps"]))

    def test_duplicate_identity_and_unknown_translation_fail(self):
        self.records.append(dict(self.records[0]))
        self.failure(self.run_gate())
        self.records.pop()
        self.translations.append(dict(self.translations[0]))
        self.failure(self.run_gate())
        self.translations[-1]["id"] = "not-in-source"
        self.failure(self.run_gate())

    def test_same_name_different_entities_not_deduplicated(self):
        self.records.append(dict(self.records[0], id="city:2", lat=10))
        self.translations.append(dict(self.translations[0], id="city:2", zh="另一城市"))
        self.success(self.run_gate(build=True))
        self.assertEqual(2, self.report()["approved_count"])

    def test_old_approved_translation_cannot_follow_reused_source_id(self):
        self.success(self.run_gate())
        self.records[0].update(name="A different city", lat=20)
        self.failure(self.run_gate(build=True))
        self.assertEqual("source_record_changed_or_unbound",
                         self.report()["translation_gaps"][0]["reason"])

    def test_assigned_supplementary_han_is_shared_with_old_dotnet_consumer(self):
        self.translations[0]["zh"] = "名称\u9fff\U00030000\U00031350"
        self.success(self.run_gate(build=True))

    def test_chinese_name_separators_and_digits_are_explicit(self):
        self.translations[0]["zh"] = "第1城（第２城）·〇镇"
        self.success(self.run_gate(build=True))

    def test_reviewed_addition_can_replace_candidate_without_editing_generated_input(self):
        self.translations[0].update(zh="中文候选", status="source_candidate")
        self.failure(self.run_gate(build=True))
        review = dict(self.translations[0], zh="新增核准中文", status="approved",
                      review_notes="Original entity and Chinese source checked together.")
        self.success(self.run_gate(build=True, reviews=[review]))
        self.assertEqual(1, self.report()["manual_review_count"])
        self.assertIn("新增核准中文".encode("utf-8"), (self.base / "map.dat").read_bytes())
        self.assertEqual("source_candidate", self.translations[0]["status"])

    def test_invalid_or_stale_manual_reviews_cannot_replace_previous_output(self):
        self.success(self.run_gate(build=True))
        previous = (self.base / "map.dat").read_bytes()
        review = dict(self.translations[0], review_notes="Entity checked.")
        for changes in [dict(id="unknown"), dict(record_sha256="0" * 64),
                        dict(zh="中文English"), dict(status="source_candidate"),
                        dict(review_notes=""), dict(source="")]:
            with self.subTest(changes=changes):
                self.failure(self.run_gate(build=True, reviews=[dict(review, **changes)]))
                self.assertEqual(previous, (self.base / "map.dat").read_bytes())
        self.failure(self.run_gate(reviews=[review, review]))

    def test_peak_name_alone_cannot_drop_observed_height(self):
        self.records[0].update(kind="peak", required_display_text=["4344米"])
        self.translations[0]["zh"] = "白山峰"
        self.failure(self.run_gate(build=True))
        self.assertEqual("required_display_text_missing", self.report()["translation_gaps"][0]["reason"])
        for wrong in ("白山峰（14344米）", "白山峰（-4344米）", "白山峰（0.4344米）"):
            with self.subTest(wrong=wrong):
                self.translations[0]["zh"] = wrong
                self.failure(self.run_gate(build=True))
        self.translations[0]["zh"] = "白山峰（4344米）"
        self.success(self.run_gate(build=True))

    def test_unknown_source_invalid_coordinate_and_zoom_fail(self):
        for changes in [dict(source_id="unknown"), dict(lat=float("nan")),
                        dict(lon=181), dict(min_zoom=11), dict(priority=True)]:
            with self.subTest(changes=changes):
                saved = dict(self.records[0])
                self.records[0].update(changes)
                self.failure(self.run_gate())
                self.records[0] = saved


if __name__ == "__main__":
    unittest.main()
