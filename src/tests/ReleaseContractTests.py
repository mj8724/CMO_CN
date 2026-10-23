#!/usr/bin/env python3
"""Focused offline release regressions: unittest/mock HTTP only; real CLR resources, never real install.

Run with the project's Python: python src/tests/ReleaseContractTests.py
Temporary files are created only under ignored build/temp. No credentials are read,
no actual network call is allowed, no game/process enumeration or real installer entry point runs.
"""
from __future__ import annotations

import copy
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import urllib.error
import urllib.parse
import zipfile
from contextlib import ExitStack, redirect_stderr, redirect_stdout
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts"))
import publish_release as publisher  # noqa: E402
import release_check as checker  # noqa: E402
import sync_readme_downloads as readme_sync  # noqa: E402
import verify_regressions as report_writer  # noqa: E402


class Response(io.BytesIO):
    pass


class FakeHTTP:
    """Deny every unlisted URL; retain ALL methods so zero-write assertions cannot miss DELETE/PATCH."""
    def __init__(self, files, version, *, release=None, tag_exists=False, fail_upload=None):
        self.files = files
        self.version = version
        self.release = release
        self.tag_exists = tag_exists
        self.fail_upload = fail_upload
        self.requests = []
        self.uploads = []

    @property
    def writes(self):
        return [req for req in self.requests if req.get_method() != "GET"]

    def complete_release(self, *, missing_digest=False):
        tag = "v" + self.version
        return {"id": 91, "tag_name": tag, "draft": False, "prerelease": False,
                "upload_url": "https://uploads.github.com/repos/mj8724/CMO_CN/releases/91/assets{?name,label}",
                "assets": [{"id": n + 1, "name": name, "state": "uploaded", "size": len(data),
                            "digest": None if missing_digest else "sha256:" + hashlib.sha256(data).hexdigest(),
                            "browser_download_url": f"https://github.com/mj8724/CMO_CN/releases/download/{tag}/{name}"}
                           for n, (name, data) in enumerate(self.files.items())]}

    def open(self, req, timeout=None):
        self.requests.append(req)
        method, url = req.get_method(), req.full_url
        tag = "v" + self.version
        if method == "GET" and url == f"{publisher.API_BASE}/releases/tags/{tag}":
            if self.release is None:
                raise urllib.error.HTTPError(url, 404, "fixture-not-found", {}, None)
            return Response(json.dumps(self.release).encode("utf-8"))
        if method == "GET" and url == f"{publisher.API_BASE}/git/ref/tags/{tag}":
            if not self.tag_exists:
                raise urllib.error.HTTPError(url, 404, "fixture-not-found", {}, None)
            return Response(json.dumps({"ref": "refs/tags/" + tag}).encode("utf-8"))
        if method == "GET" and url == f"{publisher.API_BASE}/releases/latest":
            return Response(json.dumps({"tag_name": tag}).encode("utf-8"))
        prefix = f"https://github.com/mj8724/CMO_CN/releases/download/{tag}/"
        if method == "GET" and url.startswith(prefix):
            name = urllib.parse.unquote(url[len(prefix):])
            if name not in self.files:
                raise AssertionError("unlisted fixture GET")
            return Response(self.files[name])
        if method == "POST" and url == f"{publisher.API_BASE}/releases":
            payload = json.loads(req.data)
            if payload["tag_name"] != tag or payload["target_commitish"] != "a" * 40:
                raise AssertionError("wrong tag/source commit")
            self.release = {"id": 91, "tag_name": tag, "draft": False, "prerelease": False,
                            "upload_url": "https://uploads.github.com/repos/mj8724/CMO_CN/releases/91/assets{?name,label}",
                            "assets": []}
            return Response(json.dumps(self.release).encode("utf-8"))
        if method == "POST" and url.startswith("https://uploads.github.com/repos/mj8724/CMO_CN/releases/91/assets?"):
            name = urllib.parse.parse_qs(urllib.parse.urlparse(url).query)["name"][0]
            if name == self.fail_upload:
                raise urllib.error.HTTPError(url, 503, "fixture-upload-failure", {}, None)
            if req.data != self.files[name]:
                raise AssertionError("uploaded bytes were not the preflight snapshot")
            item = next(a for a in self.complete_release()["assets"] if a["name"] == name)
            self.release["assets"].append(item)
            self.uploads.append(name)
            return Response(json.dumps(item).encode("utf-8"))
        raise AssertionError(f"unlisted fixture request {method} {url}")


# This test-only entry point is compiled with the real installer/shared sources and real resources.
# It invokes the existing internal install/uninstall with NO registry/process callbacks, not CLI Main.
# There is no duplicated configuration merge algorithm in this host.
HOST = r'''using System;
using System.IO;
using System.Text;
using CMOShared;
using CMOLocalizerInstaller;
internal static class ReleaseFixtureHost {
    private static int Main(string[] args) {
        if (args.Length != 1 || !File.Exists(Path.Combine(args[0], ".release-fixture-only"))) return 2;
        string game = Path.GetFullPath(args[0]);
        CmoPaths.LocalDirOverride = Path.Combine(game, "fixture-local");
        CmoPaths.RoamingDirOverride = Path.Combine(game, "fixture-roaming");
        Directory.CreateDirectory(CmoPaths.RoamingDirOverride);
        File.WriteAllText(CmoPaths.SettingsFile, "{\"fixture\":\"player-choice\"}", new UTF8Encoding(false));
        string error;
        for (int i = 0; i < 2; i++) {
            if (!InstallerCore.Install(game, delegate(string ignored) { }, out error)) {
                Console.WriteLine("FAIL install: " + error); return 1;
            }
        }
        File.Copy(Path.Combine(game, "Command.exe.config"), Path.Combine(game, "config-after-two.xml"));
        File.Copy(Path.Combine(game, "CMOChinese_Installed.json"), Path.Combine(game, "manifest-after-two.json"));
        File.Copy(Path.Combine(game, "Scenarios", "item000.html.orig"), Path.Combine(game, "backup-after-two.bin"));
        if (!InstallerCore.Uninstall(game, delegate(string ignored) {
            string updater = Path.Combine(game, "CMO_Updater.exe");
            if (File.Exists(updater)) File.Delete(updater);
        }, out error)) { Console.WriteLine("FAIL uninstall: " + error); return 1; }
        InstallManifest.Clear(game);
        Console.WriteLine("PASS same-installer install twice + restore; no registry/process callback");
        return 0;
    }
}
'''


