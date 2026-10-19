
import subprocess, os

# We can search decompiled IL or write a small C# program that uses System.Reflection to inspect MethodBody instructions
csharp = r"""
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
        
        CheckAsm(Path.Combine(gameDir, "Command.exe"));
        CheckAsm(Path.Combine(gameDir, "Command_Core.dll"));
    }
    
    static void CheckAsm(string path) {
        Assembly asm = Assembly.LoadFrom(path);
        foreach (Type t in asm.GetTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) {
                try {
                    MethodBody mb = m.GetMethodBody();
                    if (mb == null) continue;
                    byte[] il = mb.GetILAsByteArray();
                    // We can check if any method name called contains "NavigateToString"
                    // But reflection doesn't easily resolve tokens without dnlib, so let's check method names directly:
                    if (m.Name.IndexOf("Briefing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("HTML", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.Name.IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0) {
                        Console.WriteLine(t.FullName + "." + m.Name);
                    }
                } catch {}
            }
        }
    }
}
"""
with open(r"projects\汉化\search_methods.cs", "w", encoding="utf-8") as f:
    f.write(csharp)

subprocess.run([r"C:\Windows\Microsoft.NET\Framework644.0.30319\csc.exe", "/platform:x64", "/out:projects\汉化\search_methods.exe", r"projects\汉化\search_methods.cs"], check=True)
res = subprocess.run([r"projects\汉化\search_methods.exe"], capture_output=True, text=True)
print(res.stdout[:2000])
