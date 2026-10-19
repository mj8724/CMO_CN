
using System;
using System.Reflection;

class InspectDetail {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        MethodBase mbCaller = mod.ResolveMethod(0x0601230A);
        Console.WriteLine("0x0601230A is: " + mbCaller.DeclaringType.FullName + "." + mbCaller.Name);

        // Let's also check what methods in the whole assembly return System.String and take 1 parameter (Int32)!
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }
        foreach (Type t in types) {
            if (t == null) continue;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                try {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.ReturnType == typeof(string) && ps.Length == 1 && ps[0].ParameterType == typeof(int)) {
                        Console.WriteLine(string.Format("String(int) method: {0}.{1} (Token {2:X8})", t.FullName, m.Name, m.MetadataToken));
                    }
                } catch {}
            }
        }
    }
}
