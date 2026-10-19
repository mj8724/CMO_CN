#!/usr/bin/env python3
"""Read and write World Wind Placename (.wwp) files used by CMO map labels.

Layout (little-endian):
    int32  count
    repeat count times:
        <name>  length-prefixed string
        float32 lat
        float32 lon
        uint32 flags

The length prefix is .NET `BinaryReader.ReadString` style: a 7-bit encoded
integer followed by UTF-8 bytes.  For lengths < 128 that is a single byte, which
is why the existing English files parse as a plain `uint8` length.  Writing the
full 7-bit form keeps translated names correct even when a Chinese name exceeds
127 bytes (Chinese is 3 bytes per character in UTF-8).

Usage:
    python wwp.py dump  <file.wwp>            # name, latitude, longitude, flags
    python wwp.py list  <file.wwp>            # print names only
    python wwp.py apply <file.wwp> <map.json> # rewrite names from a mapping
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path


def _read_7bit_length(raw: bytes, off: int) -> tuple[int, int]:
    """Decode a .NET 7-bit encoded integer, returning (value, new_offset)."""
    value = 0
    shift = 0
    while True:
        b = raw[off]
        off += 1
        value |= (b & 0x7F) << shift
        if not b & 0x80:
            return value, off
        shift += 7
        if shift > 35:
            raise ValueError("malformed 7-bit length")


def _write_7bit_length(value: int) -> bytes:
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        if value:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def load(path: str | Path) -> list[dict]:
    raw = Path(path).read_bytes()
    count = struct.unpack_from("<i", raw, 0)[0]
    off = 4
    items = []
    for _ in range(count):
        nlen, off = _read_7bit_length(raw, off)
        name = raw[off:off + nlen].decode("utf-8")
        off += nlen
        lat, lon, flags = struct.unpack_from("<ffI", raw, off)
        off += 12
        items.append({"name": name, "lat": lat, "lon": lon, "flags": flags})
    if off != len(raw):
        raise ValueError(f"{path}: consumed {off} of {len(raw)} bytes")
    return items


def save(path: str | Path, items: list[dict]) -> None:
    out = bytearray(struct.pack("<i", len(items)))
    for it in items:
        data = it["name"].encode("utf-8")
        out += _write_7bit_length(len(data))
        out += data
        out += struct.pack("<ffI", it["lat"], it["lon"], it["flags"])
    Path(path).write_bytes(bytes(out))


def apply_map(path: str | Path, mapping: dict[str, str]) -> tuple[int, int]:
    """Rename entries in place.  Returns (changed, untouched)."""
    items = load(path)
    changed = 0
    for it in items:
        new = mapping.get(it["name"])
        if new:
            it["name"] = new
            changed += 1
    save(path, items)
    return changed, len(items) - changed


def main() -> int:
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    mode, target = sys.argv[1], sys.argv[2]
    if mode == "dump":
        for it in load(target):
            print(f"{it['name']}\t{it['lat']:.4f}\t{it['lon']:.4f}\t{it['flags']}")
    elif mode == "list":
        for it in load(target):
            print(it["name"])
    elif mode == "apply":
        mapping = json.loads(Path(sys.argv[3]).read_text(encoding="utf-8"))
        changed, same = apply_map(target, mapping)
        print(f"{target}: renamed {changed}, unchanged {same}")
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
