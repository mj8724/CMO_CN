
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

        MethodInfo decryptMethod = null;
        foreach (Type t in asm.GetTypes()) {
            if (t.FullName.Contains("AoIBWWlDJbaf7LijnA")) {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)) {
                    var pars = m.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(int) && m.ReturnType == typeof(string)) {
                        decryptMethod = m;
                        break;
                    }
                }
                break;
            }
        }

        HashSet<int> dalOffsets = new HashSet<int>();
        foreach (Type t in asm.GetTypes()) {
            if (t.FullName != null && t.FullName.StartsWith("Command_Core.DAL")) {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    MethodBody body = null;
                    try { body = m.GetMethodBody(); } catch {}
                    if (body == null) continue;
                    byte[] il = body.GetILAsByteArray();
                    for (int i = 0; i < il.Length - 5; i++) {
                        if (il[i] == 0x20 && il[i + 5] == 0x28) {
                            int val = BitConverter.ToInt32(il, i + 1);
                            dalOffsets.Add(val);
                        }
                    }
                }
            }
        }

        Console.WriteLine("Total string offsets referenced in Command_Core.DAL: " + dalOffsets.Count);
        // Write all these offsets and their decoded strings to a file
        using (var sw = new StreamWriter(@"projects\汉化\dal_offsets.txt")) {
            foreach (int off in dalOffsets) {
                string strVal = "";
                try {
                    if (decryptMethod != null) strVal = (string)decryptMethod.Invoke(null, new object[] { off });
                } catch (Exception ex) { strVal = "ERR: " + ex.Message; }
                sw.WriteLine(off + "\t" + strVal);
            }
        }
        Console.WriteLine("Wrote dal_offsets.txt successfully.");
    }
}
