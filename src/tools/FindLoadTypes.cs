
using System;
using System.Reflection;

class P {
    static void Main() {
        try {
            var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
            foreach (var t in asm.GetTypes()) {
                string name = t.Name.ToLower();
                if (name.Contains("scenario") || name.Contains("load") || name.Contains("tree")) {
                    Console.WriteLine(t.FullName + " -> Base: " + (t.BaseType != null ? t.BaseType.FullName : ""));
                }
            }
        } catch (ReflectionTypeLoadException ex) {
            foreach (var t in ex.Types) {
                if (t != null) {
                    string name = t.Name.ToLower();
                    if (name.Contains("scenario") || name.Contains("load") || name.Contains("tree")) {
                        Console.WriteLine(t.FullName + " -> Base: " + (t.BaseType != null ? t.BaseType.FullName : ""));
                    }
                }
            }
        }
    }
}
