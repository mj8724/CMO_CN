
using System;
using System.Reflection;

class FindStringMethod {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        foreach (Type t in asm.GetTypes()) {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)) {
                if (m.ReturnType == typeof(string)) {
                    ParameterInfo[] pars = m.GetParameters();
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(int)) {
                        Console.WriteLine("String Decryptor Method Candidate: " + t.FullName + "." + m.Name);
                    }
                }
            }
        }
    }
}
