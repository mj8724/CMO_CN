// R77：「快速战斗」等已是纯中文的 WebBrowser 页面必须跳过【】双语注入。
//
// 背景：TranslateWebBrowser 原本对整页 InnerHtml 做「英文 + 【中文】」子串擦写，
// 那是给游戏自己生成的英文页用的。快速战斗的 Form.html 现在是随包发布的纯中文
// 文件，若还按老逻辑跑，就会在中文正文里插回英文原文，并且把型号标签插进
// <option> 文本里（value 属性不动，但显示会变脏）。所以判据必须是「页面语言」，
// 而不是「我们有没有写过标记」。
using System;
using System.IO;
using System.Reflection;
using System.Text;

class WebBrowserInjectionTests
{
    static int failures;

    static void Check(string label, bool actual, bool expected)
    {
        bool ok = actual == expected;
        Console.WriteLine((ok ? "--> OK   " : "--> FAIL ") + label
                          + " | expected=" + expected + " actual=" + actual);
        if (!ok) failures++;
    }

    static MethodInfo Lookup(Assembly asm)
    {
        Type type = asm.GetType("CMOLocalizer.CustomAppDomainManager");
        if (type == null) return null;
        return type.GetMethod("LooksLocalizedChinese",
                              BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    }

    static bool Invoke(MethodInfo method, string text)
    {
        return (bool)method.Invoke(null, new object[] { text });
    }

    static int Main(string[] args)
    {
        // 门禁约定（见 verify_regressions.cs_case）：args[0]=引擎 DLL，args[1]=词典路径，
        // args[2]=mode。本用例不需要词典，所以只认 args[0]，避免把 JSON 当程序集加载。
        string dll = (args.Length > 0 && args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            ? args[0]
            : Path.Combine(Directory.GetCurrentDirectory(), "build", "CMOLocalizer.dll");
        Assembly asm = Assembly.LoadFrom(dll);
        MethodInfo looks = Lookup(asm);
        if (looks == null)
        {
            Console.WriteLine("FAIL: LooksLocalizedChinese not found (guard missing)");
            return 1;
        }

        // 快速战斗表单页的实际形态：中文正文 + 少量型号代号 + 属性。
        Check("中文化后的快速战斗页面",
              Invoke(looks, "<html><body>简单空空交战 你操控的是 6 x " +
                            "F-22A 猛禽 Blk 35 (美国空军 2013) 敌方交战规则为 自由开火：" +
                            "无需识别即交战任何已探测目标</body></html>"), true);

        // 游戏自己生成的英文页：必须继续走【】双语注入。
        Check("纯英文数据库页面",
              Invoke(looks, "<html><body>Aircraft size: 50 ft Wingspan: 42 ft " +
                            "Average Climb Rate: 250 ft/s Service Ceiling: 50000 ft</body></html>"), false);

        // 英文正文 + 几个中文标签（历史上注入过少量词条）：仍按英文页处理。
        Check("英文正文只带少量中文标签",
              Invoke(looks, "<html><body>Aircraft size: 机体尺寸 Average Climb Rate: " +
                            "平均爬升率 Service Ceiling: 实用升限 Combat Radius: 作战半径</body></html>"), false);

        Check("空页面", Invoke(looks, ""), false);
        Check("null 页面", Invoke(looks, null), false);

        // 汉字段落很短但英文很多时不得误判（防止把待注入页当已汉化页跳过）。
        StringBuilder english = new StringBuilder();
        for (int i = 0; i < 40; i++) english.Append("Some English sentence here. ");
        english.Append("中文说明");
        Check("长英文短中文", Invoke(looks, english.ToString()), false);

        Console.WriteLine(failures == 0 ? "ALL PASS - WebBrowserInjectionTests"
                                       : "WebBrowserInjectionTests: " + failures + " failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
