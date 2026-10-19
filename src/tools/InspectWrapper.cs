
using System;
using System.Reflection;

class InspectWrapper {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        foreach (Type t in asm.GetTypes()) {
            if (t.Name == "G7JZLAxiZym8sY748W") {
                Console.WriteLine("Found Type: " + t.FullName);
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    Console.WriteLine("  Method: " + m.Name + " -> " + m.ReturnType + " [Token: " + m.MetadataToken.ToString("X8") + "]");
                }
            }
        }
        // Also check how strings in Command.exe forms are actually loaded! Let's inspect MainForm or a known Form's InitializeComponent!
        foreach (Type t in asm.GetTypes()) {
            if (t.BaseType != null && t.BaseType.Name == "Form") {
                MethodInfo init = t.GetMethod("InitializeComponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (init != null) {
                    Console.WriteLine("Form: " + t.FullName + " has InitializeComponent");
                    byte[] il = init.GetMethodBody().GetILAsByteArray();
                    // Print first 30 calls/ldstr/ldsfld in InitializeComponent
                    int count = 0;
                    for (int i = 0; i < il.Length - 5 && count < 25; i++) {
                        byte op = il[i];
                        if (op == 0x72 || op == 0x28 || op == 0x6F || op == 0x7E) {
                            int tok = BitConverter.ToInt32(il, i + 1);
                            try {
                                if (op == 0x72) {
                                    Console.WriteLine("    ldstr: " + mod.ResolveString(tok));
                                    count++;
                                } else {
                                    MemberInfo mem = mod.ResolveMember(tok);
                                    Console.WriteLine(string.Format("    op {0:X2}: {1}.{2} ({3:X8})", op, mem.DeclaringType != null ? mem.DeclaringType.Name : "", mem.Name, tok));
                                    count++;
                                }
                            } catch {}
                        }
                    }
                    break;
                }
            }
        }
    }
}
