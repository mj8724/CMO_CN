// R45：翻译必须**幂等**，否则界面会越翻越长（用户实测的"频闪"另一半）。
//
// 真实事故（2026-10-19，WPF_TEXTLOG 抓到）：
//   'ACTIVE'        -> '主动 (Active)'
//   '主动 (Active)' -> '主动 (主动 (Active))'   ← 每刷新一次多裹一层
// 原因是括号片段翻译把"主动 (Active)"当英文载荷再嵌一层。游戏每个脉冲都会把界面文本
// 刷回并重新触发翻译，任何非幂等规则都会表现成频闪/文本变长。
//
// 这里同时门禁两类缺陷：
//   ① 幂等：T(x) == T(T(x))，覆盖括号、冒号混合、组合名、纯英文、纯中文；
//   ② 半译：`状态: Unassigned` 这种「中文标签 + 英文值」必须整条变成中文，
//      而不是因为"整串含中文"就早退（旧守卫的实际后果）。
using System;
using System.IO;
using System.Reflection;

class IdempotencyTests
{
    static int failures;

    /// <summary>统计成对括号数量，用来判断"每刷一次多裹一层"。</summary>
    static int CountBrackets(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0;
        for (int i = 0; i < s.Length; i++) if (s[i] == '(' || s[i] == '[' || s[i] == '（') n++;
        return n;
    }

    static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + label + " | " + detail);
        if (!ok) failures++;
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string dll = args.Length > 1 ? args[1] : Path.Combine(root, "build", "CMOLocalizer.dll");
        Assembly asm = Assembly.LoadFrom(dll);
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (loc == null) { Console.WriteLine("FAIL: type not found"); return 1; }

        MethodInfo load = loc.GetMethod("LoadDictionaries",
                                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (load != null) load.Invoke(null, new object[] { Path.Combine(root, "dict", "CMOLocalizer.json") });
        MethodInfo tr = loc.GetMethod("TranslateString", BindingFlags.Static | BindingFlags.Public);
        if (tr == null) { Console.WriteLine("FAIL: TranslateString not found"); return 1; }
        Func<string, string> T = s => (string)tr.Invoke(null, new object[] { s });

        // ① 幂等：第二次翻译不得再改变结果
        string[] corpus =
        {
            "ACTIVE", "PASSIVE", "Active", "Passive",
            "主动 (Active)", "被动 (Passive)", "主动 (主动 (Active))",
            "状态: Unassigned", "状态：Unassigned", "Status: Unassigned",
            "所属阵营: Israel", "所属基地: Nothing",
            "151 ft", "Airfield (73 units)", "AvGas (780k Liter Underground Tank)",
            "Hardened Aircraft Shelter (6x Medium Aircraft)",
            "Inherited, 持续制导至命中", "Inherited, Tight",
            "Runway", "跑道", "跑道 (2600m)", "Nothing", "No unit selected",
        };
        int unstable = 0;
        foreach (string raw in corpus)
        {
            string once = T(raw);
            string twice = T(once);
            if (once != twice)
            {
                unstable++;
                Console.WriteLine("     不幂等: " + raw + " -> " + once + " -> " + twice);
            }
        }
        Check("幂等 T(x)==T(T(x))", unstable == 0, "样本 " + corpus.Length + " 条，不稳定 " + unstable + " 条");

        // ② 反复翻译不得让文本「越长越啰嗦」。
        // 注意：`Active` -> `主动 (Active)` 是本项目**刻意**的「中文（英文提示）」写法，
        // 它只加一次就稳定，属于正常。真正的缺陷是每刷一次都再裹一层，所以判据是
        // 括号层数在连续三次翻译中必须收敛，而不是"结果不许比原文长"。
        int grown = 0;
        foreach (string raw in corpus)
        {
            string t1 = T(raw);
            string t2 = T(t1);
            string t3 = T(t2);
            if (CountBrackets(t1) != CountBrackets(t2) || CountBrackets(t2) != CountBrackets(t3))
            {
                grown++;
                Console.WriteLine("     括号增长: " + raw + " -> " + t1 + " -> " + t2 + " -> " + t3);
            }
        }
        Check("括号层数收敛", grown == 0, "增长 " + grown + " 条");

        // ③ 具体形状必须整条汉化（不是半译，也不是只翻标签）
        Check("中文标签+英文值", T("状态: Unassigned") == "状态：未分配任务", T("状态: Unassigned"));
        Check("中文标签+英文值 2", T("所属阵营: Israel") == "所属阵营：以色列", T("所属阵营: Israel"));
        Check("纯英文冒号", T("Status: Unassigned") == "状态：未分配任务", T("Status: Unassigned"));
        Check("括号不再叠加", T("主动 (Active)") == "主动 (Active)", T("主动 (Active)"));
        Check("布尔值", T("Nothing") == "无", T("Nothing"));

        Console.WriteLine(failures == 0 ? "ALL PASS" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 2;
    }
}
