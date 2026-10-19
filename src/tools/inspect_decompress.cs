
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string name = new AssemblyName(a.Name).Name;
            string p1 = Path.Combine(gameDir, name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            return null;
        };
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Command_Core.dll"));
        foreach (Type t in asmCore.GetTypes()) {
            if (t.Name.IndexOf("Compress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                t.Name.IndexOf("Scenario", StringComparison.OrdinalIgnoreCase) >= 0) {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
                    if (m.Name.IndexOf("Decompress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Uncompress", StringComparison.OrdinalIgnoreCase) >= 0) {
                        Console.WriteLine(t.FullName + "." + m.Name);
                    }
                }
            }
        }
    }
}
