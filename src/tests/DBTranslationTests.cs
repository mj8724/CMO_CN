// R33/R39: exercise the real engine, including dynamic database fields and mixed prefixes.
using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

class DBTranslationTests
{
    static int failures;
    static MethodInfo translate;

    static string Translate(string text)
    {
        return (string)translate.Invoke(null, new object[] { text });
    }

    static void Check(string text, string expected)
    {
        string actual = Translate(text);
        bool ok = actual == expected;
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + text + " -> " + actual
                          + (ok ? "" : " expected=" + expected));
        if (!ok) failures++;
    }

    static void CheckTranslated(MethodInfo method, string text, params string[] residuals)
    {
        string actual = (string)method.Invoke(null, new object[] { text });
        bool ok = actual != null && actual != text && Regex.IsMatch(actual, "[\\u4e00-\\u9fff]");
        foreach (string residual in residuals)
            if (actual == null || actual.IndexOf(residual, StringComparison.OrdinalIgnoreCase) >= 0)
                ok = false;
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + text + " -> " + actual);
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        // Require the freshly compiled DLL path: a stale build/ DLL must never make this gate pass.
        string root = Path.GetFullPath(args[0]);
        Assembly asm = Assembly.LoadFrom(Path.GetFullPath(args[1]));
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager", true);
        loc.GetMethod("LoadDictionaries", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
           .Invoke(null, new object[] { Path.Combine(root, "dict", "CMOLocalizer.json") });
        translate = loc.GetMethod("TranslateString", BindingFlags.Static | BindingFlags.Public);
        MethodInfo loadout = loc.GetMethod("TranslateLoadoutValue", BindingFlags.Static | BindingFlags.NonPublic);

        CheckTranslated(translate, "Unmanned Aerial Vehicle (UAV) (WRA: Aircraft - Unspecified)",
                        "Unmanned", "Aerial", "Vehicle", "Aircraft", "Unspecified");
        CheckTranslated(translate, "0 (Autonomy Level: ChangeableMission)", "Autonomy", "Level", "ChangeableMission");
        CheckTranslated(translate, "Undefined (Use Below) (ex: )", "Undefined", "Use Below", "ex:");
        CheckTranslated(loadout, "2200 nm Recon radius. Cruise at optimum altitude and speed. No form-up time. 10% reserves.",
                        "Recon", "radius", "Cruise", "optimum", "altitude", "speed", "form-up", "reserves");
        Check("Comms Monitor and Jammer", "通信监听与干扰");
        CheckTranslated(translate, "1x Generic Comms Jammer Pod [Advanced]", "Generic", "Comms", "Jammer", "Pod", "Advanced");

        Check("Inherited, 始终开火", "继承自上级，始终开火");
        Check("Inherited, 战斗机与反潜战(ASW)", "继承自上级，战斗机与反潜战(ASW)");
        Check("Inherited 持续制导至命中", "继承自上级，持续制导至命中");
        Check("Aircraft - Unspecified", "飞机 - 未指定");
        Check("Multirole (Fighter/Attack)", "多用途 (战斗机与攻击机)");

        Console.WriteLine(failures == 0 ? "ALL PASS" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
