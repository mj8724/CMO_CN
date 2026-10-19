
using System;
using System.Reflection;

class InspectG7 {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Type t = asm.GetType("EJsBjqD3uVy8IwCD9w.G7JZLAxiZym8sY748W");
        if (t == null) {
            Console.WriteLine("Type null");
            return;
        }
        Console.WriteLine("G7JZLAxiZym8sY748W Methods:");
        foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
            Console.WriteLine(string.Format("  {0} -> {1} (Token: {2:X8})", m.Name, m.ReturnType.Name, m.MetadataToken));
            MethodBody mb = m.GetMethodBody();
            if (mb != null) {
                Console.WriteLine("    IL length: " + mb.GetILAsByteArray().Length);
            }
        }
        Console.WriteLine("G7JZLAxiZym8sY748W Fields:");
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)) {
            Console.WriteLine(string.Format("  {0} : {1}", f.Name, f.FieldType.Name));
        }
    }
}
