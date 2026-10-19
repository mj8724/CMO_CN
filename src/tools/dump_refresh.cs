
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

        Type tSandbox = asm.GetType("Command_Core.Lua.LuaSandBox");
        foreach (MethodInfo m in tSandbox.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            if (m.Name.Contains("RefreshStats") || m.Name.Contains("Init") || m.Name.Contains("Run") || m.Name.Contains("Exec")) {
                Console.WriteLine("=== Method: " + m.ToString() + " ===");
                var body = m.GetMethodBody();
                if (body == null) continue;
                byte[] il = body.GetILAsByteArray();
                for (int i = 0; i < il.Length; i++) {
                    if (il[i] == 0x28 && i + 4 < il.Length) { // call
                        int token = BitConverter.ToInt32(il, i + 1);
                        try {
                            MethodBase mb = mod.ResolveMethod(token);
                            if (mb != null) {
                                string decl = mb.DeclaringType != null ? mb.DeclaringType.FullName : "";
                                if (decl.Contains("AoIBWWlDJbaf7LijnA") || mb.Name.Contains("DoString") || decl.Contains("Lua")) {
                                    int prevInt = (i >= 5 && il[i - 5] == 0x20) ? BitConverter.ToInt32(il, i - 4) : -1;
                                    string strVal = "";
                                    if (prevInt >= 0 && decl.Contains("AoIBWWlDJbaf7LijnA")) {
                                        try { strVal = (string)decryptMethod.Invoke(null, new object[] { prevInt }); } catch {}
                                    }
                                    Console.WriteLine(string.Format("  IL_{0:X4}: call {1}.{2} (prev ldc.i4={3}) => {4}", i, decl, mb.Name, prevInt, strVal.Replace("\r", "\\r").Replace("\n", "\\n")));
                                }
                            }
                        } catch {}
                    }
                    if (il[i] == 0x72 && i + 4 < il.Length) { // ldstr
                        int token = BitConverter.ToInt32(il, i + 1);
                        try {
                            string s = mod.ResolveString(token);
                            Console.WriteLine(string.Format("  IL_{0:X4}: ldstr {1}", i, s));
                        } catch {}
                    }
                }
            }
        }
    }
}
