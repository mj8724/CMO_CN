#!/usr/bin/env python3
"""Quick Battle (快速战斗) form pages: worklist, render, and gate.

The game ships 20 self-contained HTML forms under ``<game>/QuickBattle/<entry>/
Form.html`` and renders the selected one into a WinForms ``WebBrowser``
(``Command.QuickBattle.theBrowser``).  The pages are plain English and never
passed through the string pool, so neither the dictionary nor the engine's
``WebBrowser`` injection can localize them properly.

Sub-commands
------------
work    list every display segment that still needs Chinese, plus an
        attribute fingerprint of the untouched game pages
build   render the pages with ``dict/quickbattle_html.json`` into
        ``build/qb_html/`` and pack ``build/QuickBattle_HTML.zip``
verify  gate: bundle covers every page, no segment left English, and every
        attribute value matches the installed game page byte for byte

Only text between tags is rewritten (``localize_html``), so ``name``/``value``
attributes and the ``<select>`` structure read by each entry's ``Script.lua``
cannot be touched.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DICT = ROOT / "dict"
SOURCE = DICT / "quickbattle_html.json"
ROOT_BUILD = ROOT / "build"
# Artifacts follow QB_BUILD_DIR so an isolated candidate build keeps its own bundle;
# the translation worklist always lives in build/ because it is authoring input.
BUILD = pathlib.Path(os.environ.get("QB_BUILD_DIR") or ROOT_BUILD)
WORK = ROOT_BUILD / "qb_work"
PAYLOAD = BUILD / "qb_html"
BUNDLE = BUILD / "QuickBattle_HTML.zip"
MANIFEST = BUILD / "qb_html_manifest.json"
ZIP_PREFIX = "QuickBattle"
BUNDLE_MIN = 15  # a game update may add entries; losing pages must fail loudly

sys.path.insert(0, str(ROOT / "scripts"))
import localize_html_files as html_files  # noqa: E402

# The form pages need their dropdown labels translated; the scenario briefings
# deliberately keep them English, so this stays local to Quick Battle.
FORM_PROTECTED = html_files.PROTECTED - {"select", "option"}


def entries(game: Path) -> list[tuple[str, Path]]:
    root = game / "QuickBattle"
    if not root.is_dir():
        raise FileNotFoundError(f"Quick Battle directory is missing: {root}")
    found = [(p.parent.name, p) for p in sorted(root.glob("*/Form.html"))]
    if not found:
        raise FileNotFoundError(f"no Quick Battle forms under {root}")
    return found


def attribute_fingerprint(document: str) -> str:
    """Hash every attribute value, so an edit to name/value cannot go unnoticed."""
    import hashlib

    pairs = []
    for token in html_files.html_tokens(document):
        markup = token.group()
        tag = html_files.TAG.match(markup)
        if not tag or tag.group(1):
            continue
        for key, value in sorted(html_files.attributes_of(markup, tag.end()).items()):
            if key in html_files.VOID or key in ("class", "style", "href", "src"):
                continue
            pairs.append(f"{tag.group(2).lower()}|{key}|{value}")
    return hashlib.sha256("\n".join(pairs).encode("utf-8")).hexdigest()


def page_texts(path: Path) -> tuple[str, str]:
    document, encoding = html_files.decode_html(path.read_bytes())
    return document, encoding


def is_translatable(segment: str) -> bool:
    """Skip segments that are really leaked markup.

    Some game pages carry malformed tags (``<link ... crossorigin">`` without the
    ``=``), which makes the tokenizer treat the trailing attributes as text.
    Translating that is pointless and injects visible junk into the page, so any
    segment still containing angle brackets is left untouched.
    """
    return "<" not in segment and ">" not in segment


def collect_work(game: Path) -> dict:
    missing: dict[str, list[str]] = {}
    stats = []
    attrs = {}
    for entry, path in entries(game):
        document, _ = page_texts(path)
        _, result = html_files.localize_html(document, {}, {}, FORM_PROTECTED)
        for key in result["missing"]:
            if is_translatable(key):
                missing.setdefault(key, []).append(entry)
        attrs.setdefault(entry, {})["Form.html"] = attribute_fingerprint(document)
        stats.append({"entry": entry, "missing": len(result["missing"])})
    WORK.mkdir(parents=True, exist_ok=True)
    order = sorted(missing, key=lambda k: (-len(missing[k]), k.lower()))
    (WORK / "worklist.json").write_text(json.dumps({
        "schema": 1,
        "distinct": len(order),
        "pages": stats,
        "segments": order,
        "where": {k: missing[k] for k in order},
    }, ensure_ascii=False, indent=1), encoding="utf-8")
    (WORK / "attrs.json").write_text(
        json.dumps({"schema": 1, "pages": attrs}, ensure_ascii=False, indent=1),
        encoding="utf-8")
    print(f"work: {len(stats)} pages, {len(order)} distinct segments -> "
          f"{WORK / 'worklist.json'}")
    for row in stats:
        print(f"  {row['missing']:4d} segments  {row['entry']}")
    return {"distinct": len(order), "pages": len(stats)}


def load_table() -> dict[str, str]:
    if not SOURCE.is_file():
        return {}
    data = json.loads(SOURCE.read_text(encoding="utf-8"))
    return {k: v for k, v in data.items()
            if isinstance(v, str) and k and not k.startswith("_")}


def glossary() -> dict[str, str]:
    """Reference translations already curated for the database and UI files."""
    merged: dict[str, str] = {}
    for name in ("db_platforms_facilities.json", "db_record_names.json",
                 "db_enum_displays.json", "terms.json", "air_ops_terms.json"):
        path = DICT / "src" / name
        if not path.is_file():
            continue
        data = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            continue
        for key, value in data.items():
            if isinstance(value, str) and key and not key.startswith("_"):
                merged.setdefault(key.strip(), value)
    return merged


def hints() -> dict:
    """Attach existing curated translations to each segment as a glossary.

    Measured before writing this: automatic composition cannot carry these
    labels (of 503 parenthesised segments only 2 have *both* halves already
    translated), so the curated names are handed to the translator as required
    terminology instead of being glued together by code.
    """
    work = json.loads((WORK / "worklist.json").read_text(encoding="utf-8"))
    segments = work["segments"]
    reference = glossary()
    gram = 8
    index: dict[str, list[str]] = {}
    for key in reference:
        if len(key) < gram:
            continue
        index.setdefault(key[:gram], []).append(key)
    table = {}
    for segment in segments:
        found = []
        seen = set()
        for i in range(len(segment) - gram + 1):
            for key in index.get(segment[i:i + gram], ()):
                if key in seen or key not in segment:
                    continue
                seen.add(key)
                found.append((len(key), key))
        found.sort(reverse=True)
        table[segment] = {k: reference[k] for _, k in found[:4]}
    # Batches keep related entries together so terminology stays consistent.
    batches = []
    size = 160
    for start in range(0, len(segments), size):
        chunk = segments[start:start + size]
        batches.append([{"src": s, "glossary": table[s]} for s in chunk])
    (WORK / "batches").mkdir(parents=True, exist_ok=True)
    for old in (WORK / "batches").glob("batch_*.json"):
        old.unlink()
    for number, chunk in enumerate(batches, start=1):
        (WORK / "batches" / f"batch_{number:02d}.json").write_text(
            json.dumps(chunk, ensure_ascii=False, indent=1), encoding="utf-8")
    hinted = sum(1 for v in table.values() if v)
    print(f"hints: {len(segments)} segments, {hinted} carry glossary terms, "
          f"{len(batches)} batches -> {WORK / 'batches'}")
    return {"batches": len(batches), "hinted": hinted}


# Latin runs that stay as written: model/weapon designations and unit tokens.
KEEP_LATIN = {
    "SARH", "ARH", "IR", "AAM", "ASM", "ARM", "SAM", "SSN", "SSK", "SSBN", "SSGN",
    "GPS", "LGB", "JDAM", "ASW", "A2A", "A2G", "BVR", "nm", "km", "kg", "Blk", "Mk",
    "AGM", "RIM", "AIM", "RB", "AA", "SA", "Type", "Mod", "CWDB", "DB3K",
    "WRA", "EMCON", "ROE", "OECM", "DECM", "IRST", "SEAD", "CAP", "DCA", "AAW", "ASuW",
}
RE_LATIN_RUN = re.compile(r"[A-Za-z0-9][A-Za-z0-9'&./:_-]*")
RE_ROMAN = re.compile(r"[IVXivx]{1,5}(?:-[IVXivx]{1,5})*")
RE_BRACKET = re.compile(r"\[[^\]]*\]")


def guard(key: str, value: str) -> tuple[str | None, list[str]]:
    """Reject a translation that is not Chinese or changes the payload.

    Hard checks (reject) are structural: the row must carry Chinese, must keep
    every `%s`/`{0}` placeholder, and must not balloon in length.  Leftover Latin
    is *reported* rather than rejected: designations and project names are
    legitimate (`F-22A`, `AIM-120C-7`, `093B`, `Delta III`, `[Shchuka]`), and
    chasing them with an ever-growing allow-list would reject good rows.
    """
    if not isinstance(value, str) or not value.strip():
        return "empty", []
    if not re.search(r"[\u4e00-\u9fff]", value):
        return "no Chinese", []
    if html_files.PLACEHOLDER.findall(value) != html_files.PLACEHOLDER.findall(key):
        return "placeholder changed", []
    if len(value) > max(24, len(key) * 3):
        return "too long", []
    residual = []
    bracketed = [(m.start(), m.end()) for m in RE_BRACKET.finditer(value)]
    for match in RE_LATIN_RUN.finditer(value):
        token = match.group().strip("'&.-/: ")
        if not token or not re.search(r"[A-Za-z]", token):
            continue  # pure numbers are quantities, not English
        if (token in KEEP_LATIN or re.search(r"\d", token) or len(token) == 1
                or RE_ROMAN.fullmatch(token)
                or any(start <= match.start() < end for start, end in bracketed)):
            continue
        residual.append(token)
    return None, residual


def merge_translations(paths: list[Path]) -> dict:
    """Import batch results into the translation source, rejecting bad rows."""
    work = json.loads((WORK / "worklist.json").read_text(encoding="utf-8"))
    allowed = set(work["segments"])
    table = load_table()
    rejected: dict[str, str] = {}
    residual: dict[str, list[str]] = {}
    added = 0
    for path in paths:
        data = json.loads(path.read_text(encoding="utf-8"))
        rows = data.items() if isinstance(data, dict) else (
            (row.get("src", ""), row.get("zh", "")) for row in data)
        for key, value in rows:
            if not key or key.startswith("_"):
                continue
            if key not in allowed:
                rejected[key] = "not in worklist"
                continue
            problem, leftover = guard(key, value)
            if problem:
                rejected[key] = problem
                continue
            if leftover:
                residual[key] = leftover
            if table.get(key) != value:
                table[key] = value
                added += 1
    SOURCE.parent.mkdir(parents=True, exist_ok=True)
    merged = {"_note": ("快速战斗（Quick Battle）Form.html 显示文本译文。"
                         "键是页面里的原始显示片段（空白已折叠），"
                         "只用于 scripts/quickbattle_html.py，不进 build_dict 的 exact。")}
    merged.update({k: table[k] for k in sorted(table)})
    SOURCE.write_text(json.dumps(merged, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"import: {added} new/updated, {len(rejected)} rejected -> {SOURCE}")
    (WORK / "latin_audit.json").write_text(
        json.dumps(residual, ensure_ascii=False, indent=1), encoding="utf-8")
    if residual:
        print(f"  {len(residual)} rows keep Latin tokens (audit: {WORK / 'latin_audit.json'})")
    for key, why in list(rejected.items())[:10]:
        print(f"  reject {why}: {key[:80]!r}")
    return {"added": added, "rejected": len(rejected), "residual": len(residual)}


def build(game: Path) -> dict:
    table = {k: v for k, v in load_table().items() if is_translatable(k)}
    normalized = html_files.normalized_table(table)
    PAYLOAD.mkdir(parents=True, exist_ok=True)
    manifest = {"schema": 1, "source_sha256": html_files.digest(SOURCE.read_bytes()),
                "files": [], "translated_segments": 0, "untranslated_segments": 0,
                "missing": []}
    payload: dict[str, bytes] = {}
    for entry, path in entries(game):
        document, encoding = page_texts(path)
        translated, stats = html_files.localize_html(document, table, normalized, FORM_PROTECTED)
        relative = f"{ZIP_PREFIX}/{entry}/Form.html"
        payload[relative] = translated.encode(encoding, errors="xmlcharrefreplace")
        target = PAYLOAD / entry / "Form.html"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload[relative])
        before = attribute_fingerprint(document)
        after = attribute_fingerprint(translated)
        manifest["files"].append({
            "entry": entry, "path": relative,
            "translated_segments": stats["translated"],
            "untranslated_segments": len([s for s in stats["missing"] if is_translatable(s)]),
            "untranslated_samples": [s for s in stats["missing"] if is_translatable(s)][:5],
            "attrs_unchanged": before == after,
            "source_sha256": html_files.digest(path.read_bytes()),
        })
        manifest["translated_segments"] += stats["translated"]
        manifest["untranslated_segments"] += len([s for s in stats["missing"] if is_translatable(s)])
        manifest["missing"].extend([s for s in stats["missing"] if is_translatable(s)][:20])
        if before != after:
            raise SystemExit(f"ERROR: attributes changed while rendering {relative}")
    if len(payload) < BUNDLE_MIN:
        raise SystemExit(f"ERROR: only {len(payload)} Quick Battle pages rendered")
    with zipfile.ZipFile(BUNDLE, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in sorted(payload):
            archive.writestr(name, payload[name])
    manifest["bundle_sha256"] = html_files.digest(BUNDLE.read_bytes())
    manifest["coverage"] = ("complete" if not manifest["untranslated_segments"]
                            else "partial")
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"build: {len(payload)} pages, translated={manifest['translated_segments']}, "
          f"untranslated={manifest['untranslated_segments']} ({manifest['coverage']})")
    if manifest["untranslated_segments"]:
        for row in manifest["files"]:
            if row["untranslated_segments"]:
                print(f"  {row['untranslated_segments']:3d} left  {row['entry']}: "
                      f"{row['untranslated_samples'][:2]}")
    print(f"  bundle -> {BUNDLE}")
    return manifest


def verify(game: Path, allow_partial: bool = False) -> int:
    problems: list[str] = []
    if not BUNDLE.is_file():
        problems.append(f"missing bundle: {BUNDLE}")
        bundle_names: set[str] = set()
        bundle: dict[str, bytes] = {}
    else:
        with zipfile.ZipFile(BUNDLE) as archive:
            bundle_names = set(archive.namelist())
            bundle = {n: archive.read(n) for n in bundle_names}
        for name in bundle_names:
            pure = Path(name)
            if "\\" in name or pure.is_absolute() or ".." in pure.parts:
                problems.append(f"unsafe bundle entry: {name}")
            if not name.startswith(ZIP_PREFIX + "/") or not name.endswith("/Form.html"):
                problems.append(f"unexpected bundle entry: {name}")

    for entry, path in entries(game):
        relative = f"{ZIP_PREFIX}/{entry}/Form.html"
        if relative not in bundle:
            problems.append(f"page missing from bundle: {relative}")
            continue
        original, encoding = page_texts(path)
        rendered = html_files.decode_html(bundle[relative])[0]
        if attribute_fingerprint(original) != attribute_fingerprint(rendered):
            problems.append(f"attributes changed: {relative}")
        if "<select" in original and original.count("<select") != rendered.count("<select"):
            problems.append(f"select count changed: {relative}")
        if original.count("<option") != rendered.count("<option"):
            problems.append(f"option count changed: {relative}")
        _, stats = html_files.localize_html(rendered, {}, {}, FORM_PROTECTED)
        residual = [s for s in stats["missing"] if is_translatable(s)]
        if residual:
            if allow_partial:
                print(f"  note: {relative} still has {len(residual)} English segments")
            else:
                problems.append(
                    f"{len(residual)} English segments left in {relative}: "
                    f"{residual[:3]}")

    unexpected = {n for n in bundle_names if n not in
                  {f"{ZIP_PREFIX}/{e}/Form.html" for e, _ in entries(game)}}
    if unexpected:
        problems.append(f"bundle has entries the game does not: {sorted(unexpected)[:3]}")

    for problem in problems:
        print("FAIL:", problem)
    if problems:
        print(f"verify: {len(problems)} problem(s)")
        return 1
    print(f"verify: {len(bundle)} pages OK (attributes unchanged, no English left)")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command",
                        choices=("work", "hints", "import", "build", "verify"))
    parser.add_argument("--game-dir", type=Path,
                        default=Path(os.environ.get("GAME_DIR")
                                     or "G:/soft/steam/steamapps/common/Command - Modern Operations"))
    parser.add_argument("--allow-partial", action="store_true",
                        help="verify: report residual English instead of failing")
    parser.add_argument("files", nargs="*", type=Path,
                        help="import: batch result JSON files")
    args = parser.parse_args()
    if args.command == "work":
        collect_work(args.game_dir)
        return 0
    if args.command == "hints":
        hints()
        return 0
    if args.command == "import":
        if not args.files:
            parser.error("import needs at least one batch JSON file")
        merge_translations(args.files)
        return 0
    if args.command == "build":
        build(args.game_dir)
        return 0
    return verify(args.game_dir, args.allow_partial)


if __name__ == "__main__":
    raise SystemExit(main())
