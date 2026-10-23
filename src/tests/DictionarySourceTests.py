#!/usr/bin/env python3
"""Focused dictionary-source regressions. Never overwrite the shipped dictionary.

Default: execute small negative fixtures and two complete real-source builds.
--fixtures: fixtures only. All outputs/source deletions occur in TemporaryDirectory.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import build_dict as builder  # noqa: E402


def check(ok: bool, message: str) -> None:
    if not ok:
        raise AssertionError(message)
    print("PASS " + message, flush=True)


def sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def put(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n",
                    encoding="utf-8", newline="\n")


@contextmanager
def fixture():
    with tempfile.TemporaryDirectory(prefix="cmo-dictionary-source-") as directory:
        root = Path(directory)
        dictionary = root / "dict"
        source = dictionary / "src"
        source.mkdir(parents=True)
        (root / "VERSION").write_text("fixture-only\n", encoding="utf-8", newline="\n")
        put(dictionary / "glossary.json", {})
        put(dictionary / "strings_pool.json", {})
        with patch.multiple(builder, ROOT=root, DICT=dictionary, SRC=source,
                            MAIN=dictionary / "CMOLocalizer.json"):
            yield root, dictionary, source


def fixture_checks() -> None:
    with fixture() as (_, dictionary, source):
        en = "Removable description."
        translated = "可以删除的说明。"
        put(source / "live_audit_fixes.json", {en: translated})
        first = builder.build()
        builder.write(first, builder.MAIN)
        check(first["exact"].get(en) == translated, "sole named source is included")
        (source / "live_audit_fixes.json").unlink()
        rebuilt = builder.build()
        check(en not in rebuilt["exact"], "deleting a source file cannot resurrect its old output translation")
        builder.MAIN.write_text("not valid JSON; never read as input", encoding="utf-8")
        check(builder.build() == rebuilt, "corrupt old output cannot alter a source-only build")
        builder.MAIN.unlink()
        check(builder.build() == rebuilt, "missing old output cannot alter a source-only build")

    with fixture() as (_, dictionary, source):
        pool = {"100": "Legacy display phrase", "102": "Named display phrase",
                "104": "Deleted display phrase", "106": "LongName ASC",
                "108": "Config", "110": "Game Preferences", "112": "Aircraft",
                "114": "Current build 1900.23", "116": "Value {0}",
                "118": "SELECT Name FROM Units", "120": r"C:\Game\Config.ini",
                "122": "Preserved original phrase", "124": "Bad short label", "7916": "Steam Workshop"}
        put(dictionary / "strings_pool.json", pool)
        put(source / "identifiers.json", {"identifiers": ["Config", "Game Preferences", "Steam Workshop"]})
        put(source / "terms.json", {"Aircraft": "飞机", "Test Term": "测试术语"})
        put(source / "legacy_preserved.json", {
            "exact": {"Legacy display phrase": "保全显示文本", "Named display phrase": "旧译文",
                      "LongName ASC": "名称升序", "Config": "配置", "Game Preferences": "游戏首选项",
                      "Aircraft": "飞机", "Current build 1900.23": "旧构建标题",
                      "Value {0}": "数值 {0}", "SELECT Name FROM Units": "查询单位名称",
                      r"C:\Game\Config.ini": "配置文件", "Preserved original phrase": "旧池显示文本",
                      "Bad short label": "如令查收。", "Blacklisted phrase": "应当删除",
                      "Bracket label": "失衡（", "Multiline\nlabel": "单行标签",
                      "Plain words": "untranslated"},
            "offsets": {key: en for key, en in pool.items() if key != "7916"}
        })
        put(source / "menu_terms.json", {"Named display phrase": "新译文", "Blacklisted phrase": "新黑名单译文"})
        put(source / "blacklist.json", ["Blacklisted phrase"])
        put(source / "labels.json", {"Bracket label": "失衡（", "Steam Workshop": "创意工坊"})
        put(source / "force_offsets.json", {"7916": "创意工坊"})
        doc = builder.build()
        check(doc["exact"].get("Legacy display phrase") == "保全显示文本" and
              doc["offsets"].get("100") == "保全显示文本", "legal legacy translation and explicit location survive")
        check(doc["exact"].get("Named display phrase") == "新译文" and
              doc["offsets"].get("102") == "新译文", "named source has one priority over legacy in exact and offsets")
        check("104" not in doc["offsets"], "an offset location alone cannot recreate a deleted translation")
        unsafe = {"106", "108", "110", "112", "114", "116", "118", "120"}
        check(not (unsafe & doc["offsets"].keys()), "SQL/path/format/build/native identifiers never become pool injections")
        check(doc["exact"].get("Game Preferences") == "游戏首选项" and
              doc["exact"].get("Aircraft") == "飞机", "native identifier display translations remain exact-only")
        check(doc["offsets"].get("7916") == "创意工坊", "vetted force offset still overrides the identifier filter")
        check(doc["words"] == {"Aircraft": "飞机", "Test Term": "测试术语"} and doc["partial"] == {},
              "words vocabulary and empty partial contract are preserved")
        check(not ({"Bad short label", "Blacklisted phrase", "Bracket label", "Multiline\nlabel", "Plain words"}
                   & doc["exact"].keys()) and "124" not in doc["offsets"],
              "pollution/blacklist/invalid structure/untranslated negative entries are removed from exact and offsets")
        put(dictionary / "strings_pool.json", {**pool, "122": "Changed current pool phrase"})
        check("122" not in builder.build()["offsets"], "changed pool text cannot reuse a preserved offset")
        preserved = builder.load(source / "legacy_preserved.json")
        del preserved["exact"]["Legacy display phrase"]
        put(source / "legacy_preserved.json", preserved)
        rebuilt = builder.build()
        check("Legacy display phrase" not in rebuilt["exact"] and "100" not in rebuilt["offsets"],
              "deleting a sole preserved key removes exact and offsets despite retained location metadata")

        en = "Blacklisted phrase"
        put(source / "live_audit_fixes.json", {en: "黑名单译文"})
        put(source / "blacklist.json", [en])
        put(source / "force_offsets.json", {"100": "黑名单强制译文"})
        put(dictionary / "strings_pool.json", {"100": en})
        doc = builder.build()
        check(en not in doc["exact"] and "100" not in doc["offsets"],
              "blacklist cannot be bypassed by a force offset")

    with fixture() as (_, _, source):
        en = "Atomic build description."
        put(source / "live_audit_fixes.json", {en: "原子构建说明。"})
        doc = builder.build()
        builder.write(doc, builder.MAIN)
        original = builder.MAIN.read_bytes()
        replacement = {**doc, "exact": {en: "不会覆盖的说明。"}}
        attempts = 0
        def denied(*args, **kwargs):
            nonlocal attempts
            attempts += 1
            raise PermissionError("synthetic locked destination")
        with patch.object(builder.os, "replace", denied), patch.object(builder.time, "sleep"):
            try:
                builder.write(replacement, builder.MAIN)
                raise AssertionError("permanent replace denial must fail")
            except PermissionError:
                pass
        check(attempts == 6 and builder.MAIN.read_bytes() == original and
              not builder.MAIN.with_name(builder.MAIN.name + ".tmp").exists(),
              "bounded atomic replace failure preserves old bytes and removes staging file")
        import os
        replace = os.replace
        attempts = 0
        def transient(*args, **kwargs):
            nonlocal attempts
            attempts += 1
            if attempts == 1:
                raise PermissionError("synthetic transient lock")
            return replace(*args, **kwargs)
        with patch.object(builder.os, "replace", transient), patch.object(builder.time, "sleep"):
            builder.write(replacement, builder.MAIN)
        check(attempts == 2 and builder.load(builder.MAIN)["exact"][en] == "不会覆盖的说明。",
              "transient destination lock retries without truncate fallback")


def real_source_checks() -> None:
    old_bytes = builder.MAIN.read_bytes()
    old = json.loads(old_bytes)
    original_loader = builder.load
    def source_loader(path):
        if Path(path) == builder.MAIN:
            raise AssertionError("build attempted to read its output as input")
        return original_loader(path)
    with tempfile.TemporaryDirectory(prefix="cmo-real-dictionary-") as directory:
        directory = Path(directory)
        first = directory / "first.json"
        second = directory / "second.json"
        for path in (first, second):
            start = time.perf_counter()
            with patch.object(builder, "load", source_loader):
                doc = builder.build()
            builder.write(doc, path)
            payload = path.read_bytes()
            print(f"FULL_BUILD {path.name} seconds={time.perf_counter()-start:.3f} "
                  f"bytes={len(payload)} sha256={sha256(payload)} counts={doc['_meta']['counts']}", flush=True)
        first_bytes, second_bytes = first.read_bytes(), second.read_bytes()
        check(first_bytes == second_bytes, "two full real-source serialized byte rebuilds are identical")
        check(first_bytes == old_bytes, "real-source rebuild exactly preserves all existing shipped dictionary bytes")
        check(b"\r\n" not in first_bytes, "serialized dictionary retains LF-only bytes")
        doc = json.loads(first_bytes)
        for section in ("exact", "offsets", "words", "partial"):
            missing = old[section].keys() - doc[section].keys()
            drift = [k for k, v in old[section].items() if doc[section].get(k) != v]
            check(not missing and not drift and len(doc[section]) == len(old[section]),
                  f"all old {section} entries/values retained: {len(doc[section])}")
        pool = original_loader(builder.DICT / "strings_pool.json")
        disagreements = [off for off, zh in doc["offsets"].items()
                         if doc["exact"].get(pool.get(off)) != zh]
        check(not disagreements, "every real offset agrees with its exact translation")
        terms = builder.load_terms()
        check(doc["words"] == {k: terms[k] for k in sorted(terms)}, "real words come only from the current terms source")
        preserved_path = builder.SRC / "legacy_preserved.json"
        preserved = original_loader(preserved_path)
        named_keys = set()
        for _, mapping in builder.iter_source_maps():
            named_keys.update(mapping)
        for path in [builder.SRC / "labels.json", builder.SRC / "dlc_briefing_fixes.json",
                     builder.DICT / "briefing_dict.json", builder.DICT / "brief_translations.json"]:
            named_keys.update(original_loader(path))
        for section, mapping in original_loader(builder.DICT / "glossary.json").items():
            if not section.startswith("_") and isinstance(mapping, dict):
                named_keys.update(mapping)
        forced = original_loader(builder.SRC / "force_offsets.json")
        named_keys.update(pool[off] for off in forced if off in pool)
        candidate = next(en for off, en in preserved["offsets"].items()
                         if en in preserved["exact"] and en not in named_keys and off in doc["offsets"])
        deleted = {**preserved, "exact": {k: v for k, v in preserved["exact"].items() if k != candidate}}
        deleted_path = directory / "deleted-preserved-source.json"
        put(deleted_path, deleted)
        def deleted_source_loader(path):
            if Path(path) == preserved_path:
                return original_loader(deleted_path)
            return source_loader(path)
        with patch.object(builder, "load", deleted_source_loader):
            after_delete = builder.build()
        check(candidate not in after_delete["exact"] and
              all(pool.get(off) != candidate for off in after_delete["offsets"]),
              "real sole-source deletion cannot resurrect from output or preserved offset metadata: " + repr(candidate))
        print("REAL_DELETION " + repr(candidate) + " old=" + repr(doc["exact"][candidate]) +
              " after_delete=absent", flush=True)
    check(builder.MAIN.read_bytes() == old_bytes, "tests leave the shipped artifact untouched")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixtures", action="store_true", help="run only focused small negative fixtures")
    args = parser.parse_args()
    fixture_checks()
    if not args.fixtures:
        real_source_checks()
    print("ALL DICTIONARY SOURCE CHECKS PASS", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
