#!/usr/bin/env python3
"""Load editable regional map data, sharing source tiles and source URLs."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path, PurePosixPath
import re

STORAGE = 'split-map-regions-v1'
TILE_FIELDS = ('x', 'y', 'path', 'sha256', 'source_url')
REVIEW_NOTES = {
    'translations': '原文实体与中文译名已核对，译名依据见 source。',
    'legacy_suppressions': '原 WWP 条目与本区已绘制名称为同一实体，仅按完整身份去重。',
    'shared_sources': '完整原文框与唯一输出 owner 已核对，本区不重复绘制名称。',
    'source_backgrounds': '已核对指定原图中的固定低 alpha 背景，其他像素仍须完整归账。',
    'symbols': '原图符号的来源、关联名称和位置已核对。',
    'region': '原图内容、译名、布局及实际 RGBA 已核验；修改输入须重新核验。',
}


def _read(path):
    return json.loads(Path(path).read_bytes())


def _relative(root, value):
    relative = PurePosixPath(value)
    if (relative.is_absolute() or relative.as_posix() != value or '..' in relative.parts
            or '\\' in value or ':' in value):
        raise ValueError('manifest fragment must use a relative POSIX path')
    path = (root / value).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError('manifest fragment must remain within its relative root')
    return path


def manifest_files(path):
    """Return all build inputs, including the index, without expanding data."""
    path = Path(path).resolve()
    index = _read(path)
    if 'storage' not in index:
        return [path]
    if index['storage'] != STORAGE:
        raise ValueError('unsupported map manifest storage')
    names = [index[k] for k in ('sources', 'tiles', 'reviews')] + index['region_files']
    files = [path] + [_relative(path.parent, name) for name in names]
    if len({str(p).casefold() for p in files}) != len(files):
        raise ValueError('duplicate manifest fragment')
    return files


def _rows(path, key):
    result = {}
    for line in path.read_text(encoding='utf-8').splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        identity = row[key]
        if identity in result:
            raise ValueError(f'duplicate {key} in {path}')
        result[identity] = row
    return result


def _transform(value, string):
    if isinstance(value, str):
        return string(value)
    if isinstance(value, list):
        return [_transform(row, string) for row in value]
    if isinstance(value, dict):
        return {k: _transform(v, string) for k, v in value.items()}
    return value


def load_manifest(path):
    """Expand storage only. Never create, refresh or weaken an approval."""
    path = Path(path).resolve()
    files = manifest_files(path)
    index = _read(path)
    if len(files) == 1:
        return index
    sources, tiles, reviews = (_rows(files[i], key)
                               for i, key in ((1, 'id'), (2, 'path'), (3, 'id')))

    def expand(value):
        if value.startswith('@source/'):
            identity = value[len('@source/'):]
            if identity not in sources:
                raise ValueError(f'unknown source reference: {identity}')
            return sources[identity]['url']
        if value.startswith('@note/'):
            return index['notes'][value[len('@note/'):]]
        return value

    manifest = {k: v for k, v in index.items() if k not in
                ('storage', 'sources', 'tiles', 'reviews', 'region_files', 'notes')}
    regions = []
    for file in files[4:]:
        region = _read(file)
        for field in ('original_tiles', 'context_tiles'):
            references = region.get(field, [])
            rows = []
            for reference in references:
                name = reference if isinstance(reference, str) else reference['path']
                tile = dict(tiles[name])
                if isinstance(reference, dict):
                    if set(reference) & (set(TILE_FIELDS) - {'path'}):
                        raise ValueError('regional tile reference cannot override common source fields')
                    tile.update(reference)
                rows.append(tile)
            if field in region:
                region[field] = rows
        review = dict(reviews[region['id']])
        del review['id']
        region['review'] = review
        regions.append(_transform(region, expand))
    if len(regions) != len(reviews) or len({r['id'] for r in regions}) != len(regions):
        raise ValueError('region and review identities must match exactly')
    manifest['regions'] = regions
    return manifest


def _write_object(path, value):
    """Keep arrays of small records readable without expanding coordinates."""
    fields = []
    for key, item in value.items():
        prefix = '  ' + json.dumps(key) + ': '
        if isinstance(item, list) and item:
            rows = [json.dumps(row, ensure_ascii=False, allow_nan=False) for row in item]
            fields.append(prefix + '[\n    ' + ',\n    '.join(rows) + '\n  ]')
        else:
            fields.append(prefix + json.dumps(item, ensure_ascii=False, allow_nan=False))
    path.write_text('{\n' + ',\n'.join(fields) + '\n}\n', encoding='utf-8', newline='\n')


def write_manifest(manifest, path):
    """Split into a NEW source directory, preserving logical values verbatim."""
    path = Path(path).resolve()
    directory = path.parent / 'map_tiles'
    if path.exists() or directory.exists():
        raise ValueError('split manifest destination must not exist')
    regions = copy.deepcopy(manifest['regions'])
    ids = [r['id'] for r in regions]
    if (len({identity.casefold() for identity in ids}) != len(ids)
            or any(not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,99}', i)
                   or i.split('.')[0].casefold() in {'con', 'prn', 'aux', 'nul'}
                   or re.fullmatch(r'(?:com|lpt)[1-9]', i.split('.')[0], re.I) for i in ids)):
        raise ValueError('unsafe or duplicate region filename')
    sources, tiles, reviews = {}, {}, []
    note_refs = {v: '@note/' + k for k, v in REVIEW_NOTES.items()}

    def intern(value):
        if value.startswith(('http://', 'https://')):
            identity = hashlib.sha256(value.encode('utf-8')).hexdigest()[:16]
            row = dict(id=identity, url=value)
            if identity in sources and sources[identity] != row:
                raise ValueError('conflicting source identity')
            sources[identity] = row
            return '@source/' + identity
        return note_refs.get(value, value)

    for region in regions:
        reviews.append(dict(id=region['id'], **region.pop('review')))
        for field in ('original_tiles', 'context_tiles'):
            references = []
            for tile in region.get(field, []):
                common = {k: tile[k] for k in TILE_FIELDS}
                name = tile['path']
                if name in tiles and tiles[name] != common:
                    raise ValueError(f'conflicting source tile: {name}')
                tiles[name] = common
                extra = {k: v for k, v in tile.items() if k not in TILE_FIELDS}
                references.append(dict(path=name, **extra) if extra else name)
            if field in region:
                region[field] = references
    index = {k: v for k, v in manifest.items() if k != 'regions'}
    index.update(storage=STORAGE, sources='map_tiles/sources.jsonl', tiles='map_tiles/tiles.jsonl',
                 reviews='map_tiles/reviews.jsonl', notes=REVIEW_NOTES,
                 region_files=[f'map_tiles/regions/{r["id"]}.json' for r in regions])
    (directory / 'regions').mkdir(parents=True)
    for region in regions:
        _write_object(directory / 'regions' / (region['id'] + '.json'), _transform(region, intern))
    rows_by_file = [('tiles.jsonl', [_transform(tiles[k], intern) for k in sorted(tiles)]),
                    ('reviews.jsonl', [_transform(row, intern) for row in reviews])]
    rows_by_file.append(('sources.jsonl', [sources[k] for k in sorted(sources)]))
    for name, rows in rows_by_file:
        raw = ''.join(json.dumps(row, ensure_ascii=False, separators=(',', ':'), allow_nan=False) + '\n'
                      for row in rows)
        (directory / name).write_text(raw, encoding='utf-8', newline='\n')
    _write_object(path, index)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('expand', 'split'))
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    manifest = load_manifest(args.manifest)
    if args.action == 'split':
        write_manifest(manifest, args.output)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        with args.output.open('x', encoding='utf-8', newline='\n') as stream:
            json.dump(manifest, stream, ensure_ascii=False, indent=2, allow_nan=False)
            stream.write('\n')


if __name__ == '__main__':
    main()
