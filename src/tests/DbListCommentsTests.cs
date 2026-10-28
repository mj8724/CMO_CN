// R72：数据库列表行尾 Comments 的接线回归。
//
// 背景（2026-10-28）：Comments 由游戏拼在 DB 列表行末尾，实机形态
// `A-300-600ST Beluga -- 民用/商用机 (商业民航), 1995, 5x, Super Guppy Replacement`
// （对照 DB：A-300-600ST Beluga 的 Comments = `5x, Super Guppy Replacement`；
//  Comments 为 `-` 的记录行尾没有任何尾巴，这证明尾巴就是 Comments）。
// R71 修完括号外残留后扫描器仍报 1,596 条 DB 缺口，因为 Comments 从来没进过翻译通道：
// `TranslateDBListEntry` 只处理方括号子类型与 ` -- 国家 (兵种)`，行尾原样英文。
//
// 这里锁死三件事：
//   1. **接线**：`TranslateDBListEntry` 必须调用 `TranslateDBRowCommentsTail`
//      （函数存在却没接上＝等于没修，R42 就踩过这个坑）。
//   2. **行为**：后缀整段命中词典时替换；未命中时**原样返回**，不按逗号拆词。
//   3. **安全性**：纯代号（`7x`、`1992`）不得被拼成中文；带逗号的 Comments 必须整段命中。
// 用临时 fixture 词典测行为，不依赖仓库当前词库内容（否则词库一改测试就假红/假绿）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

class DbListCommentsTests
{
    static int failures;

