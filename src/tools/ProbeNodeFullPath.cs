
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var dtNode = asm.GetType("DarkUI.Controls.DarkTreeNode");
        var setText = dtNode.GetProperty("Text").GetSetMethod();
        var body = setText.GetMethodBody();
        Console.WriteLine("set_Text IL length: " + (body != null ? body.GetILAsByteArray().Length.ToString() : "null"));
    }
}
