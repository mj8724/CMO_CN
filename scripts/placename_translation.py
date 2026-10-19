"""Shared, coordinate-aware translations for original and localized WWP files."""
from __future__ import annotations

import json
import math
from pathlib import Path

# Covers decimal rounding and float32 storage, without matching another city.
COORDINATE_TOLERANCE = 0.0001


def load_dictionary(path: str | Path) -> dict[str, str]:
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(data, dict):
        raise ValueError(f"{path}: dictionary must be an object")
    table = {}
    for name, zh in data.items():
        if name.startswith("_"):
            continue
        if not name or not isinstance(zh, str) or not zh:
            raise ValueError(f"{path}: invalid translation for {name!r}")
        table[name] = zh
    return table


def load_reviewed(directory: str | Path) -> list[dict]:
    """Read and validate every reviewed record, including cross-file conflicts."""
    directory = Path(directory)
    if not directory.is_dir():
        raise FileNotFoundError(f"review directory does not exist: {directory}")
    records = []
    for path in sorted(directory.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(data, list):
            raise ValueError(f"{path}: reviewed records must be an array")
        records.extend(data)
    PlacenameResolver({}, records)
    return records


class PlacenameResolver:
    def __init__(self, mapping: dict[str, str], reviewed: list[dict]) -> None:
        self.mapping = dict(mapping)
        self.by_alias: dict[str, list[dict]] = {}
        for record in reviewed:
            self._add(record)

    def _add(self, record: dict) -> None:
        if not isinstance(record, dict):
            raise ValueError("reviewed record must be an object")
        for key in ("name", "previous", "zh", "source"):
            if not isinstance(record.get(key), str) or not record[key].strip():
                raise ValueError(f"reviewed record requires nonempty {key}")
        for key, limit, label in (("lat", 90, "latitude"), ("lon", 180, "longitude")):
            value = record.get(key)
            if (isinstance(value, bool) or not isinstance(value, (int, float))
                    or not math.isfinite(value) or not -limit <= value <= limit):
                raise ValueError(f"invalid {label} in reviewed record {record['name']!r}")
        if not record["source"].startswith(("https://", "http://")):
            raise ValueError(f"reviewed source must be an HTTP URL: {record['name']!r}")
        aliases = {record[key] for key in ("name", "previous", "zh")}
        # Reject even identical duplicates: one alias/coordinate must have one owner.
        for alias in aliases:
            for other in self.by_alias.get(alias, ()):
                if self._near(record, other, 2 * COORDINATE_TOLERANCE):
                    raise ValueError(f"duplicate or conflicting reviewed records for {alias!r}")
        record = dict(record)
        for alias in aliases:
            self.by_alias.setdefault(alias, []).append(record)

    @staticmethod
    def _near(left: dict, right: dict, tolerance: float) -> bool:
        return (abs(left["lat"] - right["lat"]) <= tolerance
                and abs(left["lon"] - right["lon"]) <= tolerance)

    def resolve(self, item: dict) -> str:
        name = item["name"]
        for record in self.by_alias.get(name, ()):
            if self._near(item, record, COORDINATE_TOLERANCE):
                return record["zh"]
        return self.mapping.get(name, name)


def translate_items(items: list[dict], resolver: PlacenameResolver) -> int:
    """Change names only; preserve record identity, coordinates, flags and order."""
    changed = 0
    for item in items:
        name = resolver.resolve(item)
        if name != item["name"]:
            item["name"] = name
            changed += 1
    return changed
