
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
        Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        foreach (string tName in new[]{"Command.SelectSide", "Command.ScenarioFeatures", "Command.ScenarioSelectControl"}) {
            Type t = asm.GetType(tName);
            if (t == null) {
                Console.WriteLine(tName + " is NULL in Command.exe, searching all types...");
                foreach (Type sub in asm.GetTypes()) {
                    if (sub.Name.Contains(tName.Substring(8))) {
                        Console.WriteLine("  Found: " + sub.FullName + " (Base: " + (sub.BaseType != null ? sub.BaseType.FullName : "") + ")");
                    }
                }
            } else {
                Console.WriteLine("=== " + tName + " (Base: " + t.BaseType.FullName + ") ===");
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                    Console.WriteLine("  Field: " + f.Name + " : " + f.FieldType.FullName);
                }
            }
        }
    }
}
