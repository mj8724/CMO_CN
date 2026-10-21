#!/usr/bin/env python3
"""Audit the complete source inventory before compiling Chinese map labels.

This gate checks recorded evidence; it cannot discover a missing historic layer
or certify raster parity itself. A candidate source snapshot must explicitly
remain unverified until its evidence has been reviewed.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import struct
import tempfile
from urllib.parse import urlsplit

LAYERS = ("city", "admin0", "admin1", "marine", "water", "park", "peak", "region")
MAGIC = b"CMOMAP01"
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
# Pin assigned Han ranges to Unicode 15, shared with the .NET 4.8 consumer.
# Its built-in Unicode table is older than Python's; using either table here
# would otherwise produce catalogs that one side accepts and the other rejects.
HAN_RANGES = ((0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFA6D),
              (0xFA70, 0xFAD9), (0x20000, 0x2A6DF), (0x2A700, 0x2B739),
              (0x2B740, 0x2B81D), (0x2B820, 0x2CEA1), (0x2CEB0, 0x2EBE0),
              (0x2F800, 0x2FA1D), (0x30000, 0x3134A), (0x31350, 0x323AF))
NAME_SEPARATORS = " !\"#%&'()*,-./:;?@[\\]_{}·、。・‧…‐‑‒–—―‘’“”（）［］【】《》〈〉「」『』〔〕，；：？！－．"


def read_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()]


def indexed(records: list[dict], label: str) -> dict[str, dict]:
    result = {}
    for record in records:
        identity = record.get("id")
        if not isinstance(identity, str) or not identity.strip():
            raise ValueError(f"{label}: missing record id")
        if identity in result:
            raise ValueError(f"{label}: duplicate id {identity!r}")
        result[identity] = record
    return result


def is_han(character: str) -> bool:
    code = ord(character)
    return any(low <= code <= high for low, high in HAN_RANGES)


def chinese_name(value: object) -> bool:
    if not isinstance(value, str) or not value.strip() or value != value.strip():
        return False
    if not any(is_han(character) for character in value):
        return False
    # An explicit shared set avoids new Unicode digits/punctuation unsupported
    # by the old CLR's category table, and excludes other writing systems.
    return all(is_han(character) or "0" <= character <= "9"
               or "\uff10" <= character <= "\uff19" or character == "\u3007"
               or "\u3021" <= character <= "\u3029" or character in NAME_SEPARATORS
               for character in value)


def http_url(value: object) -> bool:
    if not isinstance(value, str) or any(character.isspace() for character in value):
        return False
    parts = urlsplit(value)
    return parts.scheme in ("http", "https") and bool(parts.hostname)


def record_fingerprint(record: dict) -> str:
    wire = json.dumps(record, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(wire.encode("utf-8")).hexdigest()


def check_sources(manifest: dict, inventory_path: Path) -> tuple[dict, list[str]]:
    if manifest.get("schema_version") != 1:
        raise ValueError("unsupported catalog schema_version")
    if (manifest.get("min_zoom"), manifest.get("max_zoom")) != (3, 10):
        raise ValueError("CMO source scope must cover zoom 3 through 10")
    actual = hashlib.sha256(inventory_path.read_bytes()).hexdigest()
    if manifest.get("inventory_sha256") != actual:
        raise ValueError("inventory SHA256 differs from the reviewed manifest")
    sources = indexed(manifest["sources"], "sources")
    for source in sources.values():
        if (not http_url(source.get("url"))
                or not SHA256.fullmatch(source.get("sha256", ""))
                or not isinstance(source.get("license"), str) or not source["license"].strip()):
            raise ValueError(f"source requires URL, SHA256 and license: {source['id']}")
    layers = manifest.get("layers", {})
    gaps = []
    for kind in LAYERS:
        layer = layers.get(kind, {})
        if layer.get("complete") is not True or not http_url(layer.get("evidence")):
            gaps.append(f"{kind}: source inventory/evidence incomplete")
        source_ids = layer.get("source_ids", [])
        if not source_ids or any(source_id not in sources for source_id in source_ids):
            gaps.append(f"{kind}: source identity missing or unknown")
    for field in ("snapshot_verified", "parity_verified"):
        if manifest.get(field) is not True:
            gaps.append(f"{field}: not verified")
    if manifest.get("counts", {}).get("unresolved_source_records", 0):
        gaps.append("unresolved_source_records: source records still need visibility review")
    return sources, gaps


def validate_inventory(records: dict[str, dict], sources: dict, manifest: dict) -> None:
    if not records:
        raise ValueError("source inventory is empty")
    for record in records.values():
        identity = record["id"]
        kind = record.get("kind")
        if kind not in LAYERS:
            raise ValueError(f"unknown label kind: {identity}")
        source_id = record.get("source_id")
        if source_id not in sources:
            raise ValueError(f"unknown source: {identity}")
        if source_id not in manifest["layers"].get(kind, {}).get("source_ids", []):
            raise ValueError(f"record source not assigned to layer: {identity}")
        if not isinstance(record.get("name"), str) or not record["name"].strip():
            raise ValueError(f"empty original name: {identity}")
        for key, limit in (("lat", 85.05112878), ("lon", 180)):
            value = record.get(key)
            if (isinstance(value, bool) or not isinstance(value, (int, float))
                    or not math.isfinite(value) or not -limit <= value <= limit):
                raise ValueError(f"invalid Web Mercator {key}: {identity}")
        for key in ("min_zoom", "max_zoom", "priority"):
            if type(record.get(key)) is not int:
                raise ValueError(f"invalid integer {key}: {identity}")
        if not 3 <= record["min_zoom"] <= record["max_zoom"] <= 10:
            raise ValueError(f"invalid zoom range: {identity}")
        if not 0 <= record["priority"] <= 1000000:
            raise ValueError(f"invalid priority: {identity}")
        required = record.get("required_display_text", [])
        if (not isinstance(required, list)
                or any(not isinstance(value, str) or not value for value in required)):
            raise ValueError(f"invalid required display text: {identity}")


def audit(manifest: dict, inventory_path: Path, translations_path: Path,
          reviews_path: Path | None = None) -> tuple[dict, list[dict]]:
    sources, source_gaps = check_sources(manifest, inventory_path)
    inventory = indexed(read_jsonl(inventory_path), "inventory")
    validate_inventory(inventory, sources, manifest)
    anchor_gaps = sum(record.get("anchor_verified") is False for record in inventory.values())
    if anchor_gaps:
        source_gaps.append(f"anchor_verified: {anchor_gaps} record anchors not verified")
    translations = indexed(read_jsonl(translations_path), "translations")
    unknown = translations.keys() - inventory.keys()
    if unknown:
        raise ValueError(f"translations not in source inventory: {sorted(unknown)[:5]}")
    reviews = indexed(read_jsonl(reviews_path), "manual reviews") if reviews_path else {}
    for identity, review in reviews.items():
        if identity not in inventory:
            raise ValueError(f"manual review absent from inventory: {identity}")
        if (review.get("status") != "approved" or not chinese_name(review.get("zh"))
                or not http_url(review.get("source"))
                or review.get("record_sha256") != record_fingerprint(inventory[identity])
                or not isinstance(review.get("review_notes"), str)
                or not review["review_notes"].strip()):
            raise ValueError(f"invalid or stale manual review: {identity}")
        translations[identity] = review
    approved, gaps = [], []
    counts = Counter()
    for identity in sorted(inventory):
        record = inventory[identity]
        translation = translations.get(identity, {})
        reason = None
        if not chinese_name(translation.get("zh")):
            reason = "missing_or_non_chinese"
        elif translation.get("status") != "approved":
            reason = "unreviewed_or_ambiguous"
        elif translation.get("record_sha256") != record_fingerprint(record):
            reason = "source_record_changed_or_unbound"
        elif not http_url(translation.get("source")):
            reason = "translation_source_missing"
        elif any(not re.search(r"(?<![0-9０-９+\-−－.．])" + re.escape(value) + r"(?![0-9０-９])",
                               translation["zh"]) for value in record.get("required_display_text", [])):
            reason = "required_display_text_missing"
        if reason:
            gaps.append(dict(record, zh=translation.get("zh"),
                             status=translation.get("status", "missing"), reason=reason))
        else:
            approved.append(dict(record, zh=translation["zh"]))
            counts[record["kind"]] += 1
    report = dict(schema_version=1, ready=not source_gaps and not gaps,
                  inventory_count=len(inventory), approved_count=len(approved),
                  manual_review_count=len(reviews),
                  manual_reviews_sha256=(hashlib.sha256(reviews_path.read_bytes()).hexdigest()
                                         if reviews_path else None),
                  candidate_count=sum(t.get("status") == "source_candidate" for t in translations.values()),
                  inventory_sha256=manifest["inventory_sha256"],
                  by_kind=dict(Counter(r["kind"] for r in inventory.values())),
                  approved_by_kind=dict(counts), source_gaps=source_gaps,
                  translation_gaps=gaps)
    return report, approved


def dotnet_string(value: str) -> bytes:
    wire = value.encode("utf-8")
    length = len(wire)
    prefix = bytearray()
    while length >= 128:
        prefix.append((length & 127) | 128)
        length >>= 7
    prefix.append(length)
    return bytes(prefix) + wire


def compile_catalog(records: list[dict]) -> bytes:
    # The sorted source IDs are the stable tie-breaker for otherwise equal labels.
    payload = bytearray(struct.pack("<i", len(records)))
    for record in records:
        payload.extend(dotnet_string(record["zh"]))
        payload.extend(struct.pack("<ffiiii", record["lat"], record["lon"],
                                   record["min_zoom"], record["max_zoom"],
                                   record["priority"], int(record["kind"] == "city")))
    fingerprint = hashlib.sha256(payload).hexdigest()
    return MAGIC + dotnet_string(fingerprint) + payload


def atomic_write(path: Path, wire: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(dir=path.parent, suffix=".tmp", delete=False) as output:
        temporary = Path(output.name)
        output.write(wire)
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("manifest", "inventory", "translations", "report"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--output", type=Path, help="Compile only if every source and Chinese review passes")
    parser.add_argument("--reviews", type=Path, help="Canonical reviewed additions; never edit generated candidates")
    args = parser.parse_args()
    inputs = {path.resolve() for path in (args.manifest, args.inventory, args.translations)}
    if args.reviews:
        inputs.add(args.reviews.resolve())
    outputs = [args.report.resolve()] + ([args.output.resolve()] if args.output else [])
    if inputs.intersection(outputs) or len(set(outputs)) != len(outputs):
        parser.error("input, report and output paths must be different")
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    report, approved = audit(manifest, args.inventory, args.translations, args.reviews)
    atomic_write(args.report, (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    print(f"labels={report['inventory_count']} approved={report['approved_count']} "
          f"translation_gaps={len(report['translation_gaps'])} source_gaps={len(report['source_gaps'])}")
    if not report["ready"]:
        return 1
    if args.output:
        atomic_write(args.output, compile_catalog(approved))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
