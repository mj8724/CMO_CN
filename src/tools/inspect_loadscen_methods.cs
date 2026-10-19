
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
        Assembly asmExe = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        Type tLoad = asmExe.GetType("Command.LoadScenario");
        foreach (MethodInfo m in tLoad.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
            MethodBody mb = m.GetMethodBody();
            if (mb != null) {
                byte[] il = mb.GetILAsByteArray();
                // 搜索是否使用了 RHMSxzGfLbH (WebView2 字段)
                // 查看方法名
                string name = m.Name;
                if (name.Contains("Selected") || name.Contains("Node") || name.Contains("Scen") || name.Contains("Click")) {
                    Console.WriteLine("Method: " + name + " (IL len: " + il.Length + ")");
                }
            }
        }
    }
}
