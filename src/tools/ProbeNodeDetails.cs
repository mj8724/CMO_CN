
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var nodeType = asm.GetType("DarkUI.Controls.DarkTreeNode");
        Console.WriteLine("DarkTreeNode Properties:");
        foreach (var p in nodeType.GetProperties()) {
            Console.WriteLine(string.Format("  {0} ({1})", p.Name, p.PropertyType.FullName));
        }
    }
}
