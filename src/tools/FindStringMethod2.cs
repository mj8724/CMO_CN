
using System;
using System.Reflection;

class FindStringMethod2 {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)) {
            ParameterInfo[] pars = m.GetParameters();
            if (m.ReturnType == typeof(string) || m.ReturnType == typeof(object)) {
                if (pars.Length == 1 && pars[0].ParameterType == typeof(int)) {
                    MethodBody mb = m.GetMethodBody();
                    int ilLen = mb != null ? mb.GetILAsByteArray().Length : 0;
                    if (ilLen > 25) {
                        Console.WriteLine("Method: " + m.Name + " (" + pars[0].ParameterType + ") -> " + m.ReturnType + " [IL len=" + ilLen + "]");
                    }
                }
            }
        }
    }
}
