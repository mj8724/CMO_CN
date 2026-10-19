
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
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Command_Core.dll"));
        foreach (Type t in asmCore.GetTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
                string mn = m.Name.ToLower();
                if (mn.Contains("compress") || mn.Contains("decompress") || mn.Contains("unzip")) {
                    Console.WriteLine(t.FullName + "." + m.Name);
                }
            }
        }
    }
}
