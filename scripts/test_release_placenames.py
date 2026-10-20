#!/usr/bin/env python3
"""Exercise the real release placename gate against complete synthetic assets."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import verify_all
from placename_translation import PLACENAME_SETS
from test_placenames import write_fixture
from wwp import load, save


class ReleasePlacenameTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="release-placenames-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.dictionary = self.root / "dict"
        self.reviews = {}
        metadata = {}
        for name, config in PLACENAME_SETS.items():
            records = [dict(name=f"{name}{i}", previous=f"旧{name}{i}", zh=f"核准{name}{i}",
                            lat=(i // 100) / 10, lon=(i % 100) / 10,
                            source="https://example.com/review") for i in range(config["count"])]
            review_file = self.dictionary / config["review_file"]
            self.reviews[name] = records
            self.write_json(review_file, records)
            write_fixture(self.asset(name), [(r["zh"], r["lat"], r["lon"], i)
                                             for i, r in enumerate(records)])
            wire = struct.pack("<i", len(records)) + b"".join(
                struct.pack("<ffI", r["lat"], r["lon"], i) for i, r in enumerate(records))
            metadata[name] = hashlib.sha256(wire).hexdigest()
        self.metadata = self.dictionary / "placenames/metadata.json"
        self.write_json(self.metadata, metadata)
        self.root_patch = patch.object(verify_all, "ROOT", self.root)
        self.dict_patch = patch.object(verify_all, "DICT", self.dictionary)
        self.root_patch.start()
        self.dict_patch.start()
        self.addCleanup(self.root_patch.stop)
        self.addCleanup(self.dict_patch.stop)

    def write_json(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, ensure_ascii=False) + "\n",
                        encoding="utf-8", newline="\n")

    def asset(self, name):
        return self.root / "assets/placenames" / f"{name}.wwp"

    def check_gate(self):
        with patch.object(verify_all, "results", []):
            verify_all.gate_placenames()
            self.assertEqual(1, len(verify_all.results))
            name, ok, detail = verify_all.results[0]
            self.assertEqual("placenames", name)
            return ok, detail

    def check_cli(self, name):
        config = PLACENAME_SETS[name]
        script = Path(__file__).with_name("verify_placenames.py")
        return subprocess.run([sys.executable, "-X", "utf8", "-B", str(script),
                               "--set", name, "--asset", str(self.asset(name)),
                               "--review-file", str(self.dictionary / config["review_file"]),
                               "--metadata-baseline", str(self.metadata)],
                              capture_output=True, text=True, encoding="utf-8")

    def test_complete_reviewed_release_assets_pass_without_a_game_install(self):
        with patch.object(verify_all, "GAME", self.root / "missing-game"):
            ok, detail = self.check_gate()
        self.assertTrue(ok, detail)
        self.assertIn("4287/4287", detail)
        self.assertIn("321/321", detail)
        for name in PLACENAME_SETS:
            result = self.check_cli(name)
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_wrong_chinese_label_in_either_release_asset_fails(self):
        for name in PLACENAME_SETS:
            with self.subTest(name=name):
                asset = self.asset(name)
                original = asset.read_bytes()
                items = load(asset)
                items[0]["name"] = "错误中文"
                save(asset, items)
                ok, detail = self.check_gate()
                self.assertFalse(ok, detail)
                self.assertIn(name, detail)
                self.assertIn("unreviewed", detail)
                asset.write_bytes(original)

    def test_removed_review_cannot_pass_even_when_asset_is_all_chinese(self):
        for name, config in PLACENAME_SETS.items():
            with self.subTest(name=name):
                path = self.dictionary / config["review_file"]
                self.write_json(path, self.reviews[name][1:])
                ok, detail = self.check_gate()
                self.assertFalse(ok, detail)
                self.assertIn("unreviewed", detail)
                self.write_json(path, self.reviews[name])

    def test_missing_review_file_fails_the_release_gates(self):
        for name, config in PLACENAME_SETS.items():
            with self.subTest(name=name):
                path = self.dictionary / config["review_file"]
                path.unlink()
                with self.assertRaises(FileNotFoundError):
                    self.check_gate()
                result = self.check_cli(name)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("FileNotFoundError", result.stderr)
                self.write_json(path, self.reviews[name])

    def test_changed_flags_fail_the_release_gate(self):
        for name in PLACENAME_SETS:
            with self.subTest(name=name):
                asset = self.asset(name)
                original = asset.read_bytes()
                items = load(asset)
                items[0]["flags"] ^= 1
                save(asset, items)
                ok, detail = self.check_gate()
                self.assertFalse(ok, detail)
                self.assertIn("metadata", detail)
                result = self.check_cli(name)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("metadata", result.stdout)
                asset.write_bytes(original)

    def test_reordered_records_fail_the_release_gate(self):
        for name in PLACENAME_SETS:
            with self.subTest(name=name):
                asset = self.asset(name)
                original = asset.read_bytes()
                items = load(asset)
                items[0], items[1] = items[1], items[0]
                save(asset, items)
                ok, detail = self.check_gate()
                self.assertFalse(ok, detail)
                self.assertIn("metadata", detail)
                result = self.check_cli(name)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("metadata", result.stdout)
                asset.write_bytes(original)

    def test_coordinate_change_within_review_tolerance_fails_the_release_gates(self):
        for name in PLACENAME_SETS:
            with self.subTest(name=name):
                asset = self.asset(name)
                original = asset.read_bytes()
                items = load(asset)
                items[0]["lat"] += 0.00001
                save(asset, items)
                ok, detail = self.check_gate()
                self.assertFalse(ok, detail)
                self.assertIn("metadata", detail)
                result = self.check_cli(name)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("metadata", result.stdout)
                asset.write_bytes(original)

    def test_null_metadata_cannot_disable_the_release_gates(self):
        metadata = json.loads(self.metadata.read_text(encoding="utf-8"))
        for name in PLACENAME_SETS:
            with self.subTest(name=name):
                self.write_json(self.metadata, dict(metadata, **{name: None}))
                with self.assertRaisesRegex(ValueError, "metadata"):
                    self.check_gate()
                result = self.check_cli(name)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("metadata", result.stderr)


if __name__ == "__main__":
    unittest.main()
