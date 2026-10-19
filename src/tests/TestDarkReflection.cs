
using System;
using System.Collections;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var tvType = asm.GetType("DarkUI.Controls.DarkTreeView");
        var nodeType = asm.GetType("DarkUI.Controls.DarkTreeNode");
        
        var nodesProp = tvType.GetProperty("Nodes");
        Console.WriteLine("tvType.Nodes prop: " + (nodesProp != null ? nodesProp.PropertyType.FullName : "null"));
        
        var nodeNodesProp = nodeType.GetProperty("Nodes");
        Console.WriteLine("nodeType.Nodes prop: " + (nodeNodesProp != null ? nodeNodesProp.PropertyType.FullName : "null"));
        
        var textProp = nodeType.GetProperty("Text");
        Console.WriteLine("nodeType.Text prop: " + (textProp != null ? textProp.PropertyType.FullName : "null"));
    }
}
