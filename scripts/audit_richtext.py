#!/usr/bin/env python3
"""Audit the reviewed help corpus offline; optionally compare extracted resources.

python scripts/audit_richtext.py
python scripts/audit_richtext.py --resources build/hotkeys-resource-report.json
The output is a reviewed-corpus inventory, never a live UI coverage claim.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
NEWLINES = re.compile(r"\r\n|\r|\n")
CJK = re.compile(r"[\u3400-\u9fff]")
PROTECTED = re.compile(
    r"#[A-Za-z][A-Za-z0-9_.*;()/-]*"
    r"|https?://[A-Za-z0-9._~:/?#\[\]@!$&()*+,;=%-]+"
    r"|\b[A-Za-z0-9_.-]+\.(?:scen|save|json|xml|html|csv|txt|dll|exe|ini|png|jpg)\b"
    r"|(?<!\w)\.(?:png|jpg)\b"
    r"|\{[^{}\r\n]+\}|%(?:\d+\$)?[-+0-9.]*[sdf]"
    r"|\b(?:DB|SBR)\b|-->")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def text_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def recover_records(records: dict, source: dict) -> dict:
    by_hash = {text_hash(key): key for key in source if not key.startswith("_")}
    result = {}
    for name, record in records.items():
        digest = record["sha256"]
        if digest not in by_hash:
            raise ValueError(f"Reviewed source hash missing: {name} {digest}")
        result[name] = by_hash[digest]
    return result


def recover_texts(inventory: dict, source: dict) -> tuple[dict, dict]:
    return recover_records(inventory["controls"], source), recover_records(inventory["prose"], source)


def parse_lines(text: str) -> list[dict]:
    rows = []
    for line in NEWLINES.split(text):
        if not line.strip():
            continue
        if "\t" in line:
            prefix, description, suffix = re.fullmatch(r"([^\t]*\t[ \t]*)(.*?)([ \t]*)", line).groups()
            shortcut = prefix.split("\t", 1)[0].strip()
        else:
            prefix, description, suffix = re.fullmatch(r"([ \t]*)(.*?)([ \t]*)", line).groups()
            shortcut = ""
        if not description:
            raise ValueError(f"Help line has no description: {line!r}")
        rows.append({"rawLine": line, "shortcut": shortcut, "description": description,
                     "prefix": prefix, "suffix": suffix})
    return rows


def check_layout(source: str, translated: str, label: str, errors: list[str]) -> None:
    if NEWLINES.findall(source) != NEWLINES.findall(translated):
        errors.append(f"{label}: newline layout changed")
        return
    original_lines, translated_lines = NEWLINES.split(source), NEWLINES.split(translated)
    for original, result in zip(original_lines, translated_lines):
        if original.lstrip().startswith("#"):
            if original != result:
                errors.append(f"{label}: code example changed")
            continue
        if not original.strip():
            if original != result:
                errors.append(f"{label}: blank-line whitespace changed")
            continue
        before = parse_lines(original)[0]
        try:
            after = parse_lines(result)[0] if result.strip() else None
        except ValueError:
            after = None
        if after is None:
            errors.append(f"{label}: translated description is empty")
            continue
        if (before["prefix"], before["suffix"]) != (after["prefix"], after["suffix"]):
            errors.append(f"{label}: shortcut/Tab/edge whitespace changed")
        if original.count("\t") != result.count("\t"):
            errors.append(f"{label}: Tab layout changed")
        if not CJK.search(after["description"]):
            errors.append(f"{label}: translated description has no Chinese")


def resource_texts(inventory: dict, reviewed: dict, pool: dict, resources: dict) -> tuple[dict, list[str]]:
    selected_filter = resources.get("resourceFilter", "Hotkeys").lower()
    expected = {name for name, row in inventory["controls"].items()
                if row["source"]["kind"] == "manifest-resource"
                and selected_filter in row["source"]["resource"].lower()}
    texts = {name: reviewed[name] for name, row in inventory["controls"].items()
             if row["source"]["kind"] == "manifest-resource" and name not in expected}
    unknown = []
    for row in resources.get("rows", []):
        resource = row.get("resource", "").lower()
        if (selected_filter not in resource or "hotkeys" not in resource
                or not row.get("key", "").endswith(".Text")):
            continue
        name = row["key"][:-5]
        if name not in expected:
            unknown.append(f"Unreviewed resource control: {name}")
        elif name in texts:
            unknown.append(f"Duplicate resource control: {name}")
        elif not isinstance(row.get("text"), str):
            unknown.append(f"Unparsed resource text: {name}")
        else:
            texts[name] = row["text"]
    unknown.extend(f"Missing resource control: {name}" for name in sorted(expected - texts.keys()))
    values = list(pool.values())
    for name, row in inventory["controls"].items():
        if row["source"]["kind"] != "string-pool":
            continue
        positions = [index for index, value in enumerate(values) if value == name]
        if len(positions) != 1 or positions[0] + 1 >= len(values):
            unknown.append(f"Missing/ambiguous string-pool control marker: {name}")
        else:
            texts[name] = values[positions[0] + 1]
    for name, text in texts.items():
        if text_hash(text) != inventory["controls"][name]["sha256"]:
            unknown.append(f"Unreviewed control text hash: {name}")
    return texts, unknown


def extra_resource_texts(inventory: dict, reviewed: dict, resources: dict) -> tuple[dict, list[str]]:
    """Compare only resource sets selected by the supplied extractor's filter."""
    texts = reviewed.copy()
    unknown = []
    selected_filter = resources.get("resourceFilter", "Hotkeys").lower()
    expected = {name: row for name, row in inventory["resourceHelp"].items()
                if selected_filter in row["source"]["resource"].lower()}
    observed = set()
    for row in resources.get("rows", []):
        name = row.get("resource", "") + ":" + row.get("key", "")
        if name not in expected:
            continue
        if name in observed:
            unknown.append(f"Duplicate help resource: {name}")
            continue
        observed.add(name)
        text = row.get("text")
        if not isinstance(text, str):
            unknown.append(f"Unparsed help resource: {name}")
        elif text_hash(text) != expected[name]["sha256"]:
            unknown.append(f"Unreviewed help resource hash: {name}")
            texts[name] = text
        else:
            texts[name] = text
    unknown.extend(f"Missing help resource: {name}" for name in expected.keys() - observed)
    return texts, unknown


