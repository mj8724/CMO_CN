#!/usr/bin/env python3
"""Audit finite cached map regions and render reviewed Chinese RGBA tile packs.

Pillow and fontTools are offline build dependencies only. Source alpha accounting
finds omissions; it never replaces the required human source/render review.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
from pathlib import Path
import re
import struct

from PIL import Image, ImageChops, ImageDraw, ImageFont
from fontTools.ttLib import TTFont

from global_map_catalog import (LAYERS, atomic_write, chinese_name, dotnet_string,
                                http_url, record_fingerprint)
from map_tile_manifest import load_manifest, manifest_files

MAGIC = b"CMOTIL01"
MAGIC_WITH_LEGACY = b"CMOTIL02"
TILE_SIZE = 256
SCALE = 3
MAX_REGION_TILES = 256
MAX_TOTAL_TILES = 16384
SAFE_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\Z")
DEVICE_IDS = {"con", "prn", "aux", "nul"} | {
    f"{prefix}{digit}" for prefix in ("com", "lpt") for digit in range(1, 10)}


def sha256(wire: bytes) -> str:
    return hashlib.sha256(wire).hexdigest()


def number(value, name, low, high):
    if (isinstance(value, bool) or not isinstance(value, (int, float))
            or not math.isfinite(value) or not low <= value <= high):
        raise ValueError(f"invalid {name}: {value!r}")
    return value


def integer(value, name, low, high):
    if type(value) is not int:
        raise ValueError(f"invalid integer {name}: {value!r}")
    return number(value, name, low, high)


def float32_bits(value):
    return struct.unpack("<I", struct.pack("<f", value))[0]


def legacy_key(z, name, lat, lon, level):
    return z, name, float32_bits(lat), float32_bits(lon), level


def canonical_legacy_points():
    """Read repository-owned WWP only when suppression rules are requested."""
    from wwp import load

    allowed, sources = set(), []
    for filename, level in (("Cities.wwp", 1), ("Synoptic.wwp", 0)):
        path = Path(__file__).resolve().parent.parent / "assets/placenames" / filename
        sources.append(dict(file=filename, sha256=sha256(path.read_bytes()), level=level))
        for point in load(path):
            allowed.add(legacy_key(0, point["name"], point["lat"], point["lon"], level)[1:])
    return allowed, sources


def audit_legacy_suppressions(region, features, translations, labels, allowed, gap):
    # Exact canonical membership prevents broad same-name deletion. The signed
    # region review separately certifies that this old point is the same source
    # placement as the rendered feature; coordinates alone cannot prove that.
    result = []
    rendered = {label["id"] for label in labels if label["bounds"] is not None and label["tiles"]}
    for rule in records(region.get("legacy_suppressions", []), "legacy_suppressions"):
        feature_id = rule.get("feature_id")
        feature = features.get(feature_id)
        translation = translations.get(feature_id, {})
        name, level = rule.get("name"), rule.get("level")
        lat, lon = rule.get("lat"), rule.get("lon")
        valid_coordinates = all(isinstance(value, (int, float)) and not isinstance(value, bool)
                                and math.isfinite(value) and abs(value) <= limit
                                for value, limit in ((lat, 85.05113), (lon, 180)))
        if (feature is None or feature.get("elevation_only") is True or feature_id not in rendered
                or translation.get("status") != "approved" or not chinese_name(translation.get("zh"))
                or not chinese_name(name) or type(level) is not int or level not in (0, 1)
                or (feature is not None and level != (1 if feature["kind"] == "city" else 0))
                or not valid_coordinates or not http_url(rule.get("source"))
                or not isinstance(rule.get("review_notes"), str) or not rule["review_notes"].strip()):
            gap("legacy_suppression", feature_id=feature_id, reason="invalid identity, output or review evidence")
            continue
        lat, lon = struct.unpack("<ff", struct.pack("<ff", lat, lon))
        key = legacy_key(region["z"], name, lat, lon, level)
        if key[1:] not in allowed:
            gap("legacy_suppression", feature_id=feature_id, reason="not an exact canonical WWP point")
            continue
        count = 1 << region["z"]
        radians = math.radians(max(-85.05112878, min(85.05112878, lat)))
        tile_x = max(0, min(count - 1, math.floor((lon + 180) / 360 * count)))
        tile_y = max(0, min(count - 1, math.floor((1 - math.asinh(math.tan(radians)) / math.pi) / 2 * count)))
        result.append(dict(z=region["z"], name=name, lat=lat, lon=lon, level=level,
                           legacy_tile=[region["z"], tile_x, tile_y],
                           references=[dict(region=region["id"], feature_id=feature_id,
                                            source=rule["source"], review_notes=rule["review_notes"])]))
    return result


def records(value, name):
    if not isinstance(value, list) or any(not isinstance(row, dict) for row in value):
        raise ValueError(f"{name} must be a list of records")
    return value


def path_identity(path):
    # Use Windows-style case insensitivity on every platform so the same input
    # cannot certify distinct previews locally then overwrite them on Windows.
    return str(Path(path).resolve()).replace("\\", "/").casefold()


def preview_paths(regions, directory, protected):
    """Validate all identities and final paths before any preview is written."""
    identities, destinations, result = set(), set(), {}
    protected = {path_identity(path) for path in protected}
    for region in regions:
        identity = region.get("id")
        if (not isinstance(identity, str) or not SAFE_ID.fullmatch(identity)
                or identity.casefold() in identities or identity.split(".")[0].casefold() in DEVICE_IDS):
            raise ValueError(f"invalid or duplicate region id: {identity}")
        identities.add(identity.casefold())
        if directory is None:
            continue
        for suffix in ("original", "zh"):
            path = Path(directory) / f"{identity}.{suffix}.png"
            key = path_identity(path)
            if key in destinations or key in protected:
                raise ValueError("preview paths must be unique and differ from inputs, output and report")
            destinations.add(key)
            result[identity, suffix] = path
    return result


def indexed(rows, name, gap):
    result = {}
    for row in records(rows, name):
        identity = row.get("id")
        if not isinstance(identity, str) or not identity.strip() or identity in result:
            gap("record_set", collection=name, id=identity, reason="missing or duplicate id")
        else:
            result[identity] = row
    return result


def bbox(value, name, width, height):
    if not isinstance(value, list) or len(value) != 4:
        raise ValueError(f"{name} must contain left, top, right, bottom")
    left, top, right, bottom = [number(v, name, -4096, 8192) for v in value]
    if left >= right or top >= bottom or right <= 0 or bottom <= 0 or left >= width or top >= height:
        raise ValueError(f"{name} must intersect the region with positive area")
    return (math.floor(left), math.floor(top), math.ceil(right), math.ceil(bottom))


def world_original_bbox(region, value, name):
    # Validate the full numeric box before comparing it. Rounded accounting
    # bounds would incorrectly accept different fractional source instances.
    bbox(value, name, region["width_tiles"] * TILE_SIZE, region["height_tiles"] * TILE_SIZE)
    return [coordinate + region[axis] * TILE_SIZE
            for coordinate, axis in zip(value, ("x", "y", "x", "y"))]


def native_source_frame(region, box, original_root, input_paths):
    """Read a complete declared original frame; no source inference or masking."""
    if original_root is None or not all(float(v).is_integer() for v in box):
        raise ValueError("requires exact original-root and integer source frame")
    left, top, right, bottom = map(int, box)
    if not 0 < (right-left) * (bottom-top) <= 4096 * 4096:
        raise ValueError("native source frame exceeds bounded crop")
    root = Path(original_root).resolve()
    declared = {}
    for field in ("original_tiles", "context_tiles"):
        for tile in records(region.get(field, []), field):
            key = tile.get("x"), tile.get("y")
            if key in declared and tile != declared[key]:
                raise ValueError("conflicting source declarations")
            declared[key] = tile
    frame = Image.new("RGBA", (right-left, bottom-top))
    for y in range(top // TILE_SIZE, (bottom-1) // TILE_SIZE + 1):
        for x in range(left // TILE_SIZE, (right-1) // TILE_SIZE + 1):
            tile = declared.get((x, y))
            expected = f"{region['z']}/{x}/{y}.png"
            match = None if tile is None else re.fullmatch(
                r"https?://warfaresims\.slitherine\.com/MapTiles/Labels/(\d+)/(\d+)/(\d+)\.png",
                tile.get("source_url", ""))
            if (tile is None or tile.get("path") != expected or match is None
                    or tuple(map(int, match.groups())) != (region['z'], x, y)):
                raise ValueError("requires complete official source XYZ provenance")
            errors = []
            _, image = load_source(tile, root, region['z'], lambda code, **kw: errors.append(code), input_paths)
            if image is None or errors:
                raise ValueError("invalid native source PNG or SHA")
            a, b = max(left, x*TILE_SIZE), max(top, y*TILE_SIZE)
            c, d = min(right, (x+1)*TILE_SIZE), min(bottom, (y+1)*TILE_SIZE)
            frame.paste(image.crop((a-x*TILE_SIZE, b-y*TILE_SIZE, c-x*TILE_SIZE, d-y*TILE_SIZE)),
                        (a-left, b-top))
    if frame.getchannel("A").getbbox() is None:
        raise ValueError("native source frame is empty")
    return frame


def audit_owner_native_source_refs(region, source, owner, target, gap, approvals,
                                   original_root=None, input_paths=None):
    """Validate explicit existing owner frames for source accounting, never rendering."""
    refs = source["owner_native_source_refs"]

    def invalid(reason):
        gap("owner_native_source", id=source["id"], reason=reason)
        return None

    if not isinstance(refs, list) or not refs:
        return invalid("requires nonempty explicit native source refs")
    if target.get("kind") != "city" or any(k in target for k in ("elevation", "elevation_only")):
        return invalid("source-only reference requires a city, never text or height")
    review = owner.get("review", {})
    owner_sha = record_fingerprint({key: value for key, value in owner.items() if key != "review"})
    if (review.get("status") != "approved" or review.get("source_verified") is not True
            or review.get("render_verified") is not True or review.get("record_sha256") != owner_sha
            or any(not isinstance(review.get(k), str) or not review[k].strip() for k in ("reviewer", "notes"))):
        return invalid("requires unchanged approved whole owner")
    expected = dict(region=region["id"], shared_source_id=source["id"], owner_region=owner["id"],
                    feature_id=target["id"], owner_record_sha256=owner_sha, symbol_refs=refs)
    if (not isinstance(approvals, list)
            or sum(entry == expected for entry in approvals) != 1
            or sum(isinstance(entry, dict) and entry.get("region") == region["id"]
                   and entry.get("shared_source_id") == source["id"] for entry in approvals) != 1):
        return invalid("requires one exact separately supplied external entry")
    if any(symbol.get("shared_source_id") == source["id"] for symbol in region.get("symbols", [])):
        return invalid("source-only reference cannot also render a borrower symbol")
    word = world_original_bbox(owner, target["original_bbox"], "owner word original_bbox")
    if any(world_original_bbox(region, feature["original_bbox"], "borrower word original_bbox") == word
           for feature in region.get("features", [])):
        return invalid("source-only reference cannot add a duplicate borrower normal")
    result, seen = [], set()
    for ref in refs:
        if (not isinstance(ref, dict) or set(ref) != {"symbol_id", "record_sha256", "world_original_bbox", "source_rgba_sha256"}
                or not isinstance(ref["symbol_id"], str) or not ref["symbol_id"] or ref["symbol_id"] in seen
                or any(not isinstance(ref[key], str) or not re.fullmatch("[0-9a-f]{64}", ref[key])
                       for key in ("record_sha256", "source_rgba_sha256"))):
            return invalid("requires unique typed symbol identity and exact record SHA")
        seen.add(ref["symbol_id"])
        matches = [symbol for symbol in records(owner.get("symbols"), "owner symbols") if symbol.get("id") == ref["symbol_id"]]
        if len(matches) != 1:
            return invalid("requires one existing direct owner symbol")
        symbol = matches[0]
        if (symbol.get("feature_id") != target["id"] or "shared_source_id" in symbol
                or symbol.get("unlabeled", False) is not False
                or symbol.get("kind") != "circle"
                or any(k in symbol for k in ("elevation", "elevation_only"))
                or record_fingerprint(symbol) != ref["record_sha256"]):
            return invalid("requires exact named direct feature-linked native object")
        world = world_original_bbox(owner, symbol.get("original_bbox"), "owner native original_bbox")
        declared = ref["world_original_bbox"]
        if (not isinstance(declared, list) or len(declared) != 4
                or any(type(value) not in (int, float) or not math.isfinite(value) for value in declared)
                or declared != world):
            return invalid("requires complete exact world native frame")
        if any(world_original_bbox(region, local["original_bbox"], "borrower native original_bbox") == world
               for local in region.get("symbols", [])):
            return invalid("existing native source cannot have a second borrower symbol")
        # Use the unchanged actual symbol patch rather than its rectangular
        # source padding. The old target must be complete in its sole owner and
        # contribute no positive target pixels to this source-only borrower.
        patch = symbol_patch(symbol)
        alpha_box = patch.getchannel("A").getbbox()
        px = math.floor(symbol["x"] - patch.width / 2)
        py = math.floor(symbol["y"] - patch.height / 2)
        if (alpha_box is None or px+alpha_box[0] < 0 or py+alpha_box[1] < 0
                or px+alpha_box[2] > owner["width_tiles"]*TILE_SIZE
                or py+alpha_box[3] > owner["height_tiles"]*TILE_SIZE):
            return invalid("existing owner native target must be complete")
        wx, wy = owner["x"]*TILE_SIZE + px, owner["y"]*TILE_SIZE + py
        l, t = max(wx, region["x"]*TILE_SIZE), max(wy, region["y"]*TILE_SIZE)
        r = min(wx+patch.width, (region["x"]+region["width_tiles"])*TILE_SIZE)
        b = min(wy+patch.height, (region["y"]+region["height_tiles"])*TILE_SIZE)
        if l < r and t < b and patch.getchannel("A").crop((l-wx, t-wy, r-wx, b-wy)).getbbox():
            return invalid("source-only borrower must have zero positive target pixels")
        try:
            paths = input_paths if input_paths is not None else set()
            raw = native_source_frame(region, world, original_root, paths)
            old_raw = native_source_frame(owner, world, original_root, paths)
        except (ValueError, OSError) as error:
            return invalid(str(error))
        if raw.tobytes() != old_raw.tobytes() or sha256(raw.tobytes()) != ref["source_rgba_sha256"]:
            return invalid("complete native raw RGBA differs from the external qualification")
        local = [value - region[axis] * TILE_SIZE for value, axis in zip(world, ("x", "y", "x", "y"))]
        bbox(local, "referenced native original_bbox", region["width_tiles"] * TILE_SIZE, region["height_tiles"] * TILE_SIZE)
        result.append(dict(symbol_id=symbol["id"], record_sha256=ref["record_sha256"],
                           source_rgba_sha256=ref["source_rgba_sha256"],
                           world_original_bbox=world, original_bbox=local))
    return result


def audit_shared_sources(region, features, region_index, feature_index, gap, owner_native_source_approvals=None,
                         original_root=None, input_paths=None):
    sources = indexed(region.get("shared_sources", []), "shared_sources", gap)
    valid, native_owners = {}, set()
    for identity, source in sources.items():
        if "owner_native_sources" in source:
            gap("owner_native_source", id=identity, reason="derived accounting rows cannot be supplied by a manifest")
            continue
        owner_id, feature_id = source.get("owner_region"), source.get("feature_id")
        owner = region_index.get(owner_id)
        target = feature_index.get(owner_id, {}).get(feature_id)
        if (identity in features or owner is None or owner_id == region["id"]
                or owner.get("z") != region["z"] or target is None
                or not isinstance(source.get("review_notes"), str) or not source["review_notes"].strip()
                or any(source.get(field) != target.get(field) for field in ("kind", "original", "source_url"))):
            gap("shared_source", id=identity, reason="invalid owner, normal feature identity or review evidence")
            continue
        world_box = world_original_bbox(region, source.get("original_bbox"), f"{identity} shared original_bbox")
        owner_box = world_original_bbox(owner, target.get("original_bbox"), f"{feature_id} owner original_bbox")
        if world_box != owner_box:
            gap("shared_source", id=identity, reason="complete world original_bbox differs from owner",
                world_original_bbox=world_box, owner_world_original_bbox=owner_box)
            continue
        item = dict(source, world_original_bbox=world_box)
        if "owner_native_source_refs" in source:
            native = audit_owner_native_source_refs(region, source, owner, target, gap,
                owner_native_source_approvals, original_root, input_paths)
            if native is None:
                continue
            keys = {(owner_id, ref["symbol_id"]) for ref in native}
            if keys & native_owners:
                gap("owner_native_source", id=identity, reason="one existing native source cannot have duplicate aliases")
                continue
            native_owners.update(keys)
            item["owner_native_sources"] = native
        valid[identity] = item
    return valid


def mask_count(mask):
    return sum(mask.histogram()[1:])


def png_bytes(image):
    stream = io.BytesIO()
    image.save(stream, format="PNG", optimize=False)
    return stream.getvalue()


def font_info(path):
    path = Path(path)
    with TTFont(path, fontNumber=0, lazy=True) as font:
        cmap = font.getBestCmap()
        if not cmap:
            raise ValueError(f"font has no Unicode cmap: {path}")
        return dict(path=str(path.resolve()), sha256=sha256(path.read_bytes()),
                    collection_index=0), cmap


def load_source(tile, root, z, gap, input_paths):
    x = integer(tile.get("x"), "source x", 0, (1 << z) - 1)
    y = integer(tile.get("y"), "source y", 0, (1 << z) - 1)
    if not http_url(tile.get("source_url")):
        gap("source_url", x=x, y=y)
    relative = tile.get("path")
    if not isinstance(relative, str) or not relative.strip() or Path(relative).is_absolute():
        gap("source_path", path=relative)
        return (x, y), None
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        gap("source_path", path=relative)
        return (x, y), None
    input_paths.add(path)
    if not path.is_file():
        gap("source_missing", path=relative)
        return (x, y), None
    wire = path.read_bytes()
    if tile.get("sha256") != sha256(wire):
        gap("source_sha256", path=relative, actual=sha256(wire))
    try:
        with Image.open(io.BytesIO(wire)) as source:
            if source.format != "PNG" or source.size != (TILE_SIZE, TILE_SIZE):
                gap("source_dimensions", path=relative, format=source.format, size=list(source.size))
                return (x, y), None
            return (x, y), source.convert("RGBA")
    except OSError as error:
        gap("source_image", path=relative, reason=str(error))
        return (x, y), None


def source_atlas(region, root, size, gap, input_paths):
    z, rx, ry = region["z"], region["x"], region["y"]
    expected = {(x, y) for y in range(ry, ry + region["height_tiles"])
                for x in range(rx, rx + region["width_tiles"])}
    atlas = Image.new("RGBA", size)
    seen, contexts, audits, verified_sources = set(), set(), [], {}
    for field in ("original_tiles", "context_tiles"):
        for tile in records(region.get(field), field):
            source_gaps = []

            def source_gap(code, **details):
                source_gaps.append(code)
                gap(code, **details)

            key, image = load_source(tile, root, z, source_gap, input_paths)
            target = seen if field == "original_tiles" else contexts
            if key in target or (field == "context_tiles" and key in expected):
                gap("tile_set", collection=field, tile=list(key), reason="duplicate or covered context")
            target.add(key)
            audits.append(dict(collection=field, x=key[0], y=key[1],
                               path=tile.get("path"), sha256=tile.get("sha256"),
                               source_url=tile.get("source_url")))
            if image is not None and field == "original_tiles" and key in expected:
                atlas.paste(image, ((key[0] - rx) * TILE_SIZE, (key[1] - ry) * TILE_SIZE))
                if not source_gaps:
                    verified_sources[key] = tile
    if seen != expected:
        gap("tile_set", missing=sorted(expected - seen), extra=sorted(seen - expected))
    return atlas, contexts, audits, verified_sources


def audit_source_backgrounds(region, atlas, verified_sources, gap):
    rows = records(region.get("source_backgrounds", []), "source_backgrounds")
    if not rows:
        return None, []
    background = Image.new("L", atlas.size)
    seen, audits = set(), []
    for row in rows:
        x, y = row.get("x"), row.get("y")
        if type(x) is not int or type(y) is not int:
            gap("source_background", reason="tile coordinates must be integers")
            continue
        key = x, y
        source = verified_sources.get(key)
        if key in seen:
            gap("source_background", x=x, y=y, reason="duplicate background tile")
            continue
        seen.add(key)
        rgba = row.get("rgba")
        if (source is None or row.get("sha256") != source["sha256"]
                or row.get("source_url") != source["source_url"]
                or not isinstance(rgba, list) or len(rgba) != 4
                or any(type(channel) is not int or channel != 1 for channel in rgba)
                or not isinstance(row.get("review_notes"), str) or not row["review_notes"].strip()):
            gap("source_background", x=x, y=y, reason="requires exact verified core source, RGBA(1,1,1,1) and review evidence")
            continue
        left, top = (x - region["x"]) * TILE_SIZE, (y - region["y"]) * TILE_SIZE
        tile = atlas.crop((left, top, left + TILE_SIZE, top + TILE_SIZE))
        exact = Image.new("L", tile.size, 255)
        for channel in tile.split():
            exact = ImageChops.multiply(exact, channel.point(lambda value: 255 if value == 1 else 0))
        pixels = mask_count(exact)
        if pixels < TILE_SIZE * TILE_SIZE // 2:
            gap("source_background", x=x, y=y, pixels=pixels, reason="exact flat color must occupy at least half the tile")
            continue
        background.paste(exact, (left, top))
        audits.append(dict(x=x, y=y, sha256=row["sha256"], rgba=list(rgba), source_url=row["source_url"],
                           review_notes=row["review_notes"], pixels=pixels))
    return background, audits


def audit_original(region, atlas, contexts, features, symbols, gap, shared_sources=None, background=None):
    width, height = atlas.size
    alpha = atlas.getchannel("A").point(lambda value: 255 if value else 0)
    accounted = Image.new("L", atlas.size) if background is None else background.copy()
    ink = alpha if background is None else ImageChops.subtract(alpha, background)
    draw = ImageDraw.Draw(accounted)
    owner_native_sources = {}
    for identity, source in (shared_sources or {}).items():
        for ref in source.get("owner_native_sources", []):
            owner_native_sources[(identity, ref["symbol_id"])] = ref
    for category, items in (("feature", features), ("symbol", symbols), ("shared_source", shared_sources or {}),
                            ("owner_native_source", owner_native_sources)):
        for identity, item in items.items():
            box = bbox(item.get("original_bbox"), f"{identity} original_bbox", width, height)
            clipped = (max(0, box[0]), max(0, box[1]), min(width, box[2]), min(height, box[3]))
            if not ink.crop(clipped).getbbox():
                gap("empty_original_bbox", category=category, id=identity)
            draw.rectangle((clipped[0], clipped[1], clipped[2] - 1, clipped[3] - 1), fill=255)
            # Every source tile touched by a boundary-spanning original must be
            # supplied as a reviewed context image (context does not add coverage).
            for ty in range(box[1] // TILE_SIZE, (box[3] - 1) // TILE_SIZE + 1):
                for tx in range(box[0] // TILE_SIZE, (box[2] - 1) // TILE_SIZE + 1):
                    if 0 <= tx < region["width_tiles"] and 0 <= ty < region["height_tiles"]:
                        continue
                    key = (region["x"] + tx, region["y"] + ty)
                    if key not in contexts:
                        gap("context_incomplete", id=identity, tile=list(key))
    unaccounted = ImageChops.subtract(alpha, accounted)
    count = mask_count(unaccounted)
    if count:
        gap("unaccounted_alpha", pixels=count, bbox=unaccounted.getbbox())
    return dict(total_pixels=mask_count(alpha), accounted_pixels=mask_count(alpha) - count,
                unaccounted_pixels=count, unaccounted_bbox=unaccounted.getbbox(),
                background_pixels=0 if background is None else mask_count(background))


def text_patch(lines, path, font_size):
    font = ImageFont.truetype(str(path), round(font_size * SCALE), index=0)
    measure = ImageDraw.Draw(Image.new("RGBA", (1, 1)))
    boxes = [measure.textbbox((0, 0), line, font=font, stroke_width=SCALE) for line in lines]
    widths = [box[2] - box[0] for box in boxes]
    heights = [box[3] - box[1] for box in boxes]
    width, height = max(widths), sum(heights) + 2 * SCALE * (len(lines) - 1)
    if width <= 0 or height <= 0 or width > 4096 * SCALE or height > 4096 * SCALE:
        raise ValueError("rendered text block exceeds bounded atlas size")
    patch = Image.new("RGBA", (width, height))
    draw = ImageDraw.Draw(patch)
    top = 0
    line_bounds = []
    for line, box, w, h in zip(lines, boxes, widths, heights):
        left = (width - w) // 2
        draw.text((left - box[0], top - box[1]), line, font=font, fill="white",
                  stroke_width=SCALE, stroke_fill=(20, 20, 20, 255))
        line_bounds.append([left / SCALE, top / SCALE, (left + w) / SCALE, (top + h) / SCALE])
        top += h + 2 * SCALE
    patch = patch.resize((math.ceil(width / SCALE), math.ceil(height / SCALE)), Image.Resampling.LANCZOS)
    return patch, line_bounds


def symbol_patch(symbol):
    size = number(symbol.get("size"), "symbol size", 1, 128)
    outline = number(symbol.get("outline_width"), "symbol outline_width", 0.5, size / 2)
    side = math.ceil(size * SCALE)
    patch = Image.new("RGBA", (side, side))
    draw = ImageDraw.Draw(patch)
    bounds = (0, 0, side - 1, side - 1)
    if symbol.get("kind") == "circle":
        draw.ellipse(bounds, fill="black", outline="white", width=max(1, round(outline * SCALE)))
    elif symbol.get("kind") == "triangle":
        points = [(side // 2, 0), (side - 1, side - 1), (0, side - 1)]
        draw.polygon(points, fill="black")
        draw.line(points + [points[0]], fill="white", width=max(1, round(outline * SCALE)), joint="curve")
    else:
        raise ValueError(f"invalid symbol kind: {symbol.get('kind')}")
    return patch.resize((math.ceil(size), math.ceil(size)), Image.Resampling.LANCZOS)


def render_region(region, features, translations, layouts, symbols, font_paths, cmaps, gap, shared_sources=None, canonical_captions=None):
    size = (region["width_tiles"] * TILE_SIZE, region["height_tiles"] * TILE_SIZE)
    atlas, occupied = Image.new("RGBA", size), Image.new("L", size)
    labels, symbol_reports, instances = [], [], []
    shared_sources = shared_sources or {}

    def place(identity, patch, x, y, category):
        number(x, f"{identity} x", -4096, 8192)
        number(y, f"{identity} y", -4096, 8192)
        left, top = math.floor(x - patch.width / 2), math.floor(y - patch.height / 2)
        local = patch.getchannel("A").getbbox()
        if local is None:
            gap("empty_render", id=identity)
            return None, [], (left, top)
        bounds = [left + local[0], top + local[1], left + local[2], top + local[3]]
        if bounds[0] < 0 or bounds[1] < 0 or bounds[2] > size[0] or bounds[3] > size[1]:
            gap("clipped", category=category, id=identity, bounds=bounds)
        strong = Image.new("L", size)
        strong.paste(patch.getchannel("A").point(lambda value: 255 if value >= 128 else 0), (left, top))
        collision = ImageChops.multiply(strong, occupied)
        if collision.getbbox():
            gap("collision", category=category, id=identity, pixels=mask_count(collision), bbox=collision.getbbox())
        occupied.paste(ImageChops.lighter(occupied, strong))
        layer = Image.new("RGBA", size)
        layer.paste(patch, (left, top))
        atlas.alpha_composite(layer)
        touched = []
        layer_alpha = layer.getchannel("A")
        for ty in range(region["height_tiles"]):
            for tx in range(region["width_tiles"]):
                box = (tx * TILE_SIZE, ty * TILE_SIZE, (tx + 1) * TILE_SIZE, (ty + 1) * TILE_SIZE)
                if layer_alpha.crop(box).getbbox():
                    touched.append([region["z"], region["x"] + tx, region["y"] + ty])
        if len(touched) > 1:
            instances.append(dict(id=identity, category=category, tiles=touched))
        return bounds, touched, (left, top)

    for identity, feature in features.items():
        translation, layout = translations.get(identity), layouts.get(identity)
        if translation is None or layout is None:
            continue
        zh = translation.get("zh")
        elevation_only = feature.get("elevation_only", False)
        if "elevation_only" in feature and (type(elevation_only) is not bool or feature["kind"] != "peak"):
            gap("elevation_only", id=identity, reason="only a boolean peak flag is permitted")
            continue
        if (translation.get("status") != "approved" or not http_url(translation.get("source"))
                or not isinstance(translation.get("review_notes"), str) or not translation["review_notes"].strip()):
            gap("translation_review", id=identity)
        if translation.get("record_sha256") != record_fingerprint(feature):
            gap("translation_fingerprint", id=identity)
        if not chinese_name(zh):
            gap("non_chinese", id=identity, zh=zh)
            continue
        if set(layout) - {"id", "x", "y", "font_size", "font_style", "lines"}:
            gap("layout_fields", id=identity, reason="unknown layout field; elevation comes only from source")
        lines = layout.get("lines", [zh])
        if (not isinstance(lines, list) or not 1 <= len(lines) <= 16
                or any(not isinstance(line, str) or not line or "\n" in line or "\r" in line for line in lines)
                or "".join(lines) != zh):
            gap("layout_lines", id=identity)
            continue
        lines = list(lines)
        elevation = None
        if feature["kind"] == "peak":
            elevation = feature.get("original_elevation_m")
            if not isinstance(elevation, str) or not re.fullmatch(r"[0-9]{1,5}(?:\.[0-9]+)?", elevation):
                gap("original_elevation", id=identity)
                continue
            elevation += "米"
            if elevation_only:
                if (not re.fullmatch(re.escape(feature["original_elevation_m"]) + r"\s*m", feature["original"])
                        or zh != elevation or lines != [elevation]):
                    gap("elevation_only", id=identity, reason="source and single Chinese line must contain only the original elevation")
                    continue
            else:
                lines.append(elevation)
        style = layout.get("font_style")
        if style not in font_paths:
            raise ValueError(f"invalid font_style: {identity}")
        font_size = number(layout.get("font_size"), f"{identity} font_size", 1, 128)
        characters = sorted(set("".join(lines)))
        missing = [character for character in characters if ord(character) not in cmaps[style]]
        if missing:
            gap("missing_glyph", id=identity, style=style, characters=missing)
            continue
        patch, line_bounds = text_patch(lines, font_paths[style], font_size)
        bounds, touched, origin = place(identity, patch, layout.get("x"), layout.get("y"), "label")
        labels.append(dict(id=identity, kind=feature["kind"], zh=zh, lines=lines, elevation=elevation,
                           **(dict(elevation_only=True) if elevation_only else {}),
                           bounds=bounds, line_bounds=[[b[0] + origin[0], b[1] + origin[1],
                                                       b[2] + origin[0], b[3] + origin[1]] for b in line_bounds],
                           font_style=style, font_size=font_size, tiles=touched,
                           glyphs=[dict(character=c, codepoint=f"U+{ord(c):04X}", glyph=cmaps[style][ord(c)])
                                   for c in characters]))
    for identity, caption in (canonical_captions or {}).items():
        layout = caption["layout"]
        style = layout["font_style"]
        if style not in font_paths:
            raise ValueError(f"invalid canonical font_style: {identity}")
        font_size = number(layout["font_size"], f"{identity} font_size", 1, 128)
        lines = layout["lines"]
        missing = sorted({char for char in "".join(lines) if ord(char) not in cmaps[style]})
        if missing:
            gap("missing_glyph", id=identity, origin="canonical_wwp", style=style, characters=missing)
            continue
        patch, line_bounds = text_patch(lines, font_paths[style], font_size)
        bounds, touched, origin = place(identity, patch, layout["x"], layout["y"], "canonical_caption")
        labels.append(dict(id=identity, kind=caption["kind"], zh="".join(lines), lines=lines, elevation=None,
            origin="canonical_wwp", canonical_record=caption["canonical_record"], source_bbox=None,
            bounds=bounds, line_bounds=[[b[0]+origin[0], b[1]+origin[1], b[2]+origin[0], b[3]+origin[1]] for b in line_bounds],
            font_style=style, font_size=font_size, tiles=touched))

    for identity, symbol in symbols.items():
        unlabeled = symbol.get("unlabeled", False)
        if ("unlabeled" in symbol and type(unlabeled) is not bool
                or unlabeled and (symbol.get("feature_id") is not None or not http_url(symbol.get("source_url"))
                                  or not isinstance(symbol.get("review_notes"), str) or not symbol["review_notes"].strip())):
            gap("unlabeled_symbol", id=identity, reason="requires independent source evidence and no named feature")
            continue
        if "shared_source_id" in symbol:
            shared_id = symbol["shared_source_id"]
            source = shared_sources.get(shared_id) if isinstance(shared_id, str) else None
            if (unlabeled or "feature_id" in symbol or source is None
                    or (source["kind"], symbol.get("kind")) not in (("city", "circle"), ("peak", "triangle"))):
                gap("symbol_binding", id=identity, reason="requires exactly one valid named shared source")
                continue
        elif not unlabeled and symbol.get("feature_id") not in features:
            gap("symbol_feature", id=identity, feature_id=symbol.get("feature_id"))
            continue
        bounds, touched, _ = place(identity, symbol_patch(symbol), symbol.get("x"), symbol.get("y"), "symbol")
        symbol_reports.append(dict(id=identity, feature_id=symbol.get("feature_id"), kind=symbol["kind"],
                                   **(dict(shared_source_id=shared_id) if "shared_source_id" in symbol else {}),
                                   **(dict(unlabeled=True) if unlabeled else {}),
                                   bounds=bounds, tiles=touched))
    return atlas, labels, symbol_reports, instances


def canonical_caption_body_fingerprint(caption):
    """Authorization fingerprints exclude review; region review remains separate."""
    return record_fingerprint({key: value for key, value in caption.items() if key != "review"})


def read_canonical_caption_approvals(path, expected_sha256):
    """Read an explicitly hashed selection; never infer approval from a manifest."""
    if not isinstance(expected_sha256, str) or not re.fullmatch("[0-9a-f]{64}", expected_sha256):
        raise ValueError("requires external canonical-caption selection SHA256")
    raw = Path(path).read_bytes()
    if sha256(raw) != expected_sha256:
        raise ValueError("canonical-caption selection SHA256 changed")

    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate canonical-caption selection JSON key")
            result[key] = value
        return result

    def nonfinite(value):
        raise ValueError("nonfinite canonical-caption selection JSON number")

    return json.loads(raw.decode("utf-8"), object_pairs_hook=unique, parse_constant=nonfinite)


def audit_canonical_captions(region_rows, approval):
    """Exact finite WWP members, independent of supplier glyph and native ledgers."""
    if approval is None and all("canonical_captions" not in region for region in region_rows):
        return {}, []

    def invalid_ids(code, **details):
        raise ValueError("invalid canonical caption IDs: " + code)

    captions, wanted = {}, {}
    fields = {"id", "origin", "canonical_record", "source_bbox", "source_png", "source_native", "kind", "layout"}
    record_fields = {"layer", "wwp_file", "record_index_zero_based", "original_Chinese_name",
                     "lat_f32_bits", "lon_f32_bits", "flags", "level"}
    for region in region_rows:
        local = indexed(region.get("canonical_captions", []), "canonical_captions", invalid_ids)
        other = {item["id"] for field in ("features", "symbols", "shared_sources") for item in region.get(field, [])}
        if other & local.keys():
            raise ValueError("canonical caption ID overlaps Source feature/native/shared ID")
        captions[region["id"]] = local
        for identity, caption in local.items():
            if (set(caption) != fields or caption["origin"] != "canonical_wwp"
                    or any(caption[key] is not None for key in ("source_bbox", "source_png", "source_native"))
                    or caption["kind"] not in ("admin0", "region")):
                raise ValueError("requires separate canonical text with null Source fields and no native/height")
            key = region["id"], region["z"], identity
            wanted[key] = canonical_caption_body_fingerprint(caption)
    if not wanted and approval is None:
        return captions, []
    if (not isinstance(approval, dict) or set(approval) != {"schema", "role", "instances"}
            or (approval["schema"], approval["role"]) not in {
                ("explicit-canonical-WWP-caption-selection-v1", "TEST_ONLY"),
                ("root-approved-canonical-WWP-caption-v1", "named_private"),
                ("root-approved-canonical-WWP-caption-v1", "full_current")}
            or not isinstance(approval["instances"], list)):
        raise ValueError("requires explicit exact canonical caption selection and role")
    root_selected = approval["schema"] == "root-approved-canonical-WWP-caption-v1"
    selected = {}
    for entry in approval["instances"]:
        fields = {"region", "z", "caption_id", "caption_body_sha256"} | ({"evidence"} if root_selected else set())
        if (not isinstance(entry, dict) or set(entry) != fields
                or type(entry["z"]) is not int
                or any(not isinstance(entry[key], str) or not entry[key] for key in ("region", "caption_id"))
                or not isinstance(entry["caption_body_sha256"], str)
                or not re.fullmatch("[0-9a-f]{64}", entry["caption_body_sha256"])):
            raise ValueError("invalid exact canonical caption selection instance")
        if root_selected:
            evidence = entry["evidence"]
            if (not isinstance(evidence, dict)
                    or set(evidence) != {"model_selection", "manifest", "review", "target_signature"}):
                raise ValueError("requires four distinct canonical evidence roles")
            for key, value in evidence.items():
                if approval["role"] == "named_private" and key != "model_selection":
                    if value is not None:
                        raise ValueError("named private caption cannot invent future TARGET evidence")
                elif (not isinstance(value, dict) or set(value) != {"path", "sha256"}
                        or not isinstance(value["path"], str) or not Path(value["path"]).is_absolute()
                        or not isinstance(value["sha256"], str) or not re.fullmatch("[0-9a-f]{64}", value["sha256"])):
                    raise ValueError("requires exact original canonical evidence reference")
        key = entry["region"], entry["z"], entry["caption_id"]
        if key in selected:
            raise ValueError("duplicate canonical caption selection instance")
        selected[key] = entry["caption_body_sha256"]
    if selected != wanted:
        raise ValueError("canonical caption selection must exactly equal requested owner/zoom/IDs/body fingerprints")
    from wwp import load
    assets, sources, members = {}, [], set()
    for region in region_rows:
        for caption in captions[region["id"]].values():
            record = caption["canonical_record"]
            if not isinstance(record, dict) or set(record) != record_fields:
                raise ValueError("invalid canonical WWP record fields")
            layer = record["layer"]
            if layer not in ("Cities", "Synoptic"):
                raise ValueError("invalid canonical WWP layer")
            file = layer + ".wwp"
            ref = record["wwp_file"]
            if (not isinstance(ref, dict) or set(ref) != {"path", "sha256"}
                    or ref["path"] != "assets/placenames/" + file
                    or not isinstance(ref["sha256"], str) or not re.fullmatch("[0-9a-f]{64}", ref["sha256"])):
                raise ValueError("requires exact logical canonical WWP asset path and raw SHA")
            path = Path(__file__).resolve().parent.parent / ref["path"]
            if file not in assets:
                raw = path.read_bytes()
                digest = sha256(raw)
                if digest != ref["sha256"]:
                    raise ValueError("canonical WWP raw SHA mismatch before member parse")
                points = load(path)
                if sha256(path.read_bytes()) != digest:
                    raise ValueError("canonical WWP changed during member parse")
                assets[file] = digest, points
                sources.append(dict(file=file, sha256=assets[file][0], level=1 if layer == "Cities" else 0))
            digest, points = assets[file]
            index = record["record_index_zero_based"]
            if (digest != ref["sha256"] or type(index) is not int or not 0 <= index < len(points)
                    or any(type(record[key]) is not int or not 0 <= record[key] <= 0xffffffff
                           for key in ("lat_f32_bits", "lon_f32_bits", "flags"))
                    or type(record["level"]) is not int or record["level"] != (1 if layer == "Cities" else 0)):
                raise ValueError("canonical WWP raw hash/index/bits/flags/level mismatch")
            member = region["id"], region["z"], file, digest, index
            if member in members:
                raise ValueError("duplicate canonical WWP member under different caption IDs")
            members.add(member)
            point = points[index]
            if (record["original_Chinese_name"] != point["name"] or not chinese_name(point["name"])
                    or record["lat_f32_bits"] != float32_bits(point["lat"])
                    or record["lon_f32_bits"] != float32_bits(point["lon"])
                    or record["flags"] != point["flags"]):
                raise ValueError("canonical caption is not the exact selected original WWP member")
            z = integer(region["z"], "canonical owner z", 3, 10)
            n = 1 << z
            lat, lon = point["lat"], point["lon"]
            number(lat, "canonical WWP latitude", -85.05112878, 85.05112878)
            number(lon, "canonical WWP longitude", -180, 180)
            px = (lon + 180) / 360 * n
            py = (1 - math.asinh(math.tan(math.radians(lat))) / math.pi) / 2 * n
            if not (region["x"] <= px <= region["x"] + region["width_tiles"]
                    and region["y"] <= py <= region["y"] + region["height_tiles"]):
                raise ValueError("canonical WWP point is outside selected owner coverage")
            layout = caption["layout"]
            if (not isinstance(layout, dict) or set(layout) != {"x", "y", "font_style", "font_size", "lines"}
                    or not isinstance(layout["lines"], list) or not 1 <= len(layout["lines"]) <= 16
                    or any(not isinstance(line, str) or not line or "\n" in line or "\r" in line for line in layout["lines"])
                    or "".join(layout["lines"]) != point["name"]):
                raise ValueError("canonical caption must render exactly the original Chinese with explicit layout")
    return captions, sources


def compile_manifest(manifest, original_root, font_path, bold_font_path, preview_dir=None, owner_native_source_approvals=None, canonical_caption_approvals=None):
    scope = manifest.get("scope", {})
    if (type(manifest.get("schema_version")) is not int or manifest["schema_version"] != 1
            or not isinstance(scope, dict) or scope.get("type") != "cached_regions"
            or scope.get("global_complete") is not False):
        raise ValueError("expected schema_version 1 and explicitly finite cached_regions scope")
    canonical_by_region, canonical_sources = audit_canonical_captions(records(manifest.get("regions"), "regions"), canonical_caption_approvals)
    root = Path(original_root).resolve()
    font_paths = dict(regular=Path(font_path), bold=Path(bold_font_path))
    fonts, cmaps = {}, {}
    for style, path in font_paths.items():
        fonts[style], cmaps[style] = font_info(path)
    report = dict(schema_version=1, ready=False, global_complete=False,
                  manifest_sha256=record_fingerprint(manifest), fonts=fonts, regions=[], gaps=[],
                  strong_alpha_threshold=128, supersampling=SCALE, legacy_suppressions=[])

    def global_gap(code, **details):
        report["gaps"].append(dict(code=code, **details))

    for style, key in (("regular", "font_sha256"), ("bold", "bold_font_sha256")):
        if manifest.get(key) != fonts[style]["sha256"]:
            global_gap("font_sha256", style=style, expected=manifest.get(key), actual=fonts[style]["sha256"])
    region_rows = records(manifest.get("regions"), "regions")
    if not region_rows:
        global_gap("empty_regions")
    if owner_native_source_approvals is not None:
        wanted = {(region["id"], source["id"]) for region in region_rows
                  for source in region.get("shared_sources", []) if "owner_native_source_refs" in source}
        entries = owner_native_source_approvals
        if (not isinstance(entries, list) or any(not isinstance(entry, dict) for entry in entries)
                or len(entries) != len(wanted)
                or {(entry.get("region"), entry.get("shared_source_id")) for entry in entries} != wanted):
            global_gap("owner_native_source", reason="external approval set must exactly match requested finite references")
    if canonical_sources:
        report["canonical_sources"] = canonical_sources
        report["canonical_caption_selection_role"] = canonical_caption_approvals["role"]
    covered, tile_records, bounded_regions = set(), [], []
    for region in region_rows:
        identity = region.get("id")
        z = integer(region.get("z"), "z", 3, 10)
        x = integer(region.get("x"), "x", 0, (1 << z) - 1)
        y = integer(region.get("y"), "y", 0, (1 << z) - 1)
        width = integer(region.get("width_tiles"), "width_tiles", 1, 16)
        height = integer(region.get("height_tiles"), "height_tiles", 1, 16)
        if width * height > MAX_REGION_TILES or x + width > 1 << z or y + height > 1 << z:
            raise ValueError(f"region exceeds tile bounds or {MAX_REGION_TILES}-tile limit: {identity}")
        keys = {(z, tx, ty) for ty in range(y, y + height) for tx in range(x, x + width)}
        if covered & keys:
            global_gap("overlapping_tile", region=identity, tiles=sorted(covered & keys))
        covered.update(keys)
        if len(covered) > MAX_TOTAL_TILES:
            raise ValueError(f"manifest exceeds {MAX_TOTAL_TILES}-tile limit")
        bounded_regions.append((region, z, x, y, width, height, keys))
    legacy_points = None
    suppression_records = {}
    native_boxes, native_centers = {}, {}
    input_paths = {path.resolve() for path in font_paths.values()}
    input_paths.update(Path(__file__).resolve().parent.parent / "assets/placenames" / row["file"] for row in canonical_sources)
    # Collect every region's sources before writing any previews: an earlier
    # region's preview filename must not overwrite a later region's evidence.
    for region in region_rows:
        for field in ("original_tiles", "context_tiles"):
            for tile in records(region.get(field), field):
                relative = tile.get("path")
                if isinstance(relative, str):
                    input_paths.add((root / relative).resolve())
    previews = preview_paths(region_rows, preview_dir, input_paths)
    # A reference can precede its owner. Index only normal source features, so
    # shared references and symbols cannot form reference chains or cycles.
    region_index = {region["id"]: region for region in region_rows}
    feature_index = {region["id"]: {feature.get("id"): feature
                                   for feature in records(region.get("features"), "features")}
                     for region in region_rows}
    shared_references = []
    for region, z, x, y, width, height, keys in bounded_regions:
        identity = region["id"]
        region_report = dict(id=identity, gaps=[], covered_tiles=sorted(keys),
                             input_sha256=record_fingerprint({k: v for k, v in region.items() if k != "review"}))
        report["regions"].append(region_report)

        def gap(code, **details):
            entry = dict(code=code, region=identity, **details)
            report["gaps"].append(entry)
            region_report["gaps"].append(entry)

        review = region.get("review", {})
        if (review.get("status") != "approved" or review.get("source_verified") is not True
                or review.get("render_verified") is not True
                or any(not isinstance(review.get(k), str) or not review[k].strip() for k in ("reviewer", "notes"))):
            gap("region_review")
        if review.get("record_sha256") != region_report["input_sha256"]:
            gap("region_fingerprint")
        features = indexed(region.get("features"), "features", gap)
        translations = indexed(region.get("translations"), "translations", gap)
        layouts = indexed(region.get("layouts"), "layouts", gap)
        symbols = indexed(region.get("symbols"), "symbols", gap)
        for name, collection in (("translations", translations), ("layouts", layouts)):
            if collection.keys() != features.keys():
                gap("record_set", collection=name, missing=sorted(features.keys() - collection.keys()),
                    extra=sorted(collection.keys() - features.keys()))
        for feature in features.values():
            if (feature.get("kind") not in LAYERS or not isinstance(feature.get("original"), str)
                    or not feature["original"].strip() or not http_url(feature.get("source_url"))):
                raise ValueError(f"invalid original feature: {feature['id']}")
        shared_sources = audit_shared_sources(region, features, region_index, feature_index, gap,
            owner_native_source_approvals, root, input_paths)
        if "shared_sources" in region:
            region_report["shared_sources"] = list(shared_sources.values())
        shared_references.extend((region_report, source) for source in shared_sources.values())
        size = (width * TILE_SIZE, height * TILE_SIZE)
        original, contexts, sources, verified_sources = source_atlas(region, root, size, gap, input_paths)
        region_report["source_tiles"] = sources
        background, background_audits = audit_source_backgrounds(region, original, verified_sources, gap)
        if "source_backgrounds" in region:
            region_report["source_backgrounds"] = background_audits
        region_report["source_alpha"] = audit_original(
            region, original, contexts, features, symbols, gap, shared_sources, background)
        atlas, labels, symbol_reports, crossing = render_region(
            region, features, translations, layouts, symbols, font_paths, cmaps, gap, shared_sources, canonical_by_region.get(identity))
        # Native identity is physical, independent of feature/shared aliases.
        # Use actual raster bounds for center aliases after pixel rounding.
        for rendered in symbol_reports:
            symbol = symbols[rendered["id"]]
            box = world_original_bbox(region, symbol["original_bbox"], f"{symbol['id']} native original_bbox")
            bounds = rendered["bounds"]
            center = [x * TILE_SIZE + (bounds[0] + bounds[2]) / 2,
                      y * TILE_SIZE + (bounds[1] + bounds[3]) / 2]
            reference = dict(region=identity, id=symbol["id"])
            for code, key, index in (
                    ("duplicate_native_bbox", (z, *box), native_boxes),
                    ("duplicate_native_center", (z, symbol["kind"], *center), native_centers)):
                if key in index:
                    gap(code, first=index[key], second=reference)
                else:
                    index[key] = reference
        if records(region.get("legacy_suppressions", []), "legacy_suppressions"):
            if legacy_points is None:
                legacy_points, report["legacy_sources"] = canonical_legacy_points()
            rules = audit_legacy_suppressions(region, features, translations, labels, legacy_points, gap)
            region_report["legacy_suppressions"] = rules
            for rule in rules:
                key = legacy_key(z, rule["name"], rule["lat"], rule["lon"], rule["level"])
                if key in suppression_records:
                    suppression_records[key]["references"].extend(rule["references"])
                else:
                    suppression_records[key] = dict(rule, references=list(rule["references"]))
        # Sign pixels rather than PNG encoding: font/rasterizer changes require
        # visual approval again, while lossless encoder changes do not.
        render_sha256 = sha256(atlas.convert("RGBA").tobytes())
        if review.get("render_sha256") != render_sha256:
            gap("render_fingerprint", expected=review.get("render_sha256"), actual=render_sha256)
        canonical_labels = [label for label in labels if label.get("origin") == "canonical_wwp"]
        source_labels = [label for label in labels if label.get("origin") != "canonical_wwp"]
        if canonical_labels:
            region_report["canonical_captions"] = canonical_labels
        region_report.update(labels=source_labels, symbols=symbol_reports, cross_tile_instances=crossing,
                             atlas_rgba_sha256=render_sha256, atlas_png_sha256=sha256(png_bytes(atlas)), tiles=[])
        if preview_dir is not None:
            for suffix, image in (("original", original), ("zh", atlas)):
                atomic_write(previews[identity, suffix], png_bytes(image))
        for ty in range(height):
            for tx in range(width):
                key = [z, x + tx, y + ty]
                png = png_bytes(atlas.crop((tx * TILE_SIZE, ty * TILE_SIZE, (tx + 1) * TILE_SIZE, (ty + 1) * TILE_SIZE)))
                count = sum(key in label["tiles"] for label in labels)
                tile_records.append((tuple(key), count, png))
                region_report["tiles"].append(dict(z=z, x=x + tx, y=y + ty, label_count=count,
                                                   png_sha256=sha256(png)))
    rendered_regions = {region["id"]: region for region in report["regions"]}
    for borrower, source in shared_references:
        owner = rendered_regions[source["owner_region"]]
        if (owner["gaps"] or not any(label["id"] == source["feature_id"] and label["bounds"] is not None
                                     and label["tiles"] for label in owner["labels"])):
            entry = dict(code="shared_source_owner", region=borrower["id"], id=source["id"],
                         owner_region=source["owner_region"], feature_id=source["feature_id"],
                         reason="owner must have valid source/render approval and actual Chinese output")
            borrower["gaps"].append(entry)
            report["gaps"].append(entry)
    report["legacy_suppressions"] = [suppression_records[key] for key in sorted(suppression_records)]
    report.update(ready=not report["gaps"], region_count=len(region_rows), tile_count=len(covered),
                  label_count=sum(len(r["labels"]) + len(r.get("canonical_captions", [])) for r in report["regions"]),
                  symbol_count=sum(len(r["symbols"]) for r in report["regions"]))
    if canonical_sources:
        report["source_label_count"] = sum(len(r["labels"]) for r in report["regions"])
        report["canonical_caption_count"] = sum(len(r.get("canonical_captions", [])) for r in report["regions"])
    if not report["ready"]:
        return None, report
    payload = bytearray(struct.pack("<i", len(tile_records)))
    for key, count, png in sorted(tile_records):
        payload.extend(struct.pack("<iiiii", *key, count, len(png)))
        payload.extend(png)
    if suppression_records:
        payload.extend(struct.pack("<i", len(suppression_records)))
        for rule in report["legacy_suppressions"]:
            payload.extend(struct.pack("<i", rule["z"]))
            payload.extend(dotnet_string(rule["name"]))
            payload.extend(struct.pack("<ffi", rule["lat"], rule["lon"], rule["level"]))
    fingerprint = sha256(payload)
    report["payload_sha256"] = fingerprint
    magic = MAGIC_WITH_LEGACY if suppression_records else MAGIC
    wire = magic + dotnet_string(fingerprint) + payload
    report["pack_sha256"] = sha256(wire)
    return wire, report


def read_owner_native_source_approvals(path, expected_sha256):
    """Read the complete independently selected strict-v2 file, never infer it."""
    if not isinstance(expected_sha256, str) or not re.fullmatch("[0-9a-f]{64}", expected_sha256):
        raise ValueError("requires external owner-native approval file SHA256")
    raw = Path(path).read_bytes()
    if sha256(raw) != expected_sha256:
        raise ValueError("owner-native approval file SHA256 changed")
    document = json.loads(raw)
    if (not isinstance(document, dict) or set(document) != {"schema", "entries"}
            or document["schema"] != "root-approved-owner-native-source-v2"
            or not isinstance(document["entries"], list)):
        raise ValueError("requires exact strict-v2 owner-native approval document")
    seen = set()
    for entry in document["entries"]:
        if (not isinstance(entry, dict) or set(entry) != {"region", "shared_source_id", "owner_region", "feature_id", "owner_record_sha256", "symbol_refs"}
                or any(not isinstance(entry[key], str) or not entry[key]
                       for key in ("region", "shared_source_id", "owner_region", "feature_id"))
                or not isinstance(entry["owner_record_sha256"], str)
                or not re.fullmatch("[0-9a-f]{64}", entry["owner_record_sha256"])
                or not isinstance(entry["symbol_refs"], list) or not entry["symbol_refs"]):
            raise ValueError("invalid exact owner-native approval entry")
        key = entry["region"], entry["shared_source_id"]
        if key in seen:
            raise ValueError("duplicate external owner-native approval entry")
        seen.add(key)
        symbols = set()
        for ref in entry["symbol_refs"]:
            if (not isinstance(ref, dict) or set(ref) != {"symbol_id", "record_sha256", "world_original_bbox", "source_rgba_sha256"}
                    or not isinstance(ref["symbol_id"], str) or not ref["symbol_id"] or ref["symbol_id"] in symbols
                    or any(not isinstance(ref[field], str) or not re.fullmatch("[0-9a-f]{64}", ref[field])
                           for field in ("record_sha256", "source_rgba_sha256"))):
                raise ValueError("invalid exact owner-native source ref")
            symbols.add(ref["symbol_id"])
            box = ref["world_original_bbox"]
            if (not isinstance(box, list) or len(box) != 4
                    or any(type(v) not in (int, float) or not math.isfinite(v) or not float(v).is_integer() for v in box)
                    or box[0] >= box[2] or box[1] >= box[3]):
                raise ValueError("requires complete integer original native frame")
    return document["entries"]


def write_map_source_package(directory, args, manifest, manifest_wire, report, manifest_pins=None):
    """Create a finite byte-copy source bundle and its exact CLI recipe."""
    directory = Path(directory).resolve()
    if directory.exists():
        raise ValueError("source package destination must not exist")
    wire_manifest = load_manifest(args.manifest)
    if (any(region.get("canonical_captions") for document in (wire_manifest, manifest, report)
            for region in document.get("regions", [])) or report.get("canonical_caption_count", 0)):
        selection_path = getattr(args, "canonical_caption_approvals", None)
        selection_sha256 = getattr(args, "canonical_caption_approvals_sha256", None)
        if selection_path is None or selection_sha256 is None:
            raise ValueError("caption-bearing source package requires original external selection file and SHA256")
        if record_fingerprint(wire_manifest) != record_fingerprint(manifest):
            raise ValueError("source package caption manifest differs from original manifest wire")
        observed_pins = {path: sha256(path.read_bytes()) for path in manifest_files(args.manifest)}
        if manifest_pins is not None and manifest_pins != observed_pins:
            raise ValueError("caption manifest input closure changed before source package")
        manifest_pins = observed_pins
        selected = read_canonical_caption_approvals(selection_path, selection_sha256)
        captions, sources = audit_canonical_captions(records(manifest.get("regions"), "regions"), selected)
        wanted = {(region_id, identity): record_fingerprint(caption["canonical_record"])
                  for region_id, local in captions.items() for identity, caption in local.items()}
        rendered = {}
        for region in records(report.get("regions"), "report regions"):
            for caption in records(region.get("canonical_captions", []), "report canonical_captions"):
                key = region["id"], caption["id"]
                if (key in rendered or caption.get("origin") != "canonical_wwp"
                        or caption.get("source_bbox") is not None or caption.get("elevation") is not None):
                    raise ValueError("source package report has invalid or duplicate canonical caption identity")
                rendered[key] = record_fingerprint(caption["canonical_record"])
        if (rendered != wanted or type(report.get("canonical_caption_count")) is not int
                or report["canonical_caption_count"] != len(wanted)
                or record_fingerprint(report.get("canonical_sources")) != record_fingerprint(sources)):
            raise ValueError("source package report must match exact selected captions and original WWP sources")
    files = {}

    def add(relative, path, expected=None):
        relative, path = Path(relative), Path(path)
        raw = path.read_bytes()
        if expected is not None and sha256(raw) != expected:
            raise ValueError(f"source package input SHA changed: {path}")
        if relative in files and files[relative] != raw:
            raise ValueError("conflicting source package input")
        files[relative] = raw

    if args.manifest.read_bytes() != manifest_wire:
        raise ValueError("manifest changed during source build")
    add('inputs/manifest.json', args.manifest)
    for path in manifest_files(args.manifest)[1:]:
        add(Path('inputs') / path.relative_to(args.manifest.resolve().parent), path,
            manifest_pins.get(path) if manifest_pins is not None else None)
    for region in manifest['regions']:
        for field in ('original_tiles', 'context_tiles'):
            for tile in region.get(field, []):
                relative = Path(tile['path'])
                source = (args.original_root / relative).resolve()
                if relative.is_absolute() or not source.is_relative_to(args.original_root.resolve()):
                    raise ValueError("source package original must remain within its root")
                add(Path('originals') / relative, source, tile['sha256'])
    add('fonts/regular.ttc', args.font, report['fonts']['regular']['sha256'])
    add('fonts/bold.ttc', args.bold_font, report['fonts']['bold']['sha256'])
    scripts = Path(__file__).resolve().parent
    for name in ('build_map_tile_pack.py', 'map_tile_manifest.py', 'global_map_catalog.py', 'wwp.py'):
        add(Path('scripts') / name, scripts / name)
    for name in ('Cities.wwp', 'Synoptic.wwp'):
        expected = next((r['sha256'] for r in report.get('legacy_sources', []) + report.get('canonical_sources', []) if r['file'] == name), None)
        add(Path('assets/placenames') / name, scripts.parent / 'assets/placenames' / name, expected)
    argv = ['scripts/build_map_tile_pack.py', '--manifest', 'inputs/manifest.json',
            '--original-root', 'originals', '--font', 'fonts/regular.ttc', '--bold-font', 'fonts/bold.ttc',
            '--output', 'out/ChineseMapTiles.dat', '--report', 'out/map_tile_build_report.json']
    if args.owner_native_source_approvals is not None:
        relative = 'inputs/owner_native_source_approvals.json'
        add(relative, args.owner_native_source_approvals, args.owner_native_source_approvals_sha256)
        argv += ['--owner-native-source-approvals', relative,
                 '--owner-native-source-approvals-sha256', args.owner_native_source_approvals_sha256]
    if getattr(args, "canonical_caption_approvals", None) is not None:
        relative = 'inputs/canonical_caption_approvals.json'
        add(relative, args.canonical_caption_approvals, args.canonical_caption_approvals_sha256)
        argv += ['--canonical-caption-approvals', relative,
                 '--canonical-caption-approvals-sha256', args.canonical_caption_approvals_sha256]
    if manifest_pins is not None and any(sha256(path.read_bytes()) != digest for path, digest in manifest_pins.items()):
        raise ValueError("manifest fragments changed during source package collection")
    directory.mkdir(parents=True)
    rows = []
    for relative, raw in sorted(files.items()):
        target = directory / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(raw)
        rows.append(dict(path=relative.as_posix(), sha256=sha256(raw), bytes=len(raw)))
    recipe = dict(schema='finite-map-source-package-v1', argv=argv, files=rows,
                  expected_pack_sha256=report['pack_sha256'],
                  original_native_source_approvals=report.get('owner_native_source_approvals'),
                  note='Verify every listed file SHA before executing argv from this directory. External approval is copied unchanged, not derived from the manifest.')
    (directory / 'rebuild_inputs.json').write_text(json.dumps(recipe, ensure_ascii=False, indent=2)+'\n',
                                                   encoding='utf-8', newline='\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("manifest", "original-root", "font", "bold-font", "output", "report"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--preview-dir", type=Path)
    parser.add_argument("--owner-native-source-approvals", type=Path)
    parser.add_argument("--owner-native-source-approvals-sha256")
    parser.add_argument("--source-package-dir", type=Path)
    parser.add_argument("--canonical-caption-approvals", type=Path)
    parser.add_argument("--canonical-caption-approvals-sha256")
    args = parser.parse_args()
    if bool(args.owner_native_source_approvals) != bool(args.owner_native_source_approvals_sha256):
        parser.error("owner-native approval file and SHA256 must be supplied together")
    approvals = None
    if args.owner_native_source_approvals is not None:
        approvals = read_owner_native_source_approvals(args.owner_native_source_approvals,
                                                     args.owner_native_source_approvals_sha256)
    if bool(args.canonical_caption_approvals) != bool(args.canonical_caption_approvals_sha256):
        parser.error("canonical caption selection file and SHA256 must be supplied together")
    canonical_approvals = None
    if args.canonical_caption_approvals is not None:
        canonical_approvals = read_canonical_caption_approvals(args.canonical_caption_approvals,
                                                              args.canonical_caption_approvals_sha256)
    manifest_wire = args.manifest.read_bytes()
    manifest_pins = {path: sha256(path.read_bytes()) for path in manifest_files(args.manifest)}
    manifest = load_manifest(args.manifest)
    inputs = set(manifest_pins) | {p.resolve() for p in (args.font, args.bold_font)}
    if args.owner_native_source_approvals is not None:
        inputs.add(args.owner_native_source_approvals.resolve())
    if args.canonical_caption_approvals is not None:
        inputs.add(args.canonical_caption_approvals.resolve())
    for region in manifest.get("regions", []):
        for caption in region.get("canonical_captions", []):
            inputs.add((Path(__file__).resolve().parent.parent / caption["canonical_record"]["wwp_file"]["path"]).resolve())
    for region in manifest.get("regions", []):
        for field in ("original_tiles", "context_tiles"):
            inputs.update((args.original_root / row["path"]).resolve() for row in region.get(field, []))
    outputs = {args.output.resolve(), args.report.resolve()}
    if len(outputs) != 2 or inputs & outputs:
        parser.error("input, output and report paths must differ")
    try:
        preview_paths(records(manifest.get("regions"), "regions"), args.preview_dir, inputs | outputs)
    except ValueError as error:
        parser.error(str(error))
    kwargs = {} if approvals is None else {"owner_native_source_approvals": approvals}
    if canonical_approvals is not None:
        kwargs["canonical_caption_approvals"] = canonical_approvals
    wire, report = compile_manifest(manifest, args.original_root, args.font, args.bold_font, args.preview_dir, **kwargs)
    if any(sha256(path.read_bytes()) != expected for path, expected in manifest_pins.items()):
        raise ValueError("manifest fragments changed during build")
    if args.canonical_caption_approvals is not None:
        if read_canonical_caption_approvals(args.canonical_caption_approvals, args.canonical_caption_approvals_sha256) != canonical_approvals:
            raise ValueError("canonical caption selection changed during build")
        report["canonical_caption_approvals"] = dict(path=str(args.canonical_caption_approvals.resolve()),
                                                     sha256=args.canonical_caption_approvals_sha256)
    if args.owner_native_source_approvals is not None:
        if read_owner_native_source_approvals(args.owner_native_source_approvals,
                                            args.owner_native_source_approvals_sha256) != approvals:
            raise ValueError("owner-native approval entries changed during build")
        report["owner_native_source_approvals"] = dict(path=str(args.owner_native_source_approvals.resolve()),
                                                     sha256=args.owner_native_source_approvals_sha256)
    report["manifest_file_sha256"] = sha256(args.manifest.read_bytes())
    report["manifest_inputs"] = [dict(path=str(path), sha256=digest)
                                 for path, digest in manifest_pins.items()]
    if args.source_package_dir is not None:
        if wire is None:
            raise ValueError("source package requires a ready audited map")
        write_map_source_package(args.source_package_dir, args, manifest, manifest_wire, report, manifest_pins)
    atomic_write(args.report, (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    print(f"regions={report['region_count']} tiles={report['tile_count']} "
          f"labels={report['label_count']} gaps={len(report['gaps'])} ready={report['ready']}")
    if wire is None:
        return 1
    atomic_write(args.output, wire)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
