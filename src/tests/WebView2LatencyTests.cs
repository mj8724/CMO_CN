using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

// Real WebView2; the installed game is never started or modified.
public static class WebView2LatencyTests
{
    private static Type engine;
    private static MethodInfo translate;
    private static WebView2 browser;
    private static Exception timerError;
    private static readonly BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<string> checks = new List<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        int exitCode = 1;
        using (Form window = new Form { Text = "CMO independent WebView2 fixture", Width = 800, Height = 560, ShowInTaskbar = false })
        using (browser = new WebView2 { Dock = DockStyle.Fill })
        {
            window.Controls.Add(browser);
            window.Shown += async delegate
            {
                try { await Run(args); exitCode = 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { window.Close(); }
            };
            Application.Run(window);
        }
        return exitCode;
    }

    private static async Task Run(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Usage: <engine.dll> <dictionary.json> [interval] [samples]");
        int interval = args.Length > 2 ? int.Parse(args[2]) : 1500;
        int samples = args.Length > 3 ? Math.Max(5, int.Parse(args[3])) : 8;
        if (interval < 1 || samples > 100) throw new ArgumentException("Require positive interval and at most 100 samples");
        if (File.Exists(Path.Combine(Environment.CurrentDirectory, "Command.exe")))
            throw new InvalidOperationException("Run this fixture from a build directory, not the game directory");
        Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        engine = assembly.GetType("CMOLocalizer.CustomAppDomainManager", true);
        engine.GetMethod("ParseJsonSections", PrivateStatic).Invoke(null,
            new object[] { File.ReadAllText(Path.GetFullPath(args[1]), Encoding.UTF8) });
        translate = engine.GetMethod("TranslateWebView2", PrivateStatic);
        bool early = engine.GetMethod("TranslateWebViewEarly", PrivateStatic) != null;
        var exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
        string heading = exact["SITUATION"];
        const string description = "Air Ops - Multiple host units";
        string body = exact[description];
        var sources = new List<string>();
        foreach (KeyValuePair<string, string> item in exact)
        {
            if (sources.Count >= samples) break;
            if (item.Key.Length >= 40 && item.Key.Length <= 200 && item.Key == item.Key.Trim()
                && item.Key[0] >= 'A' && item.Key[0] <= 'Z' && item.Key != item.Value && item.Key != description
                && item.Key.IndexOfAny(new char[] { '<', '>', '\r', '\n', '{', '}', '[', ']' }) < 0
                && item.Key.IndexOf("ScenEdit", StringComparison.OrdinalIgnoreCase) < 0
                && item.Key.IndexOf("[LOADDOC]", StringComparison.OrdinalIgnoreCase) < 0
                && !sources.Contains(item.Key)) sources.Add(item.Key);
        }
        if (sources.Count < samples) throw new InvalidOperationException("Insufficient distinct translated fixture strings");

        // The candidate subscribes before initialization; the old adapter harmlessly returns.
        translate.Invoke(null, new object[] { browser });
        string profile = Path.Combine(Environment.CurrentDirectory, "webview2-profile");
        var environment = await CoreWebView2Environment.CreateAsync(null, profile, null);
        await browser.EnsureCoreWebView2Async(environment);
        if (early) await WaitForRegistration();
        using (var timer = new Timer { Interval = interval })
        {
            timer.Tick += delegate
            {
                try { translate.Invoke(null, new object[] { browser }); }
                catch (Exception ex) { timerError = ex; timer.Stop(); }
            };
            timer.Start();
            var navigation = new List<double>();
            double initial = 0;
            bool firstFrameHeading = false, firstFrameBody = false;
            for (int i = 0; i < samples; i++)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                browser.NavigateToString(Page(i, description));
                string condition = "document.getElementById('heading') && document.getElementById('description') && "
                    + "document.getElementById('heading').textContent === " + Js(heading)
                    + " && document.getElementById('description').textContent === " + Js(body);
                await WaitFor(condition, Math.Max(15000, interval * 4));
                double milliseconds = elapsed.Elapsed.TotalMilliseconds;
                if (i == 0)
                {
                    initial = milliseconds;
                    await WaitFor("window.__firstFrame !== undefined", 5000);
                    firstFrameHeading = await IsTrue("window.__firstFrame[0] === " + Js(heading));
                    firstFrameBody = await IsTrue("window.__firstFrame[1] === " + Js(body));
                }
                else navigation.Add(milliseconds);
            }
            bool inputUnchanged = await IsTrue("document.getElementById('input').value === 'MISSION'");
            bool editableUnchanged = await IsTrue("document.getElementById('edit').textContent === 'MISSION'");
            bool linkUnchanged = await IsTrue("document.getElementById('reference').getAttribute('href') === 'file:///cmo-fixture/reference.html?mission=MISSION#anchor'");
            if (early) Check(inputUnchanged && editableUnchanged && linkUnchanged, "input-editable-link-preserved");
            var dynamic = new List<double>();
            for (int i = 0; i < samples; i++)
            {
                string source = sources[i];
                Stopwatch elapsed = Stopwatch.StartNew();
                await browser.ExecuteScriptAsync("document.getElementById('dynamic').textContent=" + Js(source));
                await WaitFor("document.getElementById('dynamic').textContent === " + Js(exact[source]), Math.Max(15000, interval * 4));
                dynamic.Add(elapsed.Elapsed.TotalMilliseconds);
            }
            Check(true, "distinct-dynamic-sources-" + samples);
            if (early)
            {
                await browser.ExecuteScriptAsync("window.__cmoWebTranslation.receive('stale-document',[[1,"
                    + Js(sources[samples - 1]) + ",'CORRUPTED']]);");
                Check(await IsTrue("document.getElementById('dynamic').textContent === " + Js(exact[sources[samples - 1]])),
                    "stale-nonce-reply-ignored");
                await browser.ExecuteScriptAsync("document.getElementById('dynamic').firstChild.nodeValue="
                    + Js(sources[0]) + ";document.getElementById('dynamic').firstChild.nodeValue=" + Js(sources[1]) + ";");
                await WaitFor("document.getElementById('dynamic').textContent === " + Js(exact[sources[1]]), 10000);
                Check(true, "reused-node-newer-source-preserved");
            }
            timer.Stop();
            Console.WriteLine("{\"adapter\":" + Js(early ? "early" : "polling")
                + ",\"interval\":" + interval + ",\"samples\":" + samples
                + ",\"firstFrameChinese\":" + Bool(firstFrameHeading && firstFrameBody)
                + ",\"firstFrameHeadingChinese\":" + Bool(firstFrameHeading)
                + ",\"firstFrameBodyChinese\":" + Bool(firstFrameBody)
                + ",\"inputUnchanged\":" + Bool(inputUnchanged)
                + ",\"editableUnchanged\":" + Bool(editableUnchanged)
                + ",\"linkUnchanged\":" + Bool(linkUnchanged)
                + ",\"initialMs\":" + Number(initial)
                + ",\"navigationMedianMs\":" + Number(Percentile(navigation, 0.5))
                + ",\"navigationP95Ms\":" + Number(Percentile(navigation, 0.95))
                + ",\"dynamicMedianMs\":" + Number(Percentile(dynamic, 0.5))
                + ",\"dynamicP95Ms\":" + Number(Percentile(dynamic, 0.95))
                + ",\"checks\":[" + string.Join(",", checks.ConvertAll(Js).ToArray()) + "]}");
        }
    }

    private static string Page(int sample, string description)
    {
        return "<!doctype html><html><head><meta charset='utf-8'></head><body><h1 id='heading'>SITUATION</h1>"
            + "<p id='description'>" + description + "</p><p id='dynamic'>" + sample + "</p>"
            + "<input id='input' value='MISSION'><div id='edit' contenteditable='true'>MISSION</div>"
            + "<a id='reference' href='file:///cmo-fixture/reference.html?mission=MISSION#anchor'>reference</a>"
            + "<script>requestAnimationFrame(function(){window.__firstFrame=[document.getElementById('heading').textContent,"
            + "document.getElementById('description').textContent];});</script></body></html>";
    }

    private static async Task WaitForRegistration()
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 10000)
        {
            object table = engine.GetField("webTranslations", PrivateStatic).GetValue(null);
            object[] arguments = new object[] { browser, null };
            if ((bool)table.GetType().GetMethod("TryGetValue").Invoke(table, arguments))
            {
                FieldInfo field = arguments[1].GetType().GetField("Registration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    Task task = field.GetValue(arguments[1]) as Task;
                    if (task != null) { await task; return; }
                }
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Candidate document-created script registration did not complete");
    }

    private static async Task<bool> IsTrue(string condition)
    {
        return await browser.ExecuteScriptAsync("(function(){try{return !!(" + condition + ");}catch(e){return false;}})()") == "true";
    }

    private static async Task WaitFor(string condition, int timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < timeout)
        {
            if (timerError != null) throw timerError;
            if (await IsTrue(condition)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("DOM translation timeout: " + condition);
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static string Bool(bool value) { return value ? "true" : "false"; }
    private static string Number(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        return values[(int)Math.Ceiling(values.Count * percentile) - 1];
    }
    private static string Js(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char c in value)
            if (c == '"' || c == '\\') result.Append('\\').Append(c);
            else if (c < ' ' || c == '\u2028' || c == '\u2029') result.Append("\\u").Append(((int)c).ToString("x4"));
            else result.Append(c);
        return result.Append('"').ToString();
    }
}
