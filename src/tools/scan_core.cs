
using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

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

        using (var sw = new StreamWriter(@"projects\汉化\core_all_strings.txt")) {
            foreach (Type t in asm.GetTypes()) {
                if (t.FullName != null && t.FullName.StartsWith("Command_Core.")) {
                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                    catch { continue; }

                    foreach (MethodInfo m in methods) {
                        MethodBody body = null;
                        try { body = m.GetMethodBody(); } catch {}
                        if (body == null) continue;
                        byte[] il = body.GetILAsByteArray();
                        for (int i = 0; i < il.Length; i++) {
                            if (il[i] == 0x28 && i + 4 < il.Length) {
                                int token = BitConverter.ToInt32(il, i + 1);
                                try {
                                    MethodBase mb = mod.ResolveMethod(token);
                                    if (mb != null && mb.DeclaringType != null && mb.DeclaringType.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                                        int prevInt = -1;
                                        if (i >= 5 && il[i - 5] == 0x20) {
                                            prevInt = BitConverter.ToInt32(il, i - 4);
                                        }
                                        if (prevInt >= 0) {
                                            string s = "";
                                            try { s = (string)decryptMethod.Invoke(null, new object[] { prevInt }); } catch {}
                                            sw.WriteLine(t.FullName + "\t" + m.Name + "\t" + prevInt + "\t" + s.Replace("\r", "\\r").Replace("\n", "\\n"));
                                        }
                                    }
                                } catch {}
                            }
                        }
                    }
                }
            }
        }
        Console.WriteLine("Done scanning all Command_Core strings!");
    }
}
