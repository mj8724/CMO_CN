using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CMOShared
{
    public sealed class MapLabelPlace
    {
        public string Name;
        public float Lat;
        public float Lon;
        public int Level;
        // Rank 决定「谁先画、用哪种字形、在哪些缩放出现」；Level 是历史字段（0=国家/海域，1=城市）。
        // 0=国家/海域（粗体、全缩放）、1=省/州（粗体、按 MinZoom 进位、到 z8 收尾）、2=城市（常规、z>5）。
        public int Rank;
        public float MinZoom;
    }

    /// <summary>
    /// 省/州级标签的读取器（R88）。数据放在词典的 `places` 分区里：
    /// 该分区排在 `comments` 之前，旧引擎会连同 `_meta` 一起忽略，向后兼容不受影响，
    /// 也避免为它新开一个引擎资产（安装/打包/入包对账的接口面保持不变）。
    /// 形状：[[中文, lat, lon, min_zoom], ...]
    /// </summary>
    public static class MapLabelRegions
    {
        // 反斜杠用 char 常量表达：这份源码同时被引擎与安装器编译，
        // 字面量反斜杠在跨工具链粘贴时最易被多解一层。
        private const char Backslash = (char)92;

        public static List<MapLabelPlace> LoadFromDictionary(string dictionaryPath)
        {
            var places = new List<MapLabelPlace>();
            if (string.IsNullOrEmpty(dictionaryPath) || !File.Exists(dictionaryPath)) return places;
            string text;
            try { text = File.ReadAllText(dictionaryPath, Encoding.UTF8); }
            catch (Exception) { return places; }
            int pos = TopLevelArray(text, "places");
            if (pos < 0) return places;
            int i = pos + 1;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == ']') break;
                if (c != '[') { i++; continue; }
                i++;
                string name = null;
                float lat = 0f, lon = 0f, minZoom = 0f;
                int field = 0;
                while (i < n && text[i] != ']')
                {
                    c = text[i];
                    if (c == '"')
                    {
                        var sb = new StringBuilder();
                        i++;
                        while (i < n)
                        {
                            char ch = text[i];
                            if (ch == Backslash) { i++; if (i < n) sb.Append(text[i]); i++; continue; }
                            if (ch == '"') { i++; break; }
                            sb.Append(ch);
                            i++;
                        }
                        if (field == 0) name = sb.ToString();
                        field++;
                        continue;
                    }
                    if (c == ',' || char.IsWhiteSpace(c)) { i++; continue; }
                    int start = i;
                    while (i < n && text[i] != ',' && text[i] != ']' && !char.IsWhiteSpace(text[i])) i++;
                    if (i > start)
                    {
                        float value;
                        string token = text.Substring(start, i - start);
                        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        {
                            if (field == 1) lat = value;
                            else if (field == 2) lon = value;
                            else if (field == 3) minZoom = value;
                            field++;
                        }
                    }
                }
                if (i < n) i++;
                if (!string.IsNullOrEmpty(name))
                {
                    places.Add(new MapLabelPlace
                    {
                        Name = name, Lat = lat, Lon = lon, Level = 2, Rank = 1, MinZoom = minZoom
                    });
                }
            }
            return places;
        }

        private static int TopLevelArray(string text, string key)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == '"')
                {
                    int j = i + 1;
                    bool match = j + key.Length < text.Length
                        && text[j + key.Length] == '"'
                        && string.CompareOrdinal(text, j, key, 0, key.Length) == 0;
                    if (match && depth == 1)
                        return text.IndexOf('[', j + key.Length);
                    i = j;
                    while (i < text.Length)
                    {
                        if (text[i] == Backslash) { i += 2; continue; }
                        if (text[i] == '"') break;
                        i++;
                    }
                }
            }
            return -1;
        }
    }

    // Both cached and runtime labels use the same pixels, ordering and collision geometry.
    public static class MapLabelRenderer
    {
        private const int TileSize = 256;
        private const float EdgeMargin = 2f;

        public static Bitmap RenderTile(IList<MapLabelPlace> places, int z, int x, int y,
            out int candidates, out int rendered)
        {
            return RenderTile(places, z, x, y, out candidates, out rendered, 14f);
        }

        /// <summary>0=国家/海域，1=省/州，2=城市。未显式设置 Rank 的旧调用按 Level 推断。</summary>
        private static int RankOf(MapLabelPlace place)
        {
            if (place.Rank != 0) return place.Rank;
            return place.Level == 0 ? 0 : 2;
        }

        /// <summary>
        /// 各层的出现区间与原版 Stamen 标签层对齐（R88）：国家/海域全缩放；
        /// 省/州从上游建议的 min_zoom 开始、到 z8 收尾（更深只有城市）；城市沿用 z>5。
        /// </summary>
        private static bool VisibleAtZoom(MapLabelPlace place, int z)
        {
            int rank = RankOf(place);
            if (rank == 0) return true;
            if (rank == 1) return z >= (int)Math.Ceiling(place.MinZoom) && z <= 8;
            return z > 5;
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
                if (place.Lon >= minLon && place.Lon <= maxLon && place.Lat >= minLat && place.Lat <= maxLat
                    && VisibleAtZoom(place, z) && !string.IsNullOrEmpty(place.Name)) selected.Add(place);
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
                        // OrderBy is stable: equal-rank labels keep the WWP input order.
                        // 先国家、再省/州、最后城市：原版在省级视角就是「大区名压住城市名」，
                        // 反过来（城市先画）会让省名几乎全被城市挤掉。
                        foreach (MapLabelPlace place in selected.OrderBy(RankOf).ThenBy(p => p.Level))
                        {
                            int rank = RankOf(place);
                            float emPixels = rank == 0 ? cityEmPixels + (z <= 3 ? 0 : z == 4 ? 1 : 2)
                                : rank == 1 ? cityEmPixels + (z >= 7 ? 1 : 0)
                                : cityEmPixels;
                            FontStyle style = rank <= 1 ? FontStyle.Bold : FontStyle.Regular;
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

        private static double TileLatitude(int y, double n)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
        }
    }
}
