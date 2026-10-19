#!/usr/bin/env python3
"""Full-review gates and isolated Cities/Synoptic translation fixtures."""
from __future__ import annotations

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from test_placenames import ROOT, write_fixture
from verify_placenames import verify_asset
from wwp import load


class GlobalPlacenameTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="global-placenames-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.reviews = self.base / "reviewed"
        self.reviews.mkdir()
        self.records = [dict(name="Shared" if i < 2 else f"English{i}",
                             previous=f"旧译{i}", zh=f"核准{i}",
                             lat=i / 10, lon=80 + i / 10,
                             source="https://example.com/review") for i in range(321)]
        self.dictionary = self.base / "dictionary.json"
        self.write_json(self.dictionary, {})
        self.scopes = self.base / "scopes.json"
        self.write_reviews(self.records)

    def write_json(self, path, value):
        path.write_text(json.dumps(value, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    def write_reviews(self, records):
        self.write_json(self.reviews / "global.json", records)
        self.write_json(self.scopes, {"files": {"other.json": 1},
                                     "synoptic_files": {"global.json": len(records)}})

    def run_script(self, script, *args):
        return subprocess.run([sys.executable, "-X", "utf8", "-B",
                               str(ROOT / "scripts" / script), *map(str, args)],
                              capture_output=True, text=True, encoding="utf-8", check=False)

    def success(self, result):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def fixture(self, path, alias="zh"):
        return write_fixture(path, [(r[alias], r["lat"], r["lon"], i)
                                    for i, r in enumerate(self.records)])

    def gate(self, asset, *extra):
        return self.run_script("verify_placenames.py", "--set", "Synoptic",
                               "--asset", asset, "--review-dir", self.reviews,
                               "--scopes", self.scopes, *extra)

    def build(self, source, output):
        return self.run_script("build_cities_placenames.py", "--set", "Synoptic",
                               "--source", source, "--output", output,
                               "--review-dir", self.reviews)

    def test_synoptic_complete_generation_accepts_all_aliases_and_is_idempotent(self):
        canonical = None
        for alias in ("name", "previous", "zh"):
            with self.subTest(alias=alias):
                source, output = self.base / f"{alias}.wwp", self.base / "output.wwp"
                original = self.fixture(source, alias)
                self.success(self.build(source, output))
                actual = output.read_bytes()
                canonical = actual if canonical is None else canonical
                self.assertEqual(canonical, actual)
                self.assertEqual(original, source.read_bytes())
                self.success(self.gate(output, "--baseline", source))
                self.success(self.build(output, output))
                self.assertEqual(actual, output.read_bytes())

    def test_release_gate_rejects_unreviewed_point_even_with_chinese_label(self):
        asset = self.base / "asset.wwp"
        self.fixture(asset)
        self.write_reviews(self.records[:-1])
        result = self.gate(asset)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unreviewed", result.stdout)

    def test_strict_build_rejects_missing_review_before_writing(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        self.fixture(source, "previous")
        self.write_reviews(self.records[:-1])
        result = self.build(source, output)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unreviewed", result.stderr)
        self.assertFalse(output.exists())

    def test_partial_build_preserves_unknown_coordinate_without_default_dictionary(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        original = write_fixture(source, [("Tokyo", 10.0, 20.0, 7)])
        result = self.run_script("build_cities_placenames.py", "--allow-partial",
                                 "--source", source, "--output", output,
                                 "--review-dir", self.reviews)
        self.success(result)
        self.assertEqual(original, output.read_bytes())

    def test_both_sets_require_release_count_by_default(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        for kind, count in (("Cities", 4287), ("Synoptic", 321)):
            with self.subTest(kind=kind):
                write_fixture(source, [("旧译0", 0.0, 80.0, 7)])
                result = self.run_script("build_cities_placenames.py", "--set", kind,
                                         "--source", source, "--output", output,
                                         "--review-dir", self.reviews)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(f"placename count: expected {count}, found 1", result.stderr)
                self.assertFalse(output.exists())

    def test_custom_dictionary_cannot_replace_review_approval_in_strict_build(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        self.fixture(source, "name")
        self.write_reviews(self.records[:-1])
        self.write_json(self.dictionary, {"English320": "核准320"})
        result = self.run_script("build_cities_placenames.py", "--set", "Synoptic",
                                 "--require-complete", "--source", source, "--output", output,
                                 "--review-dir", self.reviews, "--dictionary", self.dictionary)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unreviewed", result.stderr)
        self.assertFalse(output.exists())

    def test_partial_build_uses_custom_dictionary_only_when_explicit(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        write_fixture(source, [("Tokyo", 10.0, 20.0, 7)])
        self.write_json(self.dictionary, {"Tokyo": "自定义东京"})
        result = self.run_script("build_cities_placenames.py", "--allow-partial",
                                 "--source", source, "--output", output,
                                 "--review-dir", self.reviews, "--dictionary", self.dictionary)
        self.success(result)
        self.assertEqual("自定义东京", load(output)[0]["name"])

    def test_apply_preserves_unknown_points_and_isolates_explicit_dictionaries(self):
        game = self.base / "game"
        city = game / "WW/PlacenameSets/Cities/0000_0000.wwp"
        synoptic = game / "WW/PlacenameSets/Synoptic/0000_0000.wwp"
        original_city = write_fixture(city, [("Tokyo", 10.0, 20.0, 7)])
        original_synoptic = write_fixture(synoptic, [("Japan", 10.0, 20.0, 7)])
        args = ("--game-dir", game, "--review-dir", self.reviews,
                "--synoptic-review-dir", self.reviews)
        self.success(self.run_script("apply_placenames.py", *args))
        self.assertEqual(original_city, city.read_bytes())
        self.assertEqual(original_synoptic, synoptic.read_bytes())
        self.assertFalse(Path(str(city) + ".orig").exists())
        self.assertFalse(Path(str(synoptic) + ".orig").exists())
        self.write_json(self.dictionary, {"Tokyo": "自定义东京", "Japan": "不能串集合"})
        self.success(self.run_script("apply_placenames.py", *args,
                                    "--dictionary", self.dictionary))
        self.assertEqual("自定义东京", load(city)[0]["name"])
        self.assertEqual(original_synoptic, synoptic.read_bytes())
        self.write_json(self.dictionary, {"Japan": "自定义日本"})
        self.success(self.run_script("apply_placenames.py", *args,
                                    "--synoptic-dictionary", self.dictionary))
        self.assertEqual("自定义日本", load(synoptic)[0]["name"])

    def test_pending_review_is_rejected_by_build_and_release_gate(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        self.fixture(source)
        for field in ("status", "review_status"):
            with self.subTest(field=field):
                records = [dict(r) for r in self.records]
                records[0][field] = "pending"
                self.write_reviews(records)
                result = self.build(source, output)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("pending", result.stderr)
                self.assertFalse(output.exists())
                gate = self.gate(source)
                self.assertNotEqual(0, gate.returncode)
                self.assertIn("pending", gate.stderr)

    def test_approved_label_with_source_anchor_conflict_remains_publishable(self):
        source, output = self.base / "source.wwp", self.base / "output.wwp"
        self.fixture(source, "previous")
        records = [dict(r) for r in self.records]
        records[0].update(status="reviewed", review_status="approved",
                          identity_status="source_anchor_conflict")
        self.write_reviews(records)
        self.success(self.build(source, output))
        self.success(self.gate(output, "--baseline", source))

    def test_complete_gate_rejects_metadata_changes_and_unapproved_renames(self):
        asset = self.base / "asset.wwp"
        self.fixture(asset)
        items = load(asset)
        for field, value in (("lat", 0.5), ("lon", 81.5), ("flags", 999),
                             ("name", "未经批准的中文")):
            with self.subTest(field=field):
                changed = [dict(item) for item in items]
                changed[0][field] = value
                result = verify_asset(changed, self.records, items, expected_count=321,
                                      require_complete=True)
                self.assertTrue(result["errors"])
        result = verify_asset(items[::-1], self.records, items, expected_count=321,
                              require_complete=True)
        self.assertTrue(any("order changed" in e for e in result["errors"]))

    def test_apply_does_not_mix_same_alias_and_coordinate_between_sets(self):
        city_reviews = self.base / "city-reviews"
        city_reviews.mkdir()
        city = dict(self.records[0], previous="城市旧译", zh="城市新译")
        synoptic = dict(self.records[0], previous="区域旧译", zh="区域新译")
        self.write_json(city_reviews / "city.json", [city])
        self.write_reviews([synoptic])
        game = self.base / "game"
        for alias in ("name", "previous", "zh"):
            with self.subTest(alias=alias):
                targets = []
                for kind, record in (("Cities", city), ("Synoptic", synoptic)):
                    target = game / "WW/PlacenameSets" / kind / "0000_0000.wwp"
                    write_fixture(target, [(record[alias], record["lat"], record["lon"], 7)])
                    targets.append((target, record))
                args = ("--game-dir", game, "--dictionary", self.dictionary,
                        "--review-dir", city_reviews, "--synoptic-review-dir", self.reviews)
                self.success(self.run_script("apply_placenames.py", *args))
                first = [p.read_bytes() for p, _ in targets]
                for target, record in targets:
                    self.assertEqual([dict(name=record["zh"], lat=record["lat"],
                                           lon=record["lon"], flags=7)], load(target))
                self.success(self.run_script("apply_placenames.py", *args))
                self.assertEqual(first, [p.read_bytes() for p, _ in targets])


if __name__ == "__main__":
    unittest.main()
