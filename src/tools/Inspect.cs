
using System;
using System.Reflection;

class Inspect {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        Console.WriteLine("Type: " + t.FullName);
        foreach (FieldInfo f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) {
            Console.WriteLine("Field: " + f.Name + " (" + f.FieldType + ")");
        }
        foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) {
            Console.WriteLine("Method: " + m.Name + " -> " + m.ReturnType);
            MethodBody mb = m.GetMethodBody();
            if (mb != null) {
                byte[] il = mb.GetILAsByteArray();
                Console.WriteLine("  IL len: " + il.Length + " bytes: " + BitConverter.ToString(il));
            }
        }
    }
}
