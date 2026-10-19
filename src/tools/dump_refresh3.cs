
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
        Module mod = asm.GetModules()[0];

        Type tStr = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        FieldInfo fDebug = tStr.GetField("gTbYFnJa7EN", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (fDebug != null) fDebug.SetValue(null, 100);
        MethodInfo decryptMethod = tStr.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (Type t in asm.GetTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                if (m.Name.Contains("RefreshStats")) {
                    var body = m.GetMethodBody();
                    Console.WriteLine(string.Format("Found RefreshStats in {0}: {1} (IL len={2})", t.FullName, m.ToString(), body != null ? body.GetILAsByteArray().Length : -1));
                    if (body != null) {
                        byte[] il = body.GetILAsByteArray();
                        for (int i = 0; i < il.Length; i++) {
                            if (il[i] == 0x20 && i + 4 < il.Length) {
                                int val = BitConverter.ToInt32(il, i + 1);
                                if (val > 100 && val < 3000000) {
                                    try {
                                        string s = (string)decryptMethod.Invoke(null, new object[] { val });
                                        Console.WriteLine("  Offset " + val + " => " + (s != null ? s.Replace("\r", "\\r").Replace("\n", "\\n") : "null"));
                                    } catch {}
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
