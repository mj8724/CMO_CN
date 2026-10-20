#!/usr/bin/env python3
"""Windows integration tests using synthetic WWP/PNG files, never a game install."""
from __future__ import annotations

import base64
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

from test_placenames import write_fixture

ROOT = Path(__file__).resolve().parent.parent
PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/lx8AAAAASUVORK5CYII=")


@unittest.skipUnless(os.name == "nt", "label renderer requires Windows/.NET Framework")
class PlacenameCacheTests(unittest.TestCase):
    def setUp(self):
        (ROOT / "build").mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="placename-cache-", dir=ROOT / "build")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.game = self.base / "game"
        self.cities = self.game / "WW/PlacenameSets/Cities/0000_0000.wwp"
        self.original = write_fixture(self.cities, [("Yichun", 47.721432, 128.875290, 1)])
        write_fixture(self.game / "WW/PlacenameSets/Synoptic/0000_0000.wwp",
                      [("Country fixture", 0.0, 0.0, 1)])
        self.dictionary = self.base / "cities.json"
        self.dictionary.write_text(json.dumps({"Yichun": "宜春"}), encoding="utf-8")
        self.review_file = self.base / "reviewed/cities.json"
        self.synoptic_review_file = self.base / "reviewed/synoptic.json"
        self.review_file.parent.mkdir(parents=True)
        self.synoptic_review_file.write_text("[]", encoding="utf-8")
        self.review_file.write_text(json.dumps([
            dict(name="Yichun", lat=47.721432, lon=128.875290, previous="宜春", zh="伊春",
                 source="https://www.yc.gov.cn/")]), encoding="utf-8")
        self.cache = self.game / "WW/Cache/Stamen Terrain - Labels"
        self.tile = self.cache / "7/109/44.png"
        self.tile.parent.mkdir(parents=True)
        self.tile.write_bytes(PNG)
        os.utime(self.tile, (946684800, 946684800))
        self.backup = self.game / "WW/Cache/Stamen Terrain - Labels.orig/7/109/44.png"
        self.terrain = self.game / "WW/Cache/Stamen Terrain/7/109/44.png"
        self.terrain.parent.mkdir(parents=True)
        self.terrain.write_bytes(b"terrain sentinel - must never be modified")

    def apply(self, *args):
        return subprocess.run([sys.executable, "-X", "utf8", "-B",
                               str(ROOT / "scripts/apply_placenames.py"),
                               "--game-dir", str(self.game), "--dictionary", str(self.dictionary),
                               "--review-file", str(self.review_file),
                               "--synoptic-review-file", str(self.synoptic_review_file), *args],
                              capture_output=True, text=True, encoding="utf-8", check=False)

    def success(self, process):
        self.assertEqual(0, process.returncode, process.stdout + process.stderr)

    def assert_rendered(self, tile: Path):
        self.assertEqual((256, 256), struct.unpack(">II", tile.read_bytes()[16:24]))

    def assert_terrain_unchanged(self):
        self.assertEqual(b"terrain sentinel - must never be modified", self.terrain.read_bytes())

    def test_changed_names_refresh_tiles_and_explicit_refresh_repairs_noop(self):
        self.success(self.apply())
        self.assert_rendered(self.tile)
        self.assertEqual(PNG, self.backup.read_bytes())
        self.assertEqual(self.original, Path(str(self.cities) + ".orig").read_bytes())
        self.assert_terrain_unchanged()
        current = self.cities.read_bytes()
        os.utime(self.tile, (946684800, 946684800))
        self.success(self.apply())
        self.assertEqual(946684800, self.tile.stat().st_mtime)
        self.success(self.apply("--refresh-tiles"))
        self.assertGreater(self.tile.stat().st_mtime, 946684800)
        self.assertEqual(current, self.cities.read_bytes())
        self.assertEqual(PNG, self.backup.read_bytes())
        self.assert_terrain_unchanged()

    def test_verify_with_refresh_request_does_not_write_or_backup(self):
        self.success(self.apply("--verify", "--refresh-tiles"))
        self.assertEqual(self.original, self.cities.read_bytes())
        self.assertEqual(PNG, self.tile.read_bytes())
        self.assertEqual(946684800, self.tile.stat().st_mtime)
        self.assertFalse(self.backup.exists())
        self.assertFalse(Path(str(self.cities) + ".orig").exists())
        self.assert_terrain_unchanged()

    def test_revert_renders_restored_names_and_new_tiles_without_consuming_backup(self):
        self.success(self.apply())
        localized = self.tile.read_bytes()
        new_tile = self.cache / "7/110/44.png"
        new_tile.parent.mkdir(parents=True)
        new_tile.write_bytes(PNG)
        os.utime(new_tile, (946684800, 946684800))
        self.success(self.apply("--revert"))
        self.assertEqual(self.original, self.cities.read_bytes())
        self.assert_rendered(self.tile)
        self.assert_rendered(new_tile)
        self.assertNotEqual(localized, self.tile.read_bytes())
        self.assertEqual(PNG, self.backup.read_bytes())
        self.assertFalse((self.backup.parent / "../110/44.png").resolve().exists())
        self.assert_terrain_unchanged()

    def test_missing_label_cache_is_skipped(self):
        self.tile.unlink()
        self.tile.parent.rmdir()
        self.tile.parent.parent.rmdir()
        self.cache.rmdir()
        self.success(self.apply("--refresh-tiles"))
        self.assertFalse(self.cache.exists())
        self.assertFalse(self.backup.exists())
        self.assert_terrain_unchanged()


if __name__ == "__main__":
    unittest.main()
