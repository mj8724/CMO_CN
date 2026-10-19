#!/usr/bin/env python3
"""Export static Chinese HTML without modifying the installed game.

GAME_DIR selects read-only input. Commercial text stays in ignored build output.
static_html_manifest.json records per-file hashes and actual translation coverage.
"""
from __future__ import annotations

import argparse
import codecs
from collections import Counter
import hashlib
import html
import json
import os
from pathlib import Path
import re
import shutil
import zipfile

ROOT = Path(__file__).resolve().parent.parent
TOKEN = re.compile(r'<!--.*?-->|<%.*?%>|<(?:(?:[^>"\']|"[^"]*"|\'[^\']*\'))*>', re.S)
TAG = re.compile(r"<\s*(/?)\s*([a-zA-Z][\w:-]*)")
ATTRIBUTE = re.compile(r'''([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?''')
PLACEHOLDER = re.compile(r"\{[^{}]*\}|%(?:\d+\$)?[-+#0]*\d*(?:\.\d+)?[sdifouxXeEgGc]")
HAN = re.compile(r"[\u4e00-\u9fff]")
WORD = re.compile(r"[A-Za-z]{2}")
PROTECTED = {"script", "style", "code", "pre", "kbd", "textarea", "select", "option"}
VOID = {"area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"}
RAW_TEXT = {"script", "style", "textarea"}


def key_of(text: str) -> str:
    return re.sub(r"\s+", " ", html.unescape(text)).strip()


def localize_html(document: str, table: dict[str, str],
                  normalized: dict[str, str] | None = None) -> tuple[str, dict]:
    """Translate display text only; unknown strings are kept and reported."""
    stats = {"translated": 0, "missing": []}
    if normalized is None:
        normalized = normalized_table(table)

    def translate(text: str) -> str:
        key = key_of(text)
        if not WORD.search(key) or HAN.search(key):
            return text
        translated = table.get(html.unescape(text).strip())
        if translated is None:
            translated = normalized.get(key.casefold())
        if not translated or translated == key:
            stats["missing"].append(key)
            return text
        if Counter(PLACEHOLDER.findall(key)) != Counter(PLACEHOLDER.findall(translated)):
            raise ValueError(f"translation changes a placeholder: {key!r}")
        lead = re.match(r"^(?:\s|&nbsp;|&#160;)*", text).group()
        trail = re.search(r"(?:\s|&nbsp;|&#160;)*$", text).group()
        stats["translated"] += 1
        return lead + html.escape(translated.strip(), quote=False) + trail

    parts = []
    position = 0
    stack = []
    for token in TOKEN.finditer(document):
        protected = stack and stack[-1][1]
        text = document[position:token.start()]
        parts.append(text if protected else translate(text))
        markup = token.group()
        parts.append(markup)
        tag = TAG.match(markup)
        if tag:
            closing, name = tag.groups()
            name = name.lower()
            if stack and stack[-1][0] in RAW_TEXT:
                if closing and name == stack[-1][0]:
                    stack.pop()
            elif closing:
                for index in range(len(stack) - 1, -1, -1):
                    if stack[index][0] == name:
                        del stack[index:]
                        break
            elif name not in VOID and not markup.rstrip().endswith("/>"):
                attributes = {match[1].lower(): next((v for v in match.groups()[1:] if v is not None), "")
                              for match in ATTRIBUTE.finditer(markup[tag.end():])}
                editable = ("contenteditable" in attributes
                            and html.unescape(attributes["contenteditable"]).strip().lower() != "false")
                stack.append((name, bool(protected or name in PROTECTED or editable)))
        position = token.end()
    tail = document[position:]
    parts.append(tail if stack and stack[-1][1] else translate(tail))
    return "".join(parts), stats


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def normalized_table(table: dict[str, str]) -> dict[str, str]:
    normalized = {}
    for key, value in table.items():
        normalized.setdefault(re.sub(r"\s+", " ", key.strip()).casefold(), value)
    return normalized


def decode_html(source: bytes) -> tuple[str, str]:
    if source.startswith(codecs.BOM_UTF8):
        return source.decode("utf-8-sig"), "utf-8-sig"
    if source.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
        return source.decode("utf-16"), "utf-16"
    charset = re.search(rb'charset\s*=\s*["\']?\s*([\w-]+)', source[:4096], re.I)
    if charset:
        encoding = charset.group(1).decode("ascii")
        return source.decode(encoding), encoding
    try:
        return source.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        return source.decode("cp1252"), "cp1252"


