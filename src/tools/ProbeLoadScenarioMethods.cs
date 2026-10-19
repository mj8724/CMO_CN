
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var ls = asm.GetType("Command.LoadScenario");
        var dtNode = asm.GetType("DarkUI.Controls.DarkTreeNode");
        var getTag = dtNode.GetProperty("Tag").GetGetMethod();
        var getText = dtNode.GetProperty("Text").GetGetMethod();
        var getFullPath = dtNode.GetProperty("FullPath").GetGetMethod();
        Console.WriteLine(string.Format("get_Tag token: 0x{0:X8}", getTag.MetadataToken));
        Console.WriteLine(string.Format("get_Text token: 0x{0:X8}", getText.MetadataToken));
        Console.WriteLine(string.Format("get_FullPath token: 0x{0:X8}", getFullPath.MetadataToken));

        foreach (var m in ls.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            var body = m.GetMethodBody();
            if (body == null) continue;
            byte[] il = body.GetILAsByteArray();
            for (int i = 0; i < il.Length - 4; i++) {
                if (il[i] == 0x28 || il[i] == 0x6F) {
                    int token = BitConverter.ToInt32(il, i + 1);
                    if (token == getTag.MetadataToken) Console.WriteLine("Method " + m.Name + " calls get_Tag!");
                    if (token == getText.MetadataToken) Console.WriteLine("Method " + m.Name + " calls get_Text!");
                    if (token == getFullPath.MetadataToken) Console.WriteLine("Method " + m.Name + " calls get_FullPath!");
                }
            }
        }
    }
}
