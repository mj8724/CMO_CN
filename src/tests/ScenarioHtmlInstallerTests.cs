// Synthetic files only. Calls the real deployment/fallback methods, never Install.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

internal static class ScenarioHtmlInstallerTests
{
    private sealed class Fixture
    {
        internal string Name;
        internal byte[] Source;
        internal byte[] Payload;
        internal bool HeaderFallback;
        internal string Charset = "windows-1252";
        internal string Body = "任务内容保持译文。";
        internal string EnglishBody = "Untranslated fixture body.";
        internal bool LegacyApostrophe = true;
        internal bool LegacyEnglish;
        internal bool HasBom;
    }

    private static int checks;
    private static int failures;

    private static void Check(bool condition, string name)
    {
        checks++;
        if (!condition) failures++;
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    }

    private static string Entities(string value, bool hexadecimal)
    {
        StringBuilder text = new StringBuilder();
        foreach (char c in value)
        {
            if (c < 128) text.Append(c);
            else if (hexadecimal) text.Append("&#x").Append(((int)c).ToString("X")).Append(';');
            else text.Append("&#").Append((int)c).Append(';');
        }
        return text.ToString();
    }

    private static List<Fixture> Fixtures()
    {
        List<Fixture> fixtures = new List<Fixture>();
        Encoding legacy = Encoding.GetEncoding(1252);
        foreach (bool hexadecimal in new bool[] { false, true })
        {
            string source = "<meta charset=\"windows-1252\"><h2>Briefing</h2>"
                + "<p>Mission details stay translated.</p><!-- author\u2019s note -->";
            // Matches the exporter's original-encoding numeric-entity contract.
            string payload = source.Replace(">Briefing<", ">" + Entities("完整简报", hexadecimal) + "<")
                .Replace("Mission details stay translated.", Entities("任务内容保持译文。", hexadecimal));
            fixtures.Add(new Fixture { Name = hexadecimal ? "hex.html" : "decimal.html",
                Source = legacy.GetBytes(source), Payload = legacy.GetBytes(payload) });
        }
        string plain = "<meta charset=\"windows-1252\"><p>Briefing</p>"
            + "<p>Mission details stay translated.</p><!-- author\u2019s note -->";
        fixtures.Add(new Fixture { Name = "no-heading.html", Source = legacy.GetBytes(plain),
            Payload = legacy.GetBytes(plain.Replace(">Briefing<", ">" + Entities("完整简报", false) + "<")
                .Replace("Mission details stay translated.", Entities("任务内容保持译文。", false))) });
        string utf8 = "<meta charset=\"utf-8\"><h2>Briefing</h2><p>Mission details stay translated.</p>";
        fixtures.Add(new Fixture { Name = "utf8.html", Source = Encoding.UTF8.GetBytes(utf8),
            Payload = Encoding.UTF8.GetBytes(utf8.Replace(">Briefing<", ">完整简报<")
                .Replace("Mission details stay translated.", "任务内容保持译文。")),
            Charset = "utf-8", LegacyApostrophe = false });
        string conflicting = utf8.Replace("utf-8", "windows-1252");
        fixtures.Add(new Fixture { Name = "bom-priority.html", Source = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(conflicting)).ToArray(),
            Payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(conflicting
                .Replace(">Briefing<", ">完整简报<")
                .Replace("Mission details stay translated.", "任务内容保持译文。"))).ToArray(),
            Charset = "utf-8", LegacyApostrophe = false });
        foreach (string charset in new string[] { "gbk", "big5" })
        {
            Encoding encoding = Encoding.GetEncoding(charset);
            string title = charset == "gbk" ? "完整简报" : "完整簡報";
            string body = charset == "gbk" ? "中文正文" : "任務翻譯";
            string source = "<meta http-equiv=\"Content-Type\" content=\"text/html; charset="
                + charset + "\"><h2>Briefing</h2><p>Mission details stay translated.</p>";
            fixtures.Add(new Fixture { Name = charset + ".html", Source = encoding.GetBytes(source),
                Payload = encoding.GetBytes(source.Replace(">Briefing<", ">" + title + "<")
                    .Replace("Mission details stay translated.", body)), Charset = charset,
                Body = body, LegacyApostrophe = false });
        }
        string english = "<meta charset=\"utf-8\"><h2>Briefing</h2><p>Untranslated fixture body.</p><p>Copyright &#169;.</p>";
        fixtures.Add(new Fixture { Name = "english.html", Source = Encoding.UTF8.GetBytes(english),
            Payload = Encoding.UTF8.GetBytes(english), HeaderFallback = true, Charset = "utf-8" });
        fixtures.Add(new Fixture { Name = "updated-english.html",
            Source = Encoding.UTF8.GetBytes(english.Replace("Untranslated fixture body.", "Old revision one.")),
            Payload = Encoding.UTF8.GetBytes(english.Replace("Untranslated fixture body.", "Current revision two.")),
            HeaderFallback = true, EnglishBody = "Current revision two.", Charset = "utf-8" });
        byte[] bomEnglish = Encoding.UTF8.GetPreamble().Concat(
            Encoding.UTF8.GetBytes(english.Replace("utf-8", "gbk"))).ToArray();
        fixtures.Add(new Fixture { Name = "bom-english.html", Source = bomEnglish,
            Payload = bomEnglish, HeaderFallback = true, Charset = "utf-8", HasBom = true });
        foreach (string charset in new string[] { "windows-1252", "gbk", "big5" })
        {
            string source = english.Replace("utf-8", charset)
                + "<script>var author = '\u2019';</script>"
                + "<p>You are NOT, REPEAT NOT authorized to use nuclear weapons.</p>";
            byte[] bytes = Encoding.GetEncoding(charset).GetBytes(source);
            fixtures.Add(new Fixture { Name = "english-" + charset + ".html", Source = bytes,
                Payload = bytes, HeaderFallback = true, Charset = charset, LegacyEnglish = true });
        }
        string unknown = english.Replace("utf-8", "unknown-encoding");
        fixtures.Add(new Fixture { Name = "unknown-charset.html", Source = Encoding.UTF8.GetBytes(unknown),
            Payload = Encoding.UTF8.GetBytes(unknown), HeaderFallback = true, Charset = "utf-8" });
        return fixtures;
    }

    private static void Prepare(string archivePath)
    {
        using (FileStream stream = File.Create(archivePath))
        using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (Fixture fixture in Fixtures())
            {
                WriteEntry(archive, "Scenarios/" + fixture.Name, fixture.Payload);
                WriteEntry(archive, "Scenarios/" + fixture.Name + ".orig", fixture.Source);
            }
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        using (Stream stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
    }

    private static int Run(string assemblyPath, string output)
    {
        // Loading this library does not construct an installer form or invoke Main.
        Type core = Assembly.LoadFrom(Path.GetFullPath(assemblyPath))
            .GetType("CMOLocalizerInstaller.InstallerCore", true);
        MethodInfo deploy = core.GetMethod("DeployScenarioHtml", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo localize = core.GetMethod("LocalizeScenarioBriefings", BindingFlags.Static | BindingFlags.NonPublic);
        string game = Path.Combine(Path.GetFullPath(output), Guid.NewGuid().ToString("N"));
        string scenarios = Path.Combine(game, "Scenarios");
        Directory.CreateDirectory(scenarios);
        foreach (Fixture fixture in Fixtures()) File.WriteAllBytes(Path.Combine(scenarios, fixture.Name), fixture.Source);
        deploy.Invoke(null, new object[] { game });
        foreach (Fixture fixture in Fixtures())
        {
            string path = Path.Combine(scenarios, fixture.Name);
            Check(File.ReadAllBytes(path).SequenceEqual(fixture.Payload), fixture.Name + " payload deployed byte-for-byte");
            Check(File.ReadAllBytes(path + ".orig").SequenceEqual(fixture.Source), fixture.Name + " original backup preserved");
        }
        localize.Invoke(null, new object[] { scenarios });
        Dictionary<string, byte[]> firstPass = new Dictionary<string, byte[]>();
        foreach (Fixture fixture in Fixtures())
        {
            string path = Path.Combine(scenarios, fixture.Name);
            byte[] bytes = File.ReadAllBytes(path);
            firstPass.Add(fixture.Name, bytes);
            if (!fixture.HeaderFallback)
            {
                Check(bytes.SequenceEqual(fixture.Payload), fixture.Name + " full translation survives heading fallback");
                string text = Encoding.GetEncoding(fixture.Charset).GetString(bytes);
                Check(WebUtility.HtmlDecode(text).Contains(fixture.Body), fixture.Name + " rendered translated body preserved");
                if (fixture.LegacyApostrophe)
                    Check(bytes.Contains((byte)0x92), fixture.Name + " original cp1252 apostrophe preserved");
            }
            else
            {
                string text = Encoding.GetEncoding(fixture.Charset).GetString(bytes);
                string rendered = WebUtility.HtmlDecode(text);
                Check(rendered.Contains("<h2>作战任务简报 (Briefing)</h2>"), fixture.Name + " untranslated English still receives heading fallback");
                Check(rendered.Contains("<p>" + fixture.EnglishBody + "</p>"), fixture.Name + " English fallback keeps current body");
                Check(text.Contains("<p>Copyright &#169;.</p>"), "non-Han entity does not suppress English fallback");
                if (fixture.LegacyEnglish)
                {
                    Check(text.Contains("<script>var author = '\u2019';</script>"), fixture.Name + " original script stays unchanged");
                    Check(text.Contains("charset=\"" + fixture.Charset + "\""), fixture.Name + " original charset stays unchanged");
                    Check(rendered.Contains("你严禁（重复：严禁）动用任何核武器。"), fixture.Name + " unrepresentable punctuation renders correctly");
                }
                if (fixture.HasBom)
                    Check(bytes.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()), fixture.Name + " fallback retains original BOM");
            }
            Check(File.ReadAllBytes(path + ".orig").SequenceEqual(fixture.Source), fixture.Name + " backup unchanged after fallback");
        }
        localize.Invoke(null, new object[] { scenarios });
        foreach (Fixture fixture in Fixtures())
            Check(File.ReadAllBytes(Path.Combine(scenarios, fixture.Name)).SequenceEqual(firstPass[fixture.Name]), fixture.Name + " second pass is idempotent");
        string standalone = Path.Combine(game, "standalone");
        Directory.CreateDirectory(standalone);
        foreach (Fixture fixture in Fixtures().Where(f => f.Name == "gbk.html" || f.Name == "big5.html" || f.HeaderFallback))
            File.WriteAllBytes(Path.Combine(standalone, fixture.Name), fixture.Payload);
        // No DeployScenarioHtml call: fallback must also work without packaged output.
        localize.Invoke(null, new object[] { standalone });
        foreach (Fixture fixture in Fixtures().Where(f => f.Name == "gbk.html" || f.Name == "big5.html" || f.HeaderFallback))
        {
            string path = Path.Combine(standalone, fixture.Name);
            if (fixture.HeaderFallback)
            {
                Check(WebUtility.HtmlDecode(Encoding.GetEncoding(fixture.Charset).GetString(File.ReadAllBytes(path)))
                    .Contains("<h2>作战任务简报 (Briefing)</h2>"),
                    fixture.Name + " standalone English fallback still runs");
                Check(File.ReadAllBytes(path + ".orig").SequenceEqual(fixture.Payload),
                    fixture.Name + " standalone original backup preserved");
            }
            else
            {
                Check(File.ReadAllBytes(path).SequenceEqual(fixture.Payload),
                    fixture.Name + " standalone native translation remains byte-for-byte");
                Check(!File.Exists(path + ".orig"), fixture.Name + " standalone translation needs no fallback backup");
            }
        }
        Console.WriteLine("Scenario HTML checks=" + checks + " failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: --prepare <zip> | <installer-library.dll> <fixture-output>");
            if (args[0] == "--prepare") { Prepare(args[1]); return 0; }
            return Run(args[0], args[1]);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
