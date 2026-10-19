#!/usr/bin/env bash
# Build the CMO localization engine DLL and the one-click installer.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="$(tr -d '[:space:]' < "$ROOT/VERSION")"
CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
OUT="$ROOT/build"
SETUP_EXE="CMO_Chinese_Setup_v${VER}.exe"
mkdir -p "$OUT"

# csc.exe is a native Windows binary: hand it backslash paths.
W() { cygpath -w "$1"; }
PY="${PY:-python}"

echo "==> building CMOLocalizer.dll"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(W "$ROOT/scripts/build.ps1")"
if [[ "${1:-}" == "--engine-only" ]]; then exit 0; fi

echo "==> building CMO_Updater.exe"
# A full build must explicitly account for its static HTML; engine-only is offline.
"$PY" "$ROOT/scripts/localize_html_files.py" --output-dir "$OUT" --verify

"$CSC" -nologo -target:winexe -platform:anycpu -optimize+ \
  -out:"$(W "$OUT/CMO_Updater.exe")" \
  -reference:System.dll -reference:System.Core.dll \
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  "$(W "$ROOT/src/shared/CMOShared.cs")" \
  "$(W "$ROOT/src/updater/CMO_Updater.cs")"

echo "==> building CMOSharedTest.exe (test host, not packaged)"
"$CSC" -nologo -target:exe -platform:anycpu -optimize+ \
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
RES+=(-resource:"$(W "$ROOT/dict/CMOLocalizer.json")",CMOLocalizer.json)
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
"$CSC" -nologo -target:winexe -platform:anycpu -optimize+ \
  -out:"$(W "$OUT/$SETUP_EXE")" \
  -reference:System.dll -reference:System.Core.dll \
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll \
  -reference:System.IO.Compression.dll -reference:System.IO.Compression.FileSystem.dll \
  "${RES[@]}" \
  "$(W "$ROOT/src/shared/CMOShared.cs")" \
  "${INSTALLER_SHARED_SOURCES[@]}" \
  "$(W "$ROOT/src/installer/CMO_Chinese_Setup.cs")"

# Keep standard CMO_Chinese_Setup.exe as compatibility copy
cp -f "$OUT/$SETUP_EXE" "$OUT/CMO_Chinese_Setup.exe"

ls -la "$OUT"
echo "==> build ok"
