#!/usr/bin/env python3
"""Regression cases for the reviewed placename release gate."""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from verify_placenames import verify_asset, verify_coverage


class PlacenameGateTests(unittest.TestCase):
    def setUp(self):
        self.review = [dict(name="Yichun", previous="宜春", zh="伊春",
                            lat=47.721432, lon=128.875290,
                            source="https://www.yc.gov.cn/")]
        self.baseline = [dict(name="宜春", lat=47.721432, lon=128.875290, flags=1),
                         dict(name="宜春", lat=27.833330, lon=114.400002, flags=2)]
        self.fixed = [dict(self.baseline[0], name="伊春"), dict(self.baseline[1])]

    def verify(self, items, baseline=None, review=None):
        return verify_asset(items, self.review if review is None else review,
                            baseline, expected_count=2)

    def test_correct_target_and_unreviewed_homonym_pass(self):
        result = self.verify(self.fixed, self.baseline)
        self.assertEqual([], result["errors"])
        self.assertEqual(1, result["verified_records"])
        self.assertEqual(1, result["unreviewed_records"])
        self.assertEqual(1, result["changed_records"])

    def test_chinese_but_wrong_label_fails(self):
        result = self.verify(self.baseline)
        self.assertTrue(any("expected 伊春" in e for e in result["errors"]))

    def test_unapproved_name_change_fails(self):
        bad = [self.fixed[0], dict(self.fixed[1], name="伊春")]
        result = self.verify(bad, self.baseline)
        self.assertTrue(any("unapproved rename" in e for e in result["errors"]))

    def test_record_reordering_and_flag_changes_fail(self):
        for items in (self.fixed[::-1],
                      [self.fixed[0], dict(self.fixed[1], flags=99)]):
            with self.subTest(items=items):
                result = self.verify(items, self.baseline)
                self.assertTrue(any("coordinates/flags/order" in e for e in result["errors"]))

    def test_empty_review_and_missing_city_fail(self):
        self.assertTrue(self.verify(self.fixed, review=[])["errors"])
        self.assertTrue(self.verify(self.fixed[:1])["errors"])

    def test_unreviewed_invalid_coordinate_fails(self):
        bad = [self.fixed[0], dict(self.fixed[1], lat=float("nan"))]
        result = self.verify(bad)
        self.assertTrue(any("invalid coordinates" in e for e in result["errors"]))

    def test_deleted_review_record_cannot_silently_shrink_scope(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp)
            path = directory / "korea.json"
            path.write_text(json.dumps(self.review), encoding="utf-8")
            scopes = {"korea.json": 1}
            self.assertEqual([], verify_coverage(directory, scopes))
            path.write_text("[]", encoding="utf-8")
            self.assertTrue(verify_coverage(directory, scopes))
            path.unlink()
            self.assertTrue(verify_coverage(directory, scopes))


if __name__ == "__main__":
    unittest.main()
