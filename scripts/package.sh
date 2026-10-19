#!/usr/bin/env bash
# Build the release artifacts for the current VERSION.
#
#   build/CMOLocalizer.dll          runtime engine
#   build/CMO_Chinese_Setup_v<ver>.exe  one-click installer (embeds dll + dict + templates)
#   build/CMO_Chinese_Pack_<ver>.zip   green/portable pack (unzip over the game root)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VER="$(tr -d '[:space:]' < "$ROOT/VERSION")"
OUT="$ROOT/build"
PACK="$OUT/pack"
PY="${PY:-C:/Users/admin/Desktop/ga-admin-portable-windows-amd64-v0.2.29/python/python.exe}"

echo "==> version $VER"

# Bundle the localized Scenarios HTML (plus the pristine .orig backups) so a
# fresh install reproduces the on-disk translation without runtime injection.
GAME_SCEN="G:/soft/steam/steamapps/common/Command - Modern Operations/Scenarios"
if [ -d "$GAME_SCEN" ]; then
  "$PY" - "$GAME_SCEN" "$OUT/Scenarios_HTML.zip" <<'PYEOF'
import pathlib, sys, zipfile
src, dest = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
game = src.parent
files = sorted(list(src.rglob("*.html")) + list(src.rglob("*.html.orig")))
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in files:
        z.write(f, f.relative_to(game).as_posix())
print(f"wrote {dest} ({len(files)} files, {dest.stat().st_size} bytes)")
PYEOF
else
  echo "==> warning: game Scenarios directory not found; skipping HTML bundle"
fi

# ── 明文凭据硬门禁（工作区 + 全历史）：不通过则中止打包 ────────────────
"$PY" "$ROOT/scripts/verify_no_secrets.py" --history \n  || { echo "==> 密钥门禁未通过，中止打包"; exit 1; }

bash "$ROOT/scripts/build.sh"

# ── 发版回归硬门禁（台账 docs/BUG_REGISTRY.md 的 static 子集）：不通过则中止打包 ──
# --no-report：不写 build/regression_report.json，避免把发版用的 live 报告覆盖掉
echo "==> 回归门禁（static）"
"$PY" "$ROOT/scripts/verify_regressions.py" --static --no-report \
  || { echo "==> 回归用例未通过，中止打包（详见 docs/RELEASE_TEST_SOP.md）"; exit 1; }

echo "==> assembling green pack"
rm -rf "$PACK"
mkdir -p "$PACK/DB/Templates"
cp "$OUT/CMOLocalizer.dll" "$PACK/"
cp "$ROOT/dict/CMOLocalizer.json" "$PACK/"
cp "$OUT/CMO_Updater.exe" "$PACK/"

# 地图地名集：地图矢量地名层与 Stamen 标签瓦片重绘的数据源
mkdir -p "$PACK/WW/PlacenameSets/Synoptic" "$PACK/WW/PlacenameSets/Cities"
cp "$ROOT/assets/placenames/Synoptic.wwp" "$PACK/WW/PlacenameSets/Synoptic/0000_0000.wwp"
cp "$ROOT/assets/placenames/Cities.wwp" "$PACK/WW/PlacenameSets/Cities/0000_0000.wwp"

# Symbols/Menu 中文按钮图 + 启动器中文资源（与安装器同源，保证绿色包同样完整）
mkdir -p "$PACK/Symbols/Menu" "$PACK/Launcher/Localization"
cp -f "$ROOT/assets/symbols_menu/"* "$PACK/Symbols/Menu/" 2>/dev/null || true
cp -f "$ROOT/assets/launcher/stringsChinese.resx" "$PACK/Launcher/Localization/"
cp "$ROOT/assets/db_templates/"*.html "$PACK/DB/Templates/"

# Green pack parity: ship the localized Scenarios HTML so unpacking over the game
# root gives the same result as the installer.
if [ -d "$GAME_SCEN" ]; then
  mkdir -p "$PACK/Scenarios"
  "$PY" - "$GAME_SCEN" "$PACK" <<'PYEOF'
