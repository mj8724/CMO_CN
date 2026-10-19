
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asmWv = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.WinForms.dll"));
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.Core.dll"));
        
        Type tWv = asmWv.GetType("Microsoft.Web.WebView2.WinForms.WebView2");
        Console.WriteLine("=== WebView2 Methods ===");
        foreach (MethodInfo m in tWv.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            Console.WriteLine("  " + m.ReturnType.Name + " " + m.Name);
        }
        
        Type tCore = asmCore.GetType("Microsoft.Web.WebView2.Core.CoreWebView2");
        Console.WriteLine("=== CoreWebView2 Methods ===");
        foreach (MethodInfo m in tCore.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
            if (m.Name.Contains("Script") || m.Name.Contains("Message") || m.Name.Contains("Navigate")) {
                Console.WriteLine("  " + m.ReturnType.Name + " " + m.Name);
            }
        }
    }
}
