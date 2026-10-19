
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
        
        foreach (string tName in new[] { "Command.LoadScenario", "Command.SelectSide", "Command.BriefingWindow", "Command.SideBriefing" }) {
            Type t = asm.GetType(tName);
            if (t == null) continue;
            Console.WriteLine("=== Type: " + t.FullName + " ===");
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                var ps = m.GetParameters();
                string pstr = "";
                foreach (var p in ps) pstr += p.ParameterType.Name + " " + p.Name + ", ";
                Console.WriteLine("  " + m.ReturnType.Name + " " + m.Name + "(" + pstr + ")");
            }
        }
    }
}
