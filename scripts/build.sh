#!/usr/bin/env bash
# Build once for a new release, or compile an isolated non-release candidate.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="$(tr -d '[:space:]' < "$ROOT/VERSION")"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
PY="${PY:-C:/Users/admin/Desktop/ga-admin-portable-windows-amd64-v0.2.29/python/python.exe}"
OUT="$ROOT/build"
ENGINE_ONLY=0
CANDIDATE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --engine-only) ENGINE_ONLY=1; shift ;;
    --candidate)
      [[ $# -ge 2 && "$2" != --* ]] || { echo "ERROR: --candidate requires an isolated build/subdirectory" >&2; exit 2; }
      OUT="$2"; CANDIDATE=1; shift 2 ;;
    *) echo "ERROR: unknown build argument: $1" >&2; exit 2 ;;
  esac
done
# Validate BEFORE mkdir/compilation/dictionary writes. Frozen release bytes must survive.
if [[ "$CANDIDATE" -eq 1 ]]; then
  OUT="$("$PY" - "$ROOT" "$OUT" <<'PYEOF'
import pathlib, sys
root, out = pathlib.Path(sys.argv[1]).resolve(), pathlib.Path(sys.argv[2]).resolve()
build = root / "build"
if out == build or not out.is_relative_to(build):
    raise SystemExit("ERROR: candidate output must be a distinct subdirectory under build/")
print(out.as_posix())
PYEOF
)"
  SETUP_EXE="SetupCandidate.exe"
  DICTIONARY="$OUT/CMOLocalizer.json"
else
  "$PY" "$ROOT/scripts/release_check.py" --check-frozen-version "$VER" --operation build
  SETUP_EXE="CMO_Chinese_Setup_v${VER}.exe"
  DICTIONARY="$ROOT/dict/CMOLocalizer.json"
fi
mkdir -p "$OUT"

# csc.exe is a native Windows binary: hand it backslash paths.
W() { cygpath -w "$1"; }

echo "==> building CMOLocalizer.dll (output=$OUT)"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(W "$ROOT/scripts/build.ps1")" -OutputDir "$(W "$OUT")"
if [[ "$ENGINE_ONLY" -eq 1 ]]; then exit 0; fi
# Candidates never change the root dictionary or a versioned release filename.
if [[ "$CANDIDATE" -eq 1 ]]; then
  "$PY" - "$ROOT" "$DICTIONARY" <<'PYEOF'
import pathlib, sys
sys.path.insert(0, str(pathlib.Path(sys.argv[1]) / "scripts"))
import build_dict
build_dict.write(build_dict.build(), pathlib.Path(sys.argv[2]))
PYEOF
else
  "$PY" "$ROOT/scripts/build_dict.py"
fi

# Build the scenario bundle before the installer, not during post-live packaging.
GAME_SCEN="${GAME_DIR:-G:/soft/steam/steamapps/common/Command - Modern Operations}/Scenarios"
if [[ -d "$GAME_SCEN" ]]; then
  "$PY" - "$GAME_SCEN" "$OUT/Scenarios_HTML.zip" <<'PYEOF'
import pathlib, sys, zipfile
src, dest = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
files = sorted(list(src.rglob("*.html")) + list(src.rglob("*.html.orig")))
if not files:
    raise SystemExit("ERROR: no scenario HTML inputs")
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in files:
        z.write(f, f.relative_to(src.parent).as_posix())
print(f"scenario bundle: {len(files)} files")
PYEOF
elif [[ ! -f "$OUT/Scenarios_HTML.zip" ]]; then
  echo "ERROR: missing Scenarios_HTML.zip; candidate fixtures must provide their own bundle" >&2
  exit 1
fi

