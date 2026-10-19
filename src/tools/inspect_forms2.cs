
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
            if (t.Name.Contains("ScenarioSelect") || t.Name.Contains("Side") || t.Name.Contains("Briefing") || t.Name.Contains("StartGame")) {
                if (typeof(Control).IsAssignableFrom(t) || typeof(System.Windows.Window).IsAssignableFrom(t) || typeof(System.Windows.Controls.Control).IsAssignableFrom(t)) {
                    Console.WriteLine("Type: " + t.FullName + " (Base: " + (t.BaseType != null ? t.BaseType.FullName : "none") + ")");
                    foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                        Console.WriteLine("  Field: " + f.Name + " : " + f.FieldType.FullName);
                    }
                }
            }
        }
    }
}