def resource_scope(inventory: dict, resources: dict) -> tuple[list[str], int]:
    known = {(row["source"]["resource"], row["source"]["key"])
             for group in (inventory["controls"], inventory["resourceHelp"])
             for row in group.values() if row["source"]["kind"] == "manifest-resource"}
    known_sets = {resource for resource, _ in known}
    excluded = {(row["resource"], row["key"]): row["sha256"]
                for row in inventory["excludedResources"]}
    unknown, outside = [], 0
    selected_filter = resources.get("resourceFilter", "Hotkeys").lower()
    for row in resources.get("rows", []):
        resource, key = row.get("resource", ""), row.get("key", "")
        text = row.get("text")
        if (resource, key) in known:
            continue
        if isinstance(text, str) and excluded.get((resource, key)) == text_hash(text):
            continue
        body_key = key.endswith((".Text", ".ToolTip"))
        multiline = isinstance(text, str) and bool(NEWLINES.search(text))
        if body_key and (resource in known_sets or (resource.startswith("Command.") and multiline)):
            unknown.append(f"Unreviewed UI help resource: {resource}:{key}")
    for row in resources.get("unknownResources", []):
        resource, reason = row.get("resource", ""), row.get("reason")
        if reason == "outside-resource-filter" and selected_filter and selected_filter not in resource.lower():
            outside += 1
        elif resource in known_sets or (resource.startswith("Command.") and resource.endswith(".resources")):
            unknown.append(f"Unparsed UI resource: {resource} ({reason})")
        elif reason == "not-a-standard-resource-set" and not resource.endswith(".resources"):
            outside += 1
        else:
            unknown.append(f"Unparsed resource: {resource} ({reason})")
    return unknown, outside


