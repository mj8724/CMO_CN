
using System;
using System.Reflection;
using System.Reflection.Emit;
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
            if (m.Name == "RefreshStats") {
                Console.WriteLine("=== RefreshStats Method Body ===");
                var body = m.GetMethodBody();
                byte[] il = body.GetILAsByteArray();
                Console.WriteLine("IL Length: " + il.Length);
                for (int i = 0; i < il.Length; i++) {
                    byte b = il[i];
                    if (b == 0x20 && i + 4 < il.Length) { // ldc.i4
                        int val = BitConverter.ToInt32(il, i + 1);
                        if (val > 1000 && val < 3000000) {
                            try {
                                string s = (string)decryptMethod.Invoke(null, new object[] { val });
                                if (!string.IsNullOrEmpty(s)) {
                                    Console.WriteLine(string.Format("Offset {0}: {1}", val, s.Length > 60 ? s.Substring(0, 60) : s));
                                }
                            } catch {}
                        }
                    }
                }
            }
        }
    }
}
