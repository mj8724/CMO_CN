
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

        Type tSandbox = asm.GetType("Command_Core.Lua.LuaSandBox");
        MethodInfo m = tSandbox.GetMethod("RefreshStats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        var body = m.GetMethodBody();
        byte[] il = body.GetILAsByteArray();
        for (int i = 0; i < il.Length; i++) {
            Console.WriteLine(string.Format("IL_{0:X4}: 0x{1:X2}", i, il[i]));
            if (il[i] == 0x28 || il[i] == 0x2B || il[i] == 0x6F) { // call, br, callvirt
                int token = BitConverter.ToInt32(il, i + 1);
                try {
                    MethodBase target = mod.ResolveMethod(token);
                    Console.WriteLine("  -> Calls: " + target.DeclaringType.FullName + "." + target.Name);
                } catch (Exception ex) {
                    Console.WriteLine("  -> Resolve failed: " + ex.Message);
                }
            }
        }
    }
}