def build_payload(game: Path, output: Path, dictionary: Path,
                  translations: Path | None = None, dry_run: bool = False) -> dict:
    scenarios = game / "Scenarios"
    if not scenarios.is_dir():
        raise FileNotFoundError(f"Scenarios source directory is missing: {scenarios}")
    sources = sorted(p for p in scenarios.rglob("*")
                     if p.is_file() and p.suffix.lower() in {".html", ".htm"})
    if not sources:
        raise FileNotFoundError(f"No HTML source files in Scenarios: {scenarios}")
    if output.resolve().is_relative_to(game.resolve()):
        raise ValueError("output must be outside the read-only game directory")
    dictionary_bytes = dictionary.read_bytes()
    exact = json.loads(dictionary_bytes)["exact"]
    table = {k: v for k, v in exact.items() if isinstance(v, str)}
    extra_bytes = translations.read_bytes() if translations else b""
    if extra_bytes:
        table.update({k: v for k, v in json.loads(extra_bytes).items()
                      if isinstance(v, str) and not k.startswith("_")})
    normalized = normalized_table(table)
    manifest = {"schema": 1, "dictionary_sha256": digest(dictionary_bytes),
                "translations_sha256": digest(extra_bytes), "files": [],
                "translated_segments": 0, "untranslated_segments": 0}
    payload_files = {}
    for path in sources:
        # Backups are unversioned and may predate a game update. The explicitly
        # selected current file is authoritative; existing Chinese is preserved.
        source = path.read_bytes()
        document, encoding = decode_html(source)
        translated, stats = localize_html(document, table, normalized)
        result = translated.encode(encoding, errors="xmlcharrefreplace")
        relative = path.relative_to(game).as_posix()
        payload_files[relative] = result
        payload_files[relative + ".orig"] = source
        manifest["files"].append({"path": relative, "source_sha256": digest(source),
                                  "output_sha256": digest(result),
                                  "encoding": encoding,
                                  "translated_segments": stats["translated"],
                                  "untranslated_segments": len(stats["missing"]),
                                  "untranslated_samples": stats["missing"][:3]})
        manifest["translated_segments"] += stats["translated"]
        manifest["untranslated_segments"] += len(stats["missing"])
    manifest["coverage"] = "partial" if manifest["untranslated_segments"] else "complete"
    if dry_run:
        return manifest
    output.mkdir(parents=True, exist_ok=True)
    payload_root = output / "static_html"
    payload_root.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output / "Scenarios_HTML.zip", "w", zipfile.ZIP_DEFLATED,
                         compresslevel=9) as archive:
        for name, data in sorted(payload_files.items()):
            dest = payload_root / name
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, data, compresslevel=9)
    manifest["archive_sha256"] = digest((output / "Scenarios_HTML.zip").read_bytes())
    (output / "static_html_manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    return manifest


def verify_payload(output: Path, dictionary: Path, copy_to: Path | None = None) -> dict:
    manifest = json.loads((output / "static_html_manifest.json").read_text(encoding="utf-8"))
    if not manifest.get("files"):
        raise ValueError("static HTML coverage manifest is empty")
    if manifest["dictionary_sha256"] != digest(dictionary.read_bytes()):
        raise ValueError("static HTML payload uses a different dictionary; rebuild it")
    if manifest["archive_sha256"] != digest((output / "Scenarios_HTML.zip").read_bytes()):
        raise ValueError("static HTML archive does not match its coverage manifest")
    payload_root = (output / "static_html").resolve()
    for entry in manifest["files"]:
        for name, expected in ((entry["path"], entry["output_sha256"]),
                               (entry["path"] + ".orig", entry["source_sha256"])):
            source = (payload_root / name).resolve()
            if not source.is_relative_to(payload_root):
                raise ValueError("static HTML manifest contains an escaping path")
            if digest(source.read_bytes()) != expected:
                raise ValueError(f"static HTML file differs from coverage manifest: {name}")
            if copy_to:
                target = copy_to / name
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, target)
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=os.environ.get("GAME_DIR"))
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build")
    parser.add_argument("--dictionary", type=Path, default=ROOT / "dict/CMOLocalizer.json")
    parser.add_argument("--translations", type=Path, help="optional offline text-to-text translation JSON")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--copy-to", type=Path, help="copy manifest-covered files into a staging pack")
    args = parser.parse_args()
    if args.verify or args.copy_to:
        manifest = verify_payload(args.output_dir, args.dictionary, args.copy_to)
    else:
        if not args.game_dir:
            parser.error("set GAME_DIR or pass --game-dir; installed game is read-only input")
        manifest = build_payload(args.game_dir, args.output_dir, args.dictionary,
                                 args.translations, args.dry_run)
    print(f"Static HTML coverage: {len(manifest['files'])} files; "
          f"{manifest['translated_segments']} translated segments; "
          f"{manifest['untranslated_segments']} untranslated segments ({manifest['coverage']}).")
    if not args.dry_run:
        print(f"Coverage manifest: {args.output_dir / 'static_html_manifest.json'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
