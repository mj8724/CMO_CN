using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

class FirstPaintTests
{
    private static Type engine;
    private static MethodInfo Method(string name)
    {
        return engine.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
    }
    private static object Call(string name, params object[] args)
    {
        return Method(name).Invoke(null, args);
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            Forms.Application.DoEvents();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            Thread.Sleep(1);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            Call("LoadDictionaries", args[1]);
            string mode = args.Length > 2 ? args[2] : "startup";
            if (mode == "startup") Startup();
            else if (mode == "mutation") Mutation();
            else if (mode == "input") Input();
            else if (mode == "wpf") Wpf();
            else if (mode == "wpf-dynamic") WpfDynamic();
            else if (mode == "wpf-editable") WpfEditable();
            else if (mode == "burst") Burst();
            else if (mode == "benchmark") Lookup();
            else throw new Exception("Unknown mode " + mode);
            Console.WriteLine("PASS " + mode);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL " + ex);
            return 1;
        }
    }

    private static void Startup()
    {
        using (Forms.Form form = new Forms.Form())
        {
            Forms.Label label = new Forms.Label { Text = "Ready", AutoSize = true };
            form.Controls.Add(label);
            Stopwatch elapsed = Stopwatch.StartNew();
            Thread watcher = new Thread(delegate() { Call("StartUIWatcher"); });
            watcher.IsBackground = true;
            form.Show();
            watcher.Start();
            while (label.Text == "Ready" && elapsed.ElapsedMilliseconds < 2500) Pump(1);
            Console.WriteLine("METRIC first_translation_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            Assert(label.Text != "Ready", "Initial label was not translated");
            Assert(elapsed.ElapsedMilliseconds < 350, "First translation still waits for the 600ms polling interval");
            form.Close();
        }
    }

    private static void Mutation()
    {
        using (Forms.ComboBox combo = new Forms.ComboBox())
        using (Forms.ListBox list = new Forms.ListBox())
        {
            combo.Items.Add("Ready");
            list.Items.Add("Ready");
            for (int i = 0; i < 20; i++)
            {
                Call("LocalizeComboBoxSafe", combo);
                Call("LocalizeListBoxSafe", list);
                int depth = (int)engine.GetField("_mutationDepth", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                Assert(depth == 0, "Item translation leaked mutation depth: " + depth);
            }
        }
    }

    private static void Input()
    {
        using (Forms.TextBox input = new Forms.TextBox { Text = "Ready" })
        {
            Call("TranslateControlTree", input);
            Assert(input.Text == "Ready", "WinForms editor content changed");
        }
        RichTextBox rich = new RichTextBox();
        rich.Document.Blocks.Add(new Paragraph(new Run("Ready")));
        Call("TranslateWpfElement", rich);
        string text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim();
        Assert(text == "Ready", "Editable WPF rich text changed");
        rich.IsReadOnly = true;
        Call("TranslateWpfElement", rich);
        text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim();
        Assert(text != "Ready", "Read-only WPF rich text failed to translate");
    }

    private static void Wpf()
    {
        TextBlock label = new TextBlock { Text = "Ready" };
        Window window = new Window { Content = label, Width = 240, Height = 100, Title = "CMO localization fixture" };
        window.Show();
        Thread scan = new Thread(delegate() { Call("TranslateWpfWindows"); });
        scan.IsBackground = true;
        scan.Start();
        Pump(350);
        FieldInfo frame = engine.GetField("_renderFrame", BindingFlags.Static | BindingFlags.NonPublic);
        int ticks = (int)frame.GetValue(null);
        Console.WriteLine("METRIC wpf_render_ticks=" + ticks);
        Assert(ticks > 0, "WPF rendering handler did not run on the window Dispatcher");
        System.Windows.Data.Binding binding = new System.Windows.Data.Binding("Text") { Source = new TextBlock { Text = "Ready" } };
        label.SetBinding(TextBlock.TextProperty, binding);
        Call("TranslateWpfElement", label);
        Assert(System.Windows.Data.BindingOperations.IsDataBound(label, TextBlock.TextProperty), "Translation removed WPF binding");
        window.Close();
    }

    private static void Lookup()
    {
        MethodInfo translate = Method("TranslateString");
        translate.Invoke(null, new object[] { "Ready" });
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int i = 0; i < 100000; i++) translate.Invoke(null, new object[] { "Ready" });
        Console.WriteLine("METRIC lookup_100k_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddWpfFixtureTranslations()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap",
            BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        exact["First paint dynamic label"] = "动态首屏标签";
        exact["First paint dynamic update"] = "动态首屏更新";
    }

    private static Window WpfFixtureWindow(object content)
    {
        return new Window { Content = content, Width = 300, Height = 200,
            Left = -2000, Top = -2000, ShowInTaskbar = false, Title = "CMO WPF dynamic fixture" };
    }

    private static void AttachWpfFixture(Window window)
    {
        window.Show();
        Thread scan = new Thread(delegate() { Call("TranslateWpfWindows"); });
        scan.IsBackground = true;
        scan.Start();
        Assert(scan.Join(1000), "WPF discovery callback did not return");
        Pump(120);
        FieldInfo frame = engine.GetField("_renderFrame", BindingFlags.Static | BindingFlags.NonPublic);
        Assert((int)frame.GetValue(null) > 0, "WPF fixture has no active render handler");
    }

    private static string TextAtNextWpfFrame(Run run)
    {
        string observed = null;
        EventHandler observer = delegate
        {
            if (observed == null) observed = run.Text;
        };
        System.Windows.Media.CompositionTarget.Rendering += observer;
        try
        {
            run.Text = "First paint dynamic update";
            Stopwatch elapsed = Stopwatch.StartNew();
            while (observed == null && elapsed.ElapsedMilliseconds < 350) Pump(1);
            Assert(observed != null, "No next WPF frame was observed");
            Console.WriteLine("METRIC wpf_next_frame_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture));
            return observed;
        }
        finally { System.Windows.Media.CompositionTarget.Rendering -= observer; }
    }

    private static void WpfDynamic()
    {
        AddWpfFixtureTranslations();
        StackPanel panel = new StackPanel();
        Run run = new Run("First paint dynamic label");
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer();
        viewer.Height = 80;
        viewer.Document = new FlowDocument(new Paragraph(run));
        panel.Children.Add(viewer);
        Window window = WpfFixtureWindow(panel);
        try
        {
            AttachWpfFixture(window);
            Assert(run.Text == "动态首屏标签", "Initial read-only document was not translated");
            int childLoads = 0;
            TextBlock added = new TextBlock { Text = "First paint dynamic update" };
            added.Loaded += delegate { childLoads++; };
            Stopwatch elapsed = Stopwatch.StartNew();
            panel.Children.Add(added);
            while ((childLoads == 0 || added.Text != "动态首屏更新") && elapsed.ElapsedMilliseconds < 200) Pump(1);
            Console.WriteLine("METRIC wpf_dynamic_child_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture) + " loaded=" + childLoads + " text=" + added.Text);
            bool childTranslated = childLoads > 0 && added.Text == "动态首屏更新";
            string observedRun = TextAtNextWpfFrame(run);
            Console.WriteLine("METRIC wpf_dynamic_run_text=" + observedRun);
            Assert(childTranslated, "A loaded child in an existing WPF window stayed English without a polling scan");
            Assert(observedRun == "动态首屏更新", "Tracked FlowDocument Run stayed English at the next WPF frame");
        }
        finally { window.Close(); }
    }

    private static void WpfEditable()
    {
        AddWpfFixtureTranslations();
        StackPanel panel = new StackPanel();
        Button focusTarget = new Button { Content = "fixture focus target" };
        RichTextBox editor = new RichTextBox { IsReadOnly = true, Height = 90 };
        Run run = new Run("First paint dynamic label");
        editor.Document = new FlowDocument(new Paragraph(run));
        panel.Children.Add(focusTarget);
        panel.Children.Add(editor);
        Window window = WpfFixtureWindow(panel);
        try
        {
            AttachWpfFixture(window);
            focusTarget.Focus();
            Pump(10);
            Call("TranslateWpfElement", editor);
            Assert(run.Text == "动态首屏标签", "Read-only editor fixture did not enter live translation");
            List<WeakReference> live = engine.GetField("liveWpfTargets", BindingFlags.Static | BindingFlags.NonPublic)
                .GetValue(null) as List<WeakReference>;
            Assert(live != null && live.Exists(delegate(WeakReference item) { return object.ReferenceEquals(item.Target, run); }),
                "The editable-transition fixture Run was never tracked");
            List<string> ancestors = new List<string>();
            DependencyObject current = run;
            bool foundOwner = false;
            for (int i = 0; current != null && i < 20; i++)
            {
                ancestors.Add(current.GetType().Name);
                if (object.ReferenceEquals(current, editor)) foundOwner = true;
                current = LogicalTreeHelper.GetParent(current);
            }
            Console.WriteLine("METRIC wpf_run_logical_ancestors=" + string.Join("->", ancestors.ToArray()));
            Assert(foundOwner, "LogicalTreeHelper did not find the RichTextBox document owner");
            editor.IsReadOnly = false;
            string observed = TextAtNextWpfFrame(run);
            Pump(100);
            Assert(observed == "First paint dynamic update" && run.Text == "First paint dynamic update",
                "Live Run translation rewrote user input after its owner became editable");
            Console.WriteLine("METRIC wpf_editable_text_preserved=true");
        }
        finally { window.Close(); }
    }

    private static void Burst()
    {
        for (int i = 0; i < 20; i++)
        {
            Assert((bool)Call("AllowWrite", 91001, "Ready", "normal refresh"), "Ordinary spaced refresh was blocked");
            Thread.Sleep(16);
        }
        bool blocked = false;
        for (int i = 0; i < 30; i++)
            if (!(bool)Call("AllowWrite", 91002, "Ready", "write loop")) blocked = true;
        Assert(blocked, "Tight write loop was not limited");
        Thread.Sleep(300);
        Assert(!(bool)Call("IsWriteBlocked", 91002), "Write loop guard did not recover after cooldown");
    }
}
