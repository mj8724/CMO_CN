#!/usr/bin/env python3
"""Import pinned Stamen label candidates without certifying historic PNG parity.

Public-style zoom eligibility and individually observed original PNG zooms form
the known candidate scope. Every remaining source record is exported separately;
neither export is a claim that the frozen game's complete inventory is known.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
from pathlib import Path
import re
import unicodedata

from global_map_catalog import (LAYERS, atomic_write, chinese_name, http_url,
                                read_jsonl, record_fingerprint)
from placename_translation import COORDINATE_TOLERANCE

ROOT = Path(__file__).resolve().parent.parent
COMMIT = re.compile(r"[0-9a-f]{40}\Z")
ARIES_PATH = "data/aries/z4to10.json"
NE_LICENSE = "https://www.naturalearthdata.com/about/terms-of-use/"
GN_LICENSE = "https://creativecommons.org/licenses/by/4.0/"


def file_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def unique_by(rows: list[dict], key: str, label: str) -> dict:
    result = {}
    for row in rows:
        identity = row[key]
        if identity in result:
            raise ValueError(f"{label}: duplicate {key} {identity!r}")
        result[identity] = row
    return result


def normalized_name(value: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFKD", value).casefold()
                   if c.isalnum() and not unicodedata.combining(c))


def distance_km(lon1: float, lat1: float, lon2: float, lat2: float) -> float:
    a, b = math.radians(lat1), math.radians(lat2)
    h = math.sin((b - a) / 2) ** 2 + math.cos(a) * math.cos(b) * math.sin(
        math.radians(lon2 - lon1) / 2) ** 2
    return 12742 * math.asin(min(1, math.sqrt(h)))


def zooms(value: object) -> set[int]:
    if isinstance(value, str):
        value = [int(z) for z in value.split(";") if z]
    if not isinstance(value, list) or any(type(z) is not int or not 3 <= z <= 10 for z in value):
        raise ValueError(f"invalid zoom list: {value!r}")
    return set(value)


def continuous_spans(values: set[int]) -> list[tuple[int, int]]:
    spans = []
    for value in sorted(values):
        if spans and value == spans[-1][1] + 1:
            spans[-1] = (spans[-1][0], value)
        else:
            spans.append((value, value))
    return spans


def verify_match(source: dict, match: dict, city: bool = False) -> None:
    point = [match["lon"], match["lat"]] if city else match["coordinates"]
    if (match["name"] != source["name"] or len(point) != 2
            or any(abs(a - b) > 1e-7 for a, b in zip(point, source["coordinates"]))
            or (not city and match["category"] != source["category"])):
        raise ValueError(f"match refers to a changed source record: {source['source_id']}")


def existing_approval(source: dict, match: dict, reviewed: dict[str, list[dict]]) -> dict | None:
    if match.get("match_status") != "reviewed_existing":
        return None
    identity = str(match.get("match_source_id", ""))
    city = source["category"] == "city"
    prefix = "Cities:" if city else "CMO_CN:Synoptic:"
    if not identity.startswith(prefix) or not identity[len(prefix):].isdigit():
        return None
    index = int(identity[len(prefix):])
    ledger = reviewed["cities" if city else "synoptic"]
    if index >= len(ledger):
        return None
    entry = ledger[index]
    evidence = match.get("chinese_name_evidence") if city else match.get("match_source")
    if (entry["zh"] != match.get("zh") or entry["source"] != evidence
            or not http_url(evidence)
            or normalized_name(entry["name"]) != normalized_name(source["name"])):
        return None
    if city:
        lon, lat = source["coordinates"]
        if (match.get("candidate_count") != 1 or match.get("ambiguity")
                or match.get("source_candidates")
                or abs(lon - entry["lon"]) > COORDINATE_TOLERANCE
                or abs(lat - entry["lat"]) > COORDINATE_TOLERANCE):
            return None
        # A submitted candidate count cannot certify identity. Recompute nearby
        # owners from the complete existing ledger, including conflicting names.
        nearby = [i for i, candidate in reviewed["city_name_index"][normalized_name(source["name"])]
                  if distance_km(lon, lat, candidate["lon"], candidate["lat"]) <= 5]
        if nearby != [index]:
            return None
    elif (not match.get("method", "").startswith("reviewed_synoptic_exact_name_")
          or match.get("ambiguity") or not match.get("identity_evidence")):
        return None
    return dict(ledger="cities.json" if city else "synoptic.json", index=index,
                record_sha256=record_fingerprint(entry))


def translation(record: dict, source: dict, match: dict, reviewed: dict) -> dict:
    zh = match.get("zh")
    valid = chinese_name(zh)
    result = dict(id=record["id"], zh=zh if valid else None,
                  status="source_candidate" if valid else "untranslated",
                  record_sha256=record_fingerprint(record),
                  source=match.get("chinese_name_evidence") or match.get("match_source"),
                  match_status=match.get("match_status", "untranslated"),
                  match_evidence=match)
    if zh and not valid:
        result["reason"] = "invalid_chinese_candidate"
    if valid:
        basis = existing_approval(source, match, reviewed)
        if basis:
            result.update(status="approved", approval_basis=basis)
        elif match.get("match_status") == "reviewed_existing":
            result["reason"] = "existing_review_unverified"
    return result


def build_records(raw: list[dict], city_rows: list[dict], other_rows: list[dict],
                  observations: list[dict], summary: dict, reviewed: dict) -> tuple[list, list, list]:
    reviewed = dict(reviewed, city_name_index=defaultdict(list))
    for index, entry in enumerate(reviewed["cities"]):
        reviewed["city_name_index"][normalized_name(entry["name"])].append((index, entry))
    sources = unique_by(raw, "source_id", "catalog")
    city_matches = unique_by(city_rows, "aries_index", "city matches")
    city_sources = {r["source_record_index"]: r for r in raw if r["category"] == "city"}
    if city_matches.keys() - city_sources.keys():
        raise ValueError("city matches contain indices absent from source catalog")
    matches = {}
    for index, match in city_matches.items():
        source = city_sources[index]
        if source["source_path"] != ARIES_PATH:
            raise ValueError("city indices require the pinned Aries source path")
        verify_match(source, match, city=True)
        matches[source["source_id"]] = match
    for identity, match in unique_by(other_rows, "source_id", "other matches").items():
        if identity not in sources or sources[identity]["category"] == "city":
            raise ValueError(f"other match absent from non-city source catalog: {identity}")
        verify_match(sources[identity], match)
        matches[identity] = match
    observed = defaultdict(list)
    for evidence in observations:
        identity = evidence["source_id"]
        if identity not in sources:
            raise ValueError(f"observed source absent from catalog: {identity}")
        source = sources[identity]
        if (evidence["source_name"] != source["name"]
                or evidence["category"] != source["category"]
                or any(abs(a - b) > 1e-7 for a, b in zip(source["coordinates"],
                    [evidence["longitude"], evidence["latitude"]]))):
            raise ValueError(f"observation refers to a changed source: {identity}")
        zooms(evidence["observed_zooms"])
        observed[identity].append(evidence)
    inventory, translations, unresolved = [], [], []
    commit = summary["terrain_classic_commit"]
    for source in sorted(raw, key=lambda row: row["source_id"]):
        identity = source["source_id"]
        kind = "region" if source["category"] == "continent" else source["category"]
        if kind not in LAYERS:
            raise ValueError(f"unknown source category: {kind}")
        public = zooms(source["eligible_zooms_3_to_10"])
        original = set().union(*(zooms(e["observed_zooms"]) for e in observed[identity]))
        eligible = public | original
        lon, lat = source["coordinates"]
        reason = None
        if not isinstance(source["name"], str) or not source["name"].strip():
            reason = "source_has_no_renderable_name"
        elif (any(isinstance(v, bool) or not isinstance(v, (int, float))
                  or not math.isfinite(v) for v in (lon, lat))
              or abs(lon) > 180 or abs(lat) > 85.05112878):
            reason = "source_anchor_outside_web_mercator"
        elif not eligible:
            reason = "visibility_unresolved"
        if reason:
            unresolved.append(dict(source_record_id=identity, reason=reason,
                                   source_record=source, match_evidence=matches.get(identity),
                                   original_png_observations=observed[identity]))
            continue
        for low, high in continuous_spans(eligible):
            record = dict(id=f"stamen:{commit}:{identity}:z{low}-{high}",
                          source_id=f"stamen-catalog:{commit}", source_record_id=identity,
                          source_record_sha256=record_fingerprint(source),
                          source_path=source["source_path"],
                          source_record_index=source["source_record_index"],
                          kind=kind, name=source["name"], lat=lat, lon=lon,
                          min_zoom=low, max_zoom=high, priority=0,
                          priority_method="unreviewed_default",
                          anchor_method=source.get("anchor_method", "unverified"),
                          anchor_verified=False, source_properties=source.get("properties", {}),
                          zoom_provenance={str(z): (["public_style_predicate"] if z in public else [])
                                           + (["original_png_observation"] if z in original else [])
                                           for z in range(low, high + 1)},
                          original_png_observations=observed[identity])
            if kind == "peak":
                elevation = record["source_properties"].get("elevation")
                if (isinstance(elevation, bool) or not isinstance(elevation, (int, float))
                        or not math.isfinite(elevation)):
                    raise ValueError(f"peak source requires finite elevation: {identity}")
                record["required_display_text"] = [format(elevation, "g") + "米"]
                record["height_basis"] = "public_source_elevation_not_verified_against_original_png"
            inventory.append(record)
            translations.append(translation(record, source, matches.get(identity, {}), reviewed))
    return inventory, translations, unresolved


def jsonl_bytes(records: list[dict]) -> bytes:
    return "".join(json.dumps(r, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
                   + "\n" for r in records).encode("utf-8")


def import_catalog(args: argparse.Namespace) -> dict:
    summary = json.loads(args.source_summary.read_text(encoding="utf-8"))
    for key in ("terrain_classic_commit", "natural_earth_commit"):
        if not COMMIT.fullmatch(summary[key]):
            raise ValueError(f"{key} must be a fixed lowercase commit SHA")
    if summary["sha256"] != file_hash(args.catalog):
        raise ValueError("source catalog SHA256 differs from source summary")
    raw = read_jsonl(args.catalog)
    if summary["records"] != len(raw):
        raise ValueError("source catalog row count differs from source summary")
    city_rows = [row for path in args.city_matches for row in read_jsonl(path)]
    other_rows = read_jsonl(args.other_matches) if args.other_matches else []
    observations = (json.loads(args.confirmed_matches.read_text(encoding="utf-8"))
                    if args.confirmed_matches else [])
    reviewed = {name: json.loads(path.read_text(encoding="utf-8")) for name, path in
                (("cities", args.reviewed_cities), ("synoptic", args.reviewed_synoptic))}
    inventory, translations, unresolved = build_records(
        raw, city_rows, other_rows, observations, summary, reviewed)
    wire = jsonl_bytes(inventory)
    commit, ne_commit = summary["terrain_classic_commit"], summary["natural_earth_commit"]
    source_id = f"stamen-catalog:{commit}"
    source_url = f"https://github.com/stamen/terrain-classic/tree/{commit}"
    paths = [("catalog", args.catalog), ("source_summary", args.source_summary),
             ("reviewed_cities", args.reviewed_cities), ("reviewed_synoptic", args.reviewed_synoptic)]
    paths += [(f"city_matches_{i}", p) for i, p in enumerate(args.city_matches)]
    paths += [(key, path) for key, path in (("other_matches", args.other_matches),
              ("confirmed_matches", args.confirmed_matches)) if path]
    selected_ids = {r["source_record_id"] for r in inventory}
    chinese_ids = {r["source_record_id"] for r, t in zip(inventory, translations) if t["zh"]}
    selected = {r["source_record_id"]: (r, t) for r, t in zip(inventory, translations)}
    selected_by_kind = {}
    for kind in LAYERS:
        members = [(r, t) for r, t in selected.values() if r["kind"] == kind]
        selected_by_kind[kind] = dict(source_records=len(members),
                                     with_chinese=sum(bool(t["zh"]) for _, t in members),
                                     translation_status=dict(Counter(t["status"] for _, t in members)))
    manifest = dict(
        schema_version=1, min_zoom=3, max_zoom=10,
        inventory_sha256=hashlib.sha256(wire).hexdigest(),
        snapshot_verified=False, parity_verified=False,
        sources=[dict(id=source_id, url=source_url, sha256=summary["sha256"],
                      artifact_kind="derived_source_catalog",
                      terrain_classic_commit=commit, natural_earth_commit=ne_commit,
                      natural_earth_url=f"https://github.com/stamen/natural-earth/tree/{ne_commit}",
                      license="Mixed candidate data: Natural Earth public domain; GeoNames Chinese aliases CC BY 4.0; Stamen static-data terms require review",
                      license_urls=[NE_LICENSE, GN_LICENSE], redistribution_verified=False)],
        input_artifacts={key: dict(filename=p.name, sha256=file_hash(p)) for key, p in paths},
        layers={kind: dict(complete=False, evidence=source_url,
                           source_ids=[source_id] if any(r["kind"] == kind for r in inventory) else [])
                for kind in LAYERS},
        counts=dict(source_records=len(raw), selected_source_records=len(selected_ids),
                    inventory_records=len(inventory), unresolved_source_records=len(unresolved),
                    selected_source_records_with_chinese=len(chinese_ids),
                    selected_source_records_without_chinese=len(selected_ids - chinese_ids),
                    selected_by_kind=selected_by_kind,
                    source_categories=dict(Counter(r["category"] for r in raw)),
                    inventory_by_kind=dict(Counter(r["kind"] for r in inventory)),
                    translation_status=dict(Counter(t["status"] for t in translations)),
                    unresolved_reasons=dict(Counter(r["reason"] for r in unresolved)),
                    original_png_observations=len(observations)),
        limitations=["Public style/data version is not verified identical to the frozen CMO PNGs.",
                     "Eligible and observed zoom union is a known candidate scope, not a complete historic visible-label manifest.",
                     "Empty eligibility does not prove absence from the original map; unresolved source records are retained.",
                     "Historic OSM water and park sources are missing; all layers remain incomplete.",
                     "Candidate anchors, priorities, collision and Chinese label layout require review.",
                     "Source candidates are not approved translations; no English fallback or automatic transliteration."])
    output_paths = {name: args.output_dir / name for name in
                    ("inventory.jsonl", "translations.jsonl", "unresolved_visibility.jsonl", "manifest.json")}
    if {p.resolve() for _, p in paths}.intersection(p.resolve() for p in output_paths.values()):
        raise ValueError("output files must not overwrite inputs")
    for name, content in (("inventory.jsonl", wire), ("translations.jsonl", jsonl_bytes(translations)),
                          ("unresolved_visibility.jsonl", jsonl_bytes(unresolved)),
                          ("manifest.json", (json.dumps(manifest, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))):
        atomic_write(output_paths[name], content)
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("catalog", "source-summary", "output-dir"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--city-matches", type=Path, nargs="+", required=True,
                        help="Matched and optionally untranslated Aries JSONL files")
    parser.add_argument("--other-matches", type=Path)
    parser.add_argument("--confirmed-matches", type=Path,
                        help="Original PNG entity/zoom observations; pass for the real import")
    parser.add_argument("--reviewed-cities", type=Path, default=ROOT / "dict/placenames/cities.json")
    parser.add_argument("--reviewed-synoptic", type=Path, default=ROOT / "dict/placenames/synoptic.json")
    manifest = import_catalog(parser.parse_args())
    print(json.dumps(manifest["counts"], ensure_ascii=True, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
