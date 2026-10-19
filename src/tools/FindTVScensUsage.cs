
using System;
using System.Reflection;

class P {
    static void Main() {
        var asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        var ls = asm.GetType("Command.LoadScenario");
        var tvProp = ls.GetProperty("TV_Scens", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var getter = tvProp.GetGetMethod(true);

        foreach (var m in ls.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
            var body = m.GetMethodBody();
            if (body == null) continue;
            byte[] il = body.GetILAsByteArray();
            for (int i = 0; i < il.Length - 4; i++) {
                if (il[i] == 0x28 || il[i] == 0x6F) {
                    int token = BitConverter.ToInt32(il, i + 1);
                    try {
                        var target = asm.ManifestModule.ResolveMethod(token);
                        if (target == getter) {
                            Console.WriteLine(string.Format("Caller in LoadScenario: {0}", m.Name));
                        }
                    } catch {}
                }
            }
        }
    }
}
