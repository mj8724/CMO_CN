
using System;
using System.Reflection;

class InspectWrapper2 {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type[] types;
        try {
            types = asm.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            types = ex.Types;
        }

        Console.WriteLine("Total types: " + types.Length);
        int formCount = 0;
        foreach (Type t in types) {
            if (t == null) continue;
            if (t.Name.Contains("G7JZLAxiZym8sY748W")) {
                Console.WriteLine("Matched Type: " + t.FullName);
            }
            if (t.BaseType != null && t.BaseType.Name == "Form") {
                formCount++;
                if (formCount == 1) {
                    Console.WriteLine("Sample Form: " + t.FullName);
                    MethodInfo init = t.GetMethod("InitializeComponent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (init != null) {
                        Console.WriteLine("  Has InitializeComponent, body size: " + init.GetMethodBody().GetILAsByteArray().Length);
                    }
                }
            }
        }
        Console.WriteLine("Total Form types: " + formCount);
    }
}
