
using System;
using System.Windows.Forms;
using System.Reflection;
using System.Collections;

class Program
{
    static void Main()
    {
        // 探测加载想定窗口 (ScenLoad) 的控件与数据
        // 游戏当前在运行，我们可以写一个注入dll或直接查看窗体类结构
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        foreach (var t in asm.GetTypes())
        {
            if (t.Name.ToLower().Contains("scenload") || t.Name.ToLower().Contains("loadscen"))
            {
                Console.WriteLine("Found form type: " + t.FullName);
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (typeof(Control).IsAssignableFrom(f.FieldType))
                    {
                        Console.WriteLine("  Control Field: " + f.Name + " (" + f.FieldType.FullName + ")");
                    }
                }
            }
        }
    }
}
