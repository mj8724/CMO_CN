
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
        foreach (Type t in asm.GetTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                if (m.Name == "RefreshStats") {
                    var b = m.GetMethodBody();
                    int len = b != null ? b.GetILAsByteArray().Length : 0;
                    if (len > 10) {
                        Console.WriteLine(string.Format("Type: {0}, Method: {1}, IL len: {2}", t.FullName, m.ToString(), len));
                    }
                }
            }
        }
    }
}
