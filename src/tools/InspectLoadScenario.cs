
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        foreach (string name in new[] { "Command.LoadScenario", "Command.CampaignScenarioWindow" }) {
            var t = asm.GetType(name);
            if (t == null) continue;
            Console.WriteLine("=== " + t.FullName + " ===");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                Console.WriteLine("  Field: " + f.FieldType.FullName + " " + f.Name);
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                Console.WriteLine("  Prop: " + p.PropertyType.FullName + " " + p.Name);
            }
        }
    }
}
