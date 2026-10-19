
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
            if (t.FullName == "Command.LoadScenario") {
                Console.WriteLine("=== LoadScenario Fields ===");
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                    Console.WriteLine("  Field: " + f.Name + " : " + f.FieldType.FullName);
                }
                Console.WriteLine("=== LoadScenario Methods ===");
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                        Console.WriteLine("  Method: " + m.Name);
                }
            }
        }
    }
}
