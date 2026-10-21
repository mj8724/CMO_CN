// Read-only manifest string inventory; never invokes EntryPoint or a game form.
// Build with C# 5 / .NET Framework 4.8:
// csc /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /out:build\ExtractRichTextResources.exe src\tools\ExtractRichTextResources.cs
// Usage:
// ExtractRichTextResources --assembly <file> --output <json> [--resource-filter Hotkeys]
//     [--decoder-type <full type name> --decoder-method <method>] [--plain-rtf]
// Without decoder arguments, the assembly is loaded reflection-only. An explicit
// decoder must be a static string[](Assembly) method; selecting it authorizes that
// method and its type initializer to execute. Decoder names are never guessed.
// Reports retain original strings, tabs and line endings. Uninspected resources
// remain unknown; this tool does not establish live UI translation coverage.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class ExtractRichTextResources
{
    const string Usage = "usage: ExtractRichTextResources --assembly <file> --output <json> "
        + "[--resource-filter Hotkeys] [--decoder-type <type> --decoder-method <method>] [--plain-rtf]";

    [STAThread]
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception e)
        {
            Exception cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            Console.Error.WriteLine(cause.GetType().Name + ": " + cause.Message);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        Dictionary<string, string> options = ParseOptions(args);
        if (!options.ContainsKey("--assembly") || !options.ContainsKey("--output"))
            throw new ArgumentException(Usage);
        bool hasDecoder = options.ContainsKey("--decoder-type");
        if (hasDecoder != options.ContainsKey("--decoder-method"))
            throw new ArgumentException("Both --decoder-type and --decoder-method are required.");

        string source = Path.GetFullPath(options["--assembly"]);
        string output = Path.GetFullPath(options["--output"]);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must differ from the source assembly.");
        string filter = options.ContainsKey("--resource-filter") ? options["--resource-filter"] : "Hotkeys";
        bool plainRtf = options.ContainsKey("--plain-rtf");
        string sourceDirectory = Path.GetDirectoryName(source);
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string dependency = Path.Combine(sourceDirectory, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
        };

        Assembly physical = Assembly.ReflectionOnlyLoadFrom(source);
        string[] physicalNames = physical.GetManifestResourceNames();
        string[] decodedNames = new string[0];
        List<Assembly> assemblies = new List<Assembly> { physical };
        if (hasDecoder)
        {
            Assembly executable = Assembly.LoadFrom(source);
            Type decoderType = executable.GetType(options["--decoder-type"], true);
            MethodInfo decoder = decoderType.GetMethod(options["--decoder-method"],
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, new Type[] { typeof(Assembly) }, null);
            if (decoder == null || decoder.ReturnType != typeof(string[]) || decoder.ContainsGenericParameters)
                throw new ArgumentException("Decoder must have signature static string[](Assembly).");
            decodedNames = (string[])decoder.Invoke(null, new object[] { executable });
            if (decodedNames == null) throw new InvalidDataException("Decoder returned null resource names.");
            HashSet<string> requested = new HashSet<string>(decodedNames, StringComparer.Ordinal);
            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (candidate == executable || candidate.IsDynamic) continue;
                foreach (string name in candidate.GetManifestResourceNames())
                {
                    if (!requested.Contains(name)) continue;
                    assemblies.Add(candidate);
                    break;
                }
            }
        }

        List<object> rows = new List<object>();
        List<object> unknown = new List<object>();
        List<object> inventory = new List<object>();
        HashSet<string> locatedNames = new HashSet<string>(StringComparer.Ordinal);
        bool failed = false;
        int selectedResources = 0;
        foreach (Assembly assembly in assemblies)
        {
            string[] names = assembly.GetManifestResourceNames();
            inventory.Add(new { assembly = assembly.FullName, resourceNames = names });
            foreach (string name in names)
            {
                locatedNames.Add(name);
                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    unknown.Add(new { assembly = assembly.FullName, resource = name, reason = "outside-resource-filter" });
                    continue;
                }
                if (!name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                {
                    unknown.Add(new { assembly = assembly.FullName, resource = name, reason = "not-a-standard-resource-set" });
                    continue;
                }
                selectedResources++;
                try
                {
                    ReadStrings(assembly, name, plainRtf, rows);
                }
                catch (Exception e)
                {
                    failed = true;
                    unknown.Add(new { assembly = assembly.FullName, resource = name,
                        reason = "resource-read-failed", error = e.GetType().FullName + ": " + e.Message });
                }
            }
        }
        foreach (string name in decodedNames)
        {
            if (locatedNames.Contains(name)) continue;
            failed = true;
            unknown.Add(new { assembly = "unresolved", resource = name, reason = "decoder-returned-resource-not-located" });
        }
        FileVersionInfo version = FileVersionInfo.GetVersionInfo(source);
        object report = new
        {
            source = source, sha256 = Hash(source), fileVersion = version.FileVersion,
            productVersion = version.ProductVersion, assembly = physical.FullName,
            decoderType = hasDecoder ? options["--decoder-type"] : null,
            decoderMethod = hasDecoder ? options["--decoder-method"] : null,
            physicalResourceNames = physicalNames, decodedResourceNames = decodedNames,
            resourceAssemblies = inventory, resourceFilter = filter, selectedResourceCount = selectedResources,
            rows = rows, unknownResources = unknown,
            coverage = "unknown: selected string resources only; dynamic strings, control ownership, visibility and live translation were not inspected"
        };
        JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        File.WriteAllText(output, serializer.Serialize(report) + "\n", new UTF8Encoding(false));
        Console.WriteLine("selected resources=" + selectedResources + " strings=" + rows.Count
            + " uninspected/unknown=" + unknown.Count + " report=" + output);
        return failed || selectedResources == 0 ? 1 : 0;
    }

    static void ReadStrings(Assembly assembly, string name, bool plainRtf, List<object> rows)
    {
        using (Stream stream = assembly.GetManifestResourceStream(name))
        using (ResourceReader reader = new ResourceReader(stream))
        {
            IDictionaryEnumerator entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                string key = (string)entries.Key, type;
                byte[] bytes;
                // Reading only raw string data avoids deserializing resource objects.
                reader.GetResourceData(key, out type, out bytes);
                if (type != "ResourceTypeCode.String") continue;
                string value;
                using (BinaryReader data = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8))
                    value = data.ReadString();
                bool isRtf = value.StartsWith("{\\rtf", StringComparison.Ordinal);
                string text = isRtf ? null : value;
                if (isRtf && plainRtf)
                {
                    // This is a fresh framework control, never shown or added to a game form.
                    using (RichTextBox box = new RichTextBox()) { box.Rtf = value; text = box.Text; }
                }
                rows.Add(new { assembly = assembly.FullName, resource = name, key = key,
                    value = value, text = text, isRtf = isRtf });
            }
        }
    }

    static string Hash(string path)
    {
        using (SHA256 hash = SHA256.Create())
        using (Stream stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static Dictionary<string, string> ParseOptions(string[] args)
    {
        Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (options.ContainsKey(key)) throw new ArgumentException("Duplicate option: " + key);
            if (key == "--plain-rtf") { options.Add(key, "true"); continue; }
            if (key != "--assembly" && key != "--output" && key != "--resource-filter"
                && key != "--decoder-type" && key != "--decoder-method")
                throw new ArgumentException("Unknown option: " + key + ". " + Usage);
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + key);
            options.Add(key, args[i]);
        }
        return options;
    }
}
