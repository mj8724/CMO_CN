
using System;
using System.Reflection;
using System.Windows.Forms;

class P {
    static void Main() {
        var cmdAsm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        foreach (var t in cmdAsm.GetTypes()) {
            try {
                if (typeof(Form).IsAssignableFrom(t) || typeof(UserControl).IsAssignableFrom(t)) {
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                        if (f.FieldType.Name.Contains("TreeView") || f.FieldType.Name.Contains("ListView") || f.FieldType.Name.Contains("Tree")) {
                            Console.WriteLine(t.FullName + " -> " + f.Name + " (" + f.FieldType.FullName + ")");
                        }
                    }
                }
            } catch {}
        }
    }
}
