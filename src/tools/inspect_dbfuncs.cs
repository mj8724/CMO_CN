
using System;
using System.Reflection;
using System.IO;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asmCore = Assembly.LoadFrom(Path.Combine(gameDir, "Command_Core.dll"));
        
        Type tDb = asmCore.GetType("Command_Core.DAL.DBFunctions");
        if (tDb != null) {
            MethodInfo mGetShip = tDb.GetMethod("GetShip", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Console.WriteLine("GetShip: " + (mGetShip != null));
            
            MethodInfo mEW = tDb.GetMethod("EW_GetAllPossibleEmissionsForUnits", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Console.WriteLine("EW_GetAllPossibleEmissionsForUnits: " + (mEW != null));
        }
    }
}
