
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string name = new AssemblyName(a.Name).Name;
            string p1 = Path.Combine(gameDir, name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            return null;
        };
        try {
            Assembly webviewAsm = Assembly.LoadFrom(Path.Combine(gameDir, "Microsoft.Web.WebView2.WinForms.dll"));
            Type wvType = webviewAsm.GetType("Microsoft.Web.WebView2.WinForms.WebView2");
            Console.WriteLine("WebView2 type: " + wvType.FullName);
            foreach (PropertyInfo p in wvType.GetProperties()) {
                if (p.Name.Contains("Core") || p.Name.Contains("Script")) Console.WriteLine("  WV Prop: " + p.Name + " (" + p.PropertyType.FullName + ")");
            }
            foreach (MethodInfo m in wvType.GetMethods()) {
                if (m.Name.Contains("Script")) Console.WriteLine("  WV Method: " + m.Name);
            }
        } catch (Exception ex) {
            Console.WriteLine("WebView2 load error: " + ex.Message);
        }
    }
}
