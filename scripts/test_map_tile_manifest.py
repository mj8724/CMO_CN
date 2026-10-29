#!/usr/bin/env python3
"""Storage round trips and portable rebuilds for split map manifests."""
import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

import map_tile_manifest as storage
import test_build_map_tile_pack as fixtures


class SplitManifestTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.TilePackBuilderTests('test_original_unnamed_peak_renders_elevation_once')
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.base = self.fixture.base
        self.path = self.base / 'split' / 'tile_regions.json'

    def write(self):
        storage.write_manifest(self.fixture.manifest, self.path)

    def test_roundtrip_preserves_every_logical_field_and_review(self):
        self.fixture.region['context_tiles'] = [dict(self.fixture.region['original_tiles'][0],
                                                   origin='cached', source_alpha_count=123)]
        before = copy.deepcopy(self.fixture.manifest)
        self.write()
        self.assertEqual(before, storage.load_manifest(self.path))
        sources = (self.path.parent / 'map_tiles/sources.jsonl').read_text(encoding='utf-8').splitlines()
        tiles = (self.path.parent / 'map_tiles/tiles.jsonl').read_text(encoding='utf-8').splitlines()
        self.assertEqual(4, len(sources))
        self.assertEqual(2, len(tiles))

    def test_writer_does_not_approve_an_edited_translation(self):
        self.fixture.region['translations'][1]['zh'] = '改名'
        self.write()
        manifest = storage.load_manifest(self.path)
        wire, report = self.fixture.builder.compile_manifest(
            manifest, self.fixture.original, fixtures.FONT, fixtures.BOLD_FONT)
        self.assertIsNone(wire)
        self.assertIn('region_fingerprint', json.dumps(report))

    def test_unknown_source_reference_is_rejected(self):
        self.write()
        region_path = self.path.parent / 'map_tiles/regions/fixture.json'
        region = json.loads(region_path.read_bytes())
        region['features'][0]['source_url'] = '@source/missing'
        region_path.write_text(json.dumps(region), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'unknown source'):
            storage.load_manifest(self.path)

    def test_duplicate_review_is_rejected(self):
        self.write()
        path = self.path.parent / 'map_tiles/reviews.jsonl'
        path.write_bytes(path.read_bytes() * 2)
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            storage.load_manifest(self.path)

    def test_fragment_cannot_escape_manifest_directory(self):
        self.write()
        index = json.loads(self.path.read_bytes())
        index['region_files'][0] = '../outside.json'
        self.path.write_text(json.dumps(index), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'relative'):
            storage.load_manifest(self.path)

    def test_legacy_manifest_is_still_read_without_rewriting_it(self):
        self.path.parent.mkdir()
        self.path.write_text(json.dumps(self.fixture.manifest), encoding='utf-8')
        self.assertEqual(self.fixture.manifest, storage.load_manifest(self.path))
        self.assertEqual([self.path.resolve()], storage.manifest_files(self.path))

    def test_split_cli_and_relocated_source_package_rebuild_identical_bytes(self):
        for tile in self.fixture.region['original_tiles']:
            original = self.fixture.original / tile['path']
            tile['path'] = f"3/{tile['x']}/{tile['y']}.png"
            destination = self.fixture.original / tile['path']
            destination.parent.mkdir(parents=True, exist_ok=True)
            original.rename(destination)
        self.fixture.approve()
        self.fixture.sign_render()
        expected, _ = self.fixture.compile()
        self.write()
        package = self.base / 'package'
        command = [sys.executable, '-B', str(fixtures.SCRIPT), '--manifest', str(self.path),
                   '--original-root', str(self.fixture.original), '--font', str(fixtures.FONT),
                   '--bold-font', str(fixtures.BOLD_FONT), '--output', str(self.base / 'actual.dat'),
                   '--report', str(self.base / 'report.json'), '--source-package-dir', str(package)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, (self.base / 'actual.dat').read_bytes())
        import verify_map_tile_source_package as verifier
        checked = verifier.verify(self.path, self.base / 'actual.dat', package)
        self.assertGreater(checked['files'], 8)
        recipe = json.loads((package / 'rebuild_inputs.json').read_bytes())
        relocated = self.base / 'relocated'
        package.rename(relocated)
        shutil.rmtree(self.path.parent)
        self.fixture.original.rename(self.base / 'original_unavailable')
        result = subprocess.run([sys.executable, '-B', *recipe['argv']], cwd=relocated,
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, (relocated / 'out/ChineseMapTiles.dat').read_bytes())


if __name__ == '__main__':
    unittest.main()
