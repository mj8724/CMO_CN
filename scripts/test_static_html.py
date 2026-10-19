#!/usr/bin/env python3
"""Offline regression checks; fixtures never read or write the installed game."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

import localize_html_files as tool


class StaticHtmlTests(unittest.TestCase):
    def setUp(self) -> None:
        self.assertTrue(callable(getattr(tool, "localize_html", None)),
                        "offline display-text translation is not implemented")
        self.assertTrue(callable(getattr(tool, "build_payload", None)),
                        "read-only static payload export is not implemented")

    def test_only_display_text_changes(self) -> None:
        source = ('<a href="Mission.html?q=>" title="Mission">Mission</a>'
                  '<script>var label="Mission";</script><style>.Mission{}</style>'
                  '<!-- Mission --><p>Category: <%Category%> {0}</p>')
        result, _ = tool.localize_html(source, {"Mission": "任务", "Category:": "类别:"})
        self.assertEqual(result, source.replace('>Mission</a>', '>任务</a>')
                         .replace('>Category: ', '>类别: '))

    def test_translation_escapes_markup_and_preserves_padding(self) -> None:
        result, stats = tool.localize_html('<p>  Search &amp; Rescue\n</p>',
                                          {"Search & Rescue": "搜救 <任务> & 支援"})
        self.assertEqual(result, '<p>  搜救 &lt;任务&gt; &amp; 支援\n</p>')
        self.assertEqual(stats["translated"], 1)

    def test_placeholder_loss_fails(self) -> None:
        with self.assertRaisesRegex(ValueError, "placeholder"):
            tool.localize_html('<p>Mission {0}</p>', {"Mission {0}": "任务"})

    def test_prose_percentage_is_not_a_printf_placeholder(self) -> None:
        result, _ = tool.localize_html('<p>50% before the mission</p>',
                                      {"50% before the mission": "任务前的50%"})
        self.assertEqual(result, '<p>任务前的50%</p>')

    def test_missing_translation_is_reported(self) -> None:
        source = '<p>New scenario text</p>'
        result, stats = tool.localize_html(source, {})
        self.assertEqual(result, source)
        self.assertEqual(stats["missing"], ["New scenario text"])

    def test_form_editor_and_option_text_stays_original(self) -> None:
        source = ('<textarea>Mission <b>Mission</b></textarea>'
                  '<select><optgroup label="Mission"><option>Mission</option></optgroup></select>'
                  '<option>Mission</option><p>Mission</p>')
        result, stats = tool.localize_html(source, {"Mission": "任务"})
        self.assertEqual(result, source.replace('<p>Mission</p>', '<p>任务</p>'))
        self.assertEqual(stats["translated"], 1)

    def test_nested_contenteditable_stays_protected_until_outer_close(self) -> None:
        source = ('<div contenteditable="true">Mission<div>Mission</div>Mission'
                  '<div contenteditable="false">Mission</div>Mission</div><p>Mission</p>')
        result, stats = tool.localize_html(source, {"Mission": "任务"})
        self.assertEqual(result, source.replace('<p>Mission</p>', '<p>任务</p>'))
        self.assertEqual(stats["translated"], 1)

    def test_boolean_plaintext_editors_and_nested_protected_tags(self) -> None:
        source = ('<div contenteditable>Mission<span>Mission</span>Mission</div>'
                  '<div CONTENTEDITABLE="plaintext-only">Mission</div>'
                  '<pre>Mission<pre>Mission</pre>Mission</pre>'
                  '<div contenteditable="false">Mission</div>'
                  '<div title="contenteditable=true">Mission</div>')
        result, stats = tool.localize_html(source, {"Mission": "任务"})
        expected = source.replace('contenteditable="false">Mission', 'contenteditable="false">任务')
        expected = expected.replace('title="contenteditable=true">Mission', 'title="contenteditable=true">任务')
        self.assertEqual(result, expected)
        self.assertEqual(stats["translated"], 2)

    def test_export_is_read_only_and_archive_is_reproducible(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            game = root / "game"
            scenarios = game / "Scenarios"
            scenarios.mkdir(parents=True)
            html = scenarios / "briefing.htm"
            html.write_text('<p>Mission</p>', encoding="utf-8")
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {"Mission": "任务"}}), encoding="utf-8")
            out = root / "out"
            manifest = tool.build_payload(game, out, dictionary)
            first_zip = (out / "Scenarios_HTML.zip").read_bytes()
            self.assertEqual(html.read_text(encoding="utf-8"), '<p>Mission</p>')
            self.assertFalse(html.with_name(html.name + ".orig").exists())
            self.assertEqual(manifest["files"][0]["path"], "Scenarios/briefing.htm")
            self.assertEqual(manifest["untranslated_segments"], 0)
            with zipfile.ZipFile(out / "Scenarios_HTML.zip") as archive:
                self.assertEqual(archive.read("Scenarios/briefing.htm").decode(), '<p>任务</p>')
                self.assertEqual(archive.read("Scenarios/briefing.htm.orig").decode(), '<p>Mission</p>')
            tool.build_payload(game, out, dictionary)
            self.assertEqual(first_zip, (out / "Scenarios_HTML.zip").read_bytes())

    def test_missing_source_fails_instead_of_empty_success(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(FileNotFoundError, "Scenarios"):
                tool.build_payload(Path(tmp) / "missing", Path(tmp) / "out", Path(tmp) / "dict.json")

    def test_refreshed_live_html_wins_over_unmanaged_original_backup(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scenarios = root / "game/Scenarios"
            scenarios.mkdir(parents=True)
            path = scenarios / "updated.html"
            original = path.with_name(path.name + ".orig")
            old = b'<h2>Briefing</h2><p>Old instructions</p>'
            current = b'<h2>Briefing</h2><p>Updated instructions</p><!-- revision 2 -->'
            original.write_bytes(old)
            path.write_bytes(current)
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {
                "Briefing": "简报", "Old instructions": "旧指令", "Updated instructions": "新指令"
            }}), encoding="utf-8")
            manifest = tool.build_payload(root / "game", root / "out", dictionary)
            with zipfile.ZipFile(root / "out/Scenarios_HTML.zip") as archive:
                self.assertEqual(archive.read("Scenarios/updated.html").decode("utf-8"),
                                 '<h2>简报</h2><p>新指令</p><!-- revision 2 -->')
                self.assertEqual(current, archive.read("Scenarios/updated.html.orig"))
            self.assertEqual(hashlib.sha256(current).hexdigest(),
                             manifest["files"][0]["source_sha256"])
            self.assertEqual(current, path.read_bytes())
            self.assertEqual(old, original.read_bytes())

    def test_localized_live_html_is_not_rolled_back_to_backup_version(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scenarios = root / "game/Scenarios"
            scenarios.mkdir(parents=True)
            current = '<h2>简报</h2><p>新指令</p><!-- revision 2 -->'.encode("utf-8")
            path = scenarios / "localized.html"
            path.write_bytes(current)
            (scenarios / "localized.html.orig").write_bytes(b'<p>Old instructions</p>')
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {"Old instructions": "旧指令"}}),
                                  encoding="utf-8")
            output = root / "out"
            tool.build_payload(root / "game", output, dictionary)
            self.assertEqual(current, (output / "static_html/Scenarios/localized.html").read_bytes())
            first_zip = (output / "Scenarios_HTML.zip").read_bytes()
            tool.build_payload(root / "game", output, dictionary)
            self.assertEqual(first_zip, (output / "Scenarios_HTML.zip").read_bytes())

    def test_manifest_verification_rejects_tampered_output(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scenarios = root / "game/Scenarios"
            scenarios.mkdir(parents=True)
            (scenarios / "test.html").write_text('<p>Mission</p>', encoding="utf-8")
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {"Mission": "任务"}}), encoding="utf-8")
            tool.build_payload(root / "game", root / "out", dictionary)
            (root / "out/static_html/Scenarios/test.html").write_text("tampered", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "differs from coverage manifest"):
                tool.verify_payload(root / "out", dictionary)

    def test_exact_lookup_precedes_first_normalized_whitespace_match(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scenarios = root / "game/Scenarios"
            scenarios.mkdir(parents=True)
            (scenarios / "test.html").write_text(
                '<p>Mission</p><p>  Fleet   Ready  </p>', encoding="utf-8")
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {
                "Mission": "修订译文", " Mission ": "旧译文",
                "Fleet \nReady": "首次规范化译文", "Fleet\tReady": "后续覆盖译文"
            }}), encoding="utf-8")
            tool.build_payload(root / "game", root / "out", dictionary)
            result = (root / "out/static_html/Scenarios/test.html").read_text(encoding="utf-8")
            self.assertEqual(result, '<p>修订译文</p><p>  首次规范化译文  </p>')

    def test_normalized_lookup_ignores_case_without_overriding_exact(self) -> None:
        result, _ = tool.localize_html('<p>Mission</p><p>mission</p>',
                                      {"MISSION": "大写译文", "Mission": "精确译文"})
        self.assertEqual(result, '<p>精确译文</p><p>大写译文</p>')

    def test_windows_1252_source_keeps_declared_encoding_and_markup(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            scenarios = root / "game/Scenarios"
            scenarios.mkdir(parents=True)
            document = '<meta charset="windows-1252"><p>Mission</p><!-- author’s note -->'
            (scenarios / "legacy.html").write_bytes(document.encode("cp1252"))
            dictionary = root / "dict.json"
            dictionary.write_text(json.dumps({"exact": {"Mission": "任务"}}), encoding="utf-8")
            tool.build_payload(root / "game", root / "out", dictionary)
            result = (root / "out/static_html/Scenarios/legacy.html").read_bytes().decode("cp1252")
            self.assertEqual(result, document.replace('>Mission<', '>&#20219;&#21153;<'))


if __name__ == "__main__":
    unittest.main()
