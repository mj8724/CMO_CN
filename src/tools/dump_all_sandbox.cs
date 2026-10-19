
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
        foreach (MethodInfo m in tSandbox.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            var body = m.GetMethodBody();
            Console.WriteLine(string.Format("{0} (IL len={1})", m.ToString(), body != null ? body.GetILAsByteArray().Length : -1));
        }
    }
}
