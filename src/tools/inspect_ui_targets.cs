
using System;
using System.Reflection;
using System.IO;
using System.Collections;
using System.Resources;

class Program {
    [STAThread]
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string name = new AssemblyName(a.Name).Name;
            string p1 = Path.Combine(gameDir, name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            return null;
        };

        Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        
        // 1. Check all DeclaredMethods in ScenarioFeatures, ScenarioSelectControl, LoadScenario for SmartAssembly string offsets
        FieldInfo fStrings = null;
        foreach (Type t in asm.GetTypes()) {
            if (t.Name == "AoIBWWlDJbaf7LijnA") {
                fStrings = t.GetField("Oj0BWr97s1A", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
        }

        string[] targetTypes = new string[] { "Command.ScenarioFeatures", "Command.ScenarioSelectControl", "Command.LoadScenario", "Command.SelectSide" };
        foreach (Type t in asm.GetTypes()) {
            bool match = false;
            foreach (string tt in targetTypes) if (t.FullName.Contains(tt) || t.Name.Contains("Side") || t.Name.Contains("ScenarioFeatures") || t.Name.Contains("ScenarioSelect")) match = true;
            if (!match) continue;

            Console.WriteLine("=== Type: " + t.FullName + " ===");
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                Console.WriteLine("  Prop: " + p.Name + " (" + p.PropertyType.Name + ")");
            }
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                MethodBody mb = null;
                try { mb = m.GetMethodBody(); } catch {}
                if (mb == null) continue;
                byte[] il = mb.GetILAsByteArray();
                for (int i = 0; i < il.Length - 9; i++) {
                    if (il[i] == 0x20 && il[i+5] == 0x28) {
                        int offset = BitConverter.ToInt32(il, i + 1);
                        int tok = BitConverter.ToInt32(il, i + 6);
                        try {
                            MethodBase called = asm.ManifestModule.ResolveMethod(tok);
                            if (called != null && called.DeclaringType != null && called.DeclaringType.Namespace == "SmartAssembly.StringsEncoding") {
                                Console.WriteLine("  [" + m.Name + "] Offset: " + offset);
                            }
                        } catch {}
                    }
                }
            }
        }
    }
}
