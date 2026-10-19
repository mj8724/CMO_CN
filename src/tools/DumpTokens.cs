
using System;
using System.Reflection;

class DumpTokens {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        MethodInfo m = t.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        byte[] il = m.GetMethodBody().GetILAsByteArray();
        
        for (int i = 0; i < il.Length - 4; i++) {
            byte op = il[i];
            // ldsfld=0x7E, stsfld=0x80, call=0x28, callvirt=0x6F
            if (op == 0x7E || op == 0x80 || op == 0x28 || op == 0x6F) {
                int token = BitConverter.ToInt32(il, i + 1);
                byte table = (byte)(token >> 24);
                if (table == 0x04 || table == 0x0A || table == 0x06) {
                    try {
                        MemberInfo mem = mod.ResolveMember(token);
                        Console.WriteLine(string.Format("Offset {0:X3} Op {1:X2}: {2} ({3})", i, op, mem.Name, mem.DeclaringType));
                    } catch {}
                }
            }
        }
    }
}
