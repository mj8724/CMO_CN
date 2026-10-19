
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
        Assembly asmCmd = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        Type tLoad = asmCmd.GetType("Command.LoadScenario");
        foreach (MethodInfo m in tLoad.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
            MethodBody mb = m.GetMethodBody();
            if (mb != null) {
                byte[] il = mb.GetILAsByteArray();
                // look for calls
                // print method name and byte count
                if (il.Length > 200) {
                    Console.WriteLine("Method: " + m.Name + " IL Len: " + il.Length);
                }
            }
        }
    }
}
