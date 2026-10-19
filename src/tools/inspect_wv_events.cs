
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asmWv = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.WinForms.dll"));
        Type t = asmWv.GetType("Microsoft.Web.WebView2.WinForms.WebView2");
        Console.WriteLine("=== WebView2 Events ===");
        foreach (EventInfo ev in t.GetEvents()) {
            Console.WriteLine("  " + ev.Name + " (" + ev.EventHandlerType.Name + ")");
        }
    }
}
