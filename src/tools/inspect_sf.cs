
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
            Console.WriteLine("ScenarioFeatures methods:");
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                if (m.Name.Contains("Initialize") || m.Name.Contains("Load") || m.Name.Contains("Set") || m.Name == ".ctor") {
                    Console.WriteLine("  " + m.Name);
                    MethodBody mb = m.GetMethodBody();
                    if (mb != null) {
                        byte[] il = mb.GetILAsByteArray();
                        Module mod = m.Module;
                        for (int i = 0; i < il.Length - 4; i++) {
                            if (il[i] == 0x72) {
                                int tok = BitConverter.ToInt32(il, i + 1);
                                try {
                                    string s = mod.ResolveString(tok);
                                    if (s.Length > 2) Console.WriteLine("    [ldstr] " + s);
                                } catch {}
                            }
                        }
                    }
                }
            }
        }
    }
}