def make_scenario_zip() -> bytes:
    with io.BytesIO() as buf:
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
            for n in range(300):
                z.writestr(f"Scenarios/item{n:03d}.html", "<html><body>已译简报</body></html>")
            z.writestr("Scenarios/item000.html.orig", "ORIGINAL-FROM-BUNDLE")
        return buf.getvalue()


class ReleaseContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        temp_root = ROOT / "build/temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        cls.directory = tempfile.TemporaryDirectory(prefix="release-contract-", dir=temp_root)
        cls.fixture = Path(cls.directory.name)
        cls.version = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
        cls.inputs_dir = cls.fixture / "resources"
        cls.inputs_dir.mkdir()
        cls.payloads = {
            "CMOLocalizer.dll": b"SYNTHETIC-EMBEDDED-ENGINE-RELEASE-TEST",
            "CMOLocalizer.json": json.dumps({"_meta": {"version": cls.version}, "exact": {"probe": "探针"}}).encode("utf-8"),
            "UPDATER_EXE": b"SYNTHETIC-UPDATER-NOT-EXECUTABLE",
            "PLACE_SYNOPTIC": b"SYNTHETIC-SYNOPTIC",
            "PLACE_CITIES": b"SYNTHETIC-CITIES",
            "LAUNCHER_ZH": b"SYNTHETIC-LAUNCHER",
            "SCEN_HTML_ZIP": make_scenario_zip(),
        }
        with io.BytesIO() as buf:
            with zipfile.ZipFile(buf, "w") as z:
                for n in range(28):
                    z.writestr(f"button{n:02d}.png", b"SYNTHETIC-MENU")
            cls.payloads["SYMBOLS_MENU_ZIP"] = buf.getvalue()
        for name in checker.DB_TEMPLATES:
            cls.payloads[f"TPL_{name}.html"] = b"<html>synthetic-template</html>"
        for name, data in cls.payloads.items():
            (cls.inputs_dir / name).write_bytes(data)
        host_path = cls.fixture / "ReleaseFixtureHost.cs"
        host_path.write_text(HOST, encoding="utf-8", newline="\n")
        cls.bundle = cls.fixture / "ReleaseFixtureHost.exe"
        csc = Path(os.environ.get("WINDIR", "C:/Windows")) / "Microsoft.NET/Framework64/v4.0.30319/csc.exe"
        if not csc.is_file() or not shutil.which("powershell.exe"):
            raise AssertionError("offline CLR resource/semantic acceptance needs .NET 4.8 csc and PowerShell; not skipped")
        cmd = [str(csc), "-nologo", "-langversion:5", "-target:exe", "-platform:anycpu", "-optimize+",
               "-main:ReleaseFixtureHost", "-out:" + str(cls.bundle)]
        cmd += ["-reference:" + ref for ref in ("System.dll", "System.Core.dll", "System.Windows.Forms.dll",
                "System.Drawing.dll", "System.Xml.dll", "System.IO.Compression.dll", "System.IO.Compression.FileSystem.dll")]
        cmd += [f"-resource:{cls.inputs_dir / name},{name}" for name in cls.payloads]
        cmd += [str(ROOT / path) for path in ("src/shared/CMOShared.cs", "src/shared/MapLabelRenderer.cs",
                                             "src/installer/CMO_Chinese_Setup.cs")]
        cmd.append(str(host_path))
        result = subprocess.run(cmd, capture_output=True, timeout=90)
        if result.returncode:
            raise AssertionError("isolated C#5 host compile failed: " + (result.stdout + result.stderr).decode("utf-8", "replace"))
        cls.missing_bundle = cls.fixture / "MissingResourceFixture.exe"
        missing_cmd = [arg for arg in cmd if not arg.startswith("-resource:") and not arg.startswith("-out:")]
        missing_cmd.insert(1, "-out:" + str(cls.missing_bundle))
        result = subprocess.run(missing_cmd, capture_output=True, timeout=90)
        if result.returncode:
            raise AssertionError("missing-resource negative fixture compile failed")
        cls.setup_bytes = cls.bundle.read_bytes()
        cls.embedded = checker.embedded_engine_hashes(cls.bundle)
        # Separate positive binding fixture uses the actual repository DLL/dictionary bytes,
        # not a synthetic byte label or a mocked embedded hash. It is only inspected, never run.
        cls.real_payloads = {"CMOLocalizer.dll": (ROOT / "build/CMOLocalizer.dll").read_bytes(),
                             "CMOLocalizer.json": (ROOT / "dict/CMOLocalizer.json").read_bytes()}
        if not cls.real_payloads["CMOLocalizer.dll"].startswith(b"MZ"):
            raise AssertionError("actual repository engine fixture is not a PE assembly")
        if json.loads(cls.real_payloads["CMOLocalizer.json"])["_meta"]["version"] != cls.version:
            raise AssertionError("actual repository dictionary fixture version mismatch")
        actual_resources = cls.fixture / "actual-resources"
        actual_resources.mkdir()
        for name, data in cls.real_payloads.items():
            (actual_resources / name).write_bytes(data)
        cls.real_bundle = cls.fixture / "RealPayloadFixture.exe"
        actual_cmd = [arg for arg in cmd if not arg.startswith("-out:") and not arg.startswith("-resource:")]
        actual_cmd.insert(1, "-out:" + str(cls.real_bundle))
        actual_cmd += [f"-resource:{(actual_resources if name in cls.real_payloads else cls.inputs_dir) / name},{name}"
                       for name in cls.payloads]
        result = subprocess.run(actual_cmd, capture_output=True, timeout=90)
        if result.returncode:
            raise AssertionError("actual engine/dictionary embedded binding fixture compile failed")
        cls.pack_bytes = cls.make_pack()
        cls.base_files = {}
        setup_name, _, pack_name, _ = checker.artifact_names(cls.version)
        for name, data in ((setup_name, cls.setup_bytes), (pack_name, cls.pack_bytes)):
            cls.base_files[name] = data
            cls.base_files[name + ".sha256"] = (hashlib.sha256(data).hexdigest() + "  " + name + "\n").encode("ascii")
        # Keep the exact four-file order specified by the production manifest.
        cls.base_files = {name: cls.base_files[name] for name in checker.artifact_names(cls.version)}

    @classmethod
    def make_pack(cls, overrides=None, extra=None, omit=()):
        files = {"CMOLocalizer.dll": cls.payloads["CMOLocalizer.dll"],
                 "CMOLocalizer.json": cls.payloads["CMOLocalizer.json"],
                 "CMO_Updater.exe": cls.payloads["UPDATER_EXE"],
                 checker.artifact_names(cls.version)[0]: cls.setup_bytes,
                 "汉化说明.md": readme_sync.render(cls.version, cls.version).encode("utf-8"),
                 "WW/PlacenameSets/Synoptic/0000_0000.wwp": cls.payloads["PLACE_SYNOPTIC"],
                 "WW/PlacenameSets/Cities/0000_0000.wwp": cls.payloads["PLACE_CITIES"],
                 "Launcher/Localization/stringsChinese.resx": cls.payloads["LAUNCHER_ZH"]}
        files.update({f"DB/Templates/{name}.html": cls.payloads[f"TPL_{name}.html"] for name in checker.DB_TEMPLATES})
        files.update({f"Symbols/Menu/button{n:02d}.png": b"SYNTHETIC-MENU" for n in range(28)})
        files.update({f"Scenarios/item{n:03d}.html": "<html>已译简报</html>".encode("utf-8") for n in range(300)})
        files.update(overrides or {})
        files.update(extra or {})
        for name in omit:
            files.pop(name, None)
        with io.BytesIO() as buf:
            with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
                for name, data in files.items():
                    z.writestr(name, data)
            return buf.getvalue()

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def setUp(self):
        self.root = self.fixture / self._testMethodName
        self.build = self.root / "build"
        self.build.mkdir(parents=True)
        (self.root / "VERSION").write_text(self.version + "\n", encoding="utf-8", newline="\n")
        (self.root / "GAME_BUILD").write_text("1900.23\n", encoding="utf-8", newline="\n")
        (self.root / "dict").mkdir()
        (self.root / "dict/CMOLocalizer.json").write_bytes(self.payloads["CMOLocalizer.json"])
        (self.build / "CMOLocalizer.dll").write_bytes(self.payloads["CMOLocalizer.dll"])
        (self.root / "src/engine").mkdir(parents=True)
        (self.root / "src/engine/Fixture.cs").write_text("// fixture-source\n", encoding="utf-8", newline="\n")
        for name, data in self.base_files.items():
            (self.build / name).write_bytes(data)
        self.inputs = checker.release_inputs(self.version, root=self.root, build_dir=self.build)
        self.now = datetime.now(timezone.utc)
        self.report = self.valid_report()
        self.report_path = self.build / "regression_report.json"
        self.save_report()
        self.http = FakeHTTP(dict(self.base_files), self.version)
        self.stack = ExitStack()
        self.stack.enter_context(patch.multiple(publisher, ROOT=self.root, BUILD_DIR=self.build,
                                                VERSION_FILE=self.root / "VERSION"))
        self.token = self.stack.enter_context(patch.object(publisher, "get_github_token", return_value="mock-not-a-token"))
        self.opener = self.stack.enter_context(patch.object(publisher, "get_opener", return_value=self.http))
        self.intake = self.stack.enter_context(patch.object(publisher, "run_intake_check", return_value=True))
        self.notes = self.stack.enter_context(patch.object(publisher, "release_notes", return_value=("fixture", "fixture")))
        self.commit = self.stack.enter_context(patch.object(publisher, "current_commit", return_value="a" * 40))
        self.stack.enter_context(patch("urllib.request.OpenerDirector.open", side_effect=AssertionError("actual HTTP forbidden")))
        self.stack.enter_context(patch("urllib.request.urlopen", side_effect=AssertionError("actual HTTP forbidden")))
        self.actual_embedded = checker.embedded_engine_hashes
        self.embed_check = self.stack.enter_context(patch.object(checker, "embedded_engine_hashes", side_effect=self.fixture_embedded))
        self.addCleanup(self.stack.close)

    def fixture_embedded(self, path):
        if Path(path).read_bytes() != self.setup_bytes:
            return self.actual_embedded(path)
        return dict(self.embedded)

    def valid_report(self):
        modes = {}
        for mode, minutes in (("static", 3), ("installer", 2), ("live", 1)):
            records = [{"id": name, "name": "fixture " + name, "status": "pass", "detail": "isolated fixture"}
                       for name in sorted(checker.MODE_REQUIRED_RESULTS[mode])]
            modes[mode] = {"generated_at": (self.now - timedelta(minutes=minutes)).isoformat(),
                           "passed": True, "results": records}
        return {"schema": 2, "version": self.version, "inputs": dict(self.inputs),
                "generated_at": self.now.isoformat(), "passed": True, "failed": [],
                "modes": list(checker.REPORT_MODES), "mode_reports": modes,
                "results": [item for mode in checker.REPORT_MODES for item in modes[mode]["results"]]}

    def save_report(self):
        self.report_path.write_text(json.dumps(self.report), encoding="utf-8", newline="\n")

    def run_main(self, args=()):
        with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            return publisher.main(list(args))

    def assert_zero_writes(self, *, no_credentials=False):
        self.assertEqual([], self.http.writes)
        if no_credentials:
            self.token.assert_not_called()
            self.opener.assert_not_called()

    def test_valid_four_asset_preflight_real_embedded_resources(self):
        self.embed_check.side_effect = self.actual_embedded
        files, problems = checker.check_upload_preflight(self.version, root=self.root, build_dir=self.build)
        self.assertEqual([], problems)
        self.assertEqual(self.base_files, files)
        self.assertEqual(hashlib.sha256(self.payloads["CMOLocalizer.dll"]).hexdigest(), self.embedded["CMOLocalizer.dll"])
        self.assertEqual(hashlib.sha256(self.payloads["CMOLocalizer.json"]).hexdigest(), self.embedded["CMOLocalizer.json"])
        self.assert_zero_writes(no_credentials=True)

    def test_actual_repo_engine_dictionary_embedded_four_assets_binding_no_hash_mocks(self):
        setup_name, _, pack_name, _ = checker.artifact_names(self.version)
        real_setup = self.real_bundle.read_bytes()
        (self.build / "CMOLocalizer.dll").write_bytes(self.real_payloads["CMOLocalizer.dll"])
        (self.root / "dict/CMOLocalizer.json").write_bytes(self.real_payloads["CMOLocalizer.json"])
        actual_pack = self.make_pack(overrides={**self.real_payloads, setup_name: real_setup})
        for name, data in ((setup_name, real_setup), (pack_name, actual_pack)):
            (self.build / name).write_bytes(data)
            (self.build / (name + ".sha256")).write_text(hashlib.sha256(data).hexdigest() + "  " + name + "\n", encoding="ascii")
        self.inputs = checker.release_inputs(self.version, root=self.root, build_dir=self.build)
        self.report = self.valid_report()
        self.save_report()
        self.embed_check.side_effect = self.actual_embedded
        files, problems = checker.check_upload_preflight(self.version, root=self.root, build_dir=self.build)
        self.assertEqual([], problems)
        self.assertEqual(real_setup, files[setup_name])
        self.assertEqual(actual_pack, files[pack_name])
        self.assertEqual(hashlib.sha256(self.real_payloads["CMOLocalizer.dll"]).hexdigest(), self.inputs["engine_sha256"])
        self.assertEqual(hashlib.sha256(self.real_payloads["CMOLocalizer.json"]).hexdigest(), self.inputs["dictionary_sha256"])
        self.assert_zero_writes(no_credentials=True)

    def test_missing_each_asset_rejected_before_credentials_or_http(self):
        for name in checker.artifact_names(self.version):
            with self.subTest(name=name):
                path = self.build / name
                path.unlink()
                self.assertEqual(1, self.run_main())
                self.assert_zero_writes(no_credentials=True)
                path.write_bytes(self.base_files[name])

    def test_missing_report_rejected_before_credentials_or_http(self):
        self.report_path.unlink()
        self.assertEqual(1, self.run_main())
        self.assert_zero_writes(no_credentials=True)

    def test_hashbad_or_malformed_sidecars_rejected_zero_writes(self):
        for name in checker.artifact_names(self.version)[1::2]:
            for payload in (b"", b"sha-not-hex", b"0" * 64 + b"  wrong-name.exe\n", b"0" * 64 + b"  " + name[:-7].encode() + b"\n"):
                with self.subTest(name=name, payload=payload[:10]):
                    (self.build / name).write_bytes(payload)
                    self.assertEqual(1, self.run_main())
                    self.assert_zero_writes(no_credentials=True)
            (self.build / name).write_bytes(self.base_files[name])

    def test_old_future_empty_or_wrong_modes_rejected_zero_writes(self):
        original = copy.deepcopy(self.report)
        mutations = []
        for label, stamp in (("old", self.now - timedelta(hours=25)), ("future", self.now + timedelta(days=1))):
            mutations.append((label + "-aggregate", lambda report, stamp=stamp: report.update(generated_at=stamp.isoformat())))
            for mode in checker.REPORT_MODES:
                mutations.append((label + "-" + mode, lambda report, mode=mode, stamp=stamp:
                                  report["mode_reports"][mode].update(generated_at=stamp.isoformat())))
        mutations += [("empty-modes", lambda r: r.update(modes=[])),
                      ("only-live", lambda r: r.update(modes=["live"])),
                      ("empty-results", lambda r: r.update(results=[])),
                      ("no-layers", lambda r: r.pop("mode_reports")),
                      ("old-schema", lambda r: r.update(schema=1)),
                      ("truthy-passed", lambda r: r.update(passed="true")),
                      ("naive-date", lambda r: r.update(generated_at=self.now.replace(tzinfo=None).isoformat())),
                      ("version", lambda r: r.update(version="0.0.1")),
                      ("bad-input", lambda r: r["inputs"].update(engine_sha256="f" * 64))]
        for mode in checker.REPORT_MODES:
            mutations.append(("empty-" + mode, lambda r, mode=mode: r["mode_reports"][mode].update(results=[])))
            mutations.append(("failing-" + mode, lambda r, mode=mode: r["mode_reports"][mode]["results"][0].update(status="fail")))
        for label, change in mutations:
            with self.subTest(label=label):
                self.report = copy.deepcopy(original)
                change(self.report)
                self.save_report()
                self.assertEqual(1, self.run_main())
                self.assert_zero_writes(no_credentials=True)

    def test_relabelled_live_only_results_not_real_static_installer_evidence(self):
        for mode in ("static", "installer"):
            self.report["mode_reports"][mode]["results"] = [{"id": mode + "-fake-live", "status": "pass"}]
        self.report["results"] = [item for mode in checker.REPORT_MODES for item in self.report["mode_reports"][mode]["results"]]
        self.save_report()
        self.assertEqual(1, self.run_main())
        self.assert_zero_writes(no_credentials=True)

    def test_source_changed_invalidates_report_without_duplicated_hash_scope(self):
        (self.root / "src/engine/Fixture.cs").write_text("// changed\n", encoding="utf-8")
        self.assertEqual(1, self.run_main())
        self.assert_zero_writes(no_credentials=True)

    def test_embedded_dictionary_or_engine_mismatch_rejected_zero_writes(self):
        for name in ("CMOLocalizer.dll", "CMOLocalizer.json"):
            with self.subTest(resource=name):
                self.embed_check.side_effect = lambda path, name=name: {**self.embedded, name: "f" * 64}
                self.assertEqual(1, self.run_main())
                self.assert_zero_writes(no_credentials=True)

    def test_pack_mismatched_installer_dll_json_config_oldbackup_rejected(self):
        variants = [self.make_pack(overrides={checker.artifact_names(self.version)[0]: b"different-setup"}),
                    self.make_pack(overrides={"CMOLocalizer.dll": b"different-engine"}),
                    self.make_pack(overrides={"CMOLocalizer.json": b"{\"_meta\":{\"version\":\"0.0.1\"}}"}),
                    self.make_pack(extra={"Command.exe.config.patch": b"official-config"}),
                    self.make_pack(extra={"Scenarios/item000.html.orig": b"overwrites-old-backup"})]
        pack_name = checker.artifact_names(self.version)[2]
        for number, payload in enumerate(variants):
            with self.subTest(variant=number):
                (self.build / pack_name).write_bytes(payload)
                (self.build / (pack_name + ".sha256")).write_text(hashlib.sha256(payload).hexdigest() + "  " + pack_name + "\n", encoding="ascii")
                self.assertEqual(1, self.run_main())
                self.assert_zero_writes(no_credentials=True)

    def test_existing_release_same_hash_is_no_write_idempotent(self):
        self.http.release = self.http.complete_release()
        self.assertEqual(0, self.run_main())
        self.assert_zero_writes()
        self.assertTrue(self.http.requests)
        self.assertTrue(all(req.get_method() == "GET" for req in self.http.requests))

    def test_existing_release_bad_hash_incomplete_or_extra_refused_zero_writes(self):
        bad = self.http.complete_release()
        bad["assets"][0]["digest"] = "sha256:" + "f" * 64
        incomplete = self.http.complete_release()
        incomplete["assets"].pop()
        extra = self.http.complete_release()
        extra["assets"].append({"name": "old-unexpected.exe", "size": 1, "state": "uploaded"})
        for release in (bad, incomplete, extra):
            with self.subTest(assets=len(release["assets"])):
                self.http.release = release
                self.assertEqual(1, self.run_main())
                self.assert_zero_writes()

    def test_missing_remote_digest_requires_get_real_bytes(self):
        self.http.release = self.http.complete_release(missing_digest=True)
        self.assertEqual(0, self.run_main(["--verify-only"]))
        binary = checker.artifact_names(self.version)[0]
        self.http.files[binary] = b"tampered"  # API still claims the old size.
        self.assertEqual(1, self.run_main(["--verify-only"]))
        self.assert_zero_writes()
        self.token.assert_not_called()

    def test_verify_only_ignores_write_gates_and_does_not_read_token(self):
        self.report_path.unlink()
        (self.root / "VERSION").write_text("0.0.1\n", encoding="utf-8")
        self.http.release = self.http.complete_release()
        self.intake.side_effect = AssertionError("write gate called")
        self.notes.side_effect = AssertionError("notes write gate called")
        self.commit.side_effect = AssertionError("source write gate called")
        with patch.object(checker, "check_upload_preflight", side_effect=AssertionError("upload preflight called")):
            self.assertEqual(0, self.run_main(["--verify-only", "--version", self.version]))
        self.assert_zero_writes()
        self.token.assert_not_called()
        self.embed_check.assert_not_called()
        self.intake.assert_not_called()
        self.notes.assert_not_called()
        self.commit.assert_not_called()

    def test_verify_only_local_missing_hashbad_or_remote_missing_is_failure_zero_writes(self):
        self.http.release = self.http.complete_release()
        sidecar = self.build / checker.artifact_names(self.version)[1]
        sidecar.unlink()
        self.assertEqual(1, self.run_main(["--verify-only"]))
        sidecar.write_bytes(b"bad")
        self.assertEqual(1, self.run_main(["--verify-only"]))
        sidecar.write_bytes(self.base_files[sidecar.name])
        self.http.release["assets"].pop()
        self.assertEqual(1, self.run_main(["--verify-only"]))
        self.assert_zero_writes()
        self.token.assert_not_called()

    def test_unknown_args_or_force_are_rejected_before_http(self):
        for args in (["--force"], ["--verfy-only"], ["--ver"], ["--version", self.version, "--unknown"]):
            with self.subTest(args=args), self.assertRaises(SystemExit) as raised:
                self.run_main(args)
            self.assertEqual(2, raised.exception.code)
        self.assert_zero_writes(no_credentials=True)

    def test_full_intake_fail_prevents_credentials_http_and_readme_write(self):
        self.intake.return_value = False
        self.assertEqual(1, self.run_main())
        self.assert_zero_writes(no_credentials=True)

    def test_existing_tag_blocks_new_release_even_without_release_object(self):
        self.http.tag_exists = True
        with patch.object(checker, "FROZEN_THROUGH", (0, 0, 0)):
            self.assertEqual(1, self.run_main())
        self.assert_zero_writes()

    def test_new_release_uploads_exact_four_after_preflight_and_get_verifies(self):
        with patch.object(checker, "FROZEN_THROUGH", (0, 0, 0)):
            self.assertEqual(0, self.run_main())
        self.assertEqual(list(checker.artifact_names(self.version)), self.http.uploads)
        self.assertEqual(["POST"] * 5, [req.get_method() for req in self.http.writes])
        first_write = next(n for n, req in enumerate(self.http.requests) if req.get_method() != "GET")
        self.assertGreaterEqual(first_write, 2)
        self.assertTrue(all(req.get_method() == "GET" for req in self.http.requests[:first_write]))

    def test_partial_upload_failure_never_claims_success_or_deletes(self):
        self.http.fail_upload = checker.artifact_names(self.version)[1]
        with patch.object(checker, "FROZEN_THROUGH", (0, 0, 0)):
            self.assertEqual(1, self.run_main())
        self.assertEqual([checker.artifact_names(self.version)[0]], self.http.uploads)
        self.assertNotIn("DELETE", [req.get_method() for req in self.http.requests])
        self.assertNotIn("PATCH", [req.get_method() for req in self.http.requests])

    def test_frozen_version_existing_assets_and_git_failure_block_before_build(self):
        good_git = subprocess.CompletedProcess([], 0, "", "")
        with patch.object(checker.subprocess, "run", return_value=good_git):
            self.assertTrue(checker.frozen_version_problems("4.2.2", root=self.root, build_dir=self.build))
            self.assertTrue(checker.frozen_version_problems("4.1.8", root=self.root, build_dir=self.build))
            newer = "99.0.0"
            self.assertEqual([], checker.frozen_version_problems(newer, root=self.root, build_dir=self.build))
            name = checker.artifact_names(newer)[2]
            (self.build / name).write_bytes(b"old-frozen-zip")
            self.assertTrue(checker.frozen_version_problems(newer, root=self.root, build_dir=self.build))
            self.assertTrue(checker.frozen_version_problems(newer, root=self.root, build_dir=self.build, operation="package"))
        with patch.object(checker.subprocess, "run", return_value=subprocess.CompletedProcess([], 1, "", "")):
            self.assertTrue(checker.frozen_version_problems("99.0.1", root=self.root, build_dir=self.build))
        with patch.object(checker.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "v99.0.1\n", "")):
            self.assertTrue(checker.frozen_version_problems("99.0.1", root=self.root, build_dir=self.build))

    def test_build_and_package_safety_is_before_any_write_and_no_recompile_in_package(self):
        build_text = (ROOT / "scripts/build.sh").read_text(encoding="utf-8")
        package_text = (ROOT / "scripts/package.sh").read_text(encoding="utf-8")
        self.assertLess(build_text.index("--check-frozen-version"), build_text.index('mkdir -p "$OUT"'))
        self.assertLess(build_text.index("--check-frozen-version"), build_text.index("powershell.exe"))
        self.assertIn('SETUP_EXE="SetupCandidate.exe"', build_text)
        self.assertIn('-OutputDir "$(W "$OUT")"', build_text)
        self.assertIn('DICTIONARY="$OUT/CMOLocalizer.json"', build_text)
        self.assertIn('if [[ "$CANDIDATE" -eq 0 ]]', build_text)
        self.assertLess(package_text.index("--check-frozen-version"), package_text.index("mktemp -d"))
        self.assertNotIn('bash "$ROOT/scripts/build.sh"', package_text)
        self.assertNotIn('rm -f "$ZIP"', package_text)
        self.assertNotIn('rm -rf "$PACK"', package_text)
        self.assertIn('cp "$SETUP" "$PACK/"', package_text)
        self.assertIn('ZipFile(dest, "x"', package_text)
        self.assertNotIn('read_text(encoding="utf-8-sig")', package_text)

    def test_readme_template_immutable_links_and_explicit_apply_check_no_write(self):
        current = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
        rendered = readme_sync.render("4.1.8", current)
        self.assertNotIn("releases/latest/download/", rendered)
        self.assertIn("releases/download/v4.1.8/CMO_Chinese_Setup_v4.1.8.exe", rendered)
        self.assertIn(f"releases/download/v{current}/CMO_Chinese_Pack_{current}.zip", rendered)
        self.assertIn("--install", rendered)
        self.assertIn("--silent", rendered)
        self.assertIn("--adopt", rendered)
        readme = self.root / "README.md"
        old = ("prefix\r\n" + rendered.replace("\n", "\r\n") + "\r\nsuffix\r\n").encode("utf-8")
        readme.write_bytes(old)
        (self.root / "STABLE_VERSION").write_text("4.1.8\n", encoding="utf-8")
        with patch.multiple(readme_sync, README=readme, STABLE_FILE=self.root / "STABLE_VERSION", VERSION_FILE=self.root / "VERSION"):
            with redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                self.assertEqual(0, readme_sync.main(["--check"]))
                # Drift must be detected through the stable explicit tag link.
                (self.root / "STABLE_VERSION").write_text("3.9.9", encoding="utf-8")
                self.assertEqual(1, readme_sync.main(["--check"]))
            self.assertEqual(old, readme.read_bytes())

    def test_report_writer_preserves_only_matching_inputs_and_exception_invalidates(self):
        # Execute only mocked test operations, never run_static/run_installer/run_live implementations.
        def mark(mode):
            return lambda runner: [runner.record(cid, "fixture", True, "mock operation") for cid in sorted(checker.MODE_REQUIRED_RESULTS[mode])]
        with patch.multiple(report_writer, ROOT=self.root, REPORT=self.report_path), \
                patch.object(report_writer, "release_inputs", return_value=self.inputs), \
                patch.object(report_writer, "run_static", side_effect=mark("static")), \
                patch.object(report_writer, "run_installer", side_effect=mark("installer")), \
                patch.object(report_writer, "run_live", side_effect=mark("live")), redirect_stdout(io.StringIO()):
            self.report_path.unlink()
            self.assertEqual(0, report_writer.main(["--static", "--installer"]))
            intermediate = json.loads(self.report_path.read_text(encoding="utf-8"))
            old_installer = copy.deepcopy(intermediate["mode_reports"]["installer"])
            self.assertEqual(0, report_writer.main(["--live"]))
            complete = json.loads(self.report_path.read_text(encoding="utf-8"))
            self.assertEqual(old_installer, complete["mode_reports"]["installer"])
            self.assertEqual([], checker.check_regression_report(self.version, self.report_path, expected_inputs=self.inputs))
            with patch.object(report_writer, "run_live", side_effect=RuntimeError("fixture exception")):
                self.assertEqual(1, report_writer.main(["--live"]))
            self.assertTrue(checker.check_regression_report(self.version, self.report_path, expected_inputs=self.inputs))
            changed = {**self.inputs, "engine_sha256": "f" * 64}
            with patch.object(report_writer, "release_inputs", return_value=changed):
                self.assertEqual(0, report_writer.main(["--live"]))
            reset = json.loads(self.report_path.read_text(encoding="utf-8"))
            self.assertEqual(["live"], reset["modes"])
            self.assertTrue(checker.check_regression_report(self.version, self.report_path, expected_inputs=changed))

    def test_report_writer_empty_or_mixed_modes_fail_closed_and_no_report_preserves_bytes(self):
        with patch.multiple(report_writer, ROOT=self.root, REPORT=self.report_path), \
                patch.object(report_writer, "release_inputs", return_value=self.inputs), \
                patch.object(report_writer, "run_live", return_value=None), \
                patch.object(report_writer, "run_installer", side_effect=AssertionError("real installer forbidden")), \
                redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
            original = self.report_path.read_bytes()
            self.assertEqual(1, report_writer.main(["--live", "--no-report"]))
            self.assertEqual(original, self.report_path.read_bytes())
            self.assertEqual(1, report_writer.main(["--live"]))
            self.assertTrue(checker.check_regression_report(self.version, self.report_path, expected_inputs=self.inputs))
            with self.assertRaises(SystemExit) as rejected:
                report_writer.main(["--installer", "--live"])
            self.assertEqual(2, rejected.exception.code)

    def test_pack_and_manifest_negative_contracts_fail_without_zip_count_shortcuts(self):
        variations = [self.make_pack(omit=("DB/Templates/Ship.html",)),
                      self.make_pack(omit=("汉化说明.md",)),
                      self.make_pack(omit=(checker.artifact_names(self.version)[0],)),
                      self.make_pack(overrides={"汉化说明.md": b"unzip and rename only"}),
                      self.make_pack(extra={"../escape.html": b"bad"}),
                      self.make_pack(extra={"DB/Templates/SHIP.html": b"duplicate"}),
                      self.make_pack(extra={".workflow/log.json": b"process"}),
                      self.make_pack(extra={"Scenarios/item000.html.ORIG": b"old-backup"}),
                      self.make_pack(extra={"command.exe.config": b"overwrites-config"})]
        for number, payload in enumerate(variations):
            with self.subTest(variant=number):
                self.assertTrue(checker.pack_problems(payload, self.version, setup_bytes=self.setup_bytes,
                                                      expected_inputs=self.inputs))
        self.assertTrue(checker.pack_problems(b"not a zip", self.version))
        self.assert_zero_writes(no_credentials=True)

    def test_build_candidate_guard_and_frozen_helpers_use_actual_isolated_scripts(self):
        # Only run script text in an isolated build fixture with stub downstream commands.
        # The immutable repository build/package entry points are never executed.
        shell = shutil.which("bash")
        self.assertIsNotNone(shell)
        sandbox = self.root / "shell-fixture"
        scripts = sandbox / "scripts"
        scripts.mkdir(parents=True)
        (sandbox / "VERSION").write_text("4.2.2\n", encoding="ascii")
        build_text = (ROOT / "scripts/build.sh").read_text(encoding="utf-8")
        package_text = (ROOT / "scripts/package.sh").read_text(encoding="utf-8")
        (scripts / "build.sh").write_text(build_text, encoding="utf-8", newline="\n")
        (scripts / "package.sh").write_text(package_text, encoding="utf-8", newline="\n")
        (scripts / "release_check.py").write_text("raise SystemExit(1)\n", encoding="ascii")
        (scripts / "build.ps1").write_text("", encoding="ascii")
        stub_dir = sandbox / "bin"
        stub_dir.mkdir()
        trace = sandbox / "compiler-called"
        (stub_dir / "powershell.exe").write_text('#!/usr/bin/env bash\nprintf called > "$STUB_TRACE"\nexit 0\n', encoding="ascii", newline="\n")
        subprocess.run([shell, "-c", 'chmod +x "$1"', "fixture", str(stub_dir / "powershell.exe")], check=True, timeout=10)
        env = {**os.environ, "PY": sys.executable, "STUB_TRACE": str(trace),
               "PATH": str(stub_dir) + os.pathsep + os.environ["PATH"]}
        before = {p.relative_to(sandbox).as_posix(): p.read_bytes() for p in sandbox.rglob("*") if p.is_file()}
        for script in ("build.sh", "package.sh"):
            rejected = subprocess.run([shell, str(scripts / script)], env=env, capture_output=True, timeout=15)
            self.assertEqual(1, rejected.returncode, (rejected.stdout + rejected.stderr).decode("utf-8", "replace"))
            self.assertFalse(trace.exists())
        after = {p.relative_to(sandbox).as_posix(): p.read_bytes() for p in sandbox.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        invalid = subprocess.run([shell, str(scripts / "build.sh"), "--candidate", str(sandbox / "outside"), "--engine-only"],
                                 env=env, capture_output=True, timeout=15)
        self.assertNotEqual(0, invalid.returncode)
        self.assertFalse(trace.exists())
        candidate = sandbox / "build/candidate"
        valid = subprocess.run([shell, str(scripts / "build.sh"), "--candidate", str(candidate), "--engine-only"],
                               env=env, capture_output=True, timeout=15)
        self.assertEqual(0, valid.returncode, (valid.stdout + valid.stderr).decode("utf-8", "replace"))
        self.assertTrue(trace.exists())
        self.assertFalse((sandbox / "build/CMO_Chinese_Setup_v4.2.2.exe").exists())

    def test_shell_and_embedded_python_fragments_parse_without_running_entrypoints(self):
        shell = shutil.which("bash")
        self.assertIsNotNone(shell)
        for name in ("build.sh", "package.sh"):
            text = (ROOT / "scripts" / name).read_text(encoding="utf-8")
            result = subprocess.run([shell, "-n"], input=text, text=True, capture_output=True, timeout=10)
            self.assertEqual(0, result.returncode, result.stderr)
            fragments = re.findall(r"<<'PYEOF'\n(.*?)\nPYEOF", text, re.S)
            self.assertTrue(fragments)
            for number, body in enumerate(fragments):
                compile(body, f"{name}:python-heredoc:{number}", "exec")

    def test_input_drift_during_remote_read_rejected_before_first_post(self):
        original_open = self.http.open
        def tamper_after_tag_read(req, timeout=None):
            try:
                return original_open(req, timeout)
            finally:
                if req.full_url.endswith("/git/ref/tags/v" + self.version):
                    (self.root / "src/engine/Fixture.cs").write_text("// changed during GET\n", encoding="utf-8")
        self.http.open = tamper_after_tag_read
        with patch.object(checker, "FROZEN_THROUGH", (0, 0, 0)):
            self.assertEqual(1, self.run_main())
        self.assert_zero_writes()

    def test_real_embedded_missing_resource_is_not_mocked_into_success(self):
        with self.assertRaises(ValueError):
            self.actual_embedded(self.missing_bundle)
        self.assert_zero_writes(no_credentials=True)

    def test_green_same_installer_semantic_unique_domain_backup_settings_restore(self):
        original = ('<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0"/></startup>'
                    '<runtime><gcServer enabled="true"/><assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">'
                    '<dependentAssembly><assemblyIdentity name="Official"/><bindingRedirect oldVersion="0.0.0.0-9.0.0.0" newVersion="9.0.0.0"/>'
                    '</dependentAssembly></assemblyBinding></runtime></configuration>').encode("utf-8")
        duplicate = original.replace(b"</runtime>", b'<appDomainManagerType/><appDomainManagerType value="old"/>'
                                     b'<appDomainManagerAssembly/><appDomainManagerAssembly value="old"/></runtime>')
        for label, config, old_backup in (("clean", original, None), ("duplicates", duplicate, original)):
            with self.subTest(label=label):
                game = self.root / label
                game.mkdir()
                (game / ".release-fixture-only").write_text("synthetic", encoding="ascii")
                (game / "Command.exe").write_bytes(b"not-executable-game-stub")
                (game / "Command.exe.config").write_bytes(config)
                if old_backup is not None:
                    (game / "Command.exe.config.cmo_bak").write_bytes(old_backup)
                (game / "Scenarios").mkdir()
                (game / "Scenarios/item000.html").write_bytes(b"ORIGINAL-PLAYER-HTML")
                (game / "Scenarios/item000.html.orig").write_bytes(b"PLAYER-OLD-BACKUP-DO-NOT-OVERWRITE")
                result = subprocess.run([str(self.bundle), str(game)], capture_output=True, timeout=60)
                self.assertEqual(0, result.returncode, (result.stdout + result.stderr).decode("utf-8", "replace"))
                after = ET.fromstring((game / "config-after-two.xml").read_bytes())
                runtime = after.find("runtime")
                self.assertEqual(1, len(runtime.findall("appDomainManagerType")))
                self.assertEqual(1, len(runtime.findall("appDomainManagerAssembly")))
                self.assertEqual("CMOLocalizer.LocalizerDomainManager", runtime.find("appDomainManagerType").get("value"))
                self.assertEqual("CMOLocalizer, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", runtime.find("appDomainManagerAssembly").get("value"))
                self.assertEqual("true", runtime.find("gcServer").get("enabled"))
                self.assertIsNotNone(runtime.find("{urn:schemas-microsoft-com:asm.v1}assemblyBinding"))
                self.assertIsNotNone(after.find("startup/supportedRuntime"))
                self.assertEqual(original if old_backup is None else old_backup, (game / "Command.exe.config").read_bytes())
                self.assertEqual(b"PLAYER-OLD-BACKUP-DO-NOT-OVERWRITE", (game / "backup-after-two.bin").read_bytes())
                self.assertEqual(b"PLAYER-OLD-BACKUP-DO-NOT-OVERWRITE", (game / "Scenarios/item000.html").read_bytes())
                self.assertEqual(b'{"fixture":"player-choice"}', (game / "fixture-roaming/settings.json").read_bytes())
                manifest = json.loads((game / "manifest-after-two.json").read_text(encoding="utf-8"))
                self.assertEqual(self.version, manifest["version"])
                self.assertEqual(self.embedded["CMOLocalizer.dll"], manifest["files"]["CMOLocalizer.dll"])
                self.assertEqual(self.embedded["CMOLocalizer.json"], manifest["files"]["CMOLocalizer.json"])
                self.assertFalse((game / "CMOChinese_Installed.json").exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
