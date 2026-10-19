#!/usr/bin/env python3
"""Offline regression tests; all writes stay in disposable build fixtures."""
from __future__ import annotations

import json
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import wwp

ROOT = Path(__file__).resolve().parent.parent


def write_fixture(path: Path, rows: list[tuple]) -> bytes:
    """Build the actual wire format independently of load/save field names."""
    raw = bytearray(struct.pack("<i", len(rows)))
    for name, lat, lon, flags in rows:
        text = name.encode("utf-8")
        size = len(text)
        while size >= 128:
            raw.append((size & 127) | 128)
            size >>= 7
        raw.append(size)
        raw.extend(text)
        raw.extend(struct.pack("<ffI", lat, lon, flags))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(raw)
    return bytes(raw)


class PlacenameTests(unittest.TestCase):
    def setUp(self) -> None:
        (ROOT / "build").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="placenames-", dir=ROOT / "build")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "source.wwp"
        self.output = self.base / "result.wwp"
        self.dictionary = self.base / "cities.json"
        self.dictionary.write_text(json.dumps({"Yichun": "宜春", "Tokyo": "东京"}),
                                   encoding="utf-8")
        self.review_dir = self.base / "reviewed"
        self.review_dir.mkdir()
        self.record = dict(name="Yichun", lat=47.721432, lon=128.875290,
                           zh="伊春", previous="宜春", source="https://www.yc.gov.cn/")
        self.write_review([self.record])

    def write_review(self, records: list[dict]) -> None:
        (self.review_dir / "review.json").write_text(
            json.dumps(records, ensure_ascii=False), encoding="utf-8")

    def run_script(self, script: str, *args: object) -> subprocess.CompletedProcess:
        return subprocess.run([sys.executable, "-X", "utf8", "-B",
                               str(ROOT / "scripts" / script), *map(str, args)],
                              cwd=self.base, capture_output=True, text=True,
                              encoding="utf-8", check=False)

    def build(self, source: Path | None = None, output: Path | None = None):
        return self.run_script("build_cities_placenames.py",
                               "--source", source or self.source,
                               "--output", output or self.output,
                               "--dictionary", self.dictionary,
                               "--review-dir", self.review_dir)

    def apply(self, *extra: str):
        return self.run_script("apply_placenames.py", "--game-dir", self.base / "game",
                               "--dictionary", self.dictionary,
                               "--review-dir", self.review_dir, *extra)

    def assert_success(self, process) -> None:
        self.assertEqual(0, process.returncode, process.stdout + process.stderr)

    def example_rows(self):
        return [("Yichun", 47.721432, 128.875290, 1),
                ("Yichun", 27.833330, 114.400002, 2),
                ("宜春", 47.721432, 128.875290, 3),
                ("宜春", 27.833330, 114.400002, 4),
                ("伊春", 47.721432, 128.875290, 5),
                ("我的自定义标签", 47.721432, 128.875290, 6),
                ("Unreviewed Custom City", 47.721432, 128.875290, 7)]

    def test_wire_coordinates_and_float32_roundtrip(self):
        raw = write_fixture(self.source, [("城市" * 40, 47.721432, 128.875290, 0xffffffff),
                                         ("Equator", -0.0, -179.99998, 0)])
        rows = wwp.load(self.source)
        self.assertAlmostEqual(47.721432, rows[0]["lat"], places=5)
        self.assertAlmostEqual(128.875290, rows[0]["lon"], places=5)
        wwp.save(self.output, rows)
        self.assertEqual(raw, self.output.read_bytes())

    def test_dump_reports_latitude_before_longitude(self):
        write_fixture(self.source, [("Point", 12.5, 123.25, 7)])
        result = self.run_script("wwp.py", "dump", self.source)
        self.assert_success(result)
        self.assertIn("Point\t12.5000\t123.2500\t7", result.stdout)

    def test_build_resolves_alias_and_coordinate_without_global_replacement(self):
        original = write_fixture(self.source, self.example_rows())
        result = self.build()
        self.assert_success(result)
        self.assertEqual(["伊春", "宜春", "伊春", "宜春", "伊春", "我的自定义标签",
                          "Unreviewed Custom City"],
                         [row["name"] for row in wwp.load(self.output)])
        self.assertEqual(original, self.source.read_bytes())
        self.assertFalse(Path(str(self.source) + ".orig").exists())
        before, after = wwp.load(self.source), wwp.load(self.output)
        self.assertEqual([(r["lat"], r["lon"], r["flags"]) for r in before],
                         [(r["lat"], r["lon"], r["flags"]) for r in after])

    def test_build_is_idempotent_from_existing_chinese_output(self):
        write_fixture(self.source, self.example_rows())
        self.assert_success(self.build())
        first = self.output.read_bytes()
        self.assert_success(self.build(self.output, self.output))
        self.assertEqual(first, self.output.read_bytes())

    def test_build_creates_explicit_output_parent_directory(self):
        write_fixture(self.source, self.example_rows())
        target = self.base / "new-build/placenames/Cities.wwp"
        self.assert_success(self.build(output=target))
        self.assertEqual("伊春", wwp.load(target)[0]["name"])

    def test_coordinate_match_is_small_and_float32_tolerant(self):
        write_fixture(self.source, [("宜春", 47.721432 + 0.00005, 128.875290, 0),
                                    ("宜春", 47.721432 + 0.001, 128.875290, 0)])
        self.assert_success(self.build())
        self.assertEqual(["伊春", "宜春"], [r["name"] for r in wwp.load(self.output)])

    def test_apply_and_build_have_identical_city_semantics(self):
        write_fixture(self.source, self.example_rows())
        target = self.base / "game/WW/PlacenameSets/Cities/0000_0000.wwp"
        original = write_fixture(target, self.example_rows())
        self.assert_success(self.build())
        self.assert_success(self.apply())
        self.assertEqual(self.output.read_bytes(), target.read_bytes())
        self.assertEqual(original, Path(str(target) + ".orig").read_bytes())
        self.assert_success(self.apply())
        self.assertEqual(self.output.read_bytes(), target.read_bytes())
        self.assertEqual(original, Path(str(target) + ".orig").read_bytes())

    def test_verify_is_read_only_even_when_old_chinese_needs_correction(self):
        target = self.base / "game/WW/PlacenameSets/Cities/0000_0000.wwp"
        original = write_fixture(target, [("宜春", 47.721432, 128.875290, 11)])
        self.assert_success(self.apply("--verify"))
        self.assertEqual(original, target.read_bytes())
        self.assertFalse(Path(str(target) + ".orig").exists())

    def test_revert_uses_selected_game_directory(self):
        target = self.base / "game/WW/PlacenameSets/Cities/0000_0000.wwp"
        original = write_fixture(target, self.example_rows())
        self.assert_success(self.apply())
        self.assert_success(self.apply("--revert"))
        self.assertEqual(original, target.read_bytes())

    def test_duplicate_reviewed_record_is_rejected_before_output(self):
        write_fixture(self.source, self.example_rows())
        self.write_review([self.record, self.record])
        result = self.build()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("duplicate or conflicting", result.stderr.lower())
        self.assertFalse(self.output.exists())

    def test_alias_overlap_with_conflicting_result_is_rejected(self):
        write_fixture(self.source, self.example_rows())
        conflicting = dict(self.record, name="Other", zh="错误", lat=47.721440)
        self.write_review([self.record, conflicting])
        result = self.build()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("duplicate or conflicting", result.stderr.lower())
        self.assertFalse(self.output.exists())

    def test_invalid_review_coordinates_are_rejected(self):
        write_fixture(self.source, self.example_rows())
        for lat in (91, float("nan"), True):
            with self.subTest(lat=lat):
                self.write_review([dict(self.record, lat=lat)])
                result = self.build()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("latitude", result.stderr.lower())

    def test_generator_requires_explicit_output(self):
        result = self.run_script("build_cities_placenames.py", "--source", self.source)
        self.assertEqual(2, result.returncode)
        self.assertIn("--output", result.stderr)


if __name__ == "__main__":
    unittest.main()
