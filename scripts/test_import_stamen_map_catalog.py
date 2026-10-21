#!/usr/bin/env python3
"""Import must retain source gaps and never approve unreviewed Chinese names."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from global_map_catalog import LAYERS, audit, record_fingerprint

ROOT = Path(__file__).resolve().parent.parent
COMMIT = "a" * 40
NE_COMMIT = "b" * 40


class ImportStamenMapCatalogTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="stamen-import-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.records = [self.city(0, "Alpha", [4, 6, 7])]
        self.cities = [dict(aries_index=0, name="Alpha", lat=30, lon=110,
                            zh="阿尔法", match_status="source_candidate",
                            match_source="GeoNames Chinese alias", match_source_id=123,
                            chinese_name_evidence="https://www.geonames.org/123/")]
        self.other = []
        self.observations = []
        self.reviewed_cities = []
        self.reviewed_synoptic = []

    def city(self, index, name, zooms):
        return dict(source_id=f"data/aries/z4to10.json#{index}",
                    source_path="data/aries/z4to10.json", source_record_index=index,
                    category="city", name=name, coordinates=[110, 30],
                    eligible_zooms_3_to_10=zooms, anchor_method="source_point",
                    properties=dict(zoom=9, population=5000))

    def write_json(self, name, value):
        path = self.base / name
        path.write_text(json.dumps(value, ensure_ascii=False) + "\n", encoding="utf-8")
        return path

    def write_lines(self, name, values):
        path = self.base / name
        path.write_text("".join(json.dumps(row, ensure_ascii=False) + "\n"
                                for row in values), encoding="utf-8")
        return path

    def run_import(self, summary_changes=None):
        catalog = self.write_lines("catalog.jsonl", self.records)
        summary = dict(terrain_classic_commit=COMMIT, natural_earth_commit=NE_COMMIT,
                       sha256=hashlib.sha256(catalog.read_bytes()).hexdigest(),
                       records=len(self.records))
        summary.update(summary_changes or {})
        cmd = [sys.executable, "-B", str(ROOT / "scripts/import_stamen_map_catalog.py"),
               "--catalog", str(catalog),
               "--source-summary", str(self.write_json("summary.json", summary)),
               "--city-matches", str(self.write_lines("cities.jsonl", self.cities)),
               "--other-matches", str(self.write_lines("other.jsonl", self.other)),
               "--confirmed-matches", str(self.write_json("observed.json", self.observations)),
               "--reviewed-cities", str(self.write_json("reviewed-cities.json", self.reviewed_cities)),
               "--reviewed-synoptic", str(self.write_json("reviewed-synoptic.json", self.reviewed_synoptic)),
               "--output-dir", str(self.base / "out")]
        return subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8")

    def success(self, result):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def lines(self, name):
        return [json.loads(line) for line in (self.base / "out" / name).read_text(
            encoding="utf-8").splitlines() if line.strip()]

    def manifest(self):
        return json.loads((self.base / "out/manifest.json").read_text(encoding="utf-8"))

    def test_union_of_observed_and_public_zoom_splits_holes(self):
        self.observations = [dict(source_id=self.records[0]["source_id"],
                                  source_name="Alpha", category="city", longitude=110,
                                  latitude=30, observed_zooms="9", observed_tiles="9/1/2.png")]
        self.success(self.run_import())
        records = self.lines("inventory.jsonl")
        self.assertEqual([(4, 4), (6, 7), (9, 9)],
                         [(r["min_zoom"], r["max_zoom"]) for r in records])
        self.assertTrue(all(COMMIT in r["id"] for r in records))
        self.assertTrue(all(r["source_record_id"] == self.records[0]["source_id"] for r in records))
        self.assertEqual(["original_png_observation"], records[-1]["zoom_provenance"]["9"])
        self.assertEqual("source_point", records[-1]["anchor_method"])
        translations = self.lines("translations.jsonl")
        self.assertEqual(["source_candidate"] * 3, [r["status"] for r in translations])
        self.assertEqual([record_fingerprint(r) for r in records],
                         [r["record_sha256"] for r in translations])

    def test_unresolved_visibility_and_unnamed_sources_are_not_lost(self):
        self.records += [self.city(1, "Unresolved", []), self.city(2, "", [9])]
        self.success(self.run_import())
        unresolved = self.lines("unresolved_visibility.jsonl")
        self.assertEqual({"visibility_unresolved", "source_has_no_renderable_name"},
                         {r["reason"] for r in unresolved})
        self.assertEqual(3, self.manifest()["counts"]["source_records"])
        self.assertEqual(2, self.manifest()["counts"]["unresolved_source_records"])
        self.assertTrue(all("source_record" in r for r in unresolved))

    def test_only_existing_reviewed_record_can_be_approved(self):
        self.records[0]["eligible_zooms_3_to_10"] = [4]
        self.cities[0].update(match_status="reviewed_existing", match_source_id="Cities:0",
                              match_source="CMO_CN reviewed Cities", candidate_count=1)
        self.success(self.run_import())
        self.assertEqual("source_candidate", self.lines("translations.jsonl")[0]["status"])
        self.reviewed_cities = [dict(name="Alpha", lat=30, lon=110, zh="阿尔法",
                                    source="https://www.geonames.org/123/")]
        self.success(self.run_import())
        self.assertEqual("approved", self.lines("translations.jsonl")[0]["status"])
        self.reviewed_cities[0]["zh"] = "另一个译名"
        self.success(self.run_import())
        self.assertEqual("source_candidate", self.lines("translations.jsonl")[0]["status"])

    def test_other_layers_stay_in_denominator_and_primary_alias_is_not_used(self):
        self.records = [dict(self.city(0, "Bay (Other name)", [6]),
                             source_id="marine.shp#0", source_path="marine.shp",
                             category="marine", anchor_method="polygon_interior_candidate")]
        self.cities = []
        self.other = [dict(source_id="marine.shp#0", name="Bay (Other name)",
                           category="marine", coordinates=[110, 30], zh=None,
                           primary_zh="海湾", match_status="untranslated_display_alias",
                           match_source="https://www.naturalearthdata.com/")]
        self.success(self.run_import())
        record = self.lines("inventory.jsonl")[0]
        translation = self.lines("translations.jsonl")[0]
        self.assertEqual("marine", record["kind"])
        self.assertIsNone(translation["zh"])
        self.assertEqual("untranslated", translation["status"])

    def test_city_review_cannot_hide_ambiguity_or_move_the_reviewed_point(self):
        self.records[0]["eligible_zooms_3_to_10"] = [4]
        self.cities[0].update(match_status="reviewed_existing", match_source_id="Cities:0",
                              match_source="CMO_CN reviewed Cities", candidate_count=1)
        entry = dict(name="Alpha", lat=30, lon=110, zh="阿尔法",
                     source="https://www.geonames.org/123/")
        self.reviewed_cities = [entry, dict(entry, zh="同名另一城")]
        self.success(self.run_import())
        self.assertEqual("source_candidate", self.lines("translations.jsonl")[0]["status"])
        self.reviewed_cities = [entry]
        for evidence in (dict(ambiguity=["unresolved entity"]),
                         dict(source_candidates=[{"source_id": "other city"}])):
            with self.subTest(evidence=evidence):
                self.cities[0].update(evidence)
                self.success(self.run_import())
                self.assertEqual("source_candidate", self.lines("translations.jsonl")[0]["status"])
                for key in evidence:
                    del self.cities[0][key]
        self.reviewed_cities[0]["lat"] = 30.0002
        self.success(self.run_import())
        self.assertEqual("source_candidate", self.lines("translations.jsonl")[0]["status"])

    def test_foreign_letters_are_not_presented_as_chinese(self):
        self.cities[0]["zh"] = "阿尔法Alpha"
        self.success(self.run_import())
        translation = self.lines("translations.jsonl")[0]
        self.assertIsNone(translation["zh"])
        self.assertEqual("invalid_chinese_candidate", translation["reason"])

    def test_peak_source_keeps_height_as_required_visible_text(self):
        self.records = [dict(self.city(0, "White Mountain Peak", [8, 9]),
                             source_id="peaks.shp#0", source_path="peaks.shp", category="peak",
                             properties={"elevation": 4344.0})]
        self.cities = []
        self.success(self.run_import())
        record = self.lines("inventory.jsonl")[0]
        self.assertEqual(["4344米"], record.get("required_display_text"))
        self.assertEqual(4344, record["source_properties"]["elevation"])

    def test_changed_city_identity_duplicate_or_unknown_match_fails(self):
        self.cities[0]["lat"] = 31
        self.assertNotEqual(0, self.run_import().returncode)
        self.cities[0]["lat"] = 30
        self.cities.append(dict(self.cities[0]))
        self.assertNotEqual(0, self.run_import().returncode)
        self.cities = [dict(self.cities[0], aries_index=999)]
        self.assertNotEqual(0, self.run_import().returncode)

    def test_source_hash_and_commit_are_verified_before_outputs(self):
        self.assertNotEqual(0, self.run_import(dict(sha256="0" * 64)).returncode)
        self.assertFalse((self.base / "out/inventory.jsonl").exists())
        self.assertNotEqual(0, self.run_import(dict(terrain_classic_commit="master")).returncode)

    def test_manifest_is_deterministic_incomplete_and_accepted_by_gate(self):
        self.success(self.run_import())
        first = {p.name: p.read_bytes() for p in (self.base / "out").iterdir()}
        self.success(self.run_import())
        self.assertEqual(first, {p.name: p.read_bytes() for p in (self.base / "out").iterdir()})
        manifest = self.manifest()
        self.assertEqual(set(LAYERS), set(manifest["layers"]))
        self.assertFalse(manifest["snapshot_verified"])
        self.assertFalse(manifest["parity_verified"])
        self.assertTrue(all(layer["complete"] is False for layer in manifest["layers"].values()))
        self.assertEqual(hashlib.sha256(first["inventory.jsonl"]).hexdigest(),
                         manifest["inventory_sha256"])
        report, approved = audit(manifest, self.base / "out/inventory.jsonl",
                                 self.base / "out/translations.jsonl")
        self.assertFalse(report["ready"])
        self.assertEqual([], approved)


if __name__ == "__main__":
    unittest.main()
