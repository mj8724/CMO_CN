
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
        Type tHelper = asmCore.GetType("Command_Core.WebBrowserHelper");
        if (tHelper != null) {
            Console.WriteLine("Command_Core.WebBrowserHelper found!");
            foreach (MethodInfo m in tHelper.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
                Console.WriteLine("  Method: " + m.Name);
            }
        }
        
        Assembly asmExe = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        Type tMod1 = asmExe.GetType("Command.Module1");
        if (tMod1 != null) {
            Console.WriteLine("Command.Module1 found!");
            foreach (MethodInfo m in tMod1.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
                if (m.Name.Contains("HTML") || m.Name.Contains("Render")) {
                    Console.WriteLine("  Method: " + m.Name);
                }
            }
        }
    }
}
