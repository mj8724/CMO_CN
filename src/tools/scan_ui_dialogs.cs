
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
        Type strHelper = asm.GetType("e2568f9O71829N6390.AoIBWWlDJbaf7LijnA");
        MethodInfo getStr = strHelper.GetMethod("N0f03e3F8s", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Module mod = asm.GetModules()[0];

        foreach (Type t in asm.GetTypes()) {
            string fn = t.FullName ?? "";
            if (fn.Contains("ScenarioFeatures") || fn.Contains("LoadScenario") || fn.Contains("ScenarioSelect") || fn.Contains("SideBriefing")) {
                MethodBase[] methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                ConstructorInfo[] ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                var all = new System.Collections.Generic.List<MethodBase>();
                all.AddRange(methods);
                all.AddRange(ctors);
                foreach (MethodBase m in all) {
                    try {
                        MethodBody body = m.GetMethodBody();
                        if (body == null) continue;
                        byte[] il = body.GetILAsByteArray();
                        for (int i = 0; i < il.Length - 5; i++) {
                            if (il[i] == 0x20 && il[i + 5] == 0x28) {
                                int arg = BitConverter.ToInt32(il, i + 1);
                                try {
                                    string s = (string)getStr.Invoke(null, new object[] { arg });
                                    if (!string.IsNullOrEmpty(s)) {
                                        Console.WriteLine(string.Format("[{0}.{1}] Offset {2}: {3}", fn, m.Name, arg, s.Replace("\r", "\\r").Replace("\n", "\\n")));
                                    }
                                } catch {}
                            }
                        }
                    } catch {}
                }
            }
        }
    }
}