import pathlib, shutil, sys
src, pack = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
game = src.parent
n = 0
for f in sorted(list(src.rglob("*.html")) + list(src.rglob("*.html.orig"))):
    dst = pack / f.relative_to(game)
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(f, dst)
    n += 1
print(f"pack: {n} Scenarios HTML files")
PYEOF
fi
cp "$ROOT/README.md" "$PACK/汉化说明.md"

# Command.exe.config patch that wires the AppDomainManager, without clobbering
# any binding redirects already present in the user's config.
"$PY" - "$PACK" <<'PYEOF'
import pathlib, re, sys
pack = pathlib.Path(sys.argv[1])
src = pathlib.Path("G:/soft/steam/steamapps/common/Command - Modern Operations/Command.exe.config")
text = src.read_text(encoding="utf-8-sig")
snippet = """    <appDomainManagerType value="CMOLocalizer.LocalizerDomainManager" />
    <appDomainManagerAssembly value="CMOLocalizer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" />
"""
inject = text.replace("</runtime>", snippet + "  </runtime>")
(pack / "Command.exe.config.patch").write_text(inject, encoding="utf-8")
print("wrote Command.exe.config.patch")
PYEOF

ZIP="$OUT/CMO_Chinese_Pack_${VER}.zip"
rm -f "$ZIP"
"$PY" - "$PACK" "$ZIP" <<'PYEOF'
import pathlib, sys, zipfile
pack, dest = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
with zipfile.ZipFile(dest, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for f in sorted(pack.rglob("*")):
        if f.is_file():
            z.write(f, f.relative_to(pack))
print(f"wrote {dest} ({dest.stat().st_size} bytes)")
PYEOF
echo "==> $ZIP"

# 发布资产摘要（自动升级用它做完整性校验；无摘要时更新助手会回退到长度/PE 头检查）
for f in "$OUT/CMO_Chinese_Setup_v${VER}.exe" "$ZIP"; do
  if [ -f "$f" ]; then
    "$PY" - "$f" <<'PYEOF'
import hashlib, pathlib, sys
p = pathlib.Path(sys.argv[1])
h = hashlib.sha256(p.read_bytes()).hexdigest()
out = p.with_name(p.name + ".sha256")
out.write_text(h + "  " + p.name + "\n", encoding="ascii")
print(f"sha256 {p.name}: {h}")
PYEOF
  fi
done

# ── 汉化资产覆盖率断言：任何一组汉化漏进包都直接失败 ──────────────────────
"$PY" - "$PACK" <<'PYEOF'
import pathlib, sys
pack = pathlib.Path(sys.argv[1])
required = [
    "CMOLocalizer.dll", "CMOLocalizer.json", "CMO_Updater.exe",
    "Command.exe.config.patch", "汉化说明.md",
    "DB/Templates/Ship.html",
    "WW/PlacenameSets/Synoptic/0000_0000.wwp",
    "WW/PlacenameSets/Cities/0000_0000.wwp",
    "Launcher/Localization/stringsChinese.resx",
]
missing = [r for r in required if not (pack / r).is_file()]
sym = list((pack / "Symbols" / "Menu").glob("*.png")) if (pack / "Symbols" / "Menu").is_dir() else []
scen = list((pack / "Scenarios").rglob("*.html")) if (pack / "Scenarios").is_dir() else []
if len(sym) < 20:
    missing.append(f"Symbols/Menu/*.png (仅 {len(sym)} 个)")
if len(scen) < 300:
    missing.append(f"Scenarios/**/*.html (仅 {len(scen)} 个)")
if missing:
    print("归档失败：以下汉化资产缺失 -> " + ", ".join(missing))
    sys.exit(1)
print(f"汉化资产覆盖检查通过：Symbols/Menu {len(sym)} 张，Scenarios HTML {len(scen)} 个")
PYEOF

ls -la "$OUT"/*.dll "$OUT"/*.exe "$OUT"/*.zip "$OUT"/*.sha256
echo "==> package ok"
