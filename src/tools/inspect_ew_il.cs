
using System;
using System.Reflection;
using System.IO;
using System.Collections.Generic;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        AppDomain.CurrentDomain.AssemblyResolve += (s, a) => {
            string name = new AssemblyName(a.Name).Name;
            string p1 = Path.Combine(gameDir, name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            return null;
        };
        
        Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        Type tDb = asm.GetType("Command_Core.DAL.DBFunctions");
        Console.WriteLine("tDb in Command.exe: " + (tDb != null));
        
        if (tDb != null) {
            MethodInfo mEW = tDb.GetMethod("EW_GetAllPossibleEmissionsForUnits", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (mEW != null) {
                byte[] il = mEW.GetMethodBody().GetILAsByteArray();
                Console.WriteLine("EW IL len: " + il.Length);
                for (int i = 0; i < Math.Min(100, il.Length); i++) {
                    Console.Write(il[i].ToString("X2") + " ");
                }
                Console.WriteLine();
            }
        }
    }
}
