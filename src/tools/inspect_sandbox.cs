
using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

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

        // Find string decryption method
        Type tStr = null;
        foreach (Type t in asm.GetTypes()) {
            if (t.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                tStr = t;
                Console.WriteLine("Found string pool type: " + t.FullName);
                break;
            }
        }

        // Find LuaSandBox
        foreach (Type t in asm.GetTypes()) {
            if (t.FullName.Contains("LuaSandBox")) {
                Console.WriteLine("Found Type: " + t.FullName);
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    Console.WriteLine("  Method: " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)) + ")");
                    DumpMethodStrings(m, mod);
                }
            }
        }
    }

    static void DumpMethodStrings(MethodInfo m, Module mod) {
        var body = m.GetMethodBody();
        if (body == null) return;
        byte[] il = body.GetILAsByteArray();
        for (int i = 0; i < il.Length - 5; i++) {
            // Check ldc.i4 (0x20) followed by call (0x28)
            if (il[i] == 0x20 && il[i + 5] == 0x28) {
                int val = BitConverter.ToInt32(il, i + 1);
                int token = BitConverter.ToInt32(il, i + 6);
                try {
                    MethodBase called = mod.ResolveMethod(token);
                    if (called != null && called.DeclaringType != null && called.DeclaringType.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                        Console.WriteLine("    -> String Offset: " + val + " (via " + called.Name + ")");
                    }
                } catch {}
            }
        }
    }
}
