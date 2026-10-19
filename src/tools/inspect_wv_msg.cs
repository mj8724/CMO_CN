
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asmWv = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.WinForms.dll"));
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.Core.dll"));
        Type tWv = asmWv.GetType("Microsoft.Web.WebView2.WinForms.WebView2");
        EventInfo ev = tWv.GetEvent("WebMessageReceived");
        Console.WriteLine("WebMessageReceived Handler: " + ev.EventHandlerType.FullName);
        Type tArgs = ev.EventHandlerType.GetGenericArguments()[0];
        Console.WriteLine("Args type: " + tArgs.FullName);
        foreach (var m in tArgs.GetMethods()) {
            Console.WriteLine("  Args method: " + m.Name + " -> " + m.ReturnType.Name);
        }
    }
}
