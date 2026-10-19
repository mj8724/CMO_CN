using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Tests the actual DLL's reflection adapter without loading a game or WebView2 SDK.
public sealed class FakeMessage : EventArgs
{
    private readonly string text;
    public FakeMessage(string text) { this.text = text; Source = "about:blank"; }
    public string Source { get; set; }
    public string TryGetWebMessageAsString() { return text; }
}

public class FakeExecuteCore
{
    public FakeSettings Settings { get; private set; }
    public readonly List<string> Executed = new List<string>();
    public readonly Queue<Task<string>> Results = new Queue<Task<string>>();
    public string Source { get; set; }
    public FakeExecuteCore() { Source = "about:blank"; Settings = new FakeSettings(); }
    public Task<string> ExecuteScriptAsync(string script)
    {
        Executed.Add(script);
        return Results.Count > 0 ? Results.Dequeue() : Task.FromResult("null");
    }
}

public sealed class FakeCore : FakeExecuteCore
{
    public event EventHandler<FakeMessage> WebMessageReceived;
    public TaskCompletionSource<string> Registration = new TaskCompletionSource<string>();
    public int Registrations;
    public string Bootstrap;
    public Task<string> AddScriptToExecuteOnDocumentCreatedAsync(string script)
    {
        Registrations++;
        Bootstrap = script;
        return Registration.Task;
    }
    public void Send(string message, string source = "about:blank")
    {
        if (WebMessageReceived != null) WebMessageReceived(this, new FakeMessage(message) { Source = source });
    }
}

public sealed class FakeSettings
{
    public bool IsWebMessageEnabled { get; set; }
    public FakeSettings() { IsWebMessageEnabled = true; }
}

public sealed class FakeWebView : Control
{
    public object CoreWebView2 { get; set; }
    public event EventHandler<EventArgs> CoreWebView2InitializationCompleted;
    public void CompleteInitialization()
    {
        if (CoreWebView2InitializationCompleted != null) CoreWebView2InitializationCompleted(this, EventArgs.Empty);
    }
}

