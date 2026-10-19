using System;
using System.Reflection;
using System.Diagnostics;

class Program
{
    static void Main(string[] args)
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string dllPath = System.IO.Path.Combine(baseDir, "CMOLocalizer.dll");
        string jsonPath = System.IO.Path.Combine(baseDir, "CMOLocalizer.json");

        Console.WriteLine("Loading Assembly: " + dllPath);
        Assembly asm = Assembly.LoadFrom(dllPath);
        Type mgrType = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (mgrType == null)
        {
            Console.WriteLine("ERROR: CustomAppDomainManager not found!");
            return;
        }

        MethodInfo loadMethod = mgrType.GetMethod("LoadDictionaries", BindingFlags.NonPublic | BindingFlags.Static);
        MethodInfo transMethod = mgrType.GetMethod("TranslateString", BindingFlags.Public | BindingFlags.Static);

        Stopwatch sw = Stopwatch.StartNew();
        loadMethod.Invoke(null, new object[] { jsonPath });
        sw.Stop();
        Console.WriteLine(string.Format("Loaded dictionaries in {0} ms", sw.ElapsedMilliseconds));

        string[] testStrings = new string[]
        {
            "Engage Target(s) - Auto",
            "Engage Target(s) - Manual",
            "Bearing-Only Launch (BOL)",
            "Return to base",
            "Switched to UNIT VIEW",
            "Speed: 480 kt - Cruise",
            "Altitude: 36000 ft",
            "Status: Operational",
            "Units with No Communications",
            "Click anywhere on the map to add a unit",
            "[Selected Unit]"
        };

        Console.WriteLine("\n=== Translation Quality Verification ===");
        foreach (string s in testStrings)
        {
            string res = (string)transMethod.Invoke(null, new object[] { s });
            Console.WriteLine(string.Format("  '{0}' -> '{1}'", s, res));
        }

        Console.WriteLine("\nBenchmarking 100,000 lookups with LRU cache...");
        sw.Restart();
        for (int i = 0; i < 100000; i++)
        {
            string res = (string)transMethod.Invoke(null, new object[] { "Engage Target(s) - Auto" });
        }
        sw.Stop();
        Console.WriteLine(string.Format("100,000 translations took {0} ms ({1:F2} ns per lookup)",
            sw.ElapsedMilliseconds, (sw.Elapsed.TotalMilliseconds * 1000000.0) / 100000.0));
    }
}
