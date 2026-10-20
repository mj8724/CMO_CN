// 全进程英文残留扫描器（R52）。
//
// 为什么需要它：`verify_regressions` 里的 L-ui-residuals 只能检查「**已入词典**却仍显英文」
// 的条目——那是个循环论证。没进词典的字符串永远不会被它发现，于是「数据库里一屏英文」
// 「军队名没翻译」这类缺口可以一直存在而所有门禁全绿。
//
// 这里换个方向：拿**游戏数据本身**（DB 各表的 Name、想定内嵌文本）当语料喂给引擎，
// 看引擎真实产出什么。判据不是「在不在词典里」，而是「翻完还是不是英文」。
//
// 用法：
//   ResidueScan.exe <root> <dll> <input.tsv> <output.tsv>
// 输入每行： category \t table \t text
// 输出每行： category \t table \t src \t dst \t verdict
//   verdict = ok      翻出了中文
//             none    完全没翻（dst == src）
//             partial 翻了但还留着拉丁字母（可能是合理的型号残留，也可能是半译）
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

class ResidueScan
{
    static readonly Regex Latin = new Regex(@"[A-Za-z]{2,}", RegexOptions.Compiled);

    static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: ResidueScan.exe <root> <dll> <input.tsv> <output.tsv>");
            return 2;
        }
        string root = args[0], dll = args[1], input = args[2], output = args[3];

        Assembly asm = Assembly.LoadFrom(dll);
        Type loc = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (loc == null) { Console.Error.WriteLine("FAIL: CMOLocalizer.CustomAppDomainManager not found"); return 1; }

        MethodInfo load = loc.GetMethod("LoadDictionaries",
                                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (load == null) { Console.Error.WriteLine("FAIL: LoadDictionaries not found"); return 1; }
        load.Invoke(null, new object[] { Path.Combine(root, "dict", "CMOLocalizer.json") });

        MethodInfo tr = loc.GetMethod("TranslateString", BindingFlags.Static | BindingFlags.Public);
        if (tr == null) { Console.Error.WriteLine("FAIL: TranslateString not found"); return 1; }

        int total = 0, ok = 0, none = 0, partial = 0;
        using (var reader = new StreamReader(input, Encoding.UTF8))
        using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 3) continue;
                string cat = parts[0], table = parts[1];
                string src = parts[2];
                if (src.Length == 0) continue;

                string dst;
                try { dst = (string)tr.Invoke(null, new object[] { src }); }
                catch (Exception ex) { dst = src + "  <EX:" + ex.GetType().Name + ">"; }
                if (dst == null) dst = "";

                string verdict;
                if (dst == src) { verdict = "none"; none++; }
                else if (HasCjk(dst) && !Latin.IsMatch(dst)) { verdict = "ok"; ok++; }
                else if (HasCjk(dst)) { verdict = "partial"; partial++; }
                else { verdict = "none"; none++; }

                total++;
                writer.WriteLine(cat + "\t" + table + "\t" + Esc(src) + "\t" + Esc(dst) + "\t" + verdict);
            }
        }
        Console.WriteLine("scanned=" + total + " ok=" + ok + " partial=" + partial + " none=" + none);
        return 0;
    }

    static bool HasCjk(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        }
        return false;
    }

    // TSV 里不能有制表符/换行；DB 名称里偶尔有多行（实测见过带 \n 的条目）。
    static string Esc(string s)
    {
        if (s == null) return "";
        return s.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    }
}
