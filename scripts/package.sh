#!/usr/bin/env bash
# Package the exact already-built/tested installer; never recompile after live acceptance.
# The portable ZIP needs an explicit first apply through its same-version installer.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="$(tr -d '[:space:]' < "$ROOT/VERSION")"
OUT="$ROOT/build"
PY="${PY:-C:/Users/admin/Desktop/ga-admin-portable-windows-amd64-v0.2.29/python/python.exe}"
[[ $# -eq 0 ]] || { echo "ERROR: package.sh accepts no arguments or bypass flags" >&2; exit 2; }

# This must precede every output write, including bundle assembly and digest creation.
"$PY" "$ROOT/scripts/release_check.py" --check-frozen-version "$VER" --operation package
echo "==> version $VER (reuse tested installer; no compile)"
SETUP="$OUT/CMO_Chinese_Setup_v${VER}.exe"
ZIP="$OUT/CMO_Chinese_Pack_${VER}.zip"
for f in "$SETUP" "$OUT/CMOLocalizer.dll" "$OUT/CMO_Updater.exe" "$OUT/Scenarios_HTML.zip"; do
  [[ -s "$f" ]] || { echo "ERROR: missing build input: $f" >&2; exit 1; }
done

# No credentials are read, and gate failure cannot be bypassed.
"$PY" "$ROOT/scripts/verify_no_secrets.py" --history \
  || { echo "==> 密钥门禁未通过，中止打包"; exit 1; }
"$PY" "$ROOT/scripts/verify_placenames.py" --set Cities
"$PY" "$ROOT/scripts/verify_placenames.py" --set Synoptic
# The final live report binds the exact source/dll/json/setup that will be packaged.
# This check does NOT launch installer/live or rewrite README/report.
"$PY" "$ROOT/scripts/release_check.py" --pre
echo "==> 回归门禁（static；--no-report 保留各层证据）"
"$PY" "$ROOT/scripts/verify_regressions.py" --static --no-report \
  || { echo "==> 回归用例未通过，中止打包（详见 docs/RELEASE_TEST_SOP.md）"; exit 1; }

# Unique staging directories avoid deleting old backups or previous package attempts.
PACK="$(mktemp -d "$OUT/pack_${VER}.XXXXXX")"
echo "==> assembling green pack in $PACK"
mkdir -p "$PACK/DB/Templates"
cp "$OUT/CMOLocalizer.dll" "$PACK/"
cp "$ROOT/dict/CMOLocalizer.json" "$PACK/"
cp "$OUT/CMO_Updater.exe" "$PACK/"
cp "$SETUP" "$PACK/"

mkdir -p "$PACK/WW/PlacenameSets/Synoptic" "$PACK/WW/PlacenameSets/Cities"
cp "$ROOT/assets/placenames/Synoptic.wwp" "$PACK/WW/PlacenameSets/Synoptic/0000_0000.wwp"
cp "$ROOT/assets/placenames/Cities.wwp" "$PACK/WW/PlacenameSets/Cities/0000_0000.wwp"
mkdir -p "$PACK/Symbols/Menu" "$PACK/Launcher/Localization"
cp "$ROOT/assets/symbols_menu/"* "$PACK/Symbols/Menu/"
cp "$ROOT/assets/launcher/stringsChinese.resx" "$PACK/Launcher/Localization/"
cp "$ROOT/assets/db_templates/"*.html "$PACK/DB/Templates/"

# Use the built scenario bundle, not fresh reads of a mutable installed game.
# Do not expose .orig files as loose ZIP entries: overwriting a player's existing
# backups during unzip would make the real installer's preservation rules moot.
"$PY" - "$ROOT" "$OUT/Scenarios_HTML.zip" "$PACK" <<'PYEOF'
import pathlib, sys, zipfile
root, bundle, pack = map(pathlib.Path, sys.argv[1:])
sys.path.insert(0, str(root / "scripts"))
from release_check import DB_TEMPLATES
count = 0
with zipfile.ZipFile(bundle) as z:
    for name in z.namelist():
        if not name.lower().endswith(".html"):
            continue
        path = pathlib.PurePosixPath(name)
        if "\\" in name or path.is_absolute() or any(p in (".", "..") for p in path.parts) or path.parts[0] != "Scenarios":
            raise SystemExit("ERROR: unsafe scenario archive entry")
        dest = pack.joinpath(*path.parts)
        dest.parent.mkdir(parents=True, exist_ok=True)
        with dest.open("xb") as stream:
            stream.write(z.read(name))
        count += 1
if count < 300:
    raise SystemExit(f"ERROR: scenario bundle contains only {count} HTML files")
print(f"pack: {count} Scenarios HTML files, no loose .orig/config backups")
PYEOF
cp "$ROOT/README.md" "$PACK/汉化说明.md"
# Config is intentionally absent: the same embedded installer merges the target's
# own runtime/bindingRedirects and preserves its first backup. --adopt alone only
# records installed state, not first activation. See the generated README section.

# Check the full archive contract BEFORE creating any final versioned ZIP/digest.
"$PY" - "$ROOT" "$PACK" "$SETUP" "$VER" <<'PYEOF'
import io, pathlib, sys, zipfile
root, pack, setup = map(pathlib.Path, sys.argv[1:4])
version = sys.argv[4]
sys.path.insert(0, str(root / "scripts"))
import release_check
with io.BytesIO() as buffer:
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for f in sorted(pack.rglob("*")):
            if f.is_file():
                z.write(f, f.relative_to(pack).as_posix())
    inputs = release_check.release_inputs(version)
    problems = release_check.check_regression_report(version, expected_inputs=inputs)
    problems += release_check.pack_problems(buffer.getvalue(), version, setup_bytes=setup.read_bytes(), expected_inputs=inputs)
    embedded = release_check.embedded_engine_hashes(setup)
    for name, key in (("CMOLocalizer.dll", "engine_sha256"), ("CMOLocalizer.json", "dictionary_sha256")):
        if embedded[name] != inputs[key]:
            problems.append("安装器内嵌 " + name + " 与本轮输入不符")
    if problems:
        raise SystemExit("归档失败：" + "；".join(problems))
print("汉化资产覆盖/首次安装器配置入口/分层报告/真实内嵌载荷检查通过")
PYEOF

"$PY" - "$PACK" "$ZIP" "$SETUP" <<'PYEOF'
import hashlib, pathlib, sys, zipfile
pack, dest, setup = map(pathlib.Path, sys.argv[1:])
# Exclusive create prevents both deliberate overwrite and a concurrent packaging race.
with zipfile.ZipFile(dest, "x", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in sorted(pack.rglob("*")):
        if f.is_file():
            z.write(f, f.relative_to(pack).as_posix())
for asset in (setup, dest):
    h = hashlib.sha256(asset.read_bytes()).hexdigest()
    with asset.with_name(asset.name + ".sha256").open("x", encoding="ascii", newline="\n") as stream:
        stream.write(h + "  " + asset.name + "\n")
    print(f"sha256 {asset.name}: {h}")
print(f"wrote {dest} ({dest.stat().st_size} bytes)")
PYEOF

"$PY" "$ROOT/scripts/release_check.py"
echo "==> package ok (four assets, same tested embedded installer; no remote writes)"
