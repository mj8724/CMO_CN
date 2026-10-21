#!/usr/bin/env python3
"""Synthetic fixtures exercise the reviewed tile builder without game files."""
from __future__ import annotations

import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

from PIL import Image, ImageDraw
from global_map_catalog import record_fingerprint

SCRIPT = Path(__file__).with_name("build_map_tile_pack.py")
FONT = Path("C:/Windows/Fonts/msyh.ttc")
BOLD_FONT = Path("C:/Windows/Fonts/msyhbd.ttc")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class TilePackBuilderTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "reviewed tile builder is not implemented")
        spec = importlib.util.spec_from_file_location("tile_builder", SCRIPT)
        self.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.builder)
        self.temp = tempfile.TemporaryDirectory(prefix="tile-pack-builder-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.original = self.base / "original"
        self.original.mkdir()
        atlas = Image.new("RGBA", (512, 256))
        draw = ImageDraw.Draw(atlas)
        draw.rectangle((80, 20, 119, 39), fill="white")
        draw.rectangle((240, 80, 271, 99), fill="white")
        draw.ellipse((380, 100, 389, 109), fill="white")
        tiles = []
        for x in range(2):
            path = self.original / f"{x}.png"
            atlas.crop((x * 256, 0, (x + 1) * 256, 256)).save(path)
            tiles.append(dict(x=x, y=0, path=path.name, sha256=sha(path),
                              source_url="https://example.com/source"))
        features = [dict(id="peak", kind="peak", original="Original Peak",
                         original_elevation_m="8848", original_bbox=[80, 20, 120, 40],
                         source_url="https://example.com/peak"),
                    dict(id="city", kind="city", original="Original City",
                         original_bbox=[240, 80, 272, 100],
                         source_url="https://example.com/city")]
        self.region = dict(id="fixture", z=3, x=0, y=0, width_tiles=2, height_tiles=1,
                           original_tiles=tiles, context_tiles=[], features=features,
                           translations=[dict(id=f["id"], zh=zh, status="approved",
                                              source="https://example.com/translation",
                                              review_notes="Synthetic translation fixture",
                                              record_sha256=record_fingerprint(f))
                                         for f, zh in zip(features, ["汉峰", "城市"])],
                           layouts=[dict(id="peak", x=100, y=80, font_size=18,
                                         font_style="regular"),
                                    dict(id="city", x=256, y=150, font_size=22,
                                         font_style="bold", lines=["城", "市"])],
                           symbols=[dict(id="dot", feature_id="city", kind="circle",
                                         x=385, y=105, size=9, outline_width=1,
                                         original_bbox=[380, 100, 390, 110])])
        self.approve()
        self.manifest = dict(schema_version=1,
                             scope=dict(type="cached_regions", global_complete=False),
                             font_sha256=sha(FONT), bold_font_sha256=sha(BOLD_FONT),
                             regions=[self.region])
        self.sign_render()

    def approve(self):
        self.region["review"] = dict(status="approved", source_verified=True,
                                     render_verified=True, reviewer="Fixture reviewer",
                                     notes="Synthetic fixture only",
                                     record_sha256=record_fingerprint(
                                         {k: v for k, v in self.region.items() if k != "review"}))

    def compile(self, preview=False):
        return self.builder.compile_manifest(self.manifest, self.original, FONT, BOLD_FONT,
                                             self.base / "preview" if preview else None)

    def sign_render(self):
        # Synthetic fixture approval only; real visual evidence is signed by a
        # separate human review and never by the production builder.
        _, report = self.compile()
        self.region["review"]["render_sha256"] = report["regions"][0]["atlas_rgba_sha256"]

    def rejected(self, reason):
        wire, report = self.compile()
        self.assertTrue(wire is None, "expected rejected manifest to produce no pack")
        self.assertFalse(report["ready"])
        self.assertIn(reason, json.dumps(report, ensure_ascii=False))
        return report

    def elevation_only_peak(self):
        feature = self.region["features"][0]
        feature.update(original="8848 m", elevation_only=True)
        self.region["translations"][0].update(zh="8848米", record_sha256=record_fingerprint(feature))
        self.approve()
        self.sign_render()

    def test_original_unnamed_peak_renders_elevation_once(self):
        self.elevation_only_peak()
        wire, report = self.compile()
        self.assertIsNotNone(wire)
        self.assertTrue(report["ready"], report["gaps"])
        label = report["regions"][0]["labels"][0]
        self.assertEqual(["8848米"], label["lines"])
        self.assertEqual("8848米", label["elevation"])
        self.assertEqual(1, len(label["line_bounds"]))
        self.assertTrue(label["elevation_only"])

    def test_elevation_only_requires_exact_source_and_no_invented_name(self):
        original = copy.deepcopy(self.region)
        for change in ("invented_name", "wrong_source_number", "named_source", "split_height", "non_boolean", "city"):
            with self.subTest(change=change):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                self.elevation_only_peak()
                feature, translation = self.region["features"][0], self.region["translations"][0]
                if change == "invented_name":
                    translation["zh"] = "无名峰"
                elif change == "wrong_source_number":
                    feature["original"] = "8849 m"
                elif change == "named_source":
                    feature["original"] = "Original Peak 8848 m"
                elif change == "split_height":
                    self.region["layouts"][0]["lines"] = ["8848", "米"]
                elif change == "non_boolean":
                    feature["elevation_only"] = 1
                else:
                    feature["kind"] = "city"
                translation["record_sha256"] = record_fingerprint(feature)
                self.approve()
                self.sign_render()
                self.rejected("elevation_only")

    def test_elevation_only_flag_change_invalidates_old_approval(self):
        self.elevation_only_peak()
        self.region["features"][0]["elevation_only"] = False
        self.rejected("region_fingerprint")

    def unlabeled_symbol(self):
        self.region["features"] = []
        self.region["translations"] = []
        self.region["layouts"] = []
        symbol = self.region["symbols"][0]
        symbol.pop("feature_id", None)
        symbol.update(unlabeled=True, source_url="https://example.com/original-dot",
                      review_notes="Original source contains this dot and no text")
        for tile in self.region["original_tiles"]:
            image = Image.new("RGBA", (256, 256))
            if tile["x"] == 1:
                ImageDraw.Draw(image).ellipse((124, 100, 133, 109), fill="white")
            path = self.original / tile["path"]
            image.save(path)
            tile["sha256"] = sha(path)
        self.approve()
        self.sign_render()

    def test_original_unlabeled_symbol_has_geometry_without_invented_name(self):
        self.unlabeled_symbol()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertIsNotNone(wire)
        self.assertEqual(0, report["label_count"])
        self.assertEqual(1, report["symbol_count"])
        symbol = report["regions"][0]["symbols"][0]
        self.assertIsNone(symbol["feature_id"])
        self.assertTrue(symbol["unlabeled"])
        self.assertEqual([[3, 1, 0]], symbol["tiles"])
        self.assertEqual(0, report["regions"][0]["source_alpha"]["unaccounted_pixels"])

    def test_unlabeled_symbol_requires_explicit_independent_source_review(self):
        self.unlabeled_symbol()
        original = copy.deepcopy(self.region)
        for change in ("non_boolean", "named", "source", "notes"):
            with self.subTest(change=change):
                self.region = copy.deepcopy(original)
                self.manifest["regions"] = [self.region]
                symbol = self.region["symbols"][0]
                if change == "non_boolean":
                    symbol["unlabeled"] = 1
                elif change == "named":
                    symbol["feature_id"] = "city"
                elif change == "source":
                    symbol["source_url"] = ""
                else:
                    symbol["review_notes"] = ""
                self.approve()
                self.sign_render()
                self.rejected("unlabeled_symbol")

    def test_binary_payload_and_cross_tile_reassembly(self):
        wire, report = self.compile(preview=True)
        self.assertTrue(report["ready"], report)
        self.assertFalse(report["global_complete"])
        self.assertEqual(b"CMOTIL01", wire[:8])
        self.assertEqual(64, wire[8])
        self.assertEqual(hashlib.sha256(wire[73:]).hexdigest(), wire[9:73].decode())
        stream = io.BytesIO(wire[73:])
        self.assertEqual(2, struct.unpack("<i", stream.read(4))[0])
        joined = Image.new("RGBA", (512, 256))
        counts = []
        for _ in range(2):
            z, x, y, labels, length = struct.unpack("<iiiii", stream.read(20))
            self.assertEqual((3, 0), (z, y))
            tile = Image.open(io.BytesIO(stream.read(length))).convert("RGBA")
            joined.paste(tile, (x * 256, 0))
            counts.append(labels)
        self.assertEqual(b"", stream.read())
        self.assertEqual([2, 1], counts)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.assertEqual(preview.tobytes(), joined.tobytes())
        region = report["regions"][0]
        self.assertEqual("8848米", region["labels"][0]["elevation"])
        self.assertEqual(["汉峰", "8848米"], region["labels"][0]["lines"])
        self.assertEqual(1, len(region["cross_tile_instances"]))
        self.assertEqual(0, region["source_alpha"]["unaccounted_pixels"])

    def test_source_hash_change(self):
        path = self.original / "0.png"
        Image.new("RGBA", (256, 256)).save(path)
        self.rejected("source_sha256")

    def test_unaccounted_source_alpha(self):
        path = self.original / "0.png"
        with Image.open(path) as source:
            image = source.convert("RGBA")
        image.putpixel((10, 10), (255, 255, 255, 1))
        image.save(path)
        self.region["original_tiles"][0]["sha256"] = sha(path)
        self.approve()
        report = self.rejected("unaccounted_alpha")
        self.assertEqual(1, report["regions"][0]["source_alpha"]["unaccounted_pixels"])

    def test_missing_and_duplicate_record_sets(self):
        for field in ("features", "translations", "layouts"):
            for duplicate in (False, True):
                with self.subTest(field=field, duplicate=duplicate):
                    saved = copy.deepcopy(self.region[field])
                    self.region[field] = saved + [saved[0]] if duplicate else saved[1:]
                    self.approve()
                    self.rejected("record_set")
                    self.region[field] = saved

    def test_non_chinese_and_stale_translation(self):
        self.region["translations"][0]["zh"] = "English"
        self.region["translations"][0]["record_sha256"] = "0" * 64
        self.region["translations"][1]["record_sha256"] = "0" * 64
        self.approve()
        report = self.rejected("non_chinese")
        self.assertIn("translation_fingerprint", json.dumps(report))
        self.assertEqual(2, sum(g["code"] == "translation_fingerprint" for g in report["gaps"]))

    def test_unapproved_translation_is_not_hidden_by_approved_region(self):
        self.region["translations"][0]["status"] = "draft"
        self.approve()
        self.rejected("translation_review")

    def test_nonfinite_layout_is_structure_error(self):
        self.region["layouts"][0]["x"] = float("nan")
        self.approve()
        with self.assertRaises(ValueError):
            self.compile()

    def test_triangle_has_real_source_and_does_not_add_label_count(self):
        self.region["symbols"][0]["kind"] = "triangle"
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)
        self.assertEqual("triangle", report["regions"][0]["symbols"][0]["kind"])
        self.assertEqual([2, 1], [t["label_count"] for t in report["regions"][0]["tiles"]])

    def test_preview_cannot_overwrite_later_region_source(self):
        later = copy.deepcopy(self.region)
        later.update(id="later", x=2)
        for tile in later["original_tiles"]:
            tile["x"] += 2
        path = self.original / "fixture.zh.png"
        path.write_bytes((self.original / "0.png").read_bytes())
        original_bytes = path.read_bytes()
        later["original_tiles"][0].update(path=path.name, sha256=sha(path))
        later["review"]["record_sha256"] = record_fingerprint({k: v for k, v in later.items() if k != "review"})
        self.manifest["regions"].append(later)
        with self.assertRaises(ValueError):
            self.builder.compile_manifest(self.manifest, self.original, FONT, BOLD_FONT, self.original)
        self.assertEqual(original_bytes, path.read_bytes())

    def test_unapproved_and_changed_review(self):
        self.region["review"]["status"] = "draft"
        self.rejected("region_review")
        self.approve()
        self.region["layouts"][0]["x"] += 1
        self.rejected("region_fingerprint")

    def test_peak_elevation_and_lines_cannot_omit_text(self):
        self.region["layouts"][0]["lines"] = ["汉", "8848米"]
        self.approve()
        self.rejected("layout_lines")

    def test_missing_font_character(self):
        self.region["translations"][0]["zh"] = "汉\U000323af"
        self.approve()
        self.rejected("missing_glyph")

    def test_font_sha_change(self):
        self.manifest["font_sha256"] = "0" * 64
        self.rejected("font_sha256")

    def test_duplicate_tiles_and_invalid_bounds(self):
        self.region["original_tiles"].append(self.region["original_tiles"][0].copy())
        self.approve()
        self.rejected("tile_set")
        self.region["original_tiles"].pop()
        self.region["x"] = 8
        with self.assertRaises(ValueError):
            self.compile()

    def test_overlapping_regions(self):
        other = copy.deepcopy(self.region)
        other["id"] = "other"
        self.manifest["regions"].append(other)
        self.rejected("overlapping_tile")

    def test_region_boundary_clipping(self):
        self.region["layouts"][0]["x"] = 0
        self.approve()
        self.rejected("clipped")

    def test_strong_pixel_collision(self):
        self.region["layouts"][1].update(x=100, y=80)
        self.approve()
        self.rejected("collision")

    def test_source_cross_region_needs_context(self):
        self.region["features"][0]["original_bbox"][0] = -10
        self.region["translations"][0]["record_sha256"] = record_fingerprint(
            self.region["features"][0])
        self.approve()
        self.rejected("context_incomplete")

    def test_empty_original_bbox_and_unknown_symbol_feature(self):
        self.region["features"][0]["original_bbox"] = [0, 0, 10, 10]
        self.region["symbols"][0]["feature_id"] = "unknown"
        self.approve()
        report = self.rejected("empty_original_bbox")
        self.assertIn("symbol_feature", json.dumps(report))

    def test_path_escape_and_source_size(self):
        self.region["original_tiles"][0]["path"] = "../escape.png"
        self.approve()
        self.rejected("source_path")
        self.region["original_tiles"][0]["path"] = "0.png"
        Image.new("RGBA", (32, 32)).save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = sha(self.original / "0.png")
        self.approve()
        self.rejected("source_dimensions")

    def test_draft_preview_is_not_pack(self):
        self.region["review"]["render_verified"] = False
        self.region["review"].pop("render_sha256")
        wire, report = self.compile(preview=True)
        self.assertIsNone(wire)
        self.assertFalse(report["ready"])
        self.assertTrue((self.base / "preview/fixture.zh.png").is_file())
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.assertEqual(hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest(),
                             report["regions"][0]["atlas_rgba_sha256"])

    def test_cli_failure_preserves_previous_output(self):
        self.region["review"]["status"] = "draft"
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        output = self.base / "pack.dat"
        output.write_bytes(b"previous")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(output),
                                 "--report", str(self.base / "report.json")],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"previous", output.read_bytes())
        self.assertFalse(json.loads((self.base / "report.json").read_text(encoding="utf-8"))["ready"])

    def test_cli_preview_cannot_overwrite_manifest(self):
        manifest = self.base / "fixture.zh.png"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        previous = manifest.read_bytes()
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(self.base / "pack.dat"),
                                 "--report", str(self.base / "report.json"),
                                 "--preview-dir", str(self.base)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(previous, manifest.read_bytes())

    def test_scope_cannot_use_numeric_false(self):
        self.manifest["scope"]["global_complete"] = 0
        with self.assertRaises(ValueError):
            self.compile()

    def test_approved_review_requires_render_pixel_digest(self):
        self.region["review"].pop("render_sha256", None)
        self.rejected("render_fingerprint")

    def test_font_swap_invalidates_visual_review_until_resigned(self):
        _, before = self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()
        old_review = copy.deepcopy(self.region["review"])
        self.manifest.update(font_sha256=sha(BOLD_FONT), bold_font_sha256=sha(FONT))
        wire, report = self.builder.compile_manifest(self.manifest, self.original, BOLD_FONT, FONT)
        self.assertIsNone(wire)
        self.assertFalse(report["ready"])
        self.assertIn("render_fingerprint", json.dumps(report))
        self.assertEqual(old_review, self.region["review"])
        actual = report["regions"][0]["atlas_rgba_sha256"]
        self.assertNotEqual(old_review["render_sha256"], actual)
        self.region["review"]["render_sha256"] = actual
        wire, report = self.builder.compile_manifest(self.manifest, self.original, BOLD_FONT, FONT)
        self.assertIsNotNone(wire, report)

    def test_renderer_change_invalidates_visual_review_until_resigned(self):
        self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()
        self.builder.SCALE = 4
        report = self.rejected("render_fingerprint")
        self.region["review"]["render_sha256"] = report["regions"][0]["atlas_rgba_sha256"]
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)

    def test_png_encoding_change_preserves_pixel_approval(self):
        _, before = self.compile(preview=True)
        with Image.open(self.base / "preview/fixture.zh.png") as preview:
            self.region["review"]["render_sha256"] = hashlib.sha256(preview.convert("RGBA").tobytes()).hexdigest()

        def different_png_encoding(image):
            stream = io.BytesIO()
            image.save(stream, format="PNG", compress_level=0)
            return stream.getvalue()

        self.builder.png_bytes = different_png_encoding
        wire, report = self.compile()
        self.assertIsNotNone(wire, report)
        self.assertEqual(self.region["review"]["render_sha256"], report["regions"][0]["atlas_rgba_sha256"])
        self.assertNotEqual(before["regions"][0]["atlas_png_sha256"], report["regions"][0]["atlas_png_sha256"])

    def test_case_alias_rejected_before_any_preview_write(self):
        second = copy.deepcopy(self.region)
        second.update(id="FIXTURE", x=2)
        for tile in second["original_tiles"]:
            tile["x"] += 2
        second["review"]["record_sha256"] = record_fingerprint({k: v for k, v in second.items() if k != "review"})
        self.manifest["regions"].append(second)
        preview = self.base / "preview"
        preview.mkdir()
        evidence = preview / "fixture.zh.png"
        evidence.write_bytes(b"existing review evidence")
        with self.assertRaises(ValueError):
            self.compile(preview=True)
        self.assertEqual(b"existing review evidence", evidence.read_bytes())
        self.assertEqual(1, len(list(preview.iterdir())))

    def test_reserved_device_ids_rejected_before_preview_write(self):
        for identity in ("NUL", "con", "COM1", "LPT9", "AUX.notes", "PRN"):
            with self.subTest(identity=identity):
                self.region["id"] = identity
                self.approve()
                with self.assertRaises(ValueError):
                    self.compile(preview=True)
                self.assertFalse((self.base / "preview").exists())

    def test_cli_case_alias_preserves_existing_preview(self):
        second = copy.deepcopy(self.region)
        second["id"] = "FIXTURE"
        self.manifest["regions"].append(second)
        preview = self.base / "preview"
        preview.mkdir()
        evidence = preview / "fixture.zh.png"
        evidence.write_bytes(b"existing review evidence")
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(self.base / "pack.dat"),
                                 "--report", str(self.base / "report.json"),
                                 "--preview-dir", str(preview)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"existing review evidence", evidence.read_bytes())
        self.assertEqual(1, len(list(preview.iterdir())))

    def legacy_rule(self):
        from wwp import load
        old = load(SCRIPT.parent.parent / "assets/placenames/Cities.wwp")[0]
        return dict(feature_id="city", name=old["name"], lat=old["lat"], lon=old["lon"],
                    level=1, source="https://example.com/legacy-identity", review_notes="Exact synthetic identity review")

    def test_reviewed_legacy_rules_emit_v2_and_are_reported(self):
        old_wire, _ = self.compile()
        rule = self.legacy_rule()
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(b"CMOTIL02", wire[:8])
        self.assertEqual(1, len(report["legacy_suppressions"]))
        self.assertEqual(rule["name"], report["legacy_suppressions"][0]["name"])
        payload = wire[73:]
        self.assertEqual(hashlib.sha256(payload).hexdigest(), wire[9:73].decode())
        self.assertEqual(old_wire[73:], payload[:len(old_wire) - 73])
        stream = io.BytesIO(payload[len(old_wire) - 73:])
        self.assertEqual((1, 3), struct.unpack("<ii", stream.read(8)))
        from wwp import _read_7bit_length
        remainder = stream.read()
        length, offset = _read_7bit_length(remainder, 0)
        self.assertEqual(rule["name"], remainder[offset:offset + length].decode("utf-8"))
        self.assertEqual(struct.pack("<ffi", rule["lat"], rule["lon"], rule["level"]),
                         remainder[offset + length:])
        self.assertEqual([3, 5, 3], report["legacy_suppressions"][0]["legacy_tile"])

    def test_unverified_or_unknown_legacy_rule_cannot_suppress(self):
        for field, value in (("name", "并不存在的旧地名"), ("lat", 89),
                             ("feature_id", "absent"), ("review_notes", ""),
                             ("source", "local-only"), ("level", 0)):
            with self.subTest(field=field):
                rule = self.legacy_rule()
                rule[field] = value
                self.region["legacy_suppressions"] = [rule]
                self.approve()
                self.sign_render()
                self.rejected("legacy_suppression")

    def test_legacy_rule_changes_require_fresh_region_review(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.rejected("region_fingerprint")

    def test_empty_rules_preserve_v1_bytes_without_loading_wwp(self):
        old_wire, _ = self.compile()

        def forbidden_load():
            self.fail("CMOTIL01 must not depend on canonical WWP files")

        self.builder.canonical_legacy_points = forbidden_load
        self.region["legacy_suppressions"] = []
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(old_wire, wire)

    def test_rules_deduplicate_across_reviewed_placements(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.approve()
        self.sign_render()
        second = copy.deepcopy(self.region)
        second.update(id="second-placement", x=2)
        for tile in second["original_tiles"]:
            tile["x"] += 2
        second["review"]["record_sha256"] = record_fingerprint(
            {key: value for key, value in second.items() if key != "review"})
        self.manifest["regions"].append(second)
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(1, len(report["legacy_suppressions"]))
        self.assertEqual(2, len(report["legacy_suppressions"][0]["references"]))
        stream = io.BytesIO(wire[73:])
        for _ in range(struct.unpack("<i", stream.read(4))[0]):
            *_, length = struct.unpack("<iiiii", stream.read(20))
            stream.seek(length, io.SEEK_CUR)
        self.assertEqual(1, struct.unpack("<i", stream.read(4))[0])

    def test_rule_wire_order_is_deterministic(self):
        from wwp import load
        other = load(SCRIPT.parent.parent / "assets/placenames/Cities.wwp")[1]
        second = dict(self.legacy_rule(), name=other["name"], lat=other["lat"], lon=other["lon"])
        self.region["legacy_suppressions"] = [self.legacy_rule(), second]
        self.approve()
        self.sign_render()
        first_wire, report = self.compile()
        self.assertTrue(first_wire is not None, report["gaps"])
        self.region["legacy_suppressions"].reverse()
        self.approve()
        self.sign_render()
        second_wire, _ = self.compile()
        self.assertEqual(first_wire, second_wire)

    def test_one_float32_bit_or_nonfinite_coordinate_is_rejected(self):
        rule = self.legacy_rule()
        bits = struct.unpack("<I", struct.pack("<f", rule["lat"]))[0]
        for latitude in (struct.unpack("<f", struct.pack("<I", bits + 1))[0],
                         float("nan"), float("inf"), -float("inf")):
            with self.subTest(latitude=latitude):
                self.region["legacy_suppressions"] = [dict(rule, lat=latitude)]
                self.approve()
                self.sign_render()
                self.rejected("legacy_suppression")

    def test_negative_zero_does_not_match_positive_zero(self):
        rule = dict(self.legacy_rule(), lat=0.0)
        key = self.builder.legacy_key(0, rule["name"], rule["lat"], rule["lon"], rule["level"])[1:]
        self.builder.canonical_legacy_points = lambda: ({key}, [])
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        rule["lat"] = -0.0
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_rule_requires_actual_chinese_label_output(self):
        self.region["legacy_suppressions"] = [self.legacy_rule()]
        self.region["layouts"] = [self.region["layouts"][0]]
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_noncity_rule_requires_synoptic_level_zero(self):
        from wwp import load
        old = load(SCRIPT.parent.parent / "assets/placenames/Synoptic.wwp")[0]
        rule = dict(self.legacy_rule(), feature_id="peak", level=0,
                    name=old["name"], lat=old["lat"], lon=old["lon"])
        self.region["legacy_suppressions"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        rule["level"] = 1
        self.approve()
        self.sign_render()
        self.rejected("legacy_suppression")

    def test_invalid_rule_cli_preserves_previous_output(self):
        self.region["legacy_suppressions"] = [dict(self.legacy_rule(), name="并不存在的旧地名")]
        self.approve()
        self.sign_render()
        manifest = self.base / "manifest.json"
        manifest.write_text(json.dumps(self.manifest, ensure_ascii=False), encoding="utf-8")
        output = self.base / "pack.dat"
        output.write_bytes(b"previous")
        result = subprocess.run([sys.executable, "-B", str(SCRIPT), "--manifest", str(manifest),
                                 "--original-root", str(self.original), "--font", str(FONT),
                                 "--bold-font", str(BOLD_FONT), "--output", str(output),
                                 "--report", str(self.base / "report.json")],
                                capture_output=True, text=True)
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertEqual(b"previous", output.read_bytes())

    def test_complete_66_tile_region_is_supported(self):
        self.region.update(z=4, width_tiles=11, height_tiles=6)
        empty = self.original / "empty.png"
        Image.new("RGBA", (256, 256)).save(empty)
        original = copy.deepcopy(self.region["original_tiles"])
        self.region["original_tiles"] = [
            original[x] if y == 0 and x < 2 else
            dict(x=x, y=y, path=empty.name, sha256=sha(empty), source_url="https://example.com/empty")
            for y in range(6) for x in range(11)]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertIsNotNone(wire, report["gaps"])
        self.assertEqual(66, report["tile_count"])

    def test_expanded_limits_keep_width_grid_and_total_bounds(self):
        self.assertEqual((256, 16384), (self.builder.MAX_REGION_TILES, self.builder.MAX_TOTAL_TILES))
        self.region.update(z=10, width_tiles=17)
        with self.assertRaisesRegex(ValueError, "width_tiles"):
            self.compile()
        self.region.update(width_tiles=2, x=1023)
        with self.assertRaisesRegex(ValueError, "tile bounds"):
            self.compile()
        self.manifest["regions"] = [dict(self.region, id=f"region-{index}",
                                         x=(index % 64) * 16, y=(index // 64) * 16,
                                         width_tiles=16, height_tiles=16)
                                     for index in range(65)]
        with self.assertRaisesRegex(ValueError, "16384-tile limit"):
            self.compile()

    def shared_source_fixture(self):
        # One original word crosses adjacent source tiles. Only its owner
        # contributes a Chinese label; the borrower accounts for its fragment.
        atlas = Image.new("RGBA", (512, 256))
        ImageDraw.Draw(atlas).rectangle((240, 80, 271, 99), fill="white")
        for index, tile in enumerate(self.region["original_tiles"]):
            path = self.original / tile["path"]
            atlas.crop((index * 256, 0, (index + 1) * 256, 256)).save(path)
            tile["sha256"] = sha(path)
        left, right = copy.deepcopy(self.region["original_tiles"])
        self.region.update(id="owner", width_tiles=1, original_tiles=[left], context_tiles=[right],
                           features=[self.region["features"][1]],
                           translations=[self.region["translations"][1]],
                           layouts=[dict(id="city", x=128, y=128, font_size=22, font_style="bold")], symbols=[])
        borrower = dict(id="borrower", z=3, x=1, y=0, width_tiles=1, height_tiles=1,
                        original_tiles=[right], context_tiles=[left], features=[], translations=[],
                        layouts=[], symbols=[], shared_sources=[dict(id="city-shared", owner_region="owner",
                            feature_id="city", kind="city", original="Original City",
                            original_bbox=[-16, 80, 16, 100], source_url="https://example.com/city",
                            review_notes="Same complete original word across the adjacent source tile")])
        self.manifest["regions"] = [borrower, self.region]
        self.sign_shared_regions()
        return borrower

    def sign_shared_regions(self):
        self.approve()
        for region in self.manifest["regions"]:
            region["review"] = dict(self.region["review"], record_sha256=record_fingerprint(
                {key: value for key, value in region.items() if key != "review"}))
        _, report = self.compile()
        for region, audit in zip(self.manifest["regions"], report["regions"]):
            region["review"]["render_sha256"] = audit["atlas_rgba_sha256"]

    def test_shared_source_accounts_both_fragments_and_renders_once(self):
        self.shared_source_fixture()
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is not None, report["gaps"])
        self.assertTrue(report["ready"], report["gaps"])
        self.assertEqual(1, report["label_count"])
        self.assertEqual(2, report["tile_count"])
        self.assertTrue(all(region["source_alpha"]["unaccounted_pixels"] == 0 for region in report["regions"]))
        borrower = report["regions"][0]
        self.assertEqual([], borrower["labels"])
        self.assertEqual("owner", borrower["shared_sources"][0]["owner_region"])
        self.assertEqual(0, borrower["tiles"][0]["label_count"])
        with Image.open(self.base / "preview/borrower.zh.png") as preview:
            self.assertIsNone(preview.getchannel("A").getbbox())

    def test_shared_source_rejects_wrong_owner_identity_bbox_and_notes(self):
        borrower = self.shared_source_fixture()
        source = borrower["shared_sources"][0]
        original = copy.deepcopy(source)
        for field, value in (("owner_region", "unknown"), ("owner_region", "borrower"),
                             ("feature_id", "absent"), ("kind", "admin0"),
                             ("original", "Different City"), ("source_url", "https://example.com/other"),
                             ("original_bbox", [-16, 80, 15, 100]),
                             ("original_bbox", [-16.5, 80, 16, 100]), ("review_notes", "")):
            with self.subTest(field=field, value=value):
                source.clear()
                source.update(original)
                source[field] = value
                self.sign_shared_regions()
                self.rejected("shared_source")

    def test_shared_source_rejects_zoom_local_id_and_reference_chain(self):
        borrower = self.shared_source_fixture()
        borrower["z"] = 4
        self.sign_shared_regions()
        self.rejected("shared_source")
        borrower["z"] = 3
        borrower["features"] = [dict(self.region["features"][0], id="city-shared", original_bbox=[0, 80, 16, 100])]
        self.sign_shared_regions()
        self.rejected("shared_source")
        borrower["features"] = []
        # References can only target normal features, never another reference.
        self.region["shared_sources"] = [dict(borrower["shared_sources"][0], id="chain", owner_region="borrower",
                                             feature_id="city-shared", original_bbox=[240, 80, 272, 100])]
        borrower["shared_sources"][0]["feature_id"] = "chain"
        self.sign_shared_regions()
        self.rejected("shared_source")

    def test_shared_source_requires_owner_output_and_review_but_allows_draft_preview(self):
        borrower = self.shared_source_fixture()
        self.region["layouts"] = []
        self.sign_shared_regions()
        self.rejected("shared_source_owner")
        self.region["layouts"] = [dict(id="city", x=128, y=128, font_size=22, font_style="bold")]
        self.region["translations"][0]["status"] = "draft"
        self.sign_shared_regions()
        self.rejected("shared_source_owner")
        self.region["translations"][0]["status"] = "approved"
        self.sign_shared_regions()
        self.region["review"]["status"] = "draft"
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is None, "draft owner must not certify a pack")
        self.assertFalse(report["ready"])
        self.assertIn("shared_source_owner", json.dumps(report))
        self.assertTrue((self.base / "preview/owner.zh.png").is_file())
        self.assertTrue((self.base / "preview/borrower.zh.png").is_file())
        self.assertEqual(borrower["review"]["status"], "approved")

    def test_shared_source_requires_local_alpha_and_boundary_context(self):
        borrower = self.shared_source_fixture()
        borrower["context_tiles"] = []
        self.sign_shared_regions()
        self.rejected("context_incomplete")
        borrower["context_tiles"] = self.region["original_tiles"]
        tile = borrower["original_tiles"][0]
        Image.new("RGBA", (256, 256)).save(self.original / tile["path"])
        tile["sha256"] = sha(self.original / tile["path"])
        self.sign_shared_regions()
        self.rejected("empty_original_bbox")

    def test_shared_source_change_invalidates_region_approval(self):
        borrower = self.shared_source_fixture()
        borrower["shared_sources"][0]["review_notes"] += " changed"
        self.rejected("region_fingerprint")

    def flat_background(self):
        tile = self.region["original_tiles"][0]
        path = self.original / tile["path"]
        with Image.open(path) as source:
            image = source.convert("RGBA")
        background = Image.new("RGBA", image.size, (1, 1, 1, 1))
        background.paste(image, mask=image.getchannel("A"))
        background.save(path)
        tile["sha256"] = sha(path)
        self.approve()
        return dict(x=tile["x"], y=tile["y"], sha256=tile["sha256"], rgba=[1, 1, 1, 1],
                    source_url=tile["source_url"], review_notes="Reviewed exact flat source background")

    def test_exact_source_background_accounts_without_changing_source_or_pack(self):
        old_wire, _ = self.compile()
        rule = self.flat_background()
        self.rejected("unaccounted_alpha")
        source = (self.original / "0.png").read_bytes()
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile(preview=True)
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(old_wire, wire)
        self.assertEqual(source, (self.original / "0.png").read_bytes())
        audit = report["regions"][0]["source_alpha"]
        self.assertEqual(64416, audit["background_pixels"])
        self.assertEqual(audit["total_pixels"], audit["accounted_pixels"])
        self.assertEqual(0, audit["unaccounted_pixels"])
        self.assertEqual(64416, report["regions"][0]["source_backgrounds"][0]["pixels"])
        with Image.open(self.base / "preview/fixture.original.png") as preview, Image.open(io.BytesIO(source)) as original:
            self.assertEqual(original.convert("RGBA").tobytes(), preview.crop((0, 0, 256, 256)).tobytes())

    def test_background_requires_exact_verified_core_source_and_evidence(self):
        valid = self.flat_background()
        for field, value in (("sha256", "0" * 64), ("source_url", "https://example.com/other"),
                             ("rgba", [0, 0, 0, 1]), ("rgba", [True, 1, 1, 1]),
                             ("x", 2), ("y", 1), ("x", True), ("review_notes", "")):
            with self.subTest(field=field, value=value):
                self.region["source_backgrounds"] = [dict(valid, **{field: value})]
                self.approve()
                self.rejected("source_background")
        self.region["source_backgrounds"] = [valid, valid.copy()]
        self.approve()
        self.rejected("source_background")
        self.region["source_backgrounds"] = [valid]
        Image.new("RGBA", (256, 256), (1, 1, 1, 1)).save(self.original / "0.png")
        # Recorded source SHA and the rule still agree, but actual bytes differ.
        self.approve()
        report = self.rejected("source_background")
        self.assertIn("source_sha256", json.dumps(report))

    def test_background_sparse_or_other_rgba_cannot_exempt_ink(self):
        tile = self.region["original_tiles"][0]
        rule = dict(x=0, y=0, sha256=tile["sha256"], rgba=[1, 1, 1, 1],
                    source_url=tile["source_url"], review_notes="Synthetic sparse candidate")
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.rejected("source_background")
        rule = self.flat_background()
        self.region["source_backgrounds"] = [rule]
        for rgba in ((255, 255, 255, 1), (1, 1, 1, 2)):
            with self.subTest(rgba=rgba):
                with Image.open(self.original / "0.png") as source:
                    image = source.convert("RGBA")
                image.putpixel((200, 200), rgba)
                image.save(self.original / "0.png")
                tile["sha256"] = rule["sha256"] = sha(self.original / "0.png")
                self.approve()
                audit = self.rejected("unaccounted_alpha")["regions"][0]["source_alpha"]
                self.assertEqual(1, audit["unaccounted_pixels"])

    def test_background_only_bboxes_do_not_prove_feature_or_symbol_exists(self):
        rule = self.flat_background()
        self.region["source_backgrounds"] = [rule]
        for category in ("features", "symbols"):
            with self.subTest(category=category):
                row = self.region[category][0]
                saved = row["original_bbox"]
                row["original_bbox"] = [0, 0, 10, 10]
                if category == "features":
                    self.region["translations"][0]["record_sha256"] = record_fingerprint(row)
                self.approve()
                report = self.rejected("empty_original_bbox")
                self.assertTrue(any(g["code"] == "empty_original_bbox" and g["category"] == category[:-1]
                                    for g in report["gaps"]))
                row["original_bbox"] = saved

    def test_background_metadata_change_invalidates_region_approval(self):
        self.region["source_backgrounds"] = [self.flat_background()]
        self.approve()
        self.region["source_backgrounds"][0]["review_notes"] += " changed"
        self.rejected("region_fingerprint")

    def test_background_requires_at_least_half_the_exact_tile(self):
        rule = self.flat_background()
        with Image.open(self.original / "0.png") as source:
            image = source.convert("RGBA")
        for y in range(128):
            for x in range(256):
                if image.getpixel((x, y)) == (1, 1, 1, 1):
                    image.putpixel((x, y), (0, 0, 0, 0))
        image.save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = rule["sha256"] = sha(self.original / "0.png")
        self.region["source_backgrounds"] = [rule]
        self.approve()
        self.sign_render()
        wire, report = self.compile()
        self.assertTrue(wire is not None, report["gaps"])
        self.assertEqual(32768, report["regions"][0]["source_alpha"]["background_pixels"])
        image.putpixel((0, 128), (0, 0, 0, 0))
        image.save(self.original / "0.png")
        self.region["original_tiles"][0]["sha256"] = rule["sha256"] = sha(self.original / "0.png")
        self.approve()
        self.rejected("source_background")

    def test_background_cannot_prove_a_shared_source_bbox_has_ink(self):
        borrower = self.shared_source_fixture()
        tile = borrower["original_tiles"][0]
        with Image.open(self.original / tile["path"]) as source:
            image = source.convert("RGBA")
        background = Image.new("RGBA", image.size, (1, 1, 1, 1))
        background.paste(image, mask=image.getchannel("A"))
        background.save(self.original / tile["path"])
        tile["sha256"] = sha(self.original / tile["path"])
        borrower["source_backgrounds"] = [dict(x=1, y=0, sha256=tile["sha256"], rgba=[1, 1, 1, 1],
            source_url=tile["source_url"], review_notes="Synthetic exact source background")]
        feature = self.region["features"][0]
        feature["original_bbox"] = [250, 10, 270, 20]
        self.region["translations"][0]["record_sha256"] = record_fingerprint(feature)
        borrower["shared_sources"][0]["original_bbox"] = [-6, 10, 14, 20]
        self.sign_shared_regions()
        report = self.rejected("empty_original_bbox")
        self.assertTrue(any(g["code"] == "empty_original_bbox" and g["category"] == "shared_source"
                            for g in report["gaps"]))


if __name__ == "__main__":
    unittest.main()
