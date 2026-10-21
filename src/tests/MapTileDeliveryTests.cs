using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

// Only isolated delivery/restore helpers are invoked. Never run Install/Uninstall.
public static class MapTileDeliveryTests
{
    static int checks;
    static string root;
    static Type installer;
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
    static string PackPath(string dir) { return Path.Combine(dir, "ChineseMapTiles.dat"); }
    static byte[] ValidPack()
    {
        byte[] png;
        using (var image = new Bitmap(256, 256))
        using (var g = Graphics.FromImage(image))
        using (var stream = new MemoryStream()) { g.Clear(Color.Red); image.Save(stream, ImageFormat.Png); png = stream.ToArray(); }
        byte[] payload;
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream)) { w.Write(1); w.Write(3); w.Write(4); w.Write(4); w.Write(1); w.Write(png.Length); w.Write(png); payload = stream.ToArray(); }
        using (var stream = new MemoryStream())
        using (var w = new BinaryWriter(stream))
        using (var sha = SHA256.Create()) {
            w.Write(Encoding.ASCII.GetBytes("CMOTIL01"));
            w.Write(BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant());
            w.Write(payload); return stream.ToArray();
        }
    }
    static string Dir(string name) { string dir = Path.Combine(root, name); Directory.CreateDirectory(dir); return dir; }
    static bool Deploy(string game, string source, out string error)
    {
        object[] args = { game, source, null };
        bool ok = (bool)installer.GetMethod("DeployMapTilePack", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        error = (string)args[2]; return ok;
    }
    static bool Equal(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
    static void Restore(string game) { installer.GetMethod("RestoreMapLabelTiles", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { game }); }
    public static int Main(string[] args)
    {
        try {
            if (args[0] == "--write-pack") { File.WriteAllBytes(args[1], ValidPack()); return 0; }
            root = Path.GetFullPath(args[2]); Directory.CreateDirectory(root);
            var assembly = Assembly.LoadFrom(args[0]); installer = assembly.GetType("CMOLocalizerInstaller.InstallerCore", true);
            Check(installer.GetMethod("DeployMapTilePack", BindingFlags.NonPublic | BindingFlags.Static) != null, "isolated delivery helper exists");
            byte[] valid = ValidPack(); string error;
            string source = Dir("source"), game = Dir("game");
            File.WriteAllBytes(PackPath(source), valid);
            Check(Deploy(game, source, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "disk pack reaches game root exactly");
            Check(Deploy(game, game, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "same path validates without self-copy damage");
            File.WriteAllBytes(PackPath(source), new byte[] { 1, 2, 3 });
            Check(!Deploy(game, source, out error) && error.Length > 0 && Equal(valid, File.ReadAllBytes(PackPath(game))), "bad disk candidate fails and preserves old pack");
            string patch = Dir("patch-source"), patchFiles = Path.Combine(patch, "patch_files"); Directory.CreateDirectory(patchFiles);
            File.WriteAllBytes(PackPath(patchFiles), valid);
            Check(Deploy(game, patch, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "existing patch_files layout supported");
            File.WriteAllBytes(PackPath(patch), new byte[] { 4 });
            Check(!Deploy(game, patch, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "bad direct pack is not silently replaced by fallback");
            string missing = Dir("missing"), absent = Dir("absent");
            string mode = args[1];
            bool embedded = Deploy(absent, missing, out error);
            if (mode == "valid") Check(embedded && Equal(valid, File.ReadAllBytes(PackPath(absent))), "exact MAP_TILES embedded resource deployed");
            else if (mode == "bad") {
                Check(!embedded && !File.Exists(PackPath(absent)) && error.Length > 0, "bad embedded pack fails before destination publication");
                Check(!Deploy(game, missing, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "bad embedded pack preserves old destination");
            }
            else Check(embedded && !File.Exists(PackPath(absent)), "resource-free legacy fixture is optional");
            File.WriteAllBytes(PackPath(source), valid);
            using (var held = File.Open(PackPath(game), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!Deploy(game, source, out error) && Equal(valid, File.ReadAllBytes(PackPath(game))), "blocked atomic publication reports failure and preserves destination");
            Check(Directory.GetFiles(game, "*.cmo_new*").Length == 0, "failed delivery cleans staging file");
            File.WriteAllBytes(PackPath(source), new byte[] { 8 });
            Check(!Deploy(source, source, out error), "same-path corrupt candidate is rejected");
            Type manifest = assembly.GetType("CMOShared.InstallManifest", true);
            string hashGame = Dir("hash-game"); File.WriteAllText(Path.Combine(hashGame,"CMOLocalizer.dll"),"dll"); File.WriteAllText(Path.Combine(hashGame,"CMOLocalizer.json"),"{}");
            var hashes = (IDictionary)manifest.GetMethod("HashEngineFiles").Invoke(null,new object[]{hashGame});
            Check(hashes.Count == 2 && !hashes.Contains("ChineseMapTiles.dat"), "legacy two-file hash contract preserved");
            File.WriteAllBytes(PackPath(hashGame),valid);
            hashes = (IDictionary)manifest.GetMethod("HashEngineFiles").Invoke(null,new object[]{hashGame});
            string expected = (string)manifest.GetMethod("Sha256OfFile").Invoke(null,new object[]{PackPath(hashGame)});
            Check(hashes.Count == 3 && (string)hashes["ChineseMapTiles.dat"] == expected, "present pack participates in install manifest hash");
            string cache = Path.Combine(game,@"WW\Cache\Stamen Terrain - Labels"), backup = cache + ".orig";
            Directory.CreateDirectory(cache); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(cache,"localized.txt"),"Chinese"); File.WriteAllText(Path.Combine(backup,"original.txt"),"original");
            Restore(game);
            Check(!File.Exists(PackPath(game)) && File.Exists(Path.Combine(cache,"original.txt")) && !File.Exists(Path.Combine(cache,"localized.txt")), "restore removes pack before restoring original Labels");
            File.WriteAllBytes(PackPath(game),valid); Restore(game);
            Check(!File.Exists(PackPath(game)), "pack removed even without Labels backup");
            File.WriteAllBytes(PackPath(game),valid); Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup,"sentinel"),"old");
            using (var held = File.Open(PackPath(game), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                bool failed = false; try { Restore(game); } catch (TargetInvocationException e) { failed = e.InnerException is IOException; }
                Check(failed && File.Exists(Path.Combine(backup,"sentinel")), "pack removal failure stops restore and propagates");
            }
            Console.WriteLine("PASS total=" + checks); return 0;
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
