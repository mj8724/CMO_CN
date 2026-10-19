
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
        Type tDecrypt = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU", false);
        Console.WriteLine("Command.exe tDecrypt: " + (tDecrypt != null));
        
        HashSet<int> dalOffsets = new HashSet<int>();
        foreach (Type t in asm.GetTypes()) {
            string ns = t.Namespace ?? "";
            string tn = t.FullName ?? "";
            bool isDalClass = ns.Contains("DAL") || tn.Contains("DBFunctions") || tn.Contains("SQLite") || tn.Contains("Xml") || tn.Contains("XML");
            
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                string mn = m.Name;
                bool isDalMethod = isDalClass || mn.Contains("XML") || mn.Contains("Xml") || mn.Contains("SQL") || mn.Contains("DB") || mn.StartsWith("Get") || mn.StartsWith("Load");
                if (!isDalClass && !(mn.Contains("XML") || mn.Contains("Xml") || mn.Contains("DB") || mn.Contains("DataTable") || mn.Contains("DataRow"))) continue;
                
                try {
                    MethodBody mb = m.GetMethodBody();
                    if (mb == null) continue;
                    byte[] il = mb.GetILAsByteArray();
                    for (int i = 0; i < il.Length - 5; i++) {
                        // ldc.i4 (0x20) followed by 4-byte int, then call (0x28)
                        if (il[i] == 0x20 && il[i+5] == 0x28) {
                            int val = BitConverter.ToInt32(il, i + 1);
                            if (val >= 0 && val < 3000000) {
                                dalOffsets.Add(val);
                            }
                        }
                    }
                } catch {}
            }
        }
        Console.WriteLine("Found DAL/XML offsets count: " + dalOffsets.Count);
        File.WriteAllLines(@"projects\汉化\dal_offsets.txt", Array.ConvertAll(new List<int>(dalOffsets).ToArray(), x => x.ToString()));
    }
}
