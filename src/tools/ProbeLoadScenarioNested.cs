
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var dtNode = asm.GetType("DarkUI.Controls.DarkTreeNode");
        var getTag = dtNode.GetProperty("Tag").GetGetMethod();
        var getText = dtNode.GetProperty("Text").GetGetMethod();
        var getFullPath = dtNode.GetProperty("FullPath").GetGetMethod();

        var ls = asm.GetType("Command.LoadScenario");
        var types = new System.Collections.Generic.List<Type>(ls.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        types.Add(ls);

        foreach (var t in types) {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                var body = m.GetMethodBody();
                if (body == null) continue;
                byte[] il = body.GetILAsByteArray();
                for (int i = 0; i < il.Length - 4; i++) {
                    if (il[i] == 0x28 || il[i] == 0x6F) {
                        int token = BitConverter.ToInt32(il, i + 1);
                        try {
                            var target = asm.ManifestModule.ResolveMethod(token);
                            if (target.DeclaringType == dtNode) {
                                Console.WriteLine(string.Format("{0}.{1} calls DarkTreeNode.{2}", t.Name, m.Name, target.Name));
                            }
                        } catch {}
                    }
                }
            }
        }
    }
}
