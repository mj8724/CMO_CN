#!/usr/bin/env python3
"""R78: localized briefing HTML must declare its own encoding.

Without a declaration mshtml (WinForms WebBrowser) falls back to the ANSI
codepage, so UTF-8 Chinese renders as GBK mojibake.  These cases pin the three
behaviours that matter: add when missing, never duplicate, and declare the
document's *real* encoding rather than always claiming utf-8.
"""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / "scripts"))

from localize_html_files import ensure_charset  # noqa: E402


class CharsetDeclarationTests(unittest.TestCase):
    def test_adds_declaration_after_html_tag(self):
        source = "<html>\r\n\t<body><p>欢迎</p></body>\r\n</html>\r\n"
        result, changed = ensure_charset(source, "utf-8")
        self.assertTrue(changed)
        self.assertIn('<meta charset="utf-8">', result)
        self.assertLess(result.index("<meta"), result.index("<body"))

    def test_declaration_lands_inside_existing_head(self):
        source = "<html><head><title>t</title></head><body>中文</body></html>"
        result, changed = ensure_charset(source, "utf-8")
        self.assertTrue(changed)
        self.assertLess(result.index("<head>"), result.index("<meta"))
        self.assertLess(result.index("<meta"), result.index("</head>"))

    def test_keeps_existing_declaration(self):
        source = '<html><head><meta charset="utf-8"></head><body>中文</body></html>'
        result, changed = ensure_charset(source, "utf-8")
        self.assertFalse(changed)
        self.assertEqual(result, source)

    def test_declares_the_real_encoding(self):
        # A GBK page must not claim utf-8, or it turns into mojibake the other way.
        result, changed = ensure_charset("<html><body>中文</body></html>", "gb18030")
        self.assertTrue(changed)
        self.assertIn('<meta charset="gb18030">', result)

    def test_utf8_sig_reports_utf8(self):
        result, _ = ensure_charset("<html><body>中文</body></html>", "utf-8-sig")
        self.assertIn('<meta charset="utf-8">', result)

    def test_negative_no_declaration_stays_detectable(self):
        # Guards the gate itself: a Han page whose bytes carry no declaration
        # must still be recognisable as broken (ensure_charset is the fixer, and
        # its `changed` flag is what the gate counts).
        source = "<html>\r\n\t<body><p>欢迎来到高级作战教程。</p></body>\r\n</html>\r\n"
        _, changed = ensure_charset(source, "utf-8")
        self.assertTrue(changed, "missing declaration must be reported as changed")


if __name__ == "__main__":
    unittest.main(verbosity=2)
