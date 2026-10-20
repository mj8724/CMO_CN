using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// Independent STA fixture. Never calls InitializeNewDomain, starts the game,
// or runs the legacy polling watcher. All windows belong to this test process.
class UiLifecycleTests
{
    private static Type engine;
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static int checks;

    private sealed class InternalDBViewer : Form { }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static object Call(string name, params object[] args)
    {
        MethodInfo method = engine.GetMethod(name, StaticPrivate);
        // The unmodified engine is the red baseline: absent lifecycle hooks
        // leave the fixture running normally, without a fake polling fallback.
        return method == null ? null : method.Invoke(null, args);
    }

    private static Form Window(string text)
    {
        Form form = new Form();
        ConfigureWindow(form, text);
        return form;
    }

    private static void ConfigureWindow(Form form, string text)
    {
        form.Text = text;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-2000, -2000);
        form.Size = new Size(320, 180);
        form.ShowInTaskbar = false;
    }

    private static double WaitUntil(Func<bool> done, int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        return elapsed.Elapsed.TotalMilliseconds;
    }

    private static void StartWatcher()
    {
        Call("StartUiLifecycleWatcher");
        FieldInfo readyField = engine.GetField("uiLifecycleReady", StaticPrivate);
        if (readyField != null)
        {
            WaitHandle ready = (WaitHandle)readyField.GetValue(null);
            Require(ready.WaitOne(3000), "Lifecycle watcher did not finish startup");
            FieldInfo hook = engine.GetField("uiLifecycleHook", StaticPrivate);
            Require(hook != null && (IntPtr)hook.GetValue(null) != IntPtr.Zero,
                "Same-process WinEvent hook was not installed");
        }
    }

    private static void NewControl()
    {
        using (Form form = Window("Fixture"))
        using (Panel parent = new Panel())
        {
            form.Controls.Add(parent);
            form.Show();
            Call("TranslateControlTree", form);
            Label label = new Label();
            label.Text = "Lifecycle label";
            Stopwatch elapsed = Stopwatch.StartNew();
            parent.Controls.Add(label);
            double latency = WaitUntil(delegate { return label.Text == "生命周期标签"; }, 350);
            Require(label.Text == "生命周期标签", "ControlAdded left a new label English without a polling scan");
            Console.WriteLine("NEW_CONTROL_MS=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3"));
            Require(latency < 350, "New control exceeded the lifecycle dispatch deadline");
            label.Text = "Lifecycle changed";
            Require(label.Text == "生命周期变化", "Existing TextChanged hook was not attached to the new label");
        }
    }

    private static void NewWindow()
    {
        StartWatcher();
        using (Form form = Window("Lifecycle window"))
        {
            Label label = new Label();
            label.Text = "Lifecycle label";
            form.Controls.Add(label);
            Stopwatch elapsed = Stopwatch.StartNew();
            form.Show();
            double latency = WaitUntil(delegate { return form.Text == "生命周期窗口" && label.Text == "生命周期标签"; }, 350);
            Require(form.Text == "生命周期窗口" && label.Text == "生命周期标签",
                "Native CREATE/SHOW left a new form English without a polling scan");
            Console.WriteLine("NEW_WINDOW_MS=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3"));
            Require(latency < 350, "New window exceeded the lifecycle dispatch deadline");
        }
    }

    private static void ExistingWindow()
    {
        using (Form form = Window("Lifecycle window"))
        {
            form.Show();
            StartWatcher();
            WaitUntil(delegate { return form.Text == "生命周期窗口"; }, 350);
            Require(form.Text == "生命周期窗口", "Watcher startup did not discover an already visible window");
        }
    }

    private static void InputsAndTokens()
    {
        StartWatcher();
        using (InternalDBViewer form = new InternalDBViewer())
        {
            ConfigureWindow(form, "Lifecycle window");
            TextBox text = new TextBox();
            text.Text = "Lifecycle label";
            ComboBox tokens = new ComboBox();
            tokens.Name = "CB_ObjectType";
            tokens.Items.Add("Aircraft");
            tokens.Items.Add("Ship");
            tokens.SelectedIndex = 0;
            tokens.Top = 30;
            form.Controls.Add(text);
            form.Controls.Add(tokens);
            form.Show();
            WaitUntil(delegate { return form.Text == "生命周期窗口"; }, 350);
            Require(form.Text == "生命周期窗口", "Token fixture was not discovered");
            Require(text.Text == "Lifecycle label", "Lifecycle translation rewrote editable text");
            Require((string)tokens.Items[0] == "Aircraft" && (string)tokens.Items[1] == "Ship"
                && tokens.SelectedIndex == 0 && tokens.Text == "Aircraft",
                "Lifecycle translation rewrote protected database tokens or selection");
            TextBox addedInput = new TextBox();
            addedInput.Text = "Lifecycle changed";
            form.Controls.Add(addedInput);
            Require(addedInput.Text == "Lifecycle changed", "ControlAdded rewrote newly added editable text");
        }
    }

    private static void WorkerUiThread()
    {
        StartWatcher();
        Exception failure = null;
        double latency = -1;
        Thread ui = new Thread(delegate()
        {
            try
            {
                using (Form form = Window("Lifecycle window"))
                {
                    Stopwatch elapsed = Stopwatch.StartNew();
                    form.Show();
                    WaitUntil(delegate { return form.Text == "生命周期窗口"; }, 350);
                    latency = elapsed.Elapsed.TotalMilliseconds;
                    Require(form.Text == "生命周期窗口", "Secondary UI thread form was not translated");
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        Require(ui.Join(5000), "Secondary UI fixture did not stop");
        if (failure != null) throw failure;
        Console.WriteLine("SECONDARY_UI_MS=" + latency.ToString("F3"));
    }

    private static void WpfWindow()
    {
        StartWatcher();
        System.Windows.Window window = new System.Windows.Window();
        window.Title = "Lifecycle window";
        window.Left = -2000;
        window.Top = -2000;
        window.Width = 320;
        window.Height = 180;
        window.ShowInTaskbar = false;
        System.Windows.Controls.TextBlock label = new System.Windows.Controls.TextBlock();
        label.Text = "Lifecycle label";
        window.Content = label;
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            window.Show();
            WaitUntil(delegate { return window.Title == "生命周期窗口" && label.Text == "生命周期标签"; }, 350);
            Require(window.Title == "生命周期窗口" && label.Text == "生命周期标签",
                "Native CREATE/SHOW did not route the WPF window to its Dispatcher");
            Console.WriteLine("WPF_WINDOW_MS=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3"));
        }
        finally { window.Close(); }
    }

    private static void ReattachAndDispose()
    {
        StartWatcher();
        for (int i = 0; i < 30; i++)
        {
            using (Form form = Window("Fixture"))
            {
                form.Show();
                Call("TranslateControlTree", form);
                for (int j = 0; j < 5; j++) Call("AttachUiLifecycle", form);
                Label label = new Label();
                label.Text = "Lifecycle label";
                form.Controls.Add(label);
                Require(label.Text == "生命周期标签", "Repeated attachment lost the ControlAdded hook");
            }
            Application.DoEvents();
        }
        // Dispose before an asynchronous native discovery callback can run.
        for (int i = 0; i < 30; i++)
        {
            Form transient = Window("Lifecycle window");
            transient.Show();
            transient.Dispose();
        }
        WaitUntil(delegate
        {
            IDictionary pendingSnapshot = engine.GetField("uiLifecyclePending", StaticPrivate).GetValue(null) as IDictionary;
            return pendingSnapshot != null && pendingSnapshot.Count == 0;
        }, 350);
        IDictionary pendingBeforeStop = engine.GetField("uiLifecyclePending", StaticPrivate).GetValue(null) as IDictionary;
        Require(pendingBeforeStop != null && pendingBeforeStop.Count == 0,
            "Destroyed windows remain rooted before the watcher stops");
        Call("StopUiLifecycleWatcher");
        FieldInfo hook = engine.GetField("uiLifecycleHook", StaticPrivate);
        Require(hook != null && (IntPtr)hook.GetValue(null) == IntPtr.Zero, "WinEvent hook was not released");
        FieldInfo thread = engine.GetField("uiLifecycleThread", StaticPrivate);
        Thread watcher = thread == null ? null : thread.GetValue(null) as Thread;
        Require(watcher == null || !watcher.IsAlive, "WinEvent message loop was not stopped");
        FieldInfo pending = engine.GetField("uiLifecyclePending", StaticPrivate);
        ICollection pendingItems = pending == null ? null : pending.GetValue(null) as ICollection;
        if (pendingItems != null) Require(pendingItems.Count == 0, "Disposed controls remain in the pending dispatch set");
        StartWatcher();
        using (Form restarted = Window("Lifecycle window"))
        {
            restarted.Show();
            WaitUntil(delegate { return restarted.Text == "生命周期窗口"; }, 350);
            Require(restarted.Text == "生命周期窗口", "Restarted watcher did not receive CREATE/SHOW");
        }
    }

    private static void PollingBenchmark()
    {
        StartWatcher();
        Thread poller = new Thread(delegate() { Call("StartUIWatcher"); });
        poller.IsBackground = true;
        poller.Start();
        List<double> elapsed = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            using (Form form = Window("Lifecycle window"))
            {
                Stopwatch time = Stopwatch.StartNew();
                form.Show();
                WaitUntil(delegate { return form.Text == "生命周期窗口"; }, 2200);
                Require(form.Text == "生命周期窗口", "Polling comparison form was never translated");
                elapsed.Add(time.Elapsed.TotalMilliseconds);
            }
            Application.DoEvents();
        }
        elapsed.Sort();
        Console.WriteLine("POLLING_COMPARISON_N=10 P50_MS=" + elapsed[4].ToString("F3")
            + " P95_MS=" + elapsed[9].ToString("F3") + " MAX_MS=" + elapsed[9].ToString("F3"));
    }

    private static void Benchmark()
    {
        StartWatcher();
        List<double> native = new List<double>();
        List<double> added = new List<double>();
        Stopwatch total = Stopwatch.StartNew();
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        for (int i = 0; i < 40; i++)
        {
            using (Form form = Window("Lifecycle window"))
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                form.Show();
                WaitUntil(delegate { return form.Text == "生命周期窗口"; }, 350);
                Require(form.Text == "生命周期窗口", "Benchmark form did not translate");
                native.Add(elapsed.Elapsed.TotalMilliseconds);
                Label label = new Label();
                label.Text = "Lifecycle label";
                elapsed.Restart();
                form.Controls.Add(label);
                Require(label.Text == "生命周期标签", "Benchmark label did not translate synchronously");
                added.Add(elapsed.Elapsed.TotalMilliseconds);
            }
            Application.DoEvents();
        }
        native.Sort();
        added.Sort();
        Console.WriteLine("WINDOW_BENCH_N=40 P50_MS=" + native[19].ToString("F3")
            + " P95_MS=" + native[37].ToString("F3") + " MAX_MS=" + native[39].ToString("F3"));
        Console.WriteLine("CONTROL_BENCH_N=40 P50_MS=" + added[19].ToString("F3")
            + " P95_MS=" + added[37].ToString("F3") + " MAX_MS=" + added[39].ToString("F3"));
        Console.WriteLine("FIXTURE_WALL_MS=" + total.Elapsed.TotalMilliseconds.ToString("F3")
            + " CPU_MS=" + (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds.ToString("F3"));
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 && args.Length != 3)
                throw new ArgumentException("Usage: UiLifecycleTests <engine.dll> [dictionary.json] <mode>");
            engine = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            if (args.Length == 3) Call("LoadDictionaries", System.IO.Path.GetFullPath(args[1]));
            IDictionary exact = (IDictionary)engine.GetField("exactMap", StaticPrivate).GetValue(null);
            exact["Lifecycle label"] = "生命周期标签";
            exact["Lifecycle window"] = "生命周期窗口";
            exact["Lifecycle changed"] = "生命周期变化";
            Control.CheckForIllegalCrossThreadCalls = true;
            string mode = args[args.Length - 1];
            if (mode == "control") NewControl();
            else if (mode == "window") NewWindow();
            else if (mode == "existing") ExistingWindow();
            else if (mode == "inputs") InputsAndTokens();
            else if (mode == "secondary") WorkerUiThread();
            else if (mode == "wpf") WpfWindow();
            else if (mode == "dispose") ReattachAndDispose();
            else if (mode == "benchmark") Benchmark();
            else if (mode == "polling-benchmark") PollingBenchmark();
            else throw new ArgumentException("Unknown mode: " + mode);
            Call("StopUiLifecycleWatcher");
            Console.WriteLine("PASS " + mode + " checks=" + checks);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
        finally
        {
            if (engine != null) Call("StopUiLifecycleWatcher");
        }
    }
}