def audit(inventory: dict, source: dict, exact: dict, pool: dict,
          resources: dict | None = None) -> dict:
    reviewed, prose = recover_texts(inventory, source)
    extra = recover_records(inventory["resourceHelp"], source)
    texts, unknown = (resource_texts(inventory, reviewed, pool, resources)
                      if resources is not None else (reviewed, []))
    outside_scope = 0
    if resources is not None:
        selected_count = resources.get("selectedResourceCount")
        if type(selected_count) is not int or selected_count <= 0:
            unknown.append("Resource report selectedResourceCount must be a positive integer")
        extra, extra_unknown = extra_resource_texts(inventory, extra, resources)
        scoped_unknown, outside_scope = resource_scope(inventory, resources)
        unknown.extend(extra_unknown + scoped_unknown)
    errors, controls = [], []
    for name, record in inventory["controls"].items():
        if name not in texts:
            continue
        raw = texts[name]
        lines = parse_lines(raw)
        if len(lines) != record["lineCount"]:
            unknown.append(f"Unreviewed line count: {name}")
        for index, row in enumerate(lines, 1):
            description = row["description"]
            translated = exact.get(description)
            if not isinstance(translated, str) or not CJK.search(translated):
                errors.append(f"{name}:{index}: missing Chinese description: {description}")
            row["exactTranslation"] = translated
            line_translation = exact.get(row["rawLine"])
            if not isinstance(line_translation, str):
                errors.append(f"{name}:{index}: missing exact layout line")
            else:
                check_layout(row["rawLine"], line_translation, f"{name}:{index}", errors)
        full_translation = exact.get(raw)
        if not isinstance(full_translation, str):
            errors.append(f"{name}: missing exact body")
        else:
            check_layout(raw, full_translation, name, errors)
        is_rtf = any(row.get("key") == name + ".Text"
                     and row.get("resource") == record["source"].get("resource")
                     and row.get("isRtf", False)
                     for row in resources.get("rows", [])) if resources is not None else False
        input_source = "reviewed-source"
        if resources is not None:
            if record["source"]["kind"] == "string-pool":
                input_source = "string-pool-snapshot"
            elif resources.get("resourceFilter", "Hotkeys").lower() in record["source"]["resource"].lower():
                input_source = "resource-report"
        controls.append({"controlName": name, "rawText": raw, "source": record["source"],
                         "inputSource": input_source,
                         "isRtf": is_rtf, "lines": lines, "lineCount": len(lines),
                         "fullTextExactTranslation": full_translation})
    prose_rows, help_rows = [], []
    for name, raw in list(prose.items()) + list(extra.items()):
        translated = exact.get(raw)
        if not isinstance(translated, str) or not CJK.search(translated):
            errors.append(f"help {name}: missing Chinese translation")
        else:
            check_layout(raw, translated, f"help {name}", errors)
            if PROTECTED.findall(raw) != PROTECTED.findall(translated):
                errors.append(f"help {name}: URL/path/format tokens changed")
        if name in prose:
            prose_rows.append({"offset": name, "rawText": raw, "exactTranslation": translated})
        else:
            record = inventory["resourceHelp"][name]
            lines = parse_lines(raw)
            line_count = len(lines)
            if line_count != record["lineCount"]:
                unknown.append(f"Unreviewed help line count: {name}")
            # Rich help is translated by individual cells/lines at runtime.
            # A complete body entry alone cannot cover those lookups.
            for index, row in enumerate(lines, 1):
                description = row["description"]
                if description.startswith("#"):
                    continue
                line_translation = exact.get(description)
                if not isinstance(line_translation, str) or not CJK.search(line_translation):
                    errors.append(f"help {name}:{index}: missing Chinese description: {description}")
            observed = resources is not None and any(
                row.get("resource") == record["source"]["resource"]
                and row.get("key") == record["source"]["key"] and isinstance(row.get("text"), str)
                for row in resources.get("rows", []))
            help_rows.append({"resourceId": name, "rawText": raw, "lineCount": line_count,
                              "inputSource": "resource-report" if observed else "reviewed-source",
                              "source": record["source"], "exactTranslation": translated})
    count = sum(row["lineCount"] for row in controls)
    if len(controls) != inventory["controlCount"] or count != inventory["lineCount"]:
        unknown.append("Reviewed control/line totals were not recovered")
    if (len(help_rows) != inventory["resourceHelpCount"]
            or sum(row["lineCount"] for row in help_rows) != inventory["resourceHelpLineCount"]):
        unknown.append("Reviewed additional help totals were not recovered")
    assembly = inventory["sourceAssembly"]
    if resources is not None:
        assembly = {key: resources.get(key) for key in ("source", "sha256", "assembly", "fileVersion", "productVersion")}
        if not assembly["sha256"]:
            unknown.append("Resource report source assembly SHA256 is missing")
    return {"scope": "reviewed-corpus", "ok": not errors and not unknown,
            "sourceAssembly": assembly, "controlCount": len(controls), "lineCount": count,
            "controls": controls, "prose": prose_rows, "errors": errors, "unknown": unknown,
            "resourceHelp": help_rows, "resourceHelpCount": len(help_rows),
            "resourceHelpLineCount": sum(row["lineCount"] for row in help_rows),
            "reviewedBodyCount": len(controls) + len(prose_rows) + len(help_rows),
            "outsideScopeUnknownCount": outside_scope,
            "limitations": inventory["limitations"] + [
                "Four Hotkeys string-pool bodies remain committed snapshots, including when --resources is supplied.",
                "Additional help outside an extractor's resource filter is checked from the reviewed source only.",
                "Outside-scope unknown counts retain unparsed containers, scripts, templates or filtered resources; they are not declared translated."]}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resources", type=Path, help="ExtractRichTextResources JSON report")
    parser.add_argument("--dictionary", type=Path, default=ROOT / "dict/CMOLocalizer.json",
                        help="Runtime dictionary JSON to audit (the exact section)")
    parser.add_argument("--output", type=Path, default=ROOT / "build/hotkeys-corpus.json")
    args = parser.parse_args()
    result = audit(load_json(ROOT / "docs/RICHTEXT_INVENTORY.json"),
                   load_json(ROOT / "dict/src/richtext_help.json"),
                   load_json(args.dictionary)["exact"],
                   load_json(ROOT / "dict/strings_pool.json"),
                   load_json(args.resources) if args.resources else None)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"reviewed-corpus: controls={result['controlCount']} lines={result['lineCount']} "
          f"prose={len(result['prose'])} resourceHelp={result['resourceHelpCount']} "
          f"errors={len(result['errors'])} unknown={len(result['unknown'])} "
          f"outsideScopeUnknown={result['outsideScopeUnknownCount']}")
    for message in result["errors"] + result["unknown"]:
        print("FAIL: " + message)
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