    static void Check(string label, bool ok, string detail)
    {
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + label + " | " + detail);
        if (!ok) failures++;
    }

    static string JsonEscape(string s)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string Fixture(Dictionary<string, string> comments)
    {
        // comments 分区必须排在第一个已知分区之前（旧引擎兼容），这里照真实词库的顺序摆放。
        const string NL = "\n";
        StringBuilder sb = new StringBuilder();
        sb.Append("{" + NL + " \"_meta\": {\"version\": \"test\"}," + NL);
        sb.Append(" \"comments\": {");
        bool first = true;
        foreach (KeyValuePair<string, string> kv in comments)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append(NL + "  \"").Append(JsonEscape(kv.Key)).Append("\": \"")
              .Append(JsonEscape(kv.Value)).Append("\"");
        }
        sb.Append(NL + " }," + NL + " \"offsets\": {}," + NL + " \"exact\": {}," + NL);
        sb.Append(" \"partial\": {}," + NL + " \"words\": {}" + NL + "}" + NL);
        return sb.ToString();
    }

    static string FindEngineSource(string start)
    {
        DirectoryInfo dir = new DirectoryInfo(start);
        if (dir.Exists)
        {
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src", "engine", "CMOLocalizer.cs");
                if (File.Exists(candidate)) return candidate;
            }
        }
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "engine", "CMOLocalizer.cs");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static int Main(string[] args)
    {
        // 参数约定与 cs_case 一致：`test.exe <engine.dll> <dict.json> <mode>`。
        // 为兼容单独手工运行，也接受 `<root> [<dll>]`（旧写法）。
        string root = Directory.GetCurrentDirectory();
        string dll, dict = null;
        if (args.Length >= 2 && args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            dll = args[0];
            dict = args[1];
        }
        else
        {
            root = args.Length > 0 ? args[0] : root;
            dll = args.Length > 1 ? args[1] : Path.Combine(root, "build", "CMOLocalizer.dll");
            dict = args.Length > 2 ? args[2] : Path.Combine(root, "dict", "CMOLocalizer.json");
        }
        // 源码位置：从 root / 当前目录向上逐级找（cs_case 在 build/<tmp> 里运行 exe，
        // 相对路径找不到仓库根，向上找才不会把「找不到源码」当成接线失败）。
        string src = FindEngineSource(root);
        if (src == null) { Console.WriteLine("FAIL: src/engine/CMOLocalizer.cs not found from " + Directory.GetCurrentDirectory()); return 1; }

        // ---- 1. 接线（源码级）：函数必须真的被 TranslateDBListEntry 调用 ----
        string code = File.ReadAllText(src);
        int entryIdx = code.IndexOf("private static string TranslateDBListEntry", StringComparison.Ordinal);
        int tailDefIdx = code.IndexOf("private static string TranslateDBRowCommentsTail", StringComparison.Ordinal);
        bool defined = tailDefIdx > 0;
        bool wired = defined && entryIdx > 0 && entryIdx < tailDefIdx
                     && code.Substring(entryIdx, tailDefIdx - entryIdx)
                            .Contains("TranslateDBRowCommentsTail(");
        Check("R72 接线 TranslateDBListEntry -> TranslateDBRowCommentsTail", wired,
              "defined=" + defined + " wired=" + wired);

        Assembly asm = Assembly.LoadFrom(dll);
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (loc == null) { Console.WriteLine("FAIL: engine type not found"); return 1; }
        MethodInfo load = loc.GetMethod("LoadDictionaries", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo entry = loc.GetMethod("TranslateDBListEntry", BindingFlags.Static | BindingFlags.NonPublic);
        if (load == null || entry == null)
        {
            Console.WriteLine("FAIL: LoadDictionaries/TranslateDBListEntry missing");
            return 1;
        }
        Func<string, string> tr = s => (string)entry.Invoke(null, new object[] { s });

        // 真实词库先加载一次：证明新分区与既有分区可以共存（真实运行路径）
        load.Invoke(null, new object[] { dict });

        string fixture = Path.Combine(Path.GetTempPath(), "cmo-r72-dict.json");
        Dictionary<string, string> comments = new Dictionary<string, string>
        {
            { "5x, Super Guppy Replacement", "5x、超级孔雀鱼替代" },
            { "Cancelled, Su-27M", "已取消，苏-27M" },
            { "2x Mounts", "2x 挂架" },
        };
        File.WriteAllText(fixture, Fixture(comments), new UTF8Encoding(false));
        load.Invoke(null, new object[] { fixture });

        // ---- 2. 行为：行尾整段命中就替换（Comments 自带逗号也要整段命中）----
        string rowGuppy = "A-300-600ST Beluga -- 民用/商用机 (商业民航), 1995, 5x, Super Guppy Replacement";
        string gotGuppy = tr(rowGuppy);
        Check("R72 带逗号 Comments 整段命中", gotGuppy.Contains("超级孔雀鱼替代"), gotGuppy);

        string rowCancelled = "Su-27M [Flanker] -- 苏联 (空军), 1985-1995, Cancelled, Su-27M";
        string gotCancelled = tr(rowCancelled);
        Check("R72 行尾 Comments 命中词典", gotCancelled.Contains("已取消"), gotCancelled);

        // ---- 3. 未命中词典时原样保留（绝不按逗号拆词拼接）----
        string rowUnknown = "X-1 Testbed -- 测试 (空军), 1990, ZZ Unknown Token";
        string gotUnknown = tr(rowUnknown);
        Check("R72 未命中时行尾原样保留", gotUnknown.Contains("ZZ Unknown Token"), gotUnknown);

        // ---- 4. 纯代号/年份不得被译（即使整串含中文也不动）----
        string rowCodes = "A.400M Atlas -- 比利时 (空军), 2020, 7x";
        string gotCodes = tr(rowCodes);
        Check("R72 纯代号行尾不产生中文污染", gotCodes.EndsWith("7x"), gotCodes);

        // ---- 3b. 数量+固定单位由规则处理（不靠词表枚举）----
        string rowTubes = "Ship X -- 苏联 (海军), 1980, 8x Tubes";
        Check("R72 数量规则 8x Tubes", tr(rowTubes).Contains("8x 发射管"), tr(rowTubes));
        string rowRnds = "Gun Y -- 苏联 (陆军), 1970-1974, 120 Rounds";
        Check("R72 数量规则 120 Rounds", tr(rowRnds).Contains("120 发"), tr(rowRnds));
        string rowBty = "Bty Z -- 苏联 (陆军), 1965-1968, 3x Bty";
        Check("R72 数量规则 3x Bty", tr(rowBty).Contains("3x 连"), tr(rowBty));
        string rowTons = "Truck W -- 苏联 (陆军), 1965-1970, 1.5 tons";
        Check("R72 数量规则 1.5 tons", tr(rowTons).Contains("1.5 吨"), tr(rowTons));

        // ---- 3c. 句首模板 + 尾部原样（Comments 里成批出现，逐个枚举不现实）----
        string rowRetired = "Ship A -- 苏联 (海军), 1977-1995, Retired 1977";
        Check("R72 模板规则 Retired 1977", tr(rowRetired).Contains("1977 年退役"), tr(rowRetired));
        string rowOper = "Ship B -- 苏联 (海军), 1976-1990, Operational 1976";
        Check("R72 模板规则 Operational 1976", tr(rowOper).Contains("1976 年服役"), tr(rowOper));
        string rowReactor =
            "Sat C -- 苏联 (空军), 1968, Reactor boosted to high orbit 22-10-1985";
        Check("R72 模板规则 Reactor boosted", tr(rowReactor).Contains("反应堆推入高轨 22-10-1985"), tr(rowReactor));
        // 模板只认固定句式：型号不得被模板吃掉
        string rowModel = "Ftr D -- 苏联 (空军), 1980-1995, SA-6";
        Check("R72 模板不吃型号 SA-6", tr(rowModel).EndsWith("SA-6"), tr(rowModel));

        // ---- 3d. `Assoc w/ X` 家族（8 种拼写变体，系统性形态）----
        foreach (string assoc in new string[]
                 {
                     "Radar A -- 苏联 (海军), 1980-1995, Assoc w/ AN/ALQ-41",
                     "Radar B -- 苏联 (海军), 1980-1995, Assoc. w/ SA-6",
                     "Radar C -- 苏联 (海军), 1980-1995, Associated with HQ-9",
                     "Radar D -- 苏联 (海军), 1980-1995, Assow w TB-16",
                 })
        {
            Check("R72 Assoc 家族: " + assoc, tr(assoc).Contains("配 "), tr(assoc));
        }

        // ---- 3e. 交付/损失类动词（Nx Ordered / Planned / Lost …）----
        Check("R72 交付类动词 6x Ordered", tr("Tank A -- 苏联 (陆军), 1970-1990, 6x Ordered").Contains("6x 订购"),
              tr("Tank A -- 苏联 (陆军), 1970-1990, 6x Ordered"));
        Check("R72 交付类动词 2x Lost", tr("Tank B -- 苏联 (陆军), 1970-1990, 2x Lost").Contains("2x 损失"),
              tr("Tank B -- 苏联 (陆军), 1970-1990, 2x Lost"));

        // ---- 3g. R75：无年份的 DB 行（兵种命中写死表）也要译行尾 ----
        // 实机 `AB.412EP -- Turkey (Coast Guard), 14x, Agusta-Bell` 这类行**没有四位年份**，
        // 原年份闸门把整行挡住——词表里有译文也不生效（R75 实测 11 行）。
        // 注意：输入必须是**英文原文行**（运行时 TranslateDarkListView 对含中文的项直接跳过），
        // 用导出里已部分汉化的行测会把闸门问题测成假红。
        string rowNoYear = "AB.412EP -- Turkey (Coast Guard), 14x, Agusta-Bell";
        Check("R75 无年份 DB 行仍译行尾", tr(rowNoYear).EndsWith("阿古斯塔-贝尔"), tr(rowNoYear));
        string rowIsUnkn = "Ababil-3 UAV [Mod. Seeker 2D Copy] -- Iraq (Army), IS Unkn";
        Check("R75 无年份 DB 行 IS Unkn", tr(rowIsUnkn).Contains("在役状态不明"), tr(rowIsUnkn));
        // 兵种写死表外的新军种（R75：实机 24 行长期原样显示）
        string rowIrgc = "Ababil-3 UAV -- Iran (IRGC Aerospace Force), 2008, AB-3";
        Check("R75 兵种 IRGC Aerospace Force", tr(rowIrgc).Contains("(伊斯兰革命卫队航天部队)"), tr(rowIrgc));
        string rowAng = "A-7D Corsair II -- United States (Air National Guard), 1976";
        Check("R75 兵种 Air National Guard", tr(rowAng).Contains("(空军国民警卫队)"), tr(rowAng));
        // 负向：非 DB 行（既无年份、兵种也不在表内）仍然不得改写
        // 注意不能直接断言整行相等：第 2 步的国家翻译本来就会把 Bravo 译成布拉沃，
        // 要断言的是**行尾 Comments 段没被翻译**（Charlie 不得变查理）。
        string notDb2 = "Alpha Team -- Bravo (Charlie), Delta";
        Check("R75 非 DB 行（兵种表外）不改写", !tr(notDb2).Contains("查理"), tr(notDb2));

        // ---- 3h. R75：交付/损失类动词带年份的写法（实机成批）----
        Check("R75 规则 1x lost 2001", tr("Tank C -- 苏联 (陆军), 1985, 1x lost 2001").Contains("1x 损失 2001"),
              tr("Tank C -- 苏联 (陆军), 1985, 1x lost 2001"));
        Check("R75 规则 8x Loaned '24", tr("Helo E -- 波兰 (陆军), 2024, 8x Loaned '24").Contains("8x 租用 ('24)"),
              tr("Helo E -- 波兰 (陆军), 2024, 8x Loaned '24"));
        Check("R75 规则 96x in '26", tr("Helo F -- 波兰 (陆军), 2024, 96x in '26").Contains("96x（'26 年）"),
              tr("Helo F -- 波兰 (陆军), 2024, 96x in '26"));
        Check("R75 规则 Upgraded to AH-1F",
              tr("Helo G -- 巴基斯坦 (陆军), 1986, Upgraded to AH-1F").Contains("升级至 AH-1F"),
              tr("Helo G -- 巴基斯坦 (陆军), 1986, Upgraded to AH-1F"));

        // ---- 3f. 实机取到的真实行（2026-10-28 实机 DB_LIST_TEXT 导出）----
        // 这些是玩家真正看到的行；计划/交付类段必须被规则吃掉。
        string realA29 =
            "A-29B Super Tucano [EMB-314] -- 马里 [1960-] (空军), 2018, 6x Planned, 4x Delivered, 1x Lost 2020";
        Check("R72 实机行 6x Planned 被规则翻译", tr(realA29).Contains("6x 计划"), tr(realA29));
        Check("R72 实机行 4x Delivered 被规则翻译", tr(realA29).Contains("4x 交付"), tr(realA29));
        string realF117 =
            "A/F-117X Sea Hawk -- 美国 (海军), 1999, 1994 proposed A/F-X cancellation compensation, 40-75x Planned";
        Check("R72 实机行 40-75x Planned 被规则翻译", tr(realF117).Contains("40-75x 计划"), tr(realF117));

        // ---- 4b. 非 DB 行不得被行尾逻辑改写（TranslateDarkListView 对全部 DarkListView 生效）----
        string notDbRow = "Alpha Team, Export";
        Check("R72 非 DB 行（无 ' -- '）不改写", tr(notDbRow) == notDbRow, tr(notDbRow));

        // 含 ' -- ' 但**没有四位年份**的行同样不是 DB 行：通用词（Charlie/Active）
        // 不许把它们误译（R72 复查 F2 的真实反例）。
        foreach (string bogus in new string[]
                 {
                     "Alpha Team -- Bravo, Charlie, Delta",
                     "Patrol -- Radar, Active",
                     "Squadron -- Command, Charlie",
                 })
        {
            Check("R72 非 DB 行（含 ' -- ' 但无年份）不改写: " + bogus, tr(bogus) == bogus, tr(bogus));
        }

        // ---- 5. 前缀（名称/国家/兵种）仍走原有通道，不能被行尾逻辑破坏 ----
        string rowPlain = "3MS-1 Bison B [Bomber] -- 苏联 [-1991] (远程航空兵 [DA]), 1960-1985";
        string gotPlain = tr(rowPlain);
        Check("R72 无 Comments 的行不受影响", gotPlain.Contains("1960-1985") && gotPlain.Contains("苏联"), gotPlain);

        // ---- 6. 端到端：用**真实词库**跑几条真实 DB 行（Comments 来自 DB3K_519）----
        // 这条挡住「词表写对了但没进词典」：fixture 只证明行为，真实词库才证明接线到数据。
        load.Invoke(null, new object[] { dict });
        string realHit = tr("A-300-600ST Beluga -- 民用/商用机 (商业民航), 1995, 5x, Super Guppy Replacement");
        // 断言**精确译文**：不许用「只要不含原文就算过」的兜底——那种写法对任何改写都恒真，
        // 等于没测（R72 复查 F1：曾出现测试假绿）。
        Check("R72 真实词库把 Super Guppy Replacement 译出",
              realHit.EndsWith("5x、超级孔雀鱼替代"), realHit);
        string realCode = tr("A.400M Atlas -- 比利时 (空军), 2020, 7x");
        Check("R72 真实词库不动纯代号 7x", realCode.EndsWith("7x"), realCode);
        string realClean = tr("3MS-1 Bison B [Bomber] -- 苏联 [-1991] (远程航空兵 [DA]), 1960-1985");
        Check("R72 真实词库不乱动无 Comments 行", realClean.Contains("1960-1985"), realClean);

        try { File.Delete(fixture); } catch { }
        Console.WriteLine(failures == 0 ? "\nR72 ALL PASS" : "\nR72 FAILURES: " + failures);
        return failures == 0 ? 0 : 1;
    }
}
