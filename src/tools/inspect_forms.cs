
using System;
using System.Reflection;
using System.IO;
using System.Windows.Forms;

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
        foreach (Type t in asm.GetTypes()) {
            if (t.Name.Contains("LoadScenario") || t.Name.Contains("SelectSide") || t.Name.Contains("ScenarioFeatures")) {
                Console.WriteLine("Type: " + t.FullName + " (Base: " + (t.BaseType != null ? t.BaseType.FullName : "none") + ")");
                // print fields of control types
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                    if (typeof(Control).IsAssignableFrom(f.FieldType) || f.FieldType.FullName.Contains("Control") || f.FieldType.FullName.Contains("Browser") || f.FieldType.FullName.Contains("View")) {
                        Console.WriteLine("  Control Field: " + f.Name + " : " + f.FieldType.FullName);
                    }
                }
            }
        }
    }
}
