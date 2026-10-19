
using System;
using System.Reflection;

class InspectFormIL {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type adv = asm.GetType("Command.AdvancedDialog");
        Console.WriteLine("Methods in Command.AdvancedDialog:");
        foreach (MethodInfo m in adv.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            MethodBody mb = m.GetMethodBody();
            if (mb != null && mb.GetILAsByteArray().Length > 100) {
                Console.WriteLine("  Method: " + m.Name + " IL len: " + mb.GetILAsByteArray().Length);
                byte[] il = mb.GetILAsByteArray();
                int shown = 0;
                for (int i = 0; i < il.Length - 5 && shown < 25; i++) {
                    byte op = il[i];
                    if (op == 0x28 || op == 0x6F || op == 0x7E || op == 0x72) {
                        int tok = BitConverter.ToInt32(il, i + 1);
                        try {
                            if (op == 0x72) {
                                Console.WriteLine(string.Format("    [{0:X4}] ldstr: {1}", i, mod.ResolveString(tok)));
                                shown++;
                            } else if ((tok >> 24) == 0x06 || (tok >> 24) == 0x0A || (tok >> 24) == 0x04) {
                                MemberInfo mem = mod.ResolveMember(tok);
                                Console.WriteLine(string.Format("    [{0:X4}] op {1:X2}: {2}.{3} ({4:X8})", i, op, mem.DeclaringType != null ? mem.DeclaringType.Name : "", mem.Name, tok));
                                shown++;
                            }
                        } catch {}
                    }
                }
                if (shown > 0) break;
            }
        }
    }
}
