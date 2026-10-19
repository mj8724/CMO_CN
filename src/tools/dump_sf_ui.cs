
using System;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;

class Program {
    [STAThread]
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string name = new AssemblyName(a.Name).Name;
            string p1 = Path.Combine(gameDir, name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            return null;
        };

        Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        Type t = asm.GetType("Command.ScenarioFeatures");
        if (t != null) {
            try {
                object win = Activator.CreateInstance(t);
                Console.WriteLine("Created ScenarioFeatures successfully!");
                Window w = (Window)win;
                DumpVisual(w, 0);
            } catch (Exception ex) {
                Console.WriteLine("Instantiate error: " + ex);
            }
        }
    }

    static void DumpVisual(DependencyObject dep, int indent) {
        if (dep == null) return;
        string prefix = new string(' ', indent * 2);
        if (dep is ContentControl) {
            ContentControl cc = (ContentControl)dep;
            if (cc.Content != null) Console.WriteLine(prefix + dep.GetType().Name + " [Content]: " + cc.Content);
        } else if (dep is TextBlock) {
            TextBlock tb = (TextBlock)dep;
            Console.WriteLine(prefix + "TextBlock [Text]: " + tb.Text);
        } else if (dep is HeaderedContentControl) {
            HeaderedContentControl hcc = (HeaderedContentControl)dep;
            Console.WriteLine(prefix + dep.GetType().Name + " [Header]: " + hcc.Header);
        } else {
            Console.WriteLine(prefix + dep.GetType().Name);
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(dep);
        for (int i = 0; i < count; i++) {
            DumpVisual(System.Windows.Media.VisualTreeHelper.GetChild(dep, i), indent + 1);
        }
    }
}
