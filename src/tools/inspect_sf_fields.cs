
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
        Type t = asm.GetType("Command.ScenarioFeatures");
        if (t != null) {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                Console.WriteLine("  Field: " + f.Name + " : " + f.FieldType.FullName);
            }
        }
    }
}