echo "==> building CMO_Updater.exe"
"$CSC" -nologo -langversion:5 -target:winexe -platform:anycpu -optimize+ \
  -out:"$(W "$OUT/CMO_Updater.exe")" \
  -reference:System.dll -reference:System.Core.dll \
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  "$(W "$ROOT/src/shared/CMOShared.cs")" \
  "$(W "$ROOT/src/updater/CMO_Updater.cs")"

echo "==> building CMOSharedTest.exe (test host, not packaged)"
"$CSC" -nologo -langversion:5 -target:exe -platform:anycpu -optimize+ \
  -out:"$(W "$OUT/CMOSharedTest.exe")" \
  -reference:System.dll -reference:System.Core.dll \
  "$(W "$ROOT/src/shared/CMOShared.cs")" \
  "$(W "$ROOT/src/shared/CMOSharedTest.cs")"

# Symbols/Menu 的中文按钮图（引擎启动时预加载，必须随包分发）
SYM_ZIP="$OUT/Symbols_Menu.zip"
if [ -d "$ROOT/assets/symbols_menu" ]; then
  "$PY" - "$ROOT/assets/symbols_menu" "$SYM_ZIP" <<'PYEOF'
import pathlib, sys, zipfile
src, dest = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in sorted(src.rglob("*")):
        if f.is_file():
            z.write(f, f.relative_to(src))
print(f"symbols menu zip: {dest.stat().st_size} bytes")
PYEOF
fi

# The independent map-label feature supplies this shared source when present.
INSTALLER_SHARED_SOURCES=()
if [ -f "$ROOT/src/shared/MapLabelRenderer.cs" ]; then
  INSTALLER_SHARED_SOURCES+=("$(W "$ROOT/src/shared/MapLabelRenderer.cs")")
fi

echo "==> building $SETUP_EXE"
RES=()
RES+=(-resource:"$(W "$OUT/CMOLocalizer.dll")",CMOLocalizer.dll)
RES+=(-resource:"$(W "$DICTIONARY")",CMOLocalizer.json)
RES+=(-resource:"$(W "$OUT/CMO_Updater.exe")",UPDATER_EXE)
RES+=(-resource:"$(W "$ROOT/assets/placenames/Synoptic.wwp")",PLACE_SYNOPTIC)
RES+=(-resource:"$(W "$ROOT/assets/placenames/Cities.wwp")",PLACE_CITIES)
RES+=(-resource:"$(W "$ROOT/assets/launcher/stringsChinese.resx")",LAUNCHER_ZH)
if [ -f "$SYM_ZIP" ]; then
  RES+=(-resource:"$(W "$SYM_ZIP")",SYMBOLS_MENU_ZIP)
fi
RES+=(-resource:"$(W "$ROOT/assets/donate/wechat_pay.png")",QR_WeChat)
RES+=(-resource:"$(W "$ROOT/assets/donate/alipay.jpg")",QR_Alipay)
for t in "$ROOT"/assets/db_templates/*.html; do
  RES+=(-resource:"$(W "$t")","TPL_$(basename "$t")")
done
# The verified coverage manifest records which text is translated or still English.
RES+=(-resource:"$(W "$OUT/Scenarios_HTML.zip")",SCEN_HTML_ZIP)
"$CSC" -nologo -langversion:5 -target:winexe -platform:anycpu -optimize+ \
  -out:"$(W "$OUT/$SETUP_EXE")" \
  -reference:System.dll -reference:System.Core.dll \
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll \
  "${RES[@]}" \
  "$(W "$ROOT/src/shared/CMOShared.cs")" \
  "${INSTALLER_SHARED_SOURCES[@]}" \
  "$(W "$ROOT/src/installer/CMO_Chinese_Setup.cs")"

# Only a new release build maintains the unversioned compatibility copy.
if [[ "$CANDIDATE" -eq 0 ]]; then
  cp "$OUT/$SETUP_EXE" "$OUT/CMO_Chinese_Setup.exe"
fi

ls -la "$OUT"
echo "==> build ok (candidate=$CANDIDATE; no packaging or game writes)"
