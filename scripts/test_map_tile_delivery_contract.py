"""Run shell-embedded Python gates with temporary inputs; never deploy/package."""
import json
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def blocks(name):
    return re.findall(r"<<'PYEOF'\n(.*?)\nPYEOF", (ROOT / "scripts" / name).read_text(encoding="utf-8"), re.S)


def run_block(code, *args):
    return subprocess.run([sys.executable, "-X", "utf8", "-c", code, *map(str, args)], capture_output=True, text=True, encoding="utf-8")


class DeliveryContract(unittest.TestCase):
    def test_installer_call_order_and_failure(self):
        text = (ROOT / "src/installer/CMO_Chinese_Setup.cs").read_text(encoding="utf-8")
        install = text.split("public static bool Install(", 1)[1].split("private static void DeploySymbols", 1)[0]
        self.assertIn("if (!DeployMapTilePack(gamePath, curDir, out writeErr))", install)
        deploy = install.index("if (!DeployMapTilePack(gamePath, curDir, out writeErr))")
        self.assertLess(deploy, install.index("LocalizeMapLabelTiles(gamePath)"))
        self.assertRegex(install[deploy:], r"out writeErr\)\)\s*\{\s*errorMsg = writeErr;\s*return false;")
        uninstall = text.split("public static bool Uninstall(", 1)[1]
        self.assertIn("RestoreMapLabelTiles(gamePath)", uninstall)

    def test_build_embeds_formal_asset_unconditionally(self):
        text = (ROOT / "scripts/build.sh").read_text(encoding="utf-8")
        self.assertIn('RES+=(-resource:"$(W "$ROOT/assets/placenames/ChineseMapTiles.dat")",MAP_TILES)', text)

    def test_deploy_copies_pack_atomically(self):
        text = (ROOT / "scripts/deploy.sh").read_text(encoding="utf-8")
        self.assertIn('deploy_file "$ROOT/assets/placenames/ChineseMapTiles.dat" "$GAME/ChineseMapTiles.dat"', text)

    def test_deploy_byte_verification_detects_stale_pack(self):
        code = blocks("deploy.sh")[0]
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            base = pathlib.Path(temp)
            root, game = base / "repo", base / "game"
            for path in [root / "build", root / "dict", root / "assets/placenames", game]:
                path.mkdir(parents=True, exist_ok=True)
            for src, dst, data in [(root / "build/CMOLocalizer.dll", game / "CMOLocalizer.dll", b"dll"),
                                   (root / "dict/CMOLocalizer.json", game / "CMOLocalizer.json", json.dumps({"offsets": {}, "exact": {}, "_meta": {"version": "fixture"}}).encode()),
                                   (root / "assets/placenames/ChineseMapTiles.dat", game / "ChineseMapTiles.dat", b"verified")]:
                src.write_bytes(data)
                dst.write_bytes(data)
            self.assertEqual(run_block(code, root, game).returncode, 0)
            (game / "ChineseMapTiles.dat").write_bytes(b"stale")
            result = run_block(code, root, game)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("ChineseMapTiles.dat", result.stdout)

    def test_package_copy_and_missing_pack_gate(self):
        text = (ROOT / "scripts/package.sh").read_text(encoding="utf-8")
        self.assertIn('cp "$ROOT/assets/placenames/ChineseMapTiles.dat" "$PACK/"', text)
        code = next(block for block in blocks("package.sh") if "required = [" in block)
        with tempfile.TemporaryDirectory(dir=ROOT / "build") as temp:
            pack = pathlib.Path(temp)
            for name in ["CMOLocalizer.dll", "CMOLocalizer.json", "CMO_Updater.exe", "Command.exe.config.patch", "汉化说明.md", "DB/Templates/Ship.html", "WW/PlacenameSets/Synoptic/0000_0000.wwp", "WW/PlacenameSets/Cities/0000_0000.wwp", "Launcher/Localization/stringsChinese.resx"] + [f"Symbols/Menu/{i}.png" for i in range(20)] + [f"Scenarios/{i}.html" for i in range(300)]:
                path = pack / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"fixture")
            result = run_block(code, pack)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("ChineseMapTiles.dat", result.stdout)
            (pack / "ChineseMapTiles.dat").write_bytes(b"verified")
            self.assertEqual(run_block(code, pack).returncode, 0)


if __name__ == "__main__":
    unittest.main()
