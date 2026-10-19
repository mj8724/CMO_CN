
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\CMOLocalizer.dll");
        foreach (var t in asm.GetTypes()) {
            Console.WriteLine("Type: " + t.FullName);
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                Console.WriteLine("  Method: " + m.Name);
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                Console.WriteLine("  Field: " + f.Name + " (" + f.FieldType.Name + ")");
            }
        }
    }
}
