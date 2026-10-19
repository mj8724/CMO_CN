#!/usr/bin/env bash
# Deploy the built engine and dictionary into the game directory.
#
#   scripts/deploy.sh            deploy build/CMOLocalizer.dll + dict/CMOLocalizer.json
#   scripts/deploy.sh --verify   deploy, then launch and report the window set
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME="G:/soft/steam/steamapps/common/Command - Modern Operations"
PY="${PY:-C:/Users/admin/Desktop/ga-admin-portable-windows-amd64-v0.2.29/python/python.exe}"

[ -f "$ROOT/build/CMOLocalizer.dll" ] || { echo "run scripts/build.sh first"; exit 1; }

# 先写临时文件再原子替换：直接 cp 覆盖已加载/被杀软扫描中的 DLL 会报
# “Device or resource busy”（2026-10-18 实测），而 rename 替换可以成功。
deploy_file() {
  cp "$1" "$2.new"
  mv -f "$2.new" "$2"
}

deploy_file "$ROOT/build/CMOLocalizer.dll" "$GAME/CMOLocalizer.dll"
deploy_file "$ROOT/dict/CMOLocalizer.json" "$GAME/CMOLocalizer.json"
cp "$ROOT/assets/db_templates/"*.html "$GAME/DB/Templates/"
cp "$ROOT/assets/placenames/Synoptic.wwp" "$GAME/WW/PlacenameSets/Synoptic/0000_0000.wwp"
cp "$ROOT/assets/placenames/Cities.wwp" "$GAME/WW/PlacenameSets/Cities/0000_0000.wwp"

"$PY" - "$ROOT" "$GAME" <<'PYEOF'
import json, pathlib, sys

root, game = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
d = json.loads((game / "CMOLocalizer.json").read_text(encoding="utf-8"))
print(f"deployed: offsets={len(d['offsets'])} exact={len(d['exact'])} "
      f"version={d['_meta']['version']}")

# 部署必须逐字节到位：历史上出现过被旧包覆盖成不含修复的引擎
# （R0 门禁会红，但那时已经白跑一整套回归）。
stale = []
for src, dst in ((root / "build/CMOLocalizer.dll", game / "CMOLocalizer.dll"),
                 (root / "dict/CMOLocalizer.json", game / "CMOLocalizer.json")):
    if src.read_bytes() != dst.read_bytes():
        stale.append(dst.name)
if stale:
    print("deploy FAIL: 与仓库产物不一致: " + ", ".join(stale))
    raise SystemExit(1)
print("deployed: dll/json 与仓库产物逐字节一致")
PYEOF

if [ "${1:-}" = "--verify" ]; then
  "${BASH_SOURCE[0]%/*}/verify_launch.sh"
fi

echo "==> deploy ok"
