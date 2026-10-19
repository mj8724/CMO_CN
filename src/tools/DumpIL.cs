
using System;
using System.Reflection;
using System.Reflection.Emit;

class DumpIL {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        MethodInfo m = t.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        MethodBody mb = m.GetMethodBody();
        byte[] il = mb.GetILAsByteArray();
        Console.WriteLine("iX38Xvt6D IL Length: " + il.Length);
        
        // Print local variables
        Console.WriteLine("Locals count: " + mb.LocalVariables.Count);
        foreach (LocalVariableInfo l in mb.LocalVariables) {
            Console.WriteLine("  Local " + l.LocalIndex + ": " + l.LocalType);
        }
    }
}
