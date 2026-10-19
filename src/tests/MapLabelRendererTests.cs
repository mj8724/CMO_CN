using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;

// Uses the actual engine DLL; the baseline path remains usable for a red/green comparison.
public static class MapLabelRendererTests
{
    private static Assembly engine;
    private static int assertions;
    private static IList lastPlaces;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
        Console.WriteLine("PASS " + message);
    }

    private static object Place(Type type, string name, int z, int x, int y, float px, float py, int level)
    {
        double n = Math.Pow(2, z);
        object place = Activator.CreateInstance(type);
        type.GetField("Name").SetValue(place, name);
        type.GetField("Lat").SetValue(place, Convert.ChangeType(
            Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + py / 256.0) / n))) * 180 / Math.PI,
            type.GetField("Lat").FieldType));
        type.GetField("Lon").SetValue(place, Convert.ChangeType((x + px / 256.0) / n * 360 - 180,
            type.GetField("Lon").FieldType));
        type.GetField("Level").SetValue(place, level);
        return place;
    }

    private static Bitmap Render(string[] names, float[] xs, float[] ys, int[] levels,
        int z, float pixels, out int candidates, out int rendered)
    {
        int x = z == 0 ? 0 : 1 << (z - 1), y = x;
        Type shared = engine.GetType("CMOShared.MapLabelRenderer");
        Type manager = engine.GetType("CMOLocalizer.CustomAppDomainManager", true);
        IList places;
        Type placeType;
        if (shared == null)
        {
            places = (IList)manager.GetField("mapPlaces", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            places.Clear();
            placeType = places.GetType().GetGenericArguments()[0];
        }
        else
        {
            placeType = engine.GetType("CMOShared.MapLabelPlace", true);
            places = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(placeType));
        }
        for (int i = 0; i < names.Length; i++) places.Add(Place(placeType, names[i], z, x, y, xs[i], ys[i], levels[i]));
        lastPlaces = places;
        if (shared == null)
        {
            object[] args = { z, x, y, 0, 0 };
            byte[] bytes = (byte[])manager.GetMethod("RenderChineseTileBytes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
            candidates = (int)args[3]; rendered = (int)args[4];
            using (var stream = new MemoryStream(bytes)) using (var image = new Bitmap(stream)) return new Bitmap(image);
        }
        object[] parameters = { places, z, x, y, 0, 0, pixels };
        foreach (MethodInfo method in shared.GetMethods())
            if (method.Name == "RenderTile" && method.GetParameters().Length == parameters.Length)
            {
                Bitmap result = (Bitmap)method.Invoke(null, parameters);
                candidates = (int)parameters[4]; rendered = (int)parameters[5];
                return result;
            }
        throw new Exception("Shared renderer style overload is missing");
    }

    private static Rectangle Bounds(Bitmap image, bool fillOnly)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++)
        {
            Color color = image.GetPixel(x, y);
            if (color.A == 0 || (fillOnly && (color.A < 200 || color.R < 200 || color.G < 200 || color.B < 200))) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool SamePixels(Bitmap first, Bitmap second)
    {
        for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++)
            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    public static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]);
            int candidates, rendered;
            using (Bitmap image = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle ink = Bounds(image, true);
                Console.WriteLine("CITY_FILL " + ink.Width + "x" + ink.Height);
                Check(image.Width == 256 && image.Height == 256 && candidates == 1 && rendered == 1,
                    "single city uses a fixed 256px tile");
                Check(ink.Height >= 11, "city fill has at least 11 actual pixels of glyph height");
                Check(Math.Abs(ink.Left + ink.Width / 2f - 128) <= 1.5f
                    && Math.Abs(ink.Top + ink.Height / 2f - 128) <= 1.5f, "actual glyph bounds center on the source coordinate");
                object original = lastPlaces[0];
                Check((string)original.GetType().GetField("Name").GetValue(original) == "鸟取"
                    && lastPlaces.Count == 1, "rendering retains source names and record count");
                Type manager = engine.GetType("CMOLocalizer.CustomAppDomainManager", true);
                IList runtime = (IList)manager.GetField("mapPlaces", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                runtime.Clear(); runtime.Add(original);
                object[] parameters = { 8, 128, 128, 0, 0 };
                byte[] bytes = (byte[])manager.GetMethod("RenderChineseTileBytes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
                using (var stream = new MemoryStream(bytes)) using (var runtimeImage = new Bitmap(stream))
                    Check(SamePixels(image, runtimeImage) && (int)parameters[3] == 1 && (int)parameters[4] == 1,
                        "runtime PNG pixels and counts equal the default shared renderer");
            }
            int previousHeight = 0;
            foreach (float pixels in new[] { 12f, 14f, 16f })
                using (Bitmap image = Render(new[] { "松江" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, pixels,
                    out candidates, out rendered))
                {
                    int height = Bounds(image, true).Height;
                    Check(height > previousHeight && rendered == 1, pixels + "px styles increase actual glyph height");
                    previousHeight = height;
                }
            foreach (float px in new[] { 0.1f, 255.9f }) foreach (float py in new[] { 0.1f, 255.9f })
                using (Bitmap image = Render(new[] { "杨州" }, new[] { px }, new[] { py }, new[] { 1 }, 8, 14f,
                    out candidates, out rendered))
                {
                    Rectangle all = Bounds(image, false);
                    Check(rendered == 1 && all.Left > 0 && all.Top > 0 && all.Right < 256 && all.Bottom < 256,
                        "halo remains inside the tile at corner " + px + "," + py);
                }
            string longPlace = "阿伊尔伊阿·阿斯特尔·富萨诺·奥斯蒂阿·安蒂阿";
            using (Bitmap image = Render(new[] { longPlace }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle all = Bounds(image, false);
                Console.WriteLine("LONG candidates=" + candidates + " rendered=" + rendered + " bounds=" + all);
                Check(candidates == 1 && rendered == 1 && all.Left > 0 && all.Right < 256,
                    "the existing longest place name remains visible and fits inside its tile");
                Check((string)lastPlaces[0].GetType().GetField("Name").GetValue(lastPlaces[0]) == longPlace,
                    "fitting a long place name preserves its complete source text");
            }
            using (Bitmap image = Render(new[] { new string('超', 100) }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                out candidates, out rendered))
            {
                Rectangle all = Bounds(image, false);
                Check(candidates == 1 && rendered == 1 && !all.IsEmpty && all.Left > 0 && all.Right < 256,
                    "an extreme name fits its complete path inside the tile without clipping");
            }
            using (Bitmap dense = Render(new[] { "鸟取", "米子", "松江" }, new[] { 128f, 128f, 128f },
                new[] { 128f, 128f, 128f }, new[] { 1, 1, 1 }, 8, 14f, out candidates, out rendered))
            {
                Check(candidates == 3 && rendered == 1, "colliding city labels cannot overlap");
                using (Bitmap first = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 8, 14f,
                    out candidates, out rendered)) Check(SamePixels(dense, first), "equal-level collision winners preserve WWP input order");
            }
            using (Bitmap mixed = Render(new[] { "鸟取", "日本" }, new[] { 128f, 128f }, new[] { 128f, 128f },
                new[] { 1, 0 }, 8, 14f, out candidates, out rendered))
            {
                Check(candidates == 2 && rendered == 1, "high zoom retains both synoptic and city candidates");
                using (Bitmap first = Render(new[] { "日本" }, new[] { 128f }, new[] { 128f }, new[] { 0 }, 8, 14f,
                    out candidates, out rendered)) Check(SamePixels(mixed, first), "synoptic labels retain priority before cities");
            }
            using (Bitmap image = Render(new[] { "鸟取", "日本" }, new[] { 64f, 192f }, new[] { 128f, 128f },
                new[] { 1, 0 }, 5, 14f, out candidates, out rendered))
                Check(candidates == 1 && rendered == 1, "low zoom excludes city candidates");
            using (Bitmap image = Render(new[] { "鸟取" }, new[] { 128f }, new[] { 128f }, new[] { 1 }, 30, 14f,
                out candidates, out rendered)) Check(candidates == 1 && rendered == 1, "offline zoom 30 remains supported");
            using (Bitmap image = Render(new string[0], new float[0], new float[0], new int[0], 8, 14f,
                out candidates, out rendered)) Check(candidates == 0 && rendered == 0 && Bounds(image, false).IsEmpty,
                    "empty tiles stay transparent");
            Console.WriteLine("ASSERTIONS=" + assertions);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
