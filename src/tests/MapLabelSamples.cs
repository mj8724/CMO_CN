using System;
using System.Collections;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

// Explicit read-only WWP inputs; generated PNGs/CSV belong outside source control.
// Usage: MapLabelSamples engine.dll synoptic.wwp cities.wwp z x y output-directory
public static class MapLabelSamples
{
    private static void Load(string source, int level, Type type, IList places)
    {
        using (var reader = new BinaryReader(File.OpenRead(source), Encoding.UTF8))
        {
            int count = reader.ReadInt32();
            if (count < 0) throw new InvalidDataException("Negative WWP record count");
            for (int i = 0; i < count; i++)
            {
                object place = Activator.CreateInstance(type);
                type.GetField("Name").SetValue(place, reader.ReadString());
                type.GetField("Lat").SetValue(place, Convert.ChangeType(reader.ReadSingle(), type.GetField("Lat").FieldType));
                type.GetField("Lon").SetValue(place, Convert.ChangeType(reader.ReadSingle(), type.GetField("Lon").FieldType));
                reader.ReadUInt32();
                type.GetField("Level").SetValue(place, level);
                places.Add(place);
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing WWP bytes");
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 7) throw new ArgumentException("Usage: MapLabelSamples engine.dll synoptic.wwp cities.wwp z x y output-directory");
            Assembly engine = Assembly.LoadFrom(args[0]);
            Type renderer = engine.GetType("CMOShared.MapLabelRenderer");
            Type manager = engine.GetType("CMOLocalizer.CustomAppDomainManager", true);
            IList places;
            Type placeType;
            if (renderer == null)
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
            Load(args[1], 0, placeType, places);
            Load(args[2], 1, placeType, places);
            int z = int.Parse(args[3], CultureInfo.InvariantCulture);
            int x = int.Parse(args[4], CultureInfo.InvariantCulture);
            int y = int.Parse(args[5], CultureInfo.InvariantCulture);
            string output = Path.GetFullPath(args[6]);
            Directory.CreateDirectory(output);
            var summary = new StringBuilder("style,candidates,rendered,pngBytes,alphaPixels,whitePixels\n");
            foreach (float pixels in renderer == null ? new[] { 0f } : new[] { 12f, 14f, 16f })
            {
                string style = renderer == null ? "baseline" : pixels.ToString(CultureInfo.InvariantCulture);
                string prefix = z + "-" + x + "-" + y + "-" + style;
                int candidates, rendered;
                Bitmap image;
                if (renderer == null)
                {
                    object[] parameters = { z, x, y, 0, 0 };
                    byte[] data = (byte[])manager.GetMethod("RenderChineseTileBytes", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, parameters);
                    using (var stream = new MemoryStream(data)) using (var temporary = new Bitmap(stream)) image = new Bitmap(temporary);
                    candidates = (int)parameters[3]; rendered = (int)parameters[4];
                }
                else
                {
                    MethodInfo render = Array.Find(renderer.GetMethods(), method => method.Name == "RenderTile" && method.GetParameters().Length == 7);
                    object[] parameters = { places, z, x, y, 0, 0, pixels };
                    image = (Bitmap)render.Invoke(null, parameters);
                    candidates = (int)parameters[4]; rendered = (int)parameters[5];
                }
                using (image)
                {
                    string png = Path.Combine(output, prefix + ".png");
                    image.Save(png, ImageFormat.Png);
                    int alpha = 0, white = 0;
                    for (int py = 0; py < image.Height; py++) for (int px = 0; px < image.Width; px++)
                    {
                        Color color = image.GetPixel(px, py);
                        if (color.A > 0) alpha++;
                        if (color.A >= 200 && color.R >= 200 && color.G >= 200 && color.B >= 200) white++;
                    }
                    foreach (bool dark in new[] { false, true }) using (var preview = new Bitmap(256, 256))
                    {
                        using (Graphics graphics = Graphics.FromImage(preview))
                        {
                            graphics.Clear(dark ? Color.FromArgb(45, 50, 55) : Color.FromArgb(220, 215, 200));
                            graphics.DrawImageUnscaled(image, 0, 0);
                        }
                        preview.Save(Path.Combine(output, prefix + (dark ? "-dark.png" : "-light.png")), ImageFormat.Png);
                    }
                    summary.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5}\n",
                        style, candidates, rendered, new FileInfo(png).Length, alpha, white);
                }
            }
            File.WriteAllText(Path.Combine(output, z + "-" + x + "-" + y + "-summary.csv"), summary.ToString(), new UTF8Encoding(false));
            Console.Write(summary);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
