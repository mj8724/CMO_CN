// R42：「英文前缀 + 已汉化尾」混合串的**接线**回归。
//
// 背景（2026-10-19 用户实测）：R39 只验证了 TranslateString 这个函数本身，
// 而组合框/列表框的条目循环在调用翻译之前就 `if (ContainsChinese(raw)) continue;`，
// 于是「Inherited, 持续制导至命中」这类整串含中文的条目永远到不了修正逻辑——
// 函数是好的、界面还是英文。所以这里既测函数，也测修正逻辑相对「含中文就跳过」
// 守卫的位置（接线），否则同样的回归可以再次静默发生。
using System;
using System.IO;
using System.Reflection;

class MixedPrefixTests
{
    static int failures;

    static void Check(string label, string actual, string expected)
    {
        bool ok = expected == null ? actual == null : expected == actual;
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + label
                          + " | expected=" + (expected ?? "<null>")
                          + " actual=" + (actual ?? "<null>"));
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        // 第二个参数是可选的引擎 DLL 路径：回归用例总用自己刚编译的产物，
        // 不去读仓库里可能过期的 build/CMOLocalizer.dll（否则门禁会给出假绿）。
        string dll = args.Length > 1 ? args[1] : Path.Combine(root, "build", "CMOLocalizer.dll");
        Assembly asm = Assembly.LoadFrom(dll);
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (loc == null)
        {
            Console.WriteLine("FAIL: CMOLocalizer.CustomAppDomainManager not found");
            return 1;
        }

        MethodInfo load = loc.GetMethod("LoadDictionaries",
                                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (load != null) load.Invoke(null, new object[] { Path.Combine(root, "dict", "CMOLocalizer.json") });

        MethodInfo mixed = loc.GetMethod("TryFixMixedBeforeChineseGuard",
                                         BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo trans = loc.GetMethod("TranslateString", BindingFlags.Static | BindingFlags.Public);
        if (mixed == null || trans == null)
        {
            Console.WriteLine("FAIL: TryFixMixedBeforeChineseGuard/TranslateString not found");
            return 1;
        }

        Func<string, string> fix = s => (string)mixed.Invoke(null, new object[] { s });
        Func<string, string> tr = s => (string)trans.Invoke(null, new object[] { s });

        // 混合串：前缀必须换成中文，尾部（已汉化）原样保留
        Check("逗号+空格", fix("Inherited, 持续制导至命中"), "继承自上级，持续制导至命中");
        Check("逗号无空格", fix("Inherited,始终开火"), "继承自上级，始终开火");
        Check("无逗号", fix("Inherited 短航线"), "继承自上级，短航线");
        Check("前导空格", fix("  Inherited,  装载设置  "), "继承自上级，装载设置");

        // 非混合串：返回 null，交给常规翻译路径（否则会把英文尾也当中文保留）
        Check("纯英文尾", fix("Inherited, Tight"), null);
        Check("纯英文", fix("Inherited"), null);
        Check("词边界", fix("InheritedFromDoctrine"), null);
        Check("无中文", fix("Inherited, Automatic for search and localization"), null);
        Check("空串", fix(""), null);
        Check("空值", fix(null), null);

        // 端到端：整串翻译同样要能修前缀（网格单元格 CellFormatting 走这条路）
        Check("端到端 混合", tr("Inherited, 持续制导至命中"), "继承自上级，持续制导至命中");
        string tight = tr("Inherited, Tight");
        Console.WriteLine((tight != "Inherited, Tight" && tight.IndexOf("Inherited") < 0
                           ? "--> OK   " : "--> FAIL ") + "端到端 整串词典: " + tight);
        if (tight == "Inherited, Tight" || tight.IndexOf("Inherited") >= 0) failures++;

        Console.WriteLine(failures == 0 ? "ALL PASS" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 2;
    }
}
