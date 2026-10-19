using System;
using System.Reflection;

class Program {
    static void Main() {
        Assembly asm = Assembly.LoadFrom(@"G:\soft\steam\steamapps\common\Command - Modern Operations\CMOLocalizer.dll");
        Type t = asm.GetType("CMOLocalizer.LocalizerEngine");
        MethodInfo loadDict = t.GetMethod("LoadDictionary", BindingFlags.NonPublic | BindingFlags.Static);
        loadDict.Invoke(null, null);
        MethodInfo trans = t.GetMethod("TranslateString", BindingFlags.Public | BindingFlags.Static);

        string[] samples = new string[] {
            "Database Viewer",
            "Fixed Wing",
            "Tiltrotor",
            "Air-Independent Propulsion (AIP)",
            "5th Generation Jet (2010s) [Stealth]",
            "SAR (Synthetic Aperture Radar)",
            "Active Radar Homing (ARH)",
            "Vertical Launch System (VLS)",
            "Over-the-Horizon Backscatter (OTH-B)",
            "Start New Game",
            "Campaigns",
            "Orbat:"
        };

        foreach (string s in samples) {
            string res = (string)trans.Invoke(null, new object[] { s });
            Console.WriteLine(s + "  ===>  " + res);
        }
    }
}
