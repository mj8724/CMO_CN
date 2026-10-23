// CMOLocalizer 共享状态层自测宿主（测试专用，不进入安装包/绿色包）
//
// 用法：bash scripts/build.sh && build/CMOSharedTest.exe
// 输出：每项一行 PASS/FAIL，退出码 = 失败项数量（0 = 全绿）。
//
// 所有断言都在系统临时目录内完成：CmoPaths 的 Roaming/Local/RegistryPath 全部被覆盖，
// 因此不会碰用户的真实设置文件，也不会碰 HKCU\Software\CMOChinese。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CMOShared;

#if CMO_INTEGRITY_TEST
// /define:CMO_INTEGRITY_TEST /main:CMOIntegritySelfTest：合成完整 bundle，禁用真实用户/进程副作用。
internal static class CMOIntegritySelfTest
{
    private const string FixtureMarker = "CMO_INTEGRITY_TEST_ONLY";
    private static string root;
    private static int checks;
    private static int failures;
    private static readonly string[] Templates = {
        "Aircraft.html", "Facility.html", "GroundUnit.html", "Platform.html",
        "Satellite.html", "Ship.html", "Submarine.html", "Weapon.html"
    };

    private static void Check(string name, bool ok, string detail)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length == 0 ? "" : " -- " + detail));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void Prepare(string output)
    {
        foreach (string marker in new string[] { "new", "rollback" })
        {
            string dir = Path.Combine(output, marker);
            Write(Path.Combine(dir, "CMOLocalizer.dll"), "SYNTHETIC-" + marker + "-ENGINE-NOT-A-REAL-DLL");
            Write(Path.Combine(dir, "CMOLocalizer.json"), "{\"_meta\":{\"version\":\"" + ReleaseConstants.Version
                + "\"},\"fixture\":\"" + marker + "\"}");
            foreach (string name in new string[] { "UPDATER_EXE", "PLACE_SYNOPTIC", "PLACE_CITIES", "LAUNCHER_ZH" })
                Write(Path.Combine(dir, name), "SYNTHETIC-" + marker + "-" + name);
            foreach (string name in Templates)
                Write(Path.Combine(dir, "TPL_" + name), "<p>合成模板 " + marker + " " + name + "</p>");
            CreateZip(Path.Combine(dir, "SCEN_HTML_ZIP"), "Scenarios/probe.html", "<p>合成简报 " + marker + "</p>");
            CreateZip(Path.Combine(dir, "SYMBOLS_MENU_ZIP"), "probe.png", "SYNTHETIC-" + marker + "-MENU");
        }
        Console.WriteLine("Prepared synthetic bundles (not game payloads): " + output);
    }

    private static void CreateZip(string path, string name, string text)
    {
        using (FileStream fs = File.Create(path))
        using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        using (Stream entry = zip.CreateEntry(name).Open())
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            entry.Write(bytes, 0, bytes.Length);
        }
    }

    private static void Isolate(string path)
    {
        CmoPaths.LocalDirOverride = Path.Combine(path, "local");
        CmoPaths.RoamingDirOverride = Path.Combine(path, "roaming");
    }

    private static string Fixture(string tag)
    {
        string game = Path.Combine(root, tag);
        Write(Path.Combine(game, ".cmo-integrity-fixture"), FixtureMarker);
        Write(Path.Combine(game, "Command.exe"), "SYNTHETIC STUB - NEVER EXECUTE");
        Write(Path.Combine(game, "Command.exe.config"), "<configuration><runtime /></configuration>");
        Write(Path.Combine(game, "CMOLocalizer.dll"), "OLD-DISK-DLL");
        Write(Path.Combine(game, "CMOLocalizer.json"), "OLD-DISK-JSON");
        Write(Path.Combine(game, "Scenarios", "probe.html"), "ORIGINAL-Scenarios");
        Write(Path.Combine(game, "Symbols", "Menu", "probe.png"), "ORIGINAL-Menu");
        Write(Path.Combine(game, "Launcher", "Localization", "stringsChinese.resx"), "ORIGINAL-Launcher");
        foreach (string set in new string[] { "Synoptic", "Cities" })
            Write(Path.Combine(game, "WW", "PlacenameSets", set, "0000_0000.wwp"), "ORIGINAL-" + set);
        foreach (string name in Templates)
            Write(Path.Combine(game, "DB", "Templates", name), "ORIGINAL-" + name);
        return game;
    }

    private static byte[] Resource(string bundle, string name)
    {
        using (Stream stream = PackageAssets.EmbeddedResourceStream(bundle, name))
        using (MemoryStream bytes = new MemoryStream())
        {
            if (stream == null) throw new InvalidOperationException("Missing fixture resource " + name);
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
    }

    private static bool Same(byte[] left, byte[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static byte[] ZipResource(string bundle, string name, string entry)
    {
        using (MemoryStream bytes = new MemoryStream(Resource(bundle, name)))
        using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Read))
        using (Stream stream = zip.GetEntry(entry).Open())
        using (MemoryStream result = new MemoryStream())
        {
            stream.CopyTo(result);
            return result.ToArray();
        }
    }

    private static void CheckBundle(string tag, string game, string bundle)
    {
        string error;
        Dictionary<string, string> hashes = InstallManifest.HashEngineFiles(game);
        bool match = PackageAssets.VerifyEngineFiles(bundle, hashes, out error);
        InstallManifest manifest = InstallManifest.Read(game);
        bool recorded = manifest != null && manifest.Version == ReleaseConstants.Version && manifest.Files.Count == 2;
        foreach (string name in InstallManifest.EngineFiles)
        {
            string hash;
            recorded = recorded && manifest.Files.TryGetValue(name, out hash)
                && hash == PackageAssets.EmbeddedSha256(bundle, name);
        }
        Check(tag + "-dll-json-embedded-and-manifest", match && recorded, error);
        bool assets = Same(File.ReadAllBytes(Path.Combine(game, "Scenarios", "probe.html")),
                ZipResource(bundle, "SCEN_HTML_ZIP", "Scenarios/probe.html"))
            && Same(File.ReadAllBytes(Path.Combine(game, "Symbols", "Menu", "probe.png")),
                ZipResource(bundle, "SYMBOLS_MENU_ZIP", "probe.png"));
        foreach (string name in Templates)
            assets = assets && Same(File.ReadAllBytes(Path.Combine(game, "DB", "Templates", name)), Resource(bundle, "TPL_" + name));
        foreach (string set in new string[] { "Synoptic", "Cities" })
            assets = assets && Same(File.ReadAllBytes(Path.Combine(game, "WW", "PlacenameSets", set, "0000_0000.wwp")),
                Resource(bundle, set == "Synoptic" ? "PLACE_SYNOPTIC" : "PLACE_CITIES"));
        assets = assets && Same(File.ReadAllBytes(Path.Combine(game, "Launcher", "Localization", "stringsChinese.resx")), Resource(bundle, "LAUNCHER_ZH"))
            && Same(File.ReadAllBytes(Path.Combine(game, "CMO_Updater.exe")), Resource(bundle, "UPDATER_EXE"));
        Check(tag + "-whole-synthetic-package-assets", assets, "Scenarios/Menu/8 templates/2 placenames/launcher/updater");
    }

    private static void InstallAndRestore(string self)
    {
        Write(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer.dll"), "STALE-NEIGHBOR-DLL");
        Write(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CMOLocalizer.json"), "STALE-NEIGHBOR-JSON");
        string game = Fixture("neighbor");
        CmoSettings settings = new CmoSettings();
        settings.GamePath = game;
        settings.CheckIntervalHours = 12;
        settings.TrayOnClose = false;
        settings.AutoStart = false;
        settings.Save(); // 文件预存在，Load 不走注册表迁移。
        byte[] saved = File.ReadAllBytes(CmoPaths.SettingsFile);
        string error;
        bool ok = CMOLocalizerInstaller.InstallerCore.Install(game, delegate(string path) { }, out error);
        Check("new-exe-with-old-neighbor-install", ok, error);
        CheckBundle("neighbor", game, self);
        Check("install-settings-preserved", Same(saved, File.ReadAllBytes(CmoPaths.SettingsFile)), "file-only settings, no registry callback");
        ok = CMOLocalizerInstaller.InstallerCore.Uninstall(game, delegate(string path) { File.Delete(Path.Combine(path, "CMO_Updater.exe")); }, out error);
        if (ok) InstallManifest.Clear(game); // 与 CLI 的最后一步一致，不执行 CleanupUpdater/Purge。
        Check("restore-config-and-clear-manifest", ok && File.ReadAllText(Path.Combine(game, "Command.exe.config")) == "<configuration><runtime /></configuration>"
            && !File.Exists(CmoPaths.ManifestPath(game)) && !File.Exists(Path.Combine(game, "CMOLocalizer.dll"))
            && !File.Exists(Path.Combine(game, "CMOLocalizer.json")), error);
        bool restored = File.ReadAllText(Path.Combine(game, "Scenarios", "probe.html")) == "ORIGINAL-Scenarios"
            && File.ReadAllText(Path.Combine(game, "Symbols", "Menu", "probe.png")) == "ORIGINAL-Menu"
            && File.ReadAllText(Path.Combine(game, "Launcher", "Localization", "stringsChinese.resx")) == "ORIGINAL-Launcher";
        foreach (string name in Templates)
            restored = restored && File.ReadAllText(Path.Combine(game, "DB", "Templates", name)) == "ORIGINAL-" + name;
        foreach (string set in new string[] { "Synoptic", "Cities" })
            restored = restored && File.ReadAllText(Path.Combine(game, "WW", "PlacenameSets", set, "0000_0000.wwp")) == "ORIGINAL-" + set;
        Check("restore-whole-assets-and-keep-settings", restored && Same(saved, File.ReadAllBytes(CmoPaths.SettingsFile)), "no user settings purge");

        game = Fixture("exe-in-stale-target");
        string setup = Path.Combine(game, "CMO_Chinese_Setup_new.exe");
        File.Copy(self, setup);
        int code = Rollback.RunProcess(setup, "--install \"" + game + "\" --silent", 15000);
        Check("new-exe-in-old-target-install", code == 0, "test-only CLI rc=" + code);
        CheckBundle("source-equals-target", game, setup);
    }

    private static void IntegrityFailures(string missingBundle)
    {
        foreach (string name in InstallManifest.EngineFiles)
        {
            string game = Fixture("tamper-" + Path.GetExtension(name).Substring(1));
            InstallManifest.Write(game, "4.0.0", "original", InstallManifest.HashEngineFiles(game));
            byte[] saved = File.ReadAllBytes(CmoPaths.ManifestPath(game));
            string error;
            bool ok = CMOLocalizerInstaller.InstallerCore.Install(game,
                delegate(string path) { Write(Path.Combine(path, name), "TAMPERED"); }, out error);
            Check("reject-" + name + "-before-new-manifest", !ok && error.Contains(name)
                && Same(saved, File.ReadAllBytes(CmoPaths.ManifestPath(game))), error);
        }
        string absent = Fixture("missing-embedded");
        InstallManifest.Write(absent, "4.0.0", "original", InstallManifest.HashEngineFiles(absent));
        byte[] original = File.ReadAllBytes(CmoPaths.ManifestPath(absent));
        int code = Rollback.RunProcess(missingBundle, "--install \"" + absent + "\" --silent", 15000);
        Check("missing-bundle-rejects-disk-fallback", code != 0 && Same(original, File.ReadAllBytes(CmoPaths.ManifestPath(absent))), "test-only CLI rc=" + code);
        string detail;
        Check("missing-embedded-hash-fails-closed", !PackageAssets.VerifyEngineFiles(missingBundle,
            InstallManifest.HashEngineFiles(absent), out detail), detail);
    }

    private static void DigestTests(string self)
    {
        int reads = 0;
        string text = "";
        Func<string, int, string> reader = delegate(string url, int timeout) { reads++; return text; };
        string detail;
        foreach (string url in new string[] { null, "", "   " })
            Check("sha-missing-url-rejected-" + (url == null ? "null" : url.Length.ToString()),
                !CMOUpdater.UpdateService.VerifySha(self, url, reader, out detail), detail);
        Check("sha-missing-rejects-before-digest-read", reads == 0
            && !CMOUpdater.UpdateService.VerifySha(self, null, out detail), "digest reads=" + reads);
        text = new string('0', 64) + "  setup.exe";
        Check("sha-mismatch-rejected", !CMOUpdater.UpdateService.VerifySha(self, "https://fixture.invalid/digest", reader, out detail), detail);
        text = InstallManifest.Sha256OfFile(self).ToUpperInvariant() + " *setup.exe";
        Check("sha-correct-accepted", CMOUpdater.UpdateService.VerifySha(self, "https://fixture.invalid/digest", reader, out detail), detail);
        text = "no digest";
        Check("sha-malformed-rejected", !CMOUpdater.UpdateService.VerifySha(self, "https://fixture.invalid/digest", reader, out detail), detail);
        Check("sha-fetch-failure-rejected", !CMOUpdater.UpdateService.VerifySha(self, "https://fixture.invalid/digest",
            delegate(string url, int timeout) { throw new IOException("synthetic read failure"); }, out detail), detail);
    }

    private static System.Diagnostics.Process Worker(string self, string name)
    {
        string exe = Path.Combine(root, "workers", name, name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.Copy(self, exe);
        System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo(exe, "--worker");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        return System.Diagnostics.Process.Start(start);
    }

    private static void StopOwned(System.Diagnostics.Process process)
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
        finally { process.Dispose(); }
    }

    private static void RollbackTests(string self, string bundle)
    {
        string game = Fixture("rollback");
        Isolate(Path.Combine(root, "rollback-user"));
        CmoSettings settings = new CmoSettings();
        settings.GamePath = game;
        settings.CheckIntervalHours = 3;
        settings.TrayOnClose = false;
        settings.Save();
        byte[] saved = File.ReadAllBytes(CmoPaths.SettingsFile);
        string cached = VersionStore.Cache(bundle);
        Check("rollback-caches-whole-synthetic-exe", cached != null && Same(File.ReadAllBytes(cached), File.ReadAllBytes(bundle)), "cache=" + cached);
        InstallManifest.Write(game, "9.9.9", "synthetic-newer", InstallManifest.HashEngineFiles(game));
        System.Diagnostics.Process command = null;
        System.Diagnostics.Process updater = null;
        int stops = 0;
        int waits = 0;
        try
        {
            command = Worker(self, "Command");
            updater = Worker(self, "CMO_Updater");
            Func<string, System.Diagnostics.Process[]> ownedOnly = delegate(string name)
            {
                List<System.Diagnostics.Process> found = new List<System.Diagnostics.Process>();
                if (name == "CMO_Updater") found.Add(System.Diagnostics.Process.GetCurrentProcess());
                System.Diagnostics.Process worker = name == "Command" ? command : updater;
                if (worker != null && !worker.HasExited) found.Add(System.Diagnostics.Process.GetProcessById(worker.Id));
                return found.ToArray(); // 只使用本测试创建的 PID，绝不 GetProcessesByName。
            };
            string error;
            bool ok = Rollback.Run(game, ReleaseConstants.Version, true, delegate
            {
                stops++;
                Rollback.KillProcesses(ownedOnly, delegate(int milliseconds) { if (milliseconds == 500) waits++; });
            }, out error);
            Check("rollback-caller-updater-survives-full-flow", ok && stops == 1
                && System.Diagnostics.Process.GetCurrentProcess().ProcessName == "CMO_Updater", error + " callerPID=" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Check("rollback-other-owned-game-updater-stopped", command.WaitForExit(3000) && updater.WaitForExit(3000),
                "owned worker PIDs=" + command.Id + "/" + updater.Id + " wait iterations=" + waits);
            CheckBundle("rollback", game, cached);
            Check("rollback-package-source-and-settings-preserved", InstallManifest.Read(game).Source == cached
                && Same(saved, File.ReadAllBytes(CmoPaths.SettingsFile)), "full EXE path; file settings unchanged");
        }
        finally { StopOwned(command); StopOwned(updater); }
        waits = 0;
        Rollback.KillProcesses(delegate(string name)
        {
            return name == "CMO_Updater" ? new System.Diagnostics.Process[] { System.Diagnostics.Process.GetCurrentProcess() }
                : new System.Diagnostics.Process[0];
        }, delegate(int milliseconds) { if (milliseconds == 500) waits++; });
        Check("self-only-updater-not-counted-in-wait", waits == 0, "wait iterations=" + waits);
        string noCache = Fixture("no-cache");
        InstallManifest.Write(noCache, "9.9.9", "original", InstallManifest.HashEngineFiles(noCache));
        byte[] old = File.ReadAllBytes(CmoPaths.ManifestPath(noCache));
        string reason;
        bool missing = Rollback.Run(noCache, "0.0.1", true, delegate { stops++; }, out reason);
        Check("rollback-no-cache-refused-before-process-stop", !missing && stops == 1
            && Same(old, File.ReadAllBytes(CmoPaths.ManifestPath(noCache))), reason);
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args.Length == 2 && args[0] == "--prepare") { Prepare(Path.GetFullPath(args[1])); return 0; }
            if (args.Length == 1 && args[0] == "--worker") { System.Threading.Thread.Sleep(30000); return 0; }
            if (args.Length >= 2 && args[0] == "--install")
            {
                string game = Path.GetFullPath(args[1]);
                if (File.ReadAllText(Path.Combine(game, ".cmo-integrity-fixture")) != FixtureMarker)
                    throw new InvalidOperationException("Not an explicitly marked test fixture");
                Isolate(Path.Combine(game, "test-only-user"));
                string error;
                bool ok = CMOLocalizerInstaller.InstallerCore.Install(game, delegate(string path) { }, out error);
                Console.WriteLine("Synthetic package CLI install=" + ok + " " + error);
                return ok ? 0 : 1;
            }
            if (args.Length != 4 || args[0] != "--tests")
                throw new ArgumentException("--prepare <inputs> | --tests <fixtures> <rollback-bundle> <missing-bundle>");
            root = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root);
            Isolate(Path.Combine(root, "user"));
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            InstallAndRestore(self);
            IntegrityFailures(Path.GetFullPath(args[3]));
            DigestTests(self);
            RollbackTests(self, Path.GetFullPath(args[2]));
        }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL integrity-harness -- " + ex); }
        Console.WriteLine("CMOIntegrityTest: checks=" + checks + " failures=" + failures + "; synthetic only, no real game/registry/process enumeration");
        return failures == 0 ? 0 : 1;
    }
}
#else
internal static class CMOSharedSelfTest
{
    private static int failures;
    private static string root;

