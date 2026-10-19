using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;

namespace CMOShared
{
    public sealed class MapLabelPlace
    {
        public string Name;
        public float Lat;
        public float Lon;
        public int Level;
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
                    && (z > 5 || place.Level == 0) && !string.IsNullOrEmpty(place.Name)) selected.Add(place);
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
                        // OrderBy is stable: equal-level labels keep the WWP input order.
                        foreach (MapLabelPlace place in selected.OrderBy(p => p.Level))
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

        private static double TileLatitude(int y, double n)
        {
            return Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n))) * 180.0 / Math.PI;
        }
    }
}
