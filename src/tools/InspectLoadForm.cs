
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        foreach (var t in asm.GetTypes()) {
            if (t.Name.Contains("LoadScenario") || t.Name.Contains("ScenLoad") || t.Name.Contains("Campaign")) {
                Console.WriteLine("Type: " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.FullName : ""));
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                    Console.WriteLine("  Field: " + f.Name + " (" + f.FieldType.FullName + ")");
                }
            }
        }
    }
}