public static class WebViewTranslationHostTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static void PumpUntil(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new Exception("UI callback timed out");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
    private static string ScriptMessage(string nonce, string source)
    {
        string message = "CMO_WEB_V1\t" + nonce + "\n1\t" + Uri.EscapeDataString(source);
        return "\"" + Uri.EscapeDataString(message) + "\"";
    }
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Assembly dll = Assembly.LoadFrom(args[0]);
            Type engine = dll.GetType("CMOLocalizer.CustomAppDomainManager", true);
            MethodInfo translate = engine.GetMethod("TranslateWebViewEarly", BindingFlags.Static | BindingFlags.NonPublic);
            Check(translate != null, "early WebView2 adapter exists");
            var exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            exact["SITUATION"] = "战场态势";
            exact["Detailed description."] = "详细说明。";
            using (var control = new FakeWebView())
            {
                control.CreateControl();
                translate.Invoke(null, new object[] { control });
                translate.Invoke(null, new object[] { control });
                var core = new FakeCore();
                control.CoreWebView2 = core;
                control.CompleteInitialization();
                Check(core.Registrations == 1, "initialization installs one document-created script");
                Check(core.Executed.Count == 0, "current document waits asynchronously for registration");
                core.Registration.SetResult("script-1");
                PumpUntil(() => core.Executed.Count == 1);
                Check(core.Bootstrap.Contains("战场态势") && !core.Bootstrap.Contains("__CMO_WEB_LABELS__"), "small label map is embedded in the bootstrap");
                translate.Invoke(null, new object[] { control });
                Check(core.Registrations == 1 && core.Executed.Count == 1, "watcher rediscovery neither registers nor executes again");
                core.Send("CMO_WEB_V1\tdoc-one\n1\tDetailed%20description.");
                PumpUntil(() => core.Executed.Count == 2);
                Check(core.Executed[1].Contains("doc-one") && core.Executed[1].Contains("详细说明。"), "host bridge invokes the real briefing translator");
                core.Send("unrelated game message");
                Application.DoEvents();
                Check(core.Executed.Count == 2, "unrelated native messages are ignored");
                core.Send("CMO_WEB_V1\tdoc-one\n3\t");
                core.Send("CMO_WEB_V1\tdoc-one\n-1\tinvalid");
                core.Send("CMO_WEB_V1\tdoc-one\n4\t" + new string('x', 524288));
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(1); }
                Check(core.Executed.Count == 2, "empty, invalid and oversized native requests do not schedule replies");
                core.Send("CMO_WEB_V1\tdoc-one\n5\tDetailed%20description.", "https://outside.invalid/");
                core.Source = "https://outside.invalid/";
                core.Send("CMO_WEB_V1\tdoc-one\n6\tDetailed%20description.");
                core.Source = "about:blank";
                core.Send("CMO_WEB_V1\tdoc-one\n7\tDetailed%20description.");
                core.Source = "https://outside.invalid/";
                for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(1); }
                Check(core.Executed.Count == 2, "event origin, native origin and reply-time navigation are all checked");
                core.Source = "about:blank";
                exact["[LOADDOC]test.html[/LOADDOC]"] = "THIS COMMAND MUST NOT BE TRANSLATED";
                core.Send("CMO_WEB_V1\tdoc-one\n8\t%5BLOADDOC%5Dtest.html%5B%2FLOADDOC%5D");
                PumpUntil(() => core.Executed.Count == 3);
                Check(!core.Executed[2].Contains("THIS COMMAND MUST NOT BE TRANSLATED") && core.Executed[2].Contains("[LOADDOC]"),
                    "document commands are acknowledged unchanged without invoking the translation chain");
                // A response for a disposed control must not be posted to its UI queue.
                control.Dispose();
                core.Send("CMO_WEB_V1\tdoc-two\n2\tDetailed%20description.");
                Application.DoEvents();
                Check(core.Executed.Count == 3, "dispose detaches the native message handler");
            }
            if (args.Length < 2 || args[1] == "retry") using (var retry = new FakeWebView())
            {
                retry.CreateControl();
                var core = new FakeCore();
                retry.CoreWebView2 = core;
                translate.Invoke(null, new object[] { retry });
                core.Results.Enqueue(Task.FromResult(ScriptMessage("failed-registration", "Detailed description.")));
                core.Registration.SetException(new Exception("synthetic registration failure"));
                PumpUntil(() => core.Executed.Exists(s => s.Contains("receive(") && s.Contains("详细说明。")));
                for (int i = 0; i < 20; i++) translate.Invoke(null, new object[] { retry });
                Check(core.Registrations == 1, "failed registration uses fallback without a rediscovery retry storm");
            }
            if (args.Length < 2 || args[1] == "disabled") using (var disabled = new FakeWebView())
            {
                disabled.CreateControl();
                var core = new FakeCore();
                core.Settings.IsWebMessageEnabled = false;
                core.Results.Enqueue(Task.FromResult(ScriptMessage("disabled-messages", "Detailed description.")));
                disabled.CoreWebView2 = core;
                translate.Invoke(null, new object[] { disabled });
                core.Registration.SetResult("script-disabled");
                PumpUntil(() => core.Executed.Exists(s => s.Contains("receive(") && s.Contains("详细说明。")));
                Check(!core.Settings.IsWebMessageEnabled, "disabled game messaging stays disabled while fallback translates paragraphs");
                core.Settings.IsWebMessageEnabled = true;
                core.Results.Enqueue(Task.FromResult(ScriptMessage("reenabled-messages", "Detailed description.")));
                PumpUntil(() => core.Executed.Exists(s => s.Contains("reenabled-messages") && s.Contains("详细说明。")));
                Check(true, "same-core messaging false-to-true retains a working fallback");
            }
            if (args.Length < 2 || args[1] == "legacy") using (var legacy = new FakeWebView())
            {
                legacy.CreateControl();
                var core = new FakeExecuteCore();
                core.Results.Enqueue(Task.FromResult(ScriptMessage("no-registration-api", "Detailed description.")));
                legacy.CoreWebView2 = core;
                translate.Invoke(null, new object[] { legacy });
                PumpUntil(() => core.Executed.Exists(s => s.Contains("no-registration-api") && s.Contains("详细说明。")));
                Check(true, "ExecuteScript-only Core translates without document-registration or message APIs");
            }
            if (args.Length < 2 || args[1] == "toggle") using (var toggle = new FakeWebView())
            {
                toggle.CreateControl();
                var core = new FakeCore();
                core.Registration.SetResult("script-toggle");
                toggle.CoreWebView2 = core;
                translate.Invoke(null, new object[] { toggle });
                PumpUntil(() => core.Executed.Count == 1);
                core.Settings.IsWebMessageEnabled = false;
                core.Results.Enqueue(Task.FromResult(ScriptMessage("disabled-after-attach", "Detailed description.")));
                translate.Invoke(null, new object[] { toggle });
                PumpUntil(() => core.Executed.Exists(s => s.Contains("disabled-after-attach") && s.Contains("详细说明。")));
                Check(core.Registrations == 1, "same-core messaging true-to-false switches to fallback without re-registering");
            }
            if (args.Length < 2 || args[1] == "budget") using (var budget = new FakeWebView())
            {
                budget.CreateControl();
                var core = new FakeExecuteCore();
                var delayed = new TaskCompletionSource<string>();
                core.Results.Enqueue(delayed.Task);
                budget.CoreWebView2 = core;
                translate.Invoke(null, new object[] { budget });
                object table = engine.GetField("webTranslations", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                object[] lookup = { budget, null };
                table.GetType().GetMethod("TryGetValue").Invoke(table, lookup);
                object state = lookup[1];
                FieldInfo count = state.GetType().GetField("FallbackDrainCount");
                count.SetValue(state, 7);
                object timer = state.GetType().GetField("FallbackTimer").GetValue(state);
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(timer, new object[] { EventArgs.Empty });
                Check((int)count.GetValue(state) == 7, "timer ticks do not replenish the budget of an in-flight fallback round");
                delayed.SetResult("null");
            }
            if (args.Length < 2 || args[1] == "navigation") using (var navigation = new FakeWebView())
            {
                navigation.CreateControl();
                var core = new FakeExecuteCore { Source = "https://outside.invalid/" };
                navigation.CoreWebView2 = core;
                translate.Invoke(null, new object[] { navigation });
                for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(1); }
                Check(core.Executed.Count == 0, "fallback does not execute in an untrusted native document");
                core.Source = "about:blank";
                var delayed = new TaskCompletionSource<string>();
                core.Results.Enqueue(delayed.Task);
                PumpUntil(() => core.Executed.Count > 0);
                core.Source = "https://outside.invalid/";
                delayed.SetResult(ScriptMessage("stale-fallback", "Detailed description."));
                for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(1); }
                Check(!core.Executed.Exists(s => s.Contains("stale-fallback") && s.Contains("receive(")),
                    "navigation rejects late fallback extraction before translating or writing it");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
