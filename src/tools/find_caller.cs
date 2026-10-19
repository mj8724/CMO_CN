
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

        MethodInfo decryptMethod = null;
        foreach (Type t in asm.GetTypes()) {
            if (t.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
                    var pars = m.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(int) && m.ReturnType == typeof(string)) {
                        decryptMethod = m;
                        break;
                    }
                }
                break;
            }
        }

        foreach (Type t in asm.GetTypes()) {
            if (t.FullName == "Command_Core.Ship" || t.FullName == "Command_Core.DAL.DBFunctions") {
                Console.WriteLine("=== Type: " + t.FullName + " ===");
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    if (m.Name.Contains("GetShip") || m.Name.Contains("Load") || m.Name.Contains("EW_") || m.Name.Contains(" Fetch")) {
                        Console.WriteLine("  Method: " + m.Name);
                        DumpMethodStrings(m, mod, decryptMethod);
                    }
                }
            }
        }
    }

    static void DumpMethodStrings(MethodInfo m, Module mod, MethodInfo decryptMethod) {
        MethodBody body = null;
        try { body = m.GetMethodBody(); } catch {}
        if (body == null) return;
        byte[] il = body.GetILAsByteArray();
        for (int i = 0; i < il.Length - 5; i++) {
            if (il[i] == 0x20 && il[i + 5] == 0x28) {
                int val = BitConverter.ToInt32(il, i + 1);
                int token = BitConverter.ToInt32(il, i + 6);
                try {
                    MethodBase called = mod.ResolveMethod(token);
                    if (called != null && called.DeclaringType != null && called.DeclaringType.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                        string strVal = "";
                        try {
                            if (decryptMethod != null) {
                                strVal = (string)decryptMethod.Invoke(null, new object[] { val });
                            }
                        } catch (Exception ex) { strVal = "ERR: " + ex.Message; }
                        Console.WriteLine("    Offset: " + val + " => " + (strVal.Length > 80 ? strVal.Substring(0, 80) + "..." : strVal));
                    }
                } catch {}
            }
        }
    }
}