    private static void Check(string name, bool ok, string reason)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (ok ? "" : " -- " + reason));
        if (!ok) failures++;
    }

    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "cmoshared-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        CmoPaths.RoamingDirOverride = Path.Combine(root, "roaming");
        CmoPaths.LocalDirOverride = Path.Combine(root, "local");
        CmoPaths.RegistryPathOverride = @"Software\CMOChinese__selftest";
        CmoPaths.EnsureDir(CmoPaths.RoamingDirOverride);
        CmoPaths.EnsureDir(CmoPaths.LocalDirOverride);

        try
        {
            SettingsRoundTrip();
            SettingsCorruptedFile();
            SettingsRegistryMigration();
            ManifestRoundTrip();
            ManifestModifiedAndClear();
            ManifestAdopt();
            PackageCachePruneAndPrevious();
            PackageVersionFromRealPackage();
            ReleaseGateTruthTable();
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine("FAIL selftest-harness -- " + ex);
        }
        finally
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(CmoPaths.RegistryPathOverride, false); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "CMOSharedTest: ALL PASS"
            : "CMOSharedTest: " + failures + " FAILED");
        return failures;
    }

    // 1. 设置原子写往返（含首次写：目标不存在时必须走 File.Move 分支）
    private static void SettingsRoundTrip()
    {
        CmoSettings s = CmoSettings.Load();
        Check("settings-defaults", s.CheckIntervalHours == 6 && s.Mirror == "https://ghproxy.net/" && s.SnapshotKeep == 2,
            "unexpected defaults: interval=" + s.CheckIntervalHours + " mirror=" + s.Mirror);
        // 没有注册表值也没有文件时 Load() 不得凭空写文件（避免污染新用户）
        Check("settings-no-write-on-empty-load", !File.Exists(CmoPaths.SettingsFile),
            "Load() wrote settings.json without any source");

        s.CheckIntervalHours = 3;
        s.NotifyMode = 0;
        s.TrayOnClose = false;
        s.GamePath = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        s.Save();   // 首次写：目标不存在 → File.Move 分支
        Check("settings-file-created", File.Exists(CmoPaths.SettingsFile), "settings.json not created at " + CmoPaths.SettingsFile);
        Check("settings-no-tmp-left", !File.Exists(CmoPaths.SettingsFile + ".tmp"), "leftover .tmp file");

        CmoSettings back = CmoSettings.Load();
        Check("settings-round-trip",
            back.CheckIntervalHours == 3 && back.NotifyMode == 0 && !back.TrayOnClose && back.GamePath == s.GamePath,
            "read back: interval=" + back.CheckIntervalHours + " notify=" + back.NotifyMode
            + " tray=" + back.TrayOnClose + " game=" + back.GamePath);

        back.CheckIntervalHours = 5;
        back.Save();   // 第二次写：目标已存在 → File.Replace 分支
        Check("settings-rewrite-existing", CmoSettings.Load().CheckIntervalHours == 5,
            "File.Replace 分支未能覆盖已有设置文件");
    }

    // 2. 损坏文件 → 备份为 .bad + 默认值
    private static void SettingsCorruptedFile()
    {
        string path = CmoPaths.SettingsFile;
        File.WriteAllText(path, "{ this is not json ", new UTF8Encoding(false));
        CmoSettings s = CmoSettings.Load();
        Check("settings-corrupt-fallback", s.CheckIntervalHours == 6 && s.CheckIntervalHours != 3,
            "corrupted file was not replaced by defaults (interval=" + s.CheckIntervalHours + ")");
        Check("settings-corrupt-backup", File.Exists(path + ".bad"), "expected backup at " + path + ".bad");
    }

    // 3. 旧注册表值一次性迁移
    private static void SettingsRegistryMigration()
    {
        try { File.Delete(CmoPaths.SettingsFile); } catch { }
        using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(CmoPaths.RegistryPathOverride))
        {
            k.SetValue("AutoCheck", 0, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("CheckIntervalHours", 12, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NotifyMode", 2, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("Mirror", "https://mirror.example/", Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("GamePath", @"D:\games\CMO", Microsoft.Win32.RegistryValueKind.String);
        }
        CmoSettings s = CmoSettings.Load();
        Check("settings-registry-migration",
            !s.AutoCheck && s.CheckIntervalHours == 12 && s.NotifyMode == 2
            && s.Mirror == "https://mirror.example/" && s.GamePath == @"D:\games\CMO",
            "migrated: auto=" + s.AutoCheck + " interval=" + s.CheckIntervalHours + " notify=" + s.NotifyMode
            + " mirror=" + s.Mirror + " game=" + s.GamePath);
        Check("settings-migration-persisted", File.Exists(CmoPaths.SettingsFile), "migration did not write settings.json");
    }

    // 4. 清单往返
    private static void ManifestRoundTrip()
    {
        string game = Game("round");
        WriteEngineFiles(game, "dll-A", "json-A");
        bool w = InstallManifest.Write(game, "4.1.6", "CMO_Chinese_Setup_v4.1.6.exe", InstallManifest.HashEngineFiles(game));
        InstallManifest m = InstallManifest.Read(game);
        Check("manifest-write-read", w && m != null && m.Version == "4.1.6"
            && m.Files.Count == 2 && m.Files.ContainsKey("CMOLocalizer.dll"),
            "manifest=" + (m == null ? "null" : m.Version + " files=" + m.Files.Count));
        Check("manifest-installed-at", m != null && m.InstalledAt.Length > 10, "installed_at missing");
        string detail;
        Check("manifest-verify-ok", InstallManifest.Verify(game, out detail) == ManifestState.Ok, detail);
    }

    // 5. 文件被改写 → Modified；Clear → NotInstalled
    private static void ManifestModifiedAndClear()
    {
        string game = Game("modified");
        WriteEngineFiles(game, "dll-A", "json-A");
        InstallManifest.Write(game, "4.1.6", "pkg", InstallManifest.HashEngineFiles(game));
        File.WriteAllText(Path.Combine(game, "CMOLocalizer.dll"), "dll-CHANGED");
        string detail;
        Check("manifest-verify-modified",
            InstallManifest.Verify(game, out detail) == ManifestState.Modified && detail.Contains("CMOLocalizer.dll"),
            "expected Modified(CMOLocalizer.dll), got " + detail);
        InstallManifest.Clear(game);
        Check("manifest-clear",
            InstallManifest.Verify(game, out detail) == ManifestState.NotInstalled && InstallManifest.Read(game) == null,
            "after Clear: " + detail);
    }

    // 6. adopt 自愈（存量 4.1.x / 绿色包用户）
    private static void ManifestAdopt()
    {
        string game = Game("adopt");
        WriteEngineFiles(game, "dll-A", "json-A");
        bool ok = InstallManifest.Adopt(game, "4.1.5");
        InstallManifest m = InstallManifest.Read(game);
        Check("manifest-adopt", ok && m != null && m.Version == "4.1.5" && m.Source == "adopted",
            "adopt=" + ok + " manifest=" + (m == null ? "null" : m.Version + "/" + m.Source));

        string empty = Game("adopt-empty");
        Check("manifest-adopt-requires-engine", !InstallManifest.Adopt(empty, "4.1.5"),
            "adopt must fail when engine files are absent");
    }

    // 7. 安装包缓存：Prune 保留最近 N 个、Previous 取正确上一版本
    private static void PackageCachePruneAndPrevious()
    {
        string src = Path.Combine(root, "src-pkgs");
        CmoPaths.EnsureDir(src);
        foreach (string v in new string[] { "4.1.4", "4.1.5", "4.1.6" })
        {
            File.WriteAllText(Path.Combine(src, "CMO_Chinese_Setup_v" + v + ".exe"), "package-" + v);
            VersionStore.Cache(Path.Combine(src, "CMO_Chinese_Setup_v" + v + ".exe"));
        }
        List<string> all = VersionStore.CachedVersions();
        Check("cache-all-versions", all.Count == 3 && all[0] == "4.1.6" && all[2] == "4.1.4",
            "cached=[" + string.Join(",", all.ToArray()) + "]");

        VersionStore.Prune(2);
        List<string> kept = VersionStore.CachedVersions();
        Check("cache-prune-keeps-latest", kept.Count == 2 && kept[0] == "4.1.6" && kept[1] == "4.1.5",
            "after prune(2)=[" + string.Join(",", kept.ToArray()) + "]");
        Check("cache-previous", VersionStore.Previous("4.1.6") == "4.1.5" && VersionStore.Previous("4.1.4") == null
            && VersionStore.Previous("") == "4.1.6",
            "previous(4.1.6)=" + VersionStore.Previous("4.1.6") + " previous(4.1.4)=" + VersionStore.Previous("4.1.4"));
        Check("cache-package-path", VersionStore.CachedPackage("4.1.5") != null && VersionStore.CachedPackage("9.9.9") == null,
            "CachedPackage lookup broken");
    }

    // 7.5 真实安装包的版本戳：必须来自内嵌 CMOLocalizer.json（文件名不可信）
    private static void PackageVersionFromRealPackage()
    {
        string buildDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "build"));
        string[] pkgs = Directory.Exists(buildDir)
            ? Directory.GetFiles(buildDir, "CMO_Chinese_Setup_v*.exe")
            : new string[0];
        if (pkgs.Length == 0)
        {
            Check("pkgversion-real-package", false, "no build/CMO_Chinese_Setup_v*.exe found -- run bash scripts/build.sh first");
            return;
        }
        // 本 API 的不变量：读出的必须是安装包内嵌词典的 _meta.version（资产自带版本戳）。
        // “词典版本戳 == VERSION”由 scripts/verify_all.py 的版本门禁负责（T3）。
        string dictJson = Path.Combine(buildDir, "..", "dict", "CMOLocalizer.json");
        if (File.Exists(dictJson))
        {
            using (FileStream fs = File.OpenRead(dictJson))
            {
                byte[] head = new byte[1024];
                int n = fs.Read(head, 0, head.Length);
                Match dm = Regex.Match(Encoding.UTF8.GetString(head, 0, n), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (dm.Success) expectedDict = dm.Groups[1].Value.Trim();
            }
        }
        // 取“刚构建的那个”（按修改时间最新）
        Array.Sort(pkgs, delegate(string a, string b)
        {
            return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a));
        });
        string chosen = pkgs[0];
        string expected = VersionOfExpectedName();
        Check("pkgversion-real-package", expected.Length > 0
            && string.Equals(VersionStore.PackageVersion(chosen), expected, StringComparison.OrdinalIgnoreCase),
            "PackageVersion(" + Path.GetFileName(chosen) + ")=" + VersionStore.PackageVersion(chosen) + " but VERSION=" + expected);

        // 文件名无版本号时，回退用内嵌词典的 _meta.version
        string stripped = Path.Combine(root, "stripped");
        CmoPaths.EnsureDir(stripped);
        string noNameVer = Path.Combine(stripped, "CMO_Chinese_Setup_custom.exe");
        File.Copy(chosen, noNameVer, true);
        Check("pkgversion-content-fallback",
            expectedDict.Length > 0 && string.Equals(VersionStore.PackageVersion(noNameVer), expectedDict, StringComparison.OrdinalIgnoreCase),
            "content fallback=" + VersionStore.PackageVersion(noNameVer) + " but dict _meta.version=" + expectedDict);

        // 文件名带假版本号 → 以文件名为准（发布资产以 VERSION 命名）
        string fakeDir = Path.Combine(root, "misnamed");
        CmoPaths.EnsureDir(fakeDir);
        string misnamed = Path.Combine(fakeDir, "CMO_Chinese_Setup_v9.9.9.exe");
        File.Copy(chosen, misnamed, true);
        Check("pkgversion-filename-wins",
            VersionStore.PackageVersion(misnamed) == "9.9.9" && VersionStore.VersionOfPackage(misnamed) == "9.9.9",
            "name version=" + VersionStore.PackageVersion(misnamed));
    }

    private static string expectedDict = "";

    private static string VersionOfExpectedName()
    {
        string vf = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "VERSION");
        return File.Exists(vf) ? File.ReadAllText(vf).Trim() : "";
    }

    // 8. 版本门控真值表（台账 R16 的负向用例基础）
    private static void ReleaseGateTruthTable()
    {
        bool newer = ReleaseGate.ShouldOfferRollback("4.1.5", "4.1.6", false);
        bool equal = ReleaseGate.ShouldOfferRollback("4.1.5", "4.1.5", true);
        bool olderNoCache = ReleaseGate.ShouldOfferRollback("4.1.6", "4.1.5", false);
        bool olderWithCache = ReleaseGate.ShouldOfferRollback("4.1.6", "4.1.5", true);
        Check("gate-truth-table", !newer && !equal && !olderNoCache && olderWithCache,
            "newer=" + newer + " equal=" + equal + " olderNoCache=" + olderNoCache + " olderWithCache=" + olderWithCache);
        Check("gate-version-compare",
            ReleaseGate.Compare("v4.1.10", "4.1.9") > 0 && ReleaseGate.Compare("4.1.5", "v4.1.5") == 0
            && ReleaseGate.Compare("4.1.6-beta", "4.1.6") == 0,
            "Compare(4.1.10,4.1.9)=" + ReleaseGate.Compare("v4.1.10", "4.1.9")
            + " Compare(4.1.6-beta,4.1.6)=" + ReleaseGate.Compare("4.1.6-beta", "4.1.6"));
        Check("gate-empty-inputs", !ReleaseGate.ShouldOfferRollback("", "4.1.5", true)
            && !ReleaseGate.ShouldOfferRollback("4.1.6", "", true),
            "empty inputs must not offer rollback");

        // 解压包含性校验：安装器是提权运行的，越界条目必须被拦下（不能靠 `..\..\` 往游戏目录外写）
        string insideRoot = Game("zip-root");
        string d1, d2, d3;
        Check("zip-inside-normal", CmoPaths.TryResolveInside(insideRoot, "MenuItem/foo.png", out d1)
            && Path.GetFullPath(d1).StartsWith(Path.GetFullPath(insideRoot), StringComparison.OrdinalIgnoreCase),
            "normal entry should resolve inside root");
        Check("zip-inside-nested-ok",
            CmoPaths.TryResolveInside(insideRoot, "a/b/c/d.bin", out d2) && d2 != null,
            "nested in-root entry should be accepted");
        Check("zip-escape-dotdot", !CmoPaths.TryResolveInside(insideRoot, @"..\..\escaped.txt", out d3),
            "dotdot escape must be rejected");
        Check("zip-escape-mixed", !CmoPaths.TryResolveInside(insideRoot, "a/../../escaped.txt", out d3),
            "mixed dotdot escape must be rejected");
        Check("zip-escape-absolute", !CmoPaths.TryResolveInside(insideRoot, @"C:\Windows\evil.dll", out d3)
            && !CmoPaths.TryResolveInside(insideRoot, "/etc/passwd", out d3),
            "absolute paths must be rejected");
        Check("zip-escape-empty", !CmoPaths.TryResolveInside(insideRoot, "", out d3)
            && !CmoPaths.TryResolveInside("", "a.txt", out d3),
            "empty root/entry must be rejected");
    }

    private static string Game(string tag)
    {
        string p = Path.Combine(root, "games", tag);
        CmoPaths.EnsureDir(p);
        File.WriteAllText(Path.Combine(p, "Command.exe"), "stub");
        return p;
    }

    private static void WriteEngineFiles(string game, string dll, string json)
    {
        File.WriteAllText(Path.Combine(game, "CMOLocalizer.dll"), dll);
        File.WriteAllText(Path.Combine(game, "CMOLocalizer.json"), json);
    }
}
#endif
