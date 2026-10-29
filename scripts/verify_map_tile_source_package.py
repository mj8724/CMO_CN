#!/usr/bin/env python3
"""Verify byte-copy map source reproduction inputs before release packaging."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
from map_tile_manifest import load_manifest, manifest_files


def package_path(value):
    path = PurePosixPath(value)
    if (not isinstance(value, str) or '\\' in value or path.is_absolute()
            or '..' in path.parts or path.as_posix() != value or ':' in value):
        raise ValueError('source package requires a canonical relative POSIX path')
    return path


def required_inputs(manifest):
    """Enumerate inputs, not approval authority, from the finite source recipe."""
    source_tree = Path(__file__).resolve().parents[1]
    expected = {}
    for relative in ('scripts/build_map_tile_pack.py', 'scripts/map_tile_manifest.py', 'scripts/global_map_catalog.py', 'scripts/wwp.py',
                     'assets/placenames/Cities.wwp', 'assets/placenames/Synoptic.wwp'):
        expected[relative] = hashlib.sha256((source_tree / relative).read_bytes()).hexdigest()
    expected['fonts/regular.ttc'] = manifest['font_sha256']
    expected['fonts/bold.ttc'] = manifest['bold_font_sha256']
    for region in manifest['regions']:
        for field in ('original_tiles', 'context_tiles'):
            for tile in region.get(field, []):
                relative = package_path(tile['path']).as_posix()
                if relative != f"{region['z']}/{tile['x']}/{tile['y']}.png":
                    raise ValueError('source package tile path differs from its XYZ')
                name = 'originals/' + relative
                if name in expected and expected[name] != tile['sha256']:
                    raise ValueError('source package has conflicting source SHA declarations')
                expected[name] = tile['sha256']
    return expected


def verify(manifest_path, asset_path, directory, *, owner_native_source_approvals=None,
           owner_native_source_approvals_sha256=None):
    manifest_path, asset_path = Path(manifest_path), Path(asset_path)
    manifest_raw = manifest_path.read_bytes()
    manifest = load_manifest(manifest_path)
    needed = any('owner_native_source_refs' in s for r in manifest['regions'] for s in r.get('shared_sources', []))
    if bool(owner_native_source_approvals) != bool(owner_native_source_approvals_sha256):
        raise ValueError('original external approval file and SHA must be supplied together')
    external = owner_native_source_approvals is not None
    if directory is None:
        if needed or external:
            raise ValueError('strict native-source manifest requires its complete reproducible source package')
        return dict(source_package_required=False)
    directory = Path(directory).resolve()
    recipe = json.loads((directory / 'rebuild_inputs.json').read_bytes())
    if recipe.get('schema') != 'finite-map-source-package-v1':
        raise ValueError('invalid finite map source package')
    expected = required_inputs(manifest)
    expected['inputs/manifest.json'] = hashlib.sha256(manifest_raw).hexdigest()
    for path in manifest_files(manifest_path)[1:]:
        relative = path.relative_to(manifest_path.resolve().parent).as_posix()
        expected['inputs/' + relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    argv = ['scripts/build_map_tile_pack.py', '--manifest', 'inputs/manifest.json',
            '--original-root', 'originals', '--font', 'fonts/regular.ttc', '--bold-font', 'fonts/bold.ttc',
            '--output', 'out/ChineseMapTiles.dat', '--report', 'out/map_tile_build_report.json']
    if needed and not external:
        raise ValueError('strict source package requires the independently supplied original approval file and SHA')
    if external:
        original_path = Path(owner_native_source_approvals).resolve()
        raw = original_path.read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        if digest != owner_native_source_approvals_sha256:
            raise ValueError('original external approval SHA changed')
        original = dict(path=str(original_path), sha256=digest)
        if recipe.get('original_native_source_approvals') != original:
            raise ValueError('source package approval provenance differs from the original build input')
        document = json.loads(raw)
        if (not isinstance(document, dict) or set(document) != {'schema', 'entries'}
                or document['schema'] != 'root-approved-owner-native-source-v2'
                or not isinstance(document['entries'], list)):
            raise ValueError('strict external approval document required')
        expected['inputs/owner_native_source_approvals.json'] = digest
        argv += ['--owner-native-source-approvals', 'inputs/owner_native_source_approvals.json',
                 '--owner-native-source-approvals-sha256', digest]
    elif recipe.get('original_native_source_approvals') is not None:
        raise ValueError('package approval provenance has no independently supplied original input')
    if recipe['argv'] != argv:
        raise ValueError('source package reproduction CLI differs from its complete fixed input set')
    seen = set()
    for row in recipe['files']:
        relative = package_path(row['path']).as_posix()
        path = (directory / relative).resolve()
        if not path.is_relative_to(directory) or relative in seen:
            raise ValueError('source package file path escapes or duplicates')
        if relative not in expected or row['sha256'] != expected[relative]:
            raise ValueError('source package input SHA differs from a required build input')
        seen.add(relative)
        raw = path.read_bytes()
        if len(raw) != row['bytes'] or hashlib.sha256(raw).hexdigest() != row['sha256']:
            raise ValueError('source package input bytes changed')
    if seen != set(expected):
        raise ValueError('source package pins omit required build inputs')
    if hashlib.sha256(asset_path.read_bytes()).hexdigest() != recipe['expected_pack_sha256']:
        raise ValueError('source package DAT differs from release DAT')
    return dict(source_package_required=needed, files=len(seen), expected_pack_sha256=recipe['expected_pack_sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--asset', type=Path, required=True)
    parser.add_argument('--source-package', type=Path)
    parser.add_argument('--owner-native-source-approvals', type=Path)
    parser.add_argument('--owner-native-source-approvals-sha256')
    args = parser.parse_args()
    print(json.dumps(verify(args.manifest, args.asset, args.source_package,
        owner_native_source_approvals=args.owner_native_source_approvals,
        owner_native_source_approvals_sha256=args.owner_native_source_approvals_sha256), indent=2))


if __name__ == '__main__':
    main()
