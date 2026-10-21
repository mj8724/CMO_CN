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


def audit_shared_sources(region, features, region_index, feature_index, gap):
    sources = indexed(region.get("shared_sources", []), "shared_sources", gap)
    valid = {}
    for identity, source in sources.items():
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
        valid[identity] = dict(source, world_original_bbox=world_box)
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
    for category, items in (("feature", features), ("symbol", symbols), ("shared_source", shared_sources or {})):
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


def render_region(region, features, translations, layouts, symbols, font_paths, cmaps, gap):
    size = (region["width_tiles"] * TILE_SIZE, region["height_tiles"] * TILE_SIZE)
    atlas, occupied = Image.new("RGBA", size), Image.new("L", size)
    labels, symbol_reports, instances = [], [], []

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
    for identity, symbol in symbols.items():
        unlabeled = symbol.get("unlabeled", False)
        if ("unlabeled" in symbol and type(unlabeled) is not bool
                or unlabeled and (symbol.get("feature_id") is not None or not http_url(symbol.get("source_url"))
                                  or not isinstance(symbol.get("review_notes"), str) or not symbol["review_notes"].strip())):
            gap("unlabeled_symbol", id=identity, reason="requires independent source evidence and no named feature")
            continue
        if not unlabeled and symbol.get("feature_id") not in features:
            gap("symbol_feature", id=identity, feature_id=symbol.get("feature_id"))
            continue
        bounds, touched, _ = place(identity, symbol_patch(symbol), symbol.get("x"), symbol.get("y"), "symbol")
        symbol_reports.append(dict(id=identity, feature_id=symbol.get("feature_id"), kind=symbol["kind"],
                                   **(dict(unlabeled=True) if unlabeled else {}),
                                   bounds=bounds, tiles=touched))
    return atlas, labels, symbol_reports, instances


def compile_manifest(manifest, original_root, font_path, bold_font_path, preview_dir=None):
    scope = manifest.get("scope", {})
    if (type(manifest.get("schema_version")) is not int or manifest["schema_version"] != 1
            or not isinstance(scope, dict) or scope.get("type") != "cached_regions"
            or scope.get("global_complete") is not False):
        raise ValueError("expected schema_version 1 and explicitly finite cached_regions scope")
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
    input_paths = {path.resolve() for path in font_paths.values()}
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
        shared_sources = audit_shared_sources(region, features, region_index, feature_index, gap)
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
            region, features, translations, layouts, symbols, font_paths, cmaps, gap)
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
        region_report.update(labels=labels, symbols=symbol_reports, cross_tile_instances=crossing,
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
                  label_count=sum(len(r["labels"]) for r in report["regions"]),
                  symbol_count=sum(len(r["symbols"]) for r in report["regions"]))
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("manifest", "original-root", "font", "bold-font", "output", "report"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--preview-dir", type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    inputs = {p.resolve() for p in (args.manifest, args.font, args.bold_font)}
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
    wire, report = compile_manifest(manifest, args.original_root, args.font, args.bold_font, args.preview_dir)
    report["manifest_file_sha256"] = sha256(args.manifest.read_bytes())
    atomic_write(args.report, (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    print(f"regions={report['region_count']} tiles={report['tile_count']} "
          f"labels={report['label_count']} gaps={len(report['gaps'])} ready={report['ready']}")
    if wire is None:
        return 1
    atomic_write(args.output, wire)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
