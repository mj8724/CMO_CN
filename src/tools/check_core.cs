
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Command_Core.dll"));
        Console.WriteLine("Command_Core FullName: " + asmCore.FullName);
        
        Type tDb = asmCore.GetType("Command_Core.DAL.DBFunctions");
        Console.WriteLine("DBFunctions: " + (tDb != null));
        
        // Check for SmartAssembly in Command_Core
        foreach (Type t in asmCore.GetTypes()) {
            if (t.FullName.Contains("SmartAssembly") || (t.Namespace != null && t.Namespace.Length > 15 && t.Name.Length > 15)) {
                // Console.WriteLine("Obf type: " + t.FullName);
            }
        }
    }
}
