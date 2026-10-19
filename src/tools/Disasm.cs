
using System;
using System.Reflection;

class Disasm {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        MethodInfo m = t.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        byte[] il = m.GetMethodBody().GetILAsByteArray();
        
        int i = 0;
        while (i < il.Length) {
            int start = i;
            byte op = il[i++];
            if (op == 0xFE) {
                byte op2 = il[i++];
                Console.WriteLine(string.Format("{0:X3}: FE {1:X2}", start, op2));
                continue;
            }
            // 4-byte token/int ops
            if (op == 0x20 || op == 0x28 || op == 0x6F || op == 0x72 || op == 0x73 || op == 0x74 || op == 0x7E || op == 0x80 || op == 0x8D || op == 0x38 || op == 0x39 || op == 0x3A || op == 0x3B || op == 0x3C || op == 0x3D || op == 0x3E || op == 0x3F || op == 0x40 || op == 0xDD) {
                int val = BitConverter.ToInt32(il, i);
                i += 4;
                string info = "";
                if (op == 0x28 || op == 0x6F || op == 0x73 || op == 0x7E || op == 0x80) {
                    try {
                        MemberInfo mem = mod.ResolveMember(val);
                        info = mem.DeclaringType.Name + "." + mem.Name;
                    } catch {}
                } else if (op == 0x72) {
                    try {
                        info = "\"" + mod.ResolveString(val) + "\"";
                    } catch {}
                }
                Console.WriteLine(string.Format("{0:X3}: Op {1:X2} Val {2:X8} {3}", start, op, val, info));
            } else if (op == 0x11 || op == 0x12 || op == 0x13 || op == 0x1F || op == 0x2B || op == 0x2C || op == 0x2D || op == 0x2E || op == 0x2F || op == 0x30 || op == 0x31 || op == 0x32 || op == 0x33 || op == 0xDE) {
                byte b = il[i++];
                Console.WriteLine(string.Format("{0:X3}: Op {1:X2} Byte {2:X2}", start, op, b));
            } else {
                Console.WriteLine(string.Format("{0:X3}: Op {1:X2}", start, op));
            }
        }
    }
}
