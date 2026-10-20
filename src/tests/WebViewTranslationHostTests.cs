using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
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
    public Action BeforeExecute;
    public string Source { get; set; }
    public FakeExecuteCore() { Source = "about:blank"; Settings = new FakeSettings(); }
    public Task<string> ExecuteScriptAsync(string script)
    {
        Executed.Add(script);
        if (BeforeExecute != null) BeforeExecute();
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
    private bool enabled;
    public Action BeforeRead;
    public bool IsWebMessageEnabled
    {
        get { if (BeforeRead != null) BeforeRead(); return enabled; }
        set { enabled = value; }
    }
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
    public void RemoveHandle() { DestroyHandle(); }
    public void RestoreHandle() { CreateHandle(); }
    public void RebuildHandle() { RecreateHandle(); }
}

public static class WebViewTranslationHostTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint first, uint last, uint flags);

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
    private static object State(Type engine, Control control)
    {
        object table = engine.GetField("webTranslations", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        object[] lookup = { control, null };
        table.GetType().GetMethod("TryGetValue").Invoke(table, lookup);
        return lookup[1];
    }
    private static object StateField(object state, string name)
    {
        return state.GetType().GetField(name).GetValue(state);
    }
    private static void PumpFor(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(1); }
    }
    private static void WaitForPostedCallback(Control control)
    {
        // Wait without dispatching: the completion must really be queued to the old HWND.
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        NativeMessage message;
        while (!PeekMessage(out message, control.Handle, 0xc000, 0xffff, 0))
        {
            if (DateTime.UtcNow > end) throw new Exception("Registration callback was not posted");
            Thread.Sleep(1);
        }
    }
    private static void RegistrationAcrossHandle(Type engine, MethodInfo translate, bool failed, bool queued)
    {
        using (var control = new FakeWebView())
        {
            control.CreateControl();
            var core = new FakeCore();
            control.CoreWebView2 = core;
            translate.Invoke(null, new object[] { control });
            if (!queued) control.RemoveHandle();
            if (failed)
            {
                core.Results.Enqueue(Task.FromResult(ScriptMessage("handle-failure", "Detailed description.")));
                core.Registration.SetException(new Exception("registration completed without a usable handle"));
            }
            else core.Registration.SetResult("script-recreated");
            if (queued)
            {
                WaitForPostedCallback(control);
                control.RemoveHandle();
            }
            else Thread.Sleep(100); // Let the worker completion run while no HWND exists.
            Check(!control.IsHandleCreated && core.Executed.Count == 0,
                "registration completion cannot execute script while the handle is absent (" + failed + ", " + queued + ")");
            translate.Invoke(null, new object[] { control });
            control.RestoreHandle();
            PumpUntil(() => failed
                ? core.Executed.Exists(s => s.Contains("handle-failure") && s.Contains("详细说明。"))
                : core.Executed.Count == 1);
            object state = State(engine, control);
            Check(core.Registrations == 1 && (failed ? (bool)StateField(state, "Fallback")
                : (string)StateField(state, "ScriptId") == "script-recreated" && core.Executed[0] == core.Bootstrap),
                "recreated handle consumes the completed registration exactly once (" + failed + ", " + queued + ")");
            object timer = StateField(state, "FallbackTimer");
            for (int i = 0; i < 10; i++) translate.Invoke(null, new object[] { control });
            if (!failed) { control.RemoveHandle(); control.RestoreHandle(); }
            PumpFor(40);
            Check(core.Registrations == 1 && (failed ? ReferenceEquals(timer, StateField(state, "FallbackTimer"))
                : core.Executed.Count == 1),
                "rediscovery and later handle recreation do not repeat registration or bootstrap (" + failed + ", " + queued + ")");
        }
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
            MethodInfo trusted = engine.GetMethod("TrustedWebSource", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (string source in new string[] { "https://127.example.invalid/", "https://127.0.0.1.example.invalid/" })
                Check(!(bool)trusted.Invoke(null, new object[] { source }), "native source rejects a numeric-prefix external domain: " + source);
            foreach (string source in new string[] { "http://127.0.0.2/", "http://127.1/", "http://localhost/", "http://loopback/",
                "http://[::1]/", "http://[::ffff:127.0.0.1]/", "http://[::127.0.0.1]/", "about:blank?x=1", "about:srcdoc?x=1" })
                Check((bool)trusted.Invoke(null, new object[] { source }), "native source permits the browser's supported local origin: " + source);
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
            if (args.Length < 2 || args[1] == "install-retry") using (var missingScript = new FakeWebView())
            {
                missingScript.CreateControl();
                var core = new FakeExecuteCore { Source = "about:blank?x=1" };
                for (int i = 0; i < 80; i++) core.Results.Enqueue(Task.FromResult("\"CMO_WEB_INSTALL\""));
                missingScript.CoreWebView2 = core;
                translate.Invoke(null, new object[] { missingScript });
                object state = State(engine, missingScript);
                var timer = (System.Windows.Forms.Timer)StateField(state, "FallbackTimer");
                timer.Stop();
                PumpFor(80);
                Check(core.Executed.Count == 1 && !(bool)StateField(state, "FallbackInFlight"),
                    "an unsuccessful fallback installation does not immediately reinstall in a hot loop");
                state.GetType().GetField("FallbackDrainCount").SetValue(state, 7);
                engine.GetMethod("PollWebFallback", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { state, false });
                PumpFor(60);
                Check(core.Executed.Count == 2 && (int)StateField(state, "FallbackDrainCount") == 8,
                    "a missing fallback script consumes the remaining round budget before reinstalling");
                typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(timer, new object[] { EventArgs.Empty });
                PumpFor(60);
                Check(core.Executed.Count == 4 && !(bool)StateField(state, "FallbackInFlight"),
                    "the next fallback timer round permits one bounded installation retry");
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
            if (args.Length < 2 || args[1] == "handle-success") RegistrationAcrossHandle(engine, translate, false, false);
            if (args.Length < 2 || args[1] == "handle-failure") RegistrationAcrossHandle(engine, translate, true, false);
            if (args.Length < 2 || args[1] == "handle-queued") RegistrationAcrossHandle(engine, translate, false, true);
            if (args.Length < 2 || args[1] == "handle-queued-failure") RegistrationAcrossHandle(engine, translate, true, true);
            if (args.Length < 2 || args[1] == "handle-replaced") using (var replaced = new FakeWebView())
            {
                replaced.CreateControl();
                var oldCore = new FakeCore();
                replaced.CoreWebView2 = oldCore;
                translate.Invoke(null, new object[] { replaced });
                replaced.RemoveHandle();
                var core = new FakeCore();
                replaced.CoreWebView2 = core;
                replaced.RestoreHandle();
                Check(core.Registrations == 1, "handle recreation attaches the actual replacement Core");
                oldCore.Registration.SetException(new Exception("obsolete Core registration"));
                PumpFor(60);
                Check(core.Executed.Count == 0 && !(bool)StateField(State(engine, replaced), "Fallback"),
                    "obsolete Core registration failure cannot enable fallback on the new Core");
                core.Registration.SetResult("new-core");
                PumpUntil(() => core.Executed.Count == 1);
                Check(oldCore.Executed.Count == 0 && core.Registrations == 1,
                    "only the current Core registration bootstraps after handle recreation");
                var third = new FakeCore();
                replaced.CoreWebView2 = third;
                translate.Invoke(null, new object[] { replaced });
                third.Registration.SetResult("third-core");
                WaitForPostedCallback(replaced);
                var fourth = new FakeCore();
                replaced.CoreWebView2 = fourth;
                translate.Invoke(null, new object[] { replaced });
                fourth.Registration.SetResult("fourth-core");
                PumpUntil(() => fourth.Executed.Count == 1);
                Check(third.Executed.Count == 0 && fourth.Registrations == 1,
                    "a queued obsolete registration success does not bootstrap the replacement Core");
            }
            if (args.Length < 2 || args[1] == "handle-dispose")
            {
                var disposed = new FakeWebView();
                disposed.CreateControl();
                var core = new FakeCore();
                disposed.CoreWebView2 = core;
                translate.Invoke(null, new object[] { disposed });
                disposed.RemoveHandle();
                disposed.Dispose();
                core.Registration.SetException(new Exception("disposed registration"));
                PumpFor(60);
                Check(core.Executed.Count == 0 && State(engine, disposed) == null,
                    "registration completion after disposal leaves no scripts or tracked state");
            }
            if (args.Length < 2 || args[1] == "handle-null")
            {
                foreach (bool failed in new bool[] { false, true }) using (var empty = new FakeWebView())
                {
                    empty.CreateControl();
                    var oldCore = new FakeCore();
                    empty.CoreWebView2 = oldCore;
                    translate.Invoke(null, new object[] { empty });
                    empty.RemoveHandle();
                    empty.CoreWebView2 = null;
                    if (failed) oldCore.Registration.SetException(new Exception("removed Core registration"));
                    else oldCore.Registration.SetResult("removed-core");
                    Thread.Sleep(100);
                    empty.RestoreHandle();
                    PumpFor(60);
                    object state = State(engine, empty);
                    Check(oldCore.Executed.Count == 0 && StateField(state, "Core") == null
                        && !(bool)StateField(state, "Fallback"),
                        "a missing actual Core cannot consume obsolete registration or start fallback (" + failed + ")");
                    var core = new FakeCore();
                    empty.CoreWebView2 = core;
                    empty.CompleteInitialization();
                    core.Registration.SetResult("later-core");
                    PumpUntil(() => core.Executed.Count == 1);
                    Check(core.Registrations == 1 && oldCore.Executed.Count == 0,
                        "initialization after a null Core attaches only the current registration (" + failed + ")");
                }
                using (var empty = new FakeWebView())
                {
                    empty.CreateControl();
                    var oldCore = new FakeExecuteCore();
                    empty.CoreWebView2 = oldCore;
                    translate.Invoke(null, new object[] { empty });
                    empty.RemoveHandle();
                    empty.CoreWebView2 = null;
                    empty.RestoreHandle();
                    PumpFor(60);
                    object state = State(engine, empty);
                    Check(oldCore.Executed.Count == 1 && !(bool)StateField(state, "Fallback")
                        && !((System.Windows.Forms.Timer)StateField(state, "FallbackTimer")).Enabled,
                        "a null Core on handle recreation cannot restart the old Core fallback timer");
                }
            }
            if (args.Length < 2 || args[1] == "handle-recreate") using (var recreated = new FakeWebView())
            {
                recreated.CreateControl();
                var core = new FakeCore();
                recreated.CoreWebView2 = core;
                translate.Invoke(null, new object[] { recreated });
                core.Registration.SetResult("winforms-recreated");
                WaitForPostedCallback(recreated);
                recreated.RebuildHandle();
                PumpUntil(() => core.Executed.Count == 1);
                PumpFor(40);
                Check(core.Registrations == 1 && core.Executed.Count == 1,
                    "WinForms RecreateHandle and its reposted old callback consume registration only once");
            }
            if (args.Length < 2 || args[1] == "handle-fallback") using (var fallback = new FakeWebView())
            {
                var core = new FakeExecuteCore();
                fallback.CoreWebView2 = core;
                translate.Invoke(null, new object[] { fallback });
                Check(!fallback.IsHandleCreated && core.Executed.Count == 0,
                    "legacy fallback can attach before a handle exists without executing");
                var delayed = new TaskCompletionSource<string>();
                core.Results.Enqueue(delayed.Task);
                fallback.RestoreHandle();
                Check(core.Executed.Count == 1, "first handle creation starts the waiting legacy fallback");
                object state = State(engine, fallback);
                object timer = StateField(state, "FallbackTimer");
                fallback.RemoveHandle();
                fallback.RestoreHandle();
                delayed.SetResult(ScriptMessage("old-handle-result", "Detailed description."));
                PumpFor(60);
                Check(core.Executed.Count == 2 && ReferenceEquals(timer, StateField(state, "FallbackTimer"))
                    && !core.Executed.Exists(s => s.Contains("old-handle-result") && s.Contains("receive(")),
                    "fallback handle recreation retains one timer and rejects the previous handle's extraction");
            }
            if (args.Length < 2 || args[1] == "handle-reentrant") using (var reentrant = new FakeWebView())
            {
                reentrant.CreateControl();
                var oldCore = new FakeCore();
                reentrant.CoreWebView2 = oldCore;
                translate.Invoke(null, new object[] { reentrant });
                reentrant.RemoveHandle();
                oldCore.Registration.SetResult("reentrant-old");
                Thread.Sleep(100);
                var core = new FakeCore();
                oldCore.BeforeExecute = () =>
                {
                    oldCore.Settings.IsWebMessageEnabled = false;
                    reentrant.CoreWebView2 = core;
                    translate.Invoke(null, new object[] { reentrant });
                };
                reentrant.RestoreHandle();
                Check(core.Registrations == 1 && core.Executed.Count == 0
                    && !(bool)StateField(State(engine, reentrant), "Fallback"),
                    "reentrant bootstrap replacing Core cannot apply the old Core messaging settings to the new one");
                core.Registration.SetResult("reentrant-new");
                PumpUntil(() => core.Executed.Count == 1);
                Check(oldCore.Executed.Count == 1 && core.Executed[0] == core.Bootstrap,
                    "reentrant replacement still waits for its own registration and bootstraps once");
            }
            if (args.Length < 2 || args[1] == "registration-settings") using (var reentrant = new FakeWebView())
            {
                reentrant.CreateControl();
                var oldCore = new FakeCore();
                reentrant.CoreWebView2 = oldCore;
                translate.Invoke(null, new object[] { reentrant });
                var core = new FakeCore();
                oldCore.Settings.BeforeRead = () =>
                {
                    oldCore.Settings.BeforeRead = null;
                    reentrant.CoreWebView2 = core;
                    translate.Invoke(null, new object[] { reentrant });
                };
                oldCore.Registration.SetResult("original-settings");
                PumpUntil(() => oldCore.Executed.Count + core.Executed.Count > 0);
                Check(core.Executed.Count == 0 && oldCore.Executed.Count == 1,
                    "registration completion uses its captured mode without a reentrant settings read executing the old bootstrap on a new Core");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
