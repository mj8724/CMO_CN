#!/usr/bin/env bash
# Deploy the built engine and dictionary into the game directory.
#
#   scripts/deploy.sh            deploy build/CMOLocalizer.dll + dict/CMOLocalizer.json
#   scripts/deploy.sh --engine <dll> --dictionary <json> --backup-dir <dir>
#   scripts/deploy.sh --verify   deploy, then launch and report the window set
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME="G:/soft/steam/steamapps/common/Command - Modern Operations"
PY="${PY:-C:/Users/admin/Desktop/ga-admin-portable-windows-amd64-v0.2.29/python/python.exe}"
ENGINE="$ROOT/build/CMOLocalizer.dll"
DICTIONARY="$ROOT/dict/CMOLocalizer.json"
BACKUP="$ROOT/build/deploy-backups/$(date -u +%Y%m%dT%H%M%SZ)"
VERIFY=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --engine|--dictionary|--game-dir|--backup-dir)
      [ "$#" -ge 2 ] || { echo "missing value for $1"; exit 2; }
      case "$1" in
        --engine) ENGINE="$2" ;;
        --dictionary) DICTIONARY="$2" ;;
        --game-dir) GAME="$2" ;;
        --backup-dir) BACKUP="$2" ;;
      esac
      shift 2 ;;
    --verify) VERIFY=1; shift ;;
    *) echo "unknown argument: $1"; exit 2 ;;
  esac
done

[ -f "$ENGINE" ] && [ -f "$DICTIONARY" ] && [ -f "$GAME/Command.exe.config" ] || {
  echo "missing engine, dictionary, or game configuration"; exit 1;
}

# Backup once before changing any installed bytes. A stopped game is required:
# renaming a loaded DLL does not replace the assembly already in that process.
"$PY" - "$ROOT" "$GAME" "$ENGINE" "$DICTIONARY" "$BACKUP" <<'PYEOF'
import hashlib, json, pathlib, shutil, subprocess, sys
root, game, engine, dictionary, backup = map(pathlib.Path, sys.argv[1:])
check = subprocess.run(["powershell.exe", "-NoProfile", "-Command",
                        "(Get-Process Command -ErrorAction SilentlyContinue).Id"],
                       capture_output=True, text=True, check=True)
if check.stdout.strip():
    raise SystemExit("close Command.exe before deploying a new candidate")
if backup.exists():
    raise SystemExit("backup destination already exists; never overwrite an earlier backup")
backup.mkdir(parents=True)
relatives = ["Command.exe.config", "CMOLocalizer.dll", "CMOLocalizer.json",
             "WW/PlacenameSets/Synoptic/0000_0000.wwp", "WW/PlacenameSets/Cities/0000_0000.wwp"]
relatives += ["DB/Templates/" + p.name for p in sorted((root / "assets/db_templates").glob("*.html"))]
records = []
for relative in relatives:
    source = game / relative
    record = {"path": relative, "existed": source.is_file()}
    if source.is_file():
        target = backup / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        record["sha256"] = hashlib.sha256(source.read_bytes()).hexdigest()
        if hashlib.sha256(target.read_bytes()).hexdigest() != record["sha256"]:
            raise SystemExit("backup hash mismatch: " + relative)
    records.append(record)
(backup / "manifest.json").write_text(json.dumps(records, ensure_ascii=False, indent=1), encoding="utf-8")
print("deployment backup: " + str(backup))
PYEOF

# 先写临时文件再原子替换：直接 cp 覆盖已加载/被杀软扫描中的 DLL 会报
# “Device or resource busy”（2026-10-18 实测），而 rename 替换可以成功。
deploy_file() {
  cp "$1" "$2.new"
  mv -f "$2.new" "$2"
}

deploy_file "$ENGINE" "$GAME/CMOLocalizer.dll"
deploy_file "$DICTIONARY" "$GAME/CMOLocalizer.json"
cp "$ROOT/assets/db_templates/"*.html "$GAME/DB/Templates/"
cp "$ROOT/assets/placenames/Synoptic.wwp" "$GAME/WW/PlacenameSets/Synoptic/0000_0000.wwp"
cp "$ROOT/assets/placenames/Cities.wwp" "$GAME/WW/PlacenameSets/Cities/0000_0000.wwp"

"$PY" - "$ROOT" "$GAME" "$ENGINE" "$DICTIONARY" <<'PYEOF'
import json, os, pathlib, sys
from xml.dom import minidom

root, game, engine, dictionary = map(pathlib.Path, sys.argv[1:])
config = game / "Command.exe.config"
doc = minidom.parseString(config.read_bytes())
configuration = doc.documentElement
runtime = next((n for n in configuration.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName == "runtime"), None)
if runtime is None:
    runtime = doc.createElement("runtime")
    configuration.appendChild(runtime)
for name, value in (("appDomainManagerType", "CMOLocalizer.LocalizerDomainManager"),
                    ("appDomainManagerAssembly", "CMOLocalizer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")):
    nodes = [n for n in runtime.childNodes if n.nodeType == n.ELEMENT_NODE and n.tagName == name]
    if nodes:
        node = nodes[0]
        for duplicate in nodes[1:]:
            runtime.removeChild(duplicate)
    else:
        node = doc.createElement(name)
        runtime.appendChild(node)
    node.setAttribute("value", value)
new = config.with_name(config.name + ".new")
new.write_bytes(doc.toxml(encoding="utf-8"))
os.replace(new, config)
d = json.loads((game / "CMOLocalizer.json").read_text(encoding="utf-8"))
print(f"deployed: offsets={len(d['offsets'])} exact={len(d['exact'])} "
      f"version={d['_meta']['version']}")

# 部署必须逐字节到位：历史上出现过被旧包覆盖成不含修复的引擎
# （R0 门禁会红，但那时已经白跑一整套回归）。
stale = []
for src, dst in ((engine, game / "CMOLocalizer.dll"),
                 (dictionary, game / "CMOLocalizer.json")):
    if src.read_bytes() != dst.read_bytes():
        stale.append(dst.name)
if stale:
    print("deploy FAIL: 与仓库产物不一致: " + ", ".join(stale))
    raise SystemExit(1)
print("deployed: dll/json 与仓库产物逐字节一致")
PYEOF

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$ROOT/scripts/refresh_placename_tiles.ps1")" -GameDir "$(cygpath -w "$GAME")"

if [ "$VERIFY" -eq 1 ]; then
  "${BASH_SOURCE[0]%/*}/verify_launch.sh"
fi

echo "==> deploy ok"
