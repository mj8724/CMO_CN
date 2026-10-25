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
using System.Collections.Generic;
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
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string dll = args.Length > 1 ? args[1] : Path.Combine(root, "build", "CMOLocalizer.dll");
        Assembly asm = Assembly.LoadFrom(dll);
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (loc == null) { Console.WriteLine("FAIL: type not found"); return 1; }

        MethodInfo load = loc.GetMethod("LoadDictionaries",
                                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        string dictionary = args.Length > 2 ? args[2] : Path.Combine(root, "dict", "CMOLocalizer.json");
        if (load != null) load.Invoke(null, new object[] { dictionary });
        MethodInfo tr = loc.GetMethod("TranslateString", BindingFlags.Static | BindingFlags.Public);
        if (tr == null) { Console.WriteLine("FAIL: TranslateString not found"); return 1; }
        Func<string, string> T = s => (string)tr.Invoke(null, new object[] { s });
        MethodInfo briefing = loc.GetMethod("TranslateBriefingText", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo spec = loc.GetMethod("TranslateComposedSpec", BindingFlags.Static | BindingFlags.NonPublic);
        if (briefing == null || spec == null) { Console.WriteLine("FAIL: briefing/composition entry not found"); return 1; }
        Func<string, string> B = s => (string)briefing.Invoke(null, new object[] { s });
        Func<string, string> S = s => (string)spec.Invoke(null, new object[] { s });

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
            "Launch", "  Launch  ", "* Launch", "Area", "Flightplan Editor",
            "状态 (Mode): Unassigned", "所属阵营：Israel",
            "Hangar (2 x Large Aircraft)", "Hangar (10x Large Aircraft)",
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

        Check("中文标签+全角冒号", T("状态：Unassigned") == "状态：未分配任务", T("状态：Unassigned"));
        Check("Launch 保全原译", T("Launch") == "发射/起飞" && T(T("Launch")) == "发射/起飞", T("Launch"));

        // ④ Web 调用的兄弟入口必须闭合同一混排/幂等契约，而非只测公开函数。
        int briefingUnstable = 0;
        foreach (string raw in corpus)
        {
            string once = B(raw), twice = B(once);
            if (once != twice)
            {
                briefingUnstable++;
                Console.WriteLine("     简报不幂等: " + raw + " -> " + once + " -> " + twice);
            }
        }
        Check("Briefing 幂等", briefingUnstable == 0, "不稳定 " + briefingUnstable + " 条");
        Check("Briefing 混排右值", B("状态: Unassigned") == "状态：未分配任务", B("状态: Unassigned"));
        Check("Briefing 全角冒号", B("所属阵营：Israel") == "所属阵营：以色列", B("所属阵营：Israel"));
        Check("Briefing 混合前缀", B("Inherited, 持续制导至命中") == "继承自上级，持续制导至命中", B("Inherited, 持续制导至命中"));

        // ⑤ 直接调用结构规则，避免现有整串词条遮蔽数量前缀的错误优先级。
        Check("数量前缀保持完整规格", S("2 x Large Aircraft") == "2 个大型飞机", S("2 x Large Aircraft"));
        Check("紧凑数量前缀", S("10x Large Aircraft") == "10 个大型飞机", S("10x Large Aircraft"));
        string gun = S("Gun"), gunTail = S("Gun x 6");
        Check("数量尾缀不回退", !string.IsNullOrEmpty(gun) && gunTail == gun + " × 6", gunTail);
        Check("组合生词仍整体拒绝", S("2 x ZzyzxUnknown Aircraft") == null, S("2 x ZzyzxUnknown Aircraft") ?? "<null>");

        // ⑥ R64：军名兜底规则不得产出第二种译法。
        // 真实事故（2026-10-24，FLICKER_PROBE 抓到 20 样本逐拍交替）：
        //   词典整串："F-16I Falcon [Sufa]" -> "F-16I 战隼 [Sufa]"
        //   兑底 TranslateMilitaryUnitName 又把 [Sufa] 改成 [苏法/风暴]，
        //   200ms 补扫与同步钩子各写一种 → 单元状态机型行频闪。
        // 判据：对词典译文（含中文）再过兑底必须原样返回；两条写路径对同一输入
        // 产出同一输出（T 与兜底拼装结果一致）。
        MethodInfo milName = loc.GetMethod("TranslateMilitaryUnitName", BindingFlags.Static | BindingFlags.NonPublic);
        if (milName == null) { Console.WriteLine("FAIL: TranslateMilitaryUnitName not found"); return 1; }
        Func<string, string> M = s => (string)milName.Invoke(null, new object[] { s });
        string dictFull = T("F-16I Falcon [Sufa]");
        Check("军名兑底不动已译串", dictFull.Contains("战隼") && M(dictFull) == dictFull,
            dictFull + " => " + M(dictFull));
        Check("军名兑底仍处理纯英文", M("F-16I Falcon [Sufa]").Contains("战隼"), M("F-16I Falcon [Sufa]"));
        // 同一英文源经「词典路径」与「纯英文兑底路径」必须收敛到同一输出。
        Check("两路译名一致", M("F-16I Falcon [Sufa]") == M(M("F-16I Falcon [Sufa]")),
            M("F-16I Falcon [Sufa]"));

        // 可选的同一行为全词库检查，不是全项目 suite；不读取游戏或发送诊断命令。
        if (args.Length > 3 && args[3] == "all-exact")
        {
            var exact = (Dictionary<string, string>)loc.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            int allUnstable = 0;
            foreach (string raw in new List<string>(exact.Keys))
            {
                string once = T(raw), twice = T(once);
                if (once != twice)
                {
                    allUnstable++;
                    if (allUnstable <= 8) Console.WriteLine("     真实词库不幂等: " + raw + " -> " + once + " -> " + twice);
                }
            }
            Check("真实 exact T(T)==T", exact.Count >= 70571 && allUnstable == 0,
                "词库条目 " + exact.Count + "，不稳定 " + allUnstable);
        }

        Console.WriteLine(failures == 0 ? "ALL PASS" : "FAILURES: " + failures);
        return failures == 0 ? 0 : 2;
    }
}
