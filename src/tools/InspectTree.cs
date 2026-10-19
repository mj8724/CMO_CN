
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\CodersLab.Windows.Controls.TreeView.dll");
        foreach (var t in asm.GetExportedTypes()) {
            Console.WriteLine("Type: " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.FullName : ""));
            foreach (var p in t.GetProperties()) {
                Console.WriteLine("  Prop: " + p.Name + " (" + p.PropertyType.FullName + ")");
            }
        }
    }
}
