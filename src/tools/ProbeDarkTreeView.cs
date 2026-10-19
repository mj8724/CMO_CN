
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var ls = asm.GetType("Command.LoadScenario");
        var prop = ls.GetProperty("TV_Scens", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var tvType = prop.PropertyType;
        Console.WriteLine("DarkTreeView assembly: " + tvType.Assembly.FullName);
        Console.WriteLine("DarkTreeView BaseType: " + tvType.BaseType.FullName);
        foreach (var p in tvType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
            Console.WriteLine("  TV Prop: " + p.PropertyType.FullName + " " + p.Name);
        }
        var nodesProp = tvType.GetProperty("Nodes");
        if (nodesProp != null) {
            var elemType = nodesProp.PropertyType.GetGenericArguments()[0];
            Console.WriteLine("Node type: " + elemType.FullName);
            foreach (var p in elemType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                Console.WriteLine("  Node Prop: " + p.PropertyType.FullName + " " + p.Name);
            }
            foreach (var f in elemType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                Console.WriteLine("  Node Field: " + f.FieldType.FullName + " " + f.Name);
            }
        }
    }
}
