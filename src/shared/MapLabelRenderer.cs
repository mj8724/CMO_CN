using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CMOShared
{
    public sealed class MapLabelPlace
    {
        public string Name;
        public float Lat;
        public float Lon;
        public int Level;
        public int MinZoom = 0;
        public int MaxZoom = 30;
        public int Priority;
        public bool IsLegacy = true;
    }

    // This payload fingerprint detects corruption and versions caches; it does not prove original-map coverage.
    public sealed class MapLabelCatalog
    {
        private const string NameSeparators = " !\"#%&'()*,-./:;?@[\\]_{}·、。・‧…‐‑‒–—―‘’“”（）［］【】《》〈〉「」『』〔〕，；：？！－．";

        public string Fingerprint { get; private set; }
        public IList<MapLabelPlace> Places { get; private set; }

        private MapLabelCatalog(string fingerprint, List<MapLabelPlace> places)
        {
            Fingerprint = fingerprint;
            Places = places.AsReadOnly();
        }

        public static MapLabelCatalog Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "CMOMAP01")
                    throw new InvalidDataException("Unknown map catalog format.");
                string fingerprint = reader.ReadString();
                int payloadOffset = checked((int)stream.Position);
                string actual;
                using (var hash = SHA256.Create())
                    actual = BitConverter.ToString(hash.ComputeHash(bytes, payloadOffset, bytes.Length - payloadOffset))
                        .Replace("-", "").ToLowerInvariant();
                if (fingerprint != actual)
                    throw new InvalidDataException("Map catalog payload SHA256 differs from its fingerprint.");
                int count = reader.ReadInt32();
                if (count <= 0 || count > (bytes.Length - stream.Position) / 25)
                    throw new InvalidDataException("Invalid map catalog record count.");
                var places = new List<MapLabelPlace>(count);
                for (int i = 0; i < count; i++)
                {
                    var place = new MapLabelPlace
                    {
                        Name = reader.ReadString(), Lat = reader.ReadSingle(), Lon = reader.ReadSingle(),
                        MinZoom = reader.ReadInt32(), MaxZoom = reader.ReadInt32(),
                        Priority = reader.ReadInt32(), Level = reader.ReadInt32(), IsLegacy = false
                    };
                    if (!ChineseName(place.Name) || float.IsNaN(place.Lat) || float.IsInfinity(place.Lat)
                        || float.IsNaN(place.Lon) || float.IsInfinity(place.Lon)
                        || Math.Abs(place.Lat) > 85.05113f || Math.Abs(place.Lon) > 180f
                        || place.MinZoom < 3 || place.MinZoom > place.MaxZoom || place.MaxZoom > 10
                        || place.Priority < 0 || place.Priority > 1000000 || (place.Level != 0 && place.Level != 1))
                        throw new InvalidDataException("Invalid Chinese map catalog record at index " + i + ".");
                    places.Add(place);
                }
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("Trailing map catalog bytes.");
                return new MapLabelCatalog(fingerprint, places);
            }
        }

        internal static bool ChineseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Trim() != name) return false;
            bool hasHan = false;
            for (int i = 0; i < name.Length; i++)
            {
                int code = char.ConvertToUtf32(name, i);
                // Assigned Han ranges are fixed to Unicode 15, matching the Python producer.
                // The CLR Unicode tables predate some of these characters.
                bool han = (code >= 0x3400 && code <= 0x4DBF) || (code >= 0x4E00 && code <= 0x9FFF)
                    || (code >= 0xF900 && code <= 0xFA6D) || (code >= 0xFA70 && code <= 0xFAD9)
                    || (code >= 0x20000 && code <= 0x2A6DF) || (code >= 0x2A700 && code <= 0x2B739)
                    || (code >= 0x2B740 && code <= 0x2B81D) || (code >= 0x2B820 && code <= 0x2CEA1)
                    || (code >= 0x2CEB0 && code <= 0x2EBE0) || (code >= 0x2F800 && code <= 0x2FA1D)
                    || (code >= 0x30000 && code <= 0x3134A) || (code >= 0x31350 && code <= 0x323AF);
                if (han) hasHan = true;
                // Digits and separators are also fixed, rather than classified by either runtime.
                else if (!((code >= 0x30 && code <= 0x39) || (code >= 0xFF10 && code <= 0xFF19)
                    || code == 0x3007 || (code >= 0x3021 && code <= 0x3029)
                    || (code <= 0xFFFF && NameSeparators.IndexOf((char)code) >= 0))) return false;
                if (code > 0xFFFF) i++;
            }
            return hasHan;
        }
    }

    // A pack certifies exact tile bytes, not language or geographic completeness.
    public sealed class MapLabelTilePack
    {
        private sealed class Tile
        {
            public int Z, X, Y, LabelCount;
            public byte[] Png;
        }

        private sealed class LegacyRule
        {
            public int Z;
            public MapLabelPlace Place;
        }

        private readonly Dictionary<string, Tile> tiles = new Dictionary<string, Tile>();
        private readonly HashSet<string> legacyKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<LegacyRule> legacyRules = new List<LegacyRule>();
        public string Fingerprint { get; private set; }

        private static string Key(int z, int x, int y)
        {
            return z + "/" + x + "/" + y;
        }

        public static MapLabelTilePack Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                string format = Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (format != "CMOTIL01" && format != "CMOTIL02")
                    throw new InvalidDataException("Unknown Chinese map tilepack format.");
                string fingerprint = reader.ReadString();
                int offset = checked((int)stream.Position);
                string actual;
                using (var hash = SHA256.Create())
                    actual = BitConverter.ToString(hash.ComputeHash(bytes, offset, bytes.Length - offset))
                        .Replace("-", "").ToLowerInvariant();
                if (fingerprint != actual)
                    throw new InvalidDataException("Chinese map tilepack payload SHA256 mismatch.");
                int count = reader.ReadInt32();
                if (count <= 0 || count > (stream.Length - stream.Position) / 21)
                    throw new InvalidDataException("Invalid Chinese map tilepack count.");
                var pack = new MapLabelTilePack { Fingerprint = fingerprint };
                for (int i = 0; i < count; i++)
                {
                    var tile = new Tile { Z = reader.ReadInt32(), X = reader.ReadInt32(),
                        Y = reader.ReadInt32(), LabelCount = reader.ReadInt32() };
                    int length = reader.ReadInt32();
                    if (tile.Z < 3 || tile.Z > 10 || tile.X < 0 || tile.X >= (1 << tile.Z)
                        || tile.Y < 0 || tile.Y >= (1 << tile.Z) || tile.LabelCount < 0
                        || length <= 0 || length > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid Chinese map tilepack record at index " + i + ".");
                    string key = Key(tile.Z, tile.X, tile.Y);
                    if (pack.tiles.ContainsKey(key)) throw new InvalidDataException("Duplicate Chinese map tile: " + key);
                    tile.Png = reader.ReadBytes(length);
                    // Verify PNG identity as well as decoding; Image.FromStream also accepts JPEG/BMP.
                    byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
                    if (length < signature.Length || !signature.SequenceEqual(tile.Png.Take(signature.Length)))
                        throw new InvalidDataException("Chinese map tile is not PNG: " + key);
                    using (var png = new MemoryStream(tile.Png, false))
                    using (var image = Image.FromStream(png, true, true))
                    {
                        if (image.Width != 256 || image.Height != 256)
                            throw new InvalidDataException("Chinese map tile must be 256x256: " + key);
                        using (var decoded = new Bitmap(image)) { decoded.GetPixel(255, 255); }
                    }
                    pack.tiles.Add(key, tile);
                }
                if (format == "CMOTIL02")
                {
                    int ruleCount = reader.ReadInt32();
                    if (ruleCount <= 0 || ruleCount > (stream.Length - stream.Position) / 18)
                        throw new InvalidDataException("Invalid legacy suppression count.");
                    var zooms = new HashSet<int>(pack.tiles.Values.Select(t => t.Z));
                    for (int i = 0; i < ruleCount; i++)
                    {
                        int z = reader.ReadInt32();
                        var place = new MapLabelPlace { Name = reader.ReadString(), Lat = reader.ReadSingle(),
                            Lon = reader.ReadSingle(), Level = reader.ReadInt32() };
                        if (!zooms.Contains(z) || !MapLabelCatalog.ChineseName(place.Name)
                            || float.IsNaN(place.Lat) || float.IsInfinity(place.Lat) || Math.Abs(place.Lat) > 90f
                            || float.IsNaN(place.Lon) || float.IsInfinity(place.Lon) || Math.Abs(place.Lon) > 180f
                            || (place.Level != 0 && place.Level != 1))
                            throw new InvalidDataException("Invalid legacy suppression at index " + i + ".");
                        if (!pack.legacyKeys.Add(LegacyKey(place, z)))
                            throw new InvalidDataException("Duplicate legacy suppression at index " + i + ".");
                        pack.legacyRules.Add(new LegacyRule { Z = z, Place = place });
                    }
                }
                if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Chinese map tilepack bytes.");
                return pack;
            }
        }

        private static string LegacyKey(MapLabelPlace place, int z)
        {
            return z + "/" + place.Level + "/" + BitConverter.ToInt32(BitConverter.GetBytes(place.Lat), 0)
                + "/" + BitConverter.ToInt32(BitConverter.GetBytes(place.Lon), 0) + "/" + place.Name;
        }

        public bool IsLegacySuppressed(MapLabelPlace place, int z)
        {
            return place.IsLegacy && legacyKeys.Contains(LegacyKey(place, z));
        }

        public bool TryGetTile(int z, int x, int y, out byte[] png, out int labelCount)
        {
            Tile tile;
            if (tiles.TryGetValue(Key(z, x, y), out tile))
            {
                png = (byte[])tile.Png.Clone();
                labelCount = tile.LabelCount;
                return true;
            }
            png = null;
            labelCount = 0;
            return false;
        }

        internal static string CacheDirectory(string gameDir)
        {
            return Path.Combine(Path.GetFullPath(gameDir), @"WW\Cache\Stamen Terrain - Labels");
        }

        internal static void AssertPlainPath(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse points are not allowed in map cache paths: " + current);
        }

        // Traverse one directory at a time: never recurse through an unchecked reparse point.
        internal static List<string> CacheFiles(string directory)
        {
            AssertPlainPath(directory);
            var files = new List<string>();
            if (!Directory.Exists(directory)) return files;
            foreach (string file in Directory.GetFiles(directory))
            {
                AssertPlainPath(file);
                files.Add(file);
            }
            foreach (string child in Directory.GetDirectories(directory)) files.AddRange(CacheFiles(child));
            return files;
        }

        private static void CopyCache(string source, string destination)
        {
            AssertPlainPath(source);
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                AssertPlainPath(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
            }
            foreach (string child in Directory.GetDirectories(source))
                CopyCache(child, Path.Combine(destination, Path.GetFileName(child)));
        }

        internal static void EnsureCacheBackup(string gameDir)
        {
            string cache = CacheDirectory(gameDir);
            string backup = cache + ".orig";
            AssertPlainPath(cache);
            AssertPlainPath(backup);
            if (Directory.Exists(backup)) return;
            string staging = backup + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(Path.GetDirectoryName(cache));
            try
            {
                if (Directory.Exists(cache)) CopyCache(cache, staging);
                else Directory.CreateDirectory(staging);
                Directory.Move(staging, backup);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    foreach (string file in CacheFiles(staging)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(staging, true);
                }
            }
        }

        internal static bool WriteCacheBytes(string path, byte[] bytes, bool forceWrite = false)
        {
            AssertPlainPath(path);
            if (!forceWrite && File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }

        // Return actual PNG writes; a marker alone never makes a missing/damaged tile trustworthy.
        public int ApplyToCache(string gameDir)
        {
            return ApplyCache(gameDir, null, true);
        }

        public int ApplyToCacheWithLegacy(string gameDir, IList<MapLabelPlace> places)
        {
            if (places == null) throw new ArgumentNullException("places");
            return ApplyCache(gameDir, places, true);
        }

        // The installer also redraws fallback tiles before declaring the refresh complete.
        internal int PrepareCacheRefresh(string gameDir)
        {
            return ApplyCache(gameDir, null, false);
        }

        internal void CompleteCacheRefresh(string gameDir)
        {
            WriteCacheBytes(Path.Combine(CacheDirectory(gameDir), ".chinese-map-tiles.sha256"), Encoding.ASCII.GetBytes(Fingerprint + "\n"));
        }

        private int ApplyCache(string gameDir, IList<MapLabelPlace> places, bool publishMarker)
        {
            string cache = CacheDirectory(gameDir);
            CacheFiles(cache);
            EnsureCacheBackup(gameDir);
            int written = 0;
            foreach (Tile tile in tiles.Values)
            {
                string path = Path.Combine(cache, tile.Z.ToString(), tile.X.ToString(), tile.Y + ".png");
                if (WriteCacheBytes(path, tile.Png)) written++;
            }
            if (places != null)
            {
                var refreshed = new HashSet<string>(StringComparer.Ordinal);
                foreach (LegacyRule rule in legacyRules)
                {
                    if (!places.Any(p => p.IsLegacy && LegacyKey(p, rule.Z) == LegacyKey(rule.Place, rule.Z))) continue;
                    int n = 1 << rule.Z;
                    double latitude = Math.Max(-85.05112878, Math.Min(85.05112878, rule.Place.Lat)) * Math.PI / 180.0;
                    int x = Math.Max(0, Math.Min(n - 1, (int)Math.Floor((rule.Place.Lon + 180.0) / 360.0 * n)));
                    int y = Math.Max(0, Math.Min(n - 1, (int)Math.Floor((1.0 - Math.Log(Math.Tan(latitude)
                        + 1.0 / Math.Cos(latitude)) / Math.PI) / 2.0 * n)));
                    // The legacy renderer includes both edges: a boundary point can occur in four tiles.
                    for (int tx = Math.Max(0, x - 1); tx <= Math.Min(n - 1, x + 1); tx++)
                    for (int ty = Math.Max(0, y - 1); ty <= Math.Min(n - 1, y + 1); ty++)
                    {
                        if (!MapLabelRenderer.TileContains(rule.Place, tx / (double)n * 360.0 - 180.0,
                            (tx + 1) / (double)n * 360.0 - 180.0, MapLabelRenderer.TileLatitude(ty + 1, n),
                            MapLabelRenderer.TileLatitude(ty, n))) continue;
                        string key = Key(rule.Z, tx, ty);
                        string path = Path.Combine(cache, rule.Z.ToString(), tx.ToString(), ty + ".png");
                        AssertPlainPath(path);
                        if (tiles.ContainsKey(key) || !File.Exists(path) || !refreshed.Add(key)) continue;
                        int candidates, rendered;
                        byte[] png = MapLabelRenderer.RenderTileBytes(places, this, rule.Z, tx, ty, out candidates, out rendered);
                        if (WriteCacheBytes(path, png)) written++;
                    }
                }
            }
            if (publishMarker) CompleteCacheRefresh(gameDir);
            return written;
        }
    }

    // Both cached and runtime labels use the same pixels, ordering and collision geometry.
    public static class MapLabelRenderer
    {
        private const int TileSize = 256;
        private const float EdgeMargin = 2f;

        public static byte[] RenderTileBytes(IList<MapLabelPlace> places, MapLabelTilePack pack,
            int z, int x, int y, out int candidates, out int rendered)
        {
            byte[] png;
            if (pack != null && pack.TryGetTile(z, x, y, out png, out rendered))
            {
                candidates = rendered;
                return png;
            }
            if (pack != null && places != null) places = places.Where(p => !pack.IsLegacySuppressed(p, z)).ToList();
            using (Bitmap bitmap = RenderTile(places, z, x, y, out candidates, out rendered))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered)
        {
            return RenderTile(places, z, x, y, out candidates, out rendered, 14f);
        }

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered, float cityEmPixels)
        {
            if (places == null) throw new ArgumentNullException("places");
            if (z < 0 || z > 30) throw new ArgumentOutOfRangeException("z");
            if (x < 0 || x >= (1 << z)) throw new ArgumentOutOfRangeException("x");
            if (y < 0 || y >= (1 << z)) throw new ArgumentOutOfRangeException("y");
            if (cityEmPixels <= 0 || float.IsNaN(cityEmPixels) || float.IsInfinity(cityEmPixels))
                throw new ArgumentOutOfRangeException("cityEmPixels");

            double n = Math.Pow(2.0, z);
            double minLon = x / n * 360.0 - 180.0;
            double maxLon = (x + 1) / n * 360.0 - 180.0;
            double maxLat = TileLatitude(y, n);
            double minLat = TileLatitude(y + 1, n);
            var selected = new List<MapLabelPlace>();
            foreach (MapLabelPlace place in places)
                if (TileContains(place, minLon, maxLon, minLat, maxLat)
                    && z >= place.MinZoom && z <= place.MaxZoom
                    && (!place.IsLegacy || z > 5 || place.Level == 0) && !string.IsNullOrEmpty(place.Name)) selected.Add(place);
            candidates = selected.Count;
            rendered = 0;

            var bitmap = new Bitmap(TileSize, TileSize, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    if (selected.Count == 0) return bitmap;
                    using (var family = new FontFamily("Microsoft YaHei"))
                    using (var format = StringFormat.GenericTypographic)
                    using (var halo = new Pen(Color.FromArgb(255, 20, 20, 20), 2f))
                    using (var fill = new SolidBrush(Color.White))
                    {
                        graphics.PageUnit = GraphicsUnit.Pixel;
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        halo.LineJoin = LineJoin.Round;
                        var occupied = new List<RectangleF>();
                        // Stable ties retain source order; legacy records all have priority zero.
                        foreach (MapLabelPlace place in selected.OrderBy(p => p.Priority).ThenBy(p => p.Level))
                        {
                            float emPixels = place.Level == 0 ? cityEmPixels + (z <= 3 ? 0 : z == 4 ? 1 : 2) : cityEmPixels;
                            FontStyle style = place.Level == 0 ? FontStyle.Bold : FontStyle.Regular;
                            using (var path = new GraphicsPath())
                            {
                                path.AddString(place.Name, family, (int)style, emPixels, PointF.Empty, format);
                                RectangleF bounds = path.GetBounds(null, halo);
                                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                                if (bounds.Width > TileSize - EdgeMargin * 2 || bounds.Height > TileSize - EdgeMargin * 2)
                                {
                                    // Fit only oversized outlines. The halo remains two pixels, and the text stays complete.
                                    RectangleF glyph = path.GetBounds();
                                    float availableWidth = TileSize - EdgeMargin * 2 - (bounds.Width - glyph.Width);
                                    float availableHeight = TileSize - EdgeMargin * 2 - (bounds.Height - glyph.Height);
                                    float scale = Math.Min(availableWidth / glyph.Width, availableHeight / glyph.Height);
                                    using (var fitting = new Matrix())
                                    {
                                        fitting.Scale(scale, scale);
                                        path.Transform(fitting);
                                    }
                                    bounds = path.GetBounds(null, halo);
                                }
                                double latRadians = place.Lat * Math.PI / 180.0;
                                float rawX = (float)(((place.Lon + 180.0) / 360.0 * n - x) * TileSize);
                                float rawY = (float)(((1.0 - Math.Log(Math.Tan(latRadians) + 1.0 / Math.Cos(latRadians)) / Math.PI)
                                    / 2.0 * n - y) * TileSize);
                                float centerX = Math.Max(bounds.Width / 2 + EdgeMargin,
                                    Math.Min(TileSize - bounds.Width / 2 - EdgeMargin, rawX));
                                float centerY = Math.Max(bounds.Height / 2 + EdgeMargin,
                                    Math.Min(TileSize - bounds.Height / 2 - EdgeMargin, rawY));
                                float dx = centerX - (bounds.Left + bounds.Width / 2);
                                float dy = centerY - (bounds.Top + bounds.Height / 2);
                                bounds.Offset(dx, dy);
                                bounds.Inflate(1f, 1f);
                                bool collides = false;
                                foreach (RectangleF previous in occupied)
                                    if (bounds.IntersectsWith(previous)) { collides = true; break; }
                                if (collides) continue;
                                occupied.Add(bounds);
                                using (var translation = new Matrix())
                                {
                                    translation.Translate(dx, dy);
                                    path.Transform(translation);
                                }
                                graphics.DrawPath(halo, path);
                                graphics.FillPath(fill, path);
                                rendered++;
                            }
                        }
                    }
                }
                return bitmap;
            }
            catch { bitmap.Dispose(); throw; }
        }

        internal static bool TileContains(MapLabelPlace place, double minLon, double maxLon, double minLat, double maxLat)
        {
            return place.Lon >= minLon && place.Lon <= maxLon && place.Lat >= minLat && place.Lat <= maxLat;
        }

        internal static double TileLatitude(int y, double n)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
        }
    }
}
