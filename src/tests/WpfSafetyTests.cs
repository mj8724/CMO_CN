using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;

// Hidden native sources exercise real Loaded/Unloaded without an active window.
class WpfSafetyTests
{
    private static Type engine;
    private static int checks;
    private sealed class Model : INotifyPropertyChanged
    {
        private string text = "WPF safety ready";
        public string Text
        {
            get { return text; }
            set { Sets++; text = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Text")); }
        }
        internal int Sets;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Interlocked.Increment(ref checks);
    }

    private static void Translate(DependencyObject target)
    {
        engine.GetMethod("TranslateWpfElement", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { target });
    }

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
    }

    private static HwndSource HiddenSource(FrameworkElement root)
    {
        HwndSourceParameters parameters = new HwndSourceParameters("CMO hidden WPF safety fixture");
        parameters.WindowStyle = unchecked((int)0x80000000); // WS_POPUP, deliberately no WS_VISIBLE.
        parameters.ExtendedWindowStyle = 0x08000000; // WS_EX_NOACTIVATE.
        parameters.PositionX = parameters.PositionY = -32000;
        parameters.Width = 200;
        parameters.Height = 120;
        HwndSource source = new HwndSource(parameters);
        source.RootVisual = root;
        root.UpdateLayout();
        Pump();
        Require(root.IsLoaded, "Hidden native source did not deliver real Loaded");
        return source;
    }

    private static void Headers()
    {
        Model model = new Model();
        GroupBox content = new GroupBox();
        TreeViewItem item = new TreeViewItem();
        content.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("Text") { Source = model });
        item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding("Text") { Source = model });
        Translate(content);
        Translate(item);
        Require(BindingOperations.IsDataBound(content, HeaderedContentControl.HeaderProperty), "HeaderedContentControl translation removed binding");
        Require(BindingOperations.IsDataBound(item, HeaderedItemsControl.HeaderProperty), "HeaderedItemsControl translation removed binding");
        Require((string)content.Header == "安全就绪" && (string)item.Header == "安全就绪", "Bound headers did not translate");
        model.Text = "WPF safety cancel";
        Require((string)content.Header == model.Text && (string)item.Header == model.Text, "Headers stopped following their source");
        Translate(content);
        Translate(item);
        Require((string)content.Header == "安全取消" && (string)item.Header == "安全取消", "Updated headers did not translate");
    }

    private static void Editable()
    {
        TextBlock label = new TextBlock { Text = "WPF safety ready" };
        Button button = new Button { Content = "WPF safety ready" };
        Paragraph paragraph = new Paragraph();
        paragraph.Inlines.Add(new InlineUIContainer(label));
        paragraph.Inlines.Add(new InlineUIContainer(button));
        RichTextBox editor = new RichTextBox { IsReadOnly = true,
            Document = new FlowDocument(paragraph) };
        using (HwndSource source = HiddenSource(editor))
        {
            Translate(label);
            Translate(button);
            Require((string)button.Content == "安全就绪", "Read-only content fixture was never translated");
            editor.IsReadOnly = false;
            label.Text = "WPF safety cancel";
            button.Content = "WPF safety cancel";
            Require(label.Text == "WPF safety cancel", "TextBlock callback rewrote editable descendant");
            Require((string)button.Content == "WPF safety cancel", "Content callback rewrote editable descendant");
            editor.IsReadOnly = true;
            label.Text = "WPF safety ready";
            button.Content = "WPF safety ready";
            Require(label.Text == "安全就绪" && (string)button.Content == "安全就绪", "Read-only transition did not resume translation");
        }
        Pump();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleased(bool loaded, bool content)
    {
        FrameworkElement target = content ? (FrameworkElement)new Button { Content = "WPF safety ready" }
            : new TextBlock { Text = "WPF safety ready" };
        if (loaded)
        {
            using (HwndSource source = HiddenSource(target))
            {
                Translate(target);
                source.RootVisual = null;
                Pump();
                Require(!target.IsLoaded, "Hidden source did not unload target");
            }
            Pump();
        }
        else Translate(target);
        return new WeakReference(target);
    }

    private static void Collection()
    {
        WeakReference[] references = { CreateReleased(false, false), CreateReleased(false, true),
            CreateReleased(true, false), CreateReleased(true, true), CreateReleasedOnShutdown() };
        for (int i = 0; i < 5; i++) { Pump(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        for (int i = 0; i < references.Length; i++)
            Require(!references[i].IsAlive, "Translation subscription retained released target " + i);
    }

    private static WeakReference CreateReleasedOnShutdown()
    {
        WeakReference reference = null;
        Exception failure = null;
        Thread thread = new Thread(delegate()
        {
            try
            {
                TextBlock target = new TextBlock { Text = "WPF safety ready" };
                using (HwndSource source = HiddenSource(target))
                {
                    Translate(target);
                    reference = new WeakReference(target);
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(10000), "Shutdown fixture did not finish");
        if (failure != null) throw failure;
        return reference;
    }

    private static void Lifecycle()
    {
        TextBlock label = new TextBlock { Text = "WPF safety ready" };
        Translate(label);
        using (HwndSource source = HiddenSource(label))
        {
            label.Text = "WPF safety cancel";
            Require(label.Text == "安全取消", "Loaded target did not subscribe");
            source.RootVisual = null;
            Pump();
            Require(!label.IsLoaded, "Target did not unload");
            label.Text = "WPF safety ready";
            Require(label.Text == "WPF safety ready", "Unloaded target still has value-change subscription");
            source.RootVisual = label;
            Pump();
            Require(label.IsLoaded, "Target did not reload");
            label.Text = "WPF safety cancel";
            Require(label.Text == "安全取消", "Reloaded target lost value-change subscription");
        }
        Pump();
    }

    private static void MixedValueUpdates()
    {
        Model model = new Model();
        TextBlock label = new TextBlock();
        Button button = new Button();
        label.SetBinding(TextBlock.TextProperty, new Binding("Text") { Source = model, Mode = BindingMode.OneWay });
        button.SetBinding(ContentControl.ContentProperty, new Binding("Text") { Source = model, Mode = BindingMode.OneWay });
        StackPanel root = new StackPanel();
        root.Children.Add(label);
        root.Children.Add(button);
        using (HwndSource source = HiddenSource(root))
        {
            Require(label.IsLoaded && button.IsLoaded, "Mixed-value targets did not receive real Loaded");
            Translate(label);
            Translate(button);
            Require(label.Text == "安全就绪" && (string)button.Content == "安全就绪", "Mixed-value subscriptions were not initialized");
            Require(BindingOperations.IsDataBound(label, TextBlock.TextProperty)
                && BindingOperations.IsDataBound(button, ContentControl.ContentProperty), "Initial translation removed a mixed-value binding");
            string[] values = { "状态: Unassigned", "所属阵营: Israel", "主动 (Active)", "主动 (Active)", "已经完成" };
            string[] expected = { "状态：未分配任务", "所属阵营：以色列", "主动 (Active)", "主动 (Active)", "已经完成" };
            for (int i = 0; i < values.Length; i++)
            {
                model.Text = values[i];
                // No explicit Translate here: the real Loaded value-change hook
                // must handle the newly bound text synchronously.
                Require(label.Text == expected[i], "TextBlock mixed-value update failed: " + label.Text);
                Require((string)button.Content == expected[i], "Button mixed-value update failed: " + button.Content);
                Require(model.Text == values[i], "Mixed-value translation rewrote the OneWay binding source");
                Require(BindingOperations.IsDataBound(label, TextBlock.TextProperty)
                    && BindingOperations.IsDataBound(button, ContentControl.ContentProperty), "Mixed-value translation removed a binding");
                engine.GetMethod("OnWpfRenderTick", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { null, EventArgs.Empty });
                Require(label.Text == expected[i] && (string)button.Content == expected[i], "Render pass changed translated bound text");
                Require(model.Text == values[i] && BindingOperations.IsDataBound(label, TextBlock.TextProperty)
                    && BindingOperations.IsDataBound(button, ContentControl.ContentProperty), "Render pass changed a binding or its source");
                Pump();
            }
        }
        Pump();
    }

    private static void MixedUnboundUpdates()
    {
        TextBlock label = new TextBlock { Text = "WPF safety ready" };
        Button button = new Button { Content = "WPF safety ready" };
        StackPanel root = new StackPanel();
        root.Children.Add(label);
        root.Children.Add(button);
        using (HwndSource source = HiddenSource(root))
        {
            Translate(label);
            Translate(button);
            label.Text = "状态: Unassigned";
            button.Content = "状态: Unassigned";
            Require(label.Text == "状态：未分配任务", "Unbound TextBlock value hook skipped a Chinese label with an English value");
            Require((string)button.Content == "状态：未分配任务", "Unbound Button value hook skipped a Chinese label with an English value");
        }
        Pump();
    }

    private static void MixedInlines()
    {
        TextBlock label = new TextBlock { Text = "状态: Unassigned" };
        Run run = new Run("状态: Unassigned");
        Run linkText = new Run("Runway");
        Hyperlink link = new Hyperlink(linkText);
        Span span = new Span();
        span.Inlines.Add(run);
        span.Inlines.Add(link);
        Paragraph paragraph = new Paragraph(span);
        FlowDocument document = new FlowDocument(paragraph);
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer { Document = document };
        StackPanel root = new StackPanel();
        root.Children.Add(label);
        root.Children.Add(viewer);
        using (HwndSource source = HiddenSource(root))
        {
            Translate(label);
            Translate(viewer);
            Require(label.Text == "状态：未分配任务", "Initial unbound TextBlock mixed inline stayed English");
            Require(run.Text == "状态：未分配任务" && linkText.Text == "跑道", "Mixed Span/Hyperlink initialization failed");
            Require(span.Inlines.Count == 2 && object.ReferenceEquals(run.Parent, span)
                && object.ReferenceEquals(link.Parent, span) && object.ReferenceEquals(viewer.Document, document), "Inline translation replaced document structure");
            run.Text = "状态: Unassigned";
            engine.GetMethod("OnWpfRenderTick", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "状态：未分配任务", "Tracked mixed Run stayed English after render pass");
            Require(span.Inlines.Count == 2 && object.ReferenceEquals(run.Parent, span)
                && object.ReferenceEquals(link.Parent, span) && object.ReferenceEquals(viewer.Document, document), "Render pass replaced Span/Hyperlink structure");
        }
        Pump();
    }

    private static void TrackedRunLifecycle()
    {
        Run run = new Run("WPF safety ready");
        Paragraph paragraph = new Paragraph(run);
        FlowDocument document = new FlowDocument(paragraph);
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer { Document = document };
        Translate(viewer);
        Require(!run.IsLoaded && run.Text == "安全就绪", "Explicit initialization skipped an unloaded document");
        StackPanel root = new StackPanel();
        root.Children.Add(viewer);
        MethodInfo render = engine.GetMethod("OnWpfRenderTick", BindingFlags.Static | BindingFlags.NonPublic);
        using (HwndSource source = HiddenSource(root))
        {
            Require(run.IsLoaded, "Tracked Run did not load into its read-only viewer");
            run.Text = "WPF safety cancel";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "安全取消", "A loaded tracked Run stopped translating");

            viewer.Document = new FlowDocument();
            Require(PresentationSource.FromDependencyObject(run) == null, "Replaced document remained in the current display tree");
            run.Text = "WPF safety ready";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "WPF safety ready", "Render pass rewrote a Run in a just-replaced document");
            paragraph.Inlines.Remove(run);
            Require(run.Parent == null, "Removed Run retained its old parent");
            run.Text = "WPF safety cancel";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "WPF safety cancel", "Render pass rewrote a detached Run before Unloaded delivery");
            Pump();
            Require(!run.IsLoaded && run.Parent == null, "Detached Run retained its old display tree");
            run.Text = "WPF safety ready";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "WPF safety ready", "Render pass rewrote a detached tracked Run");

            RichTextBox editor = new RichTextBox { IsReadOnly = false,
                Document = new FlowDocument(new Paragraph(run)) };
            root.Children.Add(editor);
            root.UpdateLayout();
            Pump();
            Require(run.IsLoaded && run.Text == "WPF safety ready", "Reattaching a detached Run changed staged editor input");
            run.Text = "WPF safety cancel";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "WPF safety cancel", "Render pass rewrote the reattached editable Run");
            editor.IsReadOnly = true;
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "安全取消", "Read-only reattachment did not resume tracked Run translation");

            source.RootVisual = null;
            Pump();
            Require(!run.IsLoaded, "Read-only Run did not unload with its native source");
            run.Text = "WPF safety ready";
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "WPF safety ready", "Render pass rewrote an unloaded read-only Run");
            source.RootVisual = root;
            root.UpdateLayout();
            Pump();
            Require(run.IsLoaded, "Read-only Run did not reload");
            render.Invoke(null, new object[] { null, EventArgs.Empty });
            Require(run.Text == "安全就绪", "Reloaded read-only Run stopped following render updates");
        }
        Pump();
    }

    private static void Dispatchers()
    {
        Exception[] failures = new Exception[2];
        Thread[] threads = new Thread[2];
        for (int i = 0; i < threads.Length; i++)
        {
            int index = i;
            threads[i] = new Thread(delegate()
            {
                try { for (int pass = 0; pass < 5; pass++) Lifecycle(); }
                catch (Exception ex) { failures[index] = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            threads[i].SetApartmentState(ApartmentState.STA);
            threads[i].Start();
        }
        foreach (Thread thread in threads) Require(thread.Join(10000), "WPF Dispatcher fixture did not finish");
        foreach (Exception failure in failures) if (failure != null) throw failure;
    }

    private sealed class UnitWeapons_WPF : StackPanel { }
    private sealed class ElementHostWeaponsFixture : System.Windows.Forms.Control
    {
        public DependencyObject Child { get; set; }
    }
    private sealed class WeaponModel : INotifyPropertyChanged
    {
        private int qty;
        private string name;
        internal int QtySets, NameSets;
        public int Qty
        {
            get { return qty; }
            set { QtySets++; qty = value; Raise("Qty"); }
        }
        public string Name
        {
            get { return name; }
            set { NameSets++; name = value; Raise("Name"); }
        }
        private void Raise(string property)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(property));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private sealed class DiagnosticConverter : IValueConverter
    {
        internal int Calls;
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        { Calls++; return value; }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        { throw new InvalidOperationException("Diagnostics invoked ConvertBack"); }
    }
    private sealed class DiagnosticMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        { return values[0]; }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        { throw new InvalidOperationException("Diagnostics invoked multi ConvertBack"); }
    }

    private static object DiagnosticCall(string name, params object[] args)
    { return engine.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }

    private static Dictionary<string, object> DiagnosticJson(string value)
    { return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = 2097152 }.Deserialize<Dictionary<string, object>>(value); }

    private static void WeaponsDiagnostics()
    {
        Model model = new Model();
        DiagnosticConverter converter = new DiagnosticConverter();
        Binding binding = new Binding("Text") { Source = model, Mode = BindingMode.OneWay,
            Converter = converter, ConverterParameter = "weapon-parameter", StringFormat = "display {0}",
            ConverterCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            FallbackValue = "fallback", TargetNullValue = "null-text" };
        TextBlock bound = new TextBlock();
        bound.SetBinding(TextBlock.TextProperty, binding);
        Run weapon = new Run("WPF safety ready");
        Hyperlink link = new Hyperlink(weapon);
        Span span = new Span();
        span.Inlines.Add(new Run("4x "));
        span.Inlines.Add(link);
        TextBlock linked = new TextBlock();
        linked.Inlines.Add(span);
        int clicked = 0;
        link.Click += delegate { clicked++; };
        UnitWeapons_WPF root = new UnitWeapons_WPF();
        root.Children.Add(bound);
        root.Children.Add(linked);
        TextBlock multiText = new TextBlock();
        MultiBinding multi = new MultiBinding { Converter = new DiagnosticMultiConverter() };
        multi.Bindings.Add(new Binding("Text") { Source = model });
        multiText.SetBinding(TextBlock.TextProperty, multi);
        root.Children.Add(multiText);
        TextBlock priorityText = new TextBlock();
        PriorityBinding priority = new PriorityBinding();
        priority.Bindings.Add(new Binding("Text") { Source = model });
        priorityText.SetBinding(TextBlock.TextProperty, priority);
        root.Children.Add(priorityText);
        using (HwndSource source = HiddenSource(root))
        {
            int calls = converter.Calls;
            string before = linked.Text;
            string data = (string)DiagnosticCall("CaptureWeaponsDiagnostic", root, 512, true);
            Dictionary<string, object> parsed = DiagnosticJson(data);
            Require((string)parsed["status"] == "ok" && (int)parsed["targetCount"] == 1, "Scoped weapons capture failed");
            Require(data.Contains("\"kind\":\"Binding\"") && data.Contains("\"kind\":\"MultiBinding\"")
                && data.Contains("\"kind\":\"PriorityBinding\""), "Binding kinds were not preserved in diagnostics");
            Require(data.Contains("\"stringFormat\":\"display {0}\"") && data.Contains("\"fallbackValue\":\"fallback\"")
                && data.Contains("\"targetNullValue\":\"null-text\"") && data.Contains("\"culture\":\"en-US\""), "Binding pipeline metadata missing");
            Require(data.Contains("System.Windows.Documents.Hyperlink") && data.Contains("System.Windows.Documents.Span")
                && data.Contains("System.Windows.Documents.Run"), "Inline graph was omitted");
            Require(converter.Calls == calls && model.Text == "WPF safety ready" && model.Sets == 0
                && object.ReferenceEquals(BindingOperations.GetBinding(bound, TextBlock.TextProperty), binding), "Read-only diagnostics changed binding or invoked converter");
            Require(linked.Text == before && object.ReferenceEquals(link.Parent, span)
                && object.ReferenceEquals(weapon.Parent, link), "Read-only diagnostics replaced TextBlock hyperlink structure");
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Require(clicked == 1, "Diagnostic capture broke hyperlink event");
            Require((string)DiagnosticJson((string)DiagnosticCall("CaptureWeaponsDiagnostic", new StackPanel(), 512, true))["status"] == "not_found", "Wrong root passed diagnostics");
            Require((string)DiagnosticJson((string)DiagnosticCall("CaptureWeaponsDiagnostic", null, 512, true))["status"] == "error", "Null root passed diagnostics");
            Require((string)DiagnosticJson((string)DiagnosticCall("CaptureWeaponsDiagnostic", root, 1, true))["status"] == "overflow", "Node overflow passed diagnostics");
            TextBlock huge = new TextBlock { Text = new string('x', 140000) };
            root.Children.Add(huge);
            Require((string)DiagnosticJson((string)DiagnosticCall("CaptureWeaponsDiagnostic", root, 512, true))["status"] == "overflow", "Byte overflow passed diagnostics");
            root.Children.Remove(huge);

            using (System.Windows.Forms.Form form = new System.Windows.Forms.Form())
            using (ElementHostWeaponsFixture host = new ElementHostWeaponsFixture { Child = root })
            {
                form.Left = form.Top = -32000;
                form.ShowInTaskbar = false;
                form.Controls.Add(host);
                form.Show();
                System.Windows.Forms.Application.DoEvents();
                string command = (string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG SNAPSHOT");
                Require((string)DiagnosticJson(command)["status"] == "ok", "WEAPONS_DIAG command was unreachable");
                Require((string)DiagnosticJson((string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG WRONG"))["status"] == "error", "Invalid diagnostic action passed");
                Require((string)DiagnosticJson((string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG START"))["status"] == "armed", "Trace command did not arm");
                Require((string)DiagnosticJson((string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG STOP"))["status"] == "incomplete", "Zero-frame trace passed");
                DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG START");
                DiagnosticCall("OnWpfElementLoaded", root, new RoutedEventArgs());
                DiagnosticCall("OnWpfRenderTick", null, EventArgs.Empty);
                string trace = (string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG STOP");
                Dictionary<string, object> frames = DiagnosticJson(trace);
                Require((string)frames["status"] == "ok" && (int)frames["frames"] == 1 && (int)frames["targetFrames"] == 1, "Trace frame counts or command path failed");
                Require(trace.Contains("loaded_before_translation") && trace.Contains("render_before_translation")
                    && trace.Contains("render_after_translation") && trace.Contains("no SetBinding interception or pixel evidence"), "Trace lost ordering or honest coverage limits");
                form.Close();
            }
        }
        Require((string)DiagnosticJson((string)DiagnosticCall("ExecuteTestCommand", "WEAPONS_DIAG SNAPSHOT"))["status"] == "error", "Missing host passed diagnostics");
    }

    private static void WeaponsBoundRunSync()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        exact["4x Python 5"] = "4x怪蛇5";
        exact["4x Python 6"] = "4x怪蛇6";
        // 复现实测结构：TextBlock → Hyperlink → Run（带 OneWay 绑定），外层 TextBlock 无绑定。
        Model model = new Model();
        model.Text = "4x Python 5";
        Run run = new Run();
        run.SetBinding(Run.TextProperty, new Binding("Text") { Source = model, Mode = BindingMode.OneWay });
        Hyperlink link = new Hyperlink(run);
        TextBlock host = new TextBlock();
        host.Inlines.Add(link);
        UnitWeapons_WPF weapons = new UnitWeapons_WPF();
        weapons.Children.Add(host);
        StackPanel root = new StackPanel();
        root.Children.Add(weapons);
        using (HwndSource source = HiddenSource(root))
        {
            Require(run.IsLoaded, "Bound weapons Run did not load");
            Translate(host);
            Translate(run);
            Require(run.Text == "4x怪蛇5", "Initial bound Run translation failed: " + run.Text);
            int sets = model.Sets;
            // 关键：游戏通过绑定推英文，不做任何显式 Translate，钩子必须同步生效。
            model.Text = "4x Python 6";
            Require(run.Text == "4x怪蛇6", "Bound weapons Run flickered to English: " + run.Text);
            // 探针自证：model.Text 赋值本身会让 Sets +1；钩子不得额外回写源（否则 Sets 会多增）。
            Require(model.Sets == sets + 1 && model.Text == "4x Python 6",
                "Run hook wrote back into the binding source: sets " + sets + " -> " + model.Sets);
            Require(BindingOperations.IsDataBound(run, Run.TextProperty), "Run hook removed the binding");
            Require(object.ReferenceEquals(run.Parent, link) && object.ReferenceEquals(link.Parent, host),
                "Run hook replaced the Hyperlink structure");
            // 非武器子树的绑定 Run 不得被这个钩子接管（保持既有队列行为）。
            TextBlock plainHost = new TextBlock();
            Run plainRun = new Run();
            plainRun.SetBinding(Run.TextProperty, new Binding("Text") { Source = model, Mode = BindingMode.OneWay });
            plainHost.Inlines.Add(plainRun);
            StackPanel plainPanel = new StackPanel();
            plainPanel.Children.Add(plainHost);
            root.Children.Add(plainPanel);
            Pump();
            Translate(plainHost);
            Translate(plainRun);
            model.Text = "4x Python 5";
            Require(plainRun.Text == "4x Python 5", "Non-weapons Run was hijacked by the weapons hook: " + plainRun.Text);
        }
        Pump();
    }

    private static void WeaponsBindingProjection()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        exact[" 2x AIM-120C-7 AMRAAM P3I.3"] = "2x AIM-120C-7阿姆拉姆P3I.3";
        exact[" 4x Python 5"] = "4x怪蛇5";
        exact[" 6x Python 5"] = "6x怪蛇5";
        // 复现实测结构：Run.Text = MultiBinding(Qty, Name) + StringFormat " {0}x {1}"
        WeaponModel weapon = new WeaponModel { Qty = 4, Name = "Python 5" };
        Run run = new Run();
        MultiBinding multi = new MultiBinding { StringFormat = " {0}x {1}" };
        multi.Bindings.Add(new Binding("Qty") { Source = weapon });
        multi.Bindings.Add(new Binding("Name") { Source = weapon });
        run.SetBinding(Run.TextProperty, multi);
        Hyperlink link = new Hyperlink(run);
        TextBlock host = new TextBlock();
        host.Inlines.Add(link);
        UnitWeapons_WPF weapons = new UnitWeapons_WPF();
        weapons.Children.Add(host);
        StackPanel root = new StackPanel();
        root.Children.Add(weapons);
        using (HwndSource source = HiddenSource(root))
        {
            Translate(host);
            Translate(run);
            // 绑定求值直接产出中文，不依赖任何补扫。
            Require(run.Text == "4x怪蛇5", "MultiBinding projection did not translate: " + run.Text);
            Require(BindingOperations.IsDataBound(run, Run.TextProperty), "Projection removed the binding");
            int qtySets = weapon.QtySets, nameSets = weapon.NameSets;
            // 游戏更新 Qty：求值链上直接得中文，不会先出现英文帧。
            weapon.Qty = 6;
            Require(run.Text == "6x怪蛇5", "Qty update flickered to English: " + run.Text);
            Require(weapon.Qty == 6 && weapon.Name == "Python 5", "Projection wrote back into the business source");
            Require(weapon.QtySets == qtySets + 1 && weapon.NameSets == nameSets,
                "Projection touched the source setter");
            Require(object.ReferenceEquals(run.Parent, link) && object.ReferenceEquals(link.Parent, host),
                "Projection replaced the Hyperlink structure");
            // 重复翻译不得叠加包装。
            Translate(run);
            Translate(run);
            Require(run.Text == "6x怪蛇5", "Repeated projection changed the text: " + run.Text);
        }
        Pump();
    }

    private static void WeaponsNegativeControls()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        exact["4x WPF safety ready"] = "4x安全就绪";
        TextBlock plain = new TextBlock { Text = "WPF safety ready" };
        DiagnosticCall("TranslateWpfNode", plain);
        Require(plain.Text == "安全就绪", "Negative controls have no working positive translation control");
        TextBlock linked = new TextBlock();
        Hyperlink link = new Hyperlink(new Run("WPF safety ready"));
        Span span = new Span();
        span.Inlines.Add(new Run("4x "));
        span.Inlines.Add(link);
        linked.Inlines.Add(span);
        Console.WriteLine("OBSERVE before: text='" + linked.Text + "' inlines=" + linked.Inlines.Count
            + " spanParent=" + object.ReferenceEquals(span.Parent, linked));
        DiagnosticCall("TranslateWpfNode", linked);
        bool linkAccepted = object.ReferenceEquals(span.Parent, linked) && object.ReferenceEquals(link.Parent, span);
        Console.WriteLine("OBSERVE after: text='" + linked.Text + "' inlines=" + linked.Inlines.Count
            + " structurePreserved=" + linkAccepted);
        Require(linkAccepted, "Aggregate-write negative control destroyed structure unexpectedly:" + linked.Text);
        Console.WriteLine("NEGATIVE link_probe_rejected=false (precondition not met: explicit Inlines keep aggregate Text empty)");

        Grid root = new Grid();
        for (int branch = 0; branch < 2; branch++)
        {
            StackPanel busy = new StackPanel { Visibility = Visibility.Collapsed };
            for (int i = 0; i < 3000; i++) busy.Children.Add(new TextBlock { Text = "UnknownWeaponsControlFixture" });
            root.Children.Add(busy);
        }
        UnitWeapons_WPF weapons = new UnitWeapons_WPF();
        root.Children.Insert(1, weapons);
        Window window = new Window { Content = root, Width = 300, Height = 150,
            Left = -2000, Top = -2000, ShowActivated = false, ShowInTaskbar = false };
        string first = null;
        TextBlock target = new TextBlock { Text = "WPF safety ready" };
        EventHandler observe = delegate { if (first == null) first = target.Text; };
        try
        {
            window.Show();
            Pump();
            DiagnosticCall("InstallWpfRenderHook");
            DiagnosticCall("QueueWpfElement", root, false);
            System.Windows.Media.CompositionTarget.Rendering += observe;
            weapons.Children.Add(target);
            System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
            while (first == null && wait.ElapsedMilliseconds < 2000) Pump();
            Require(first != null, "First-frame negative control observed no Rendering");
            bool frameAccepted = first == "安全就绪";
            Require(!frameAccepted && first == "WPF safety ready", "Busy ancestor queue did not falsify the first-frame probe: " + first);
            Console.WriteLine("NEGATIVE first_frame_probe_rejected=true observed=" + first);
        }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= observe;
            window.Close();
            object queue = DiagnosticCall("WpfQueueFor", Dispatcher.CurrentDispatcher);
            queue.GetType().GetMethod("CancelRoot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(queue, new object[] { root });
        }
    }

    private static void WeaponsFirstFrameOrder()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        exact["WPF safety ready"] = "安全就绪";
        // 变体 0 是探针自身的负向对照：同样的忙祖先队列 + 同样的新元素，但放在**非武器**子树，
        // 仍走有界队列 ⇒ 首帧必须是英文。若它也变中文，说明探针已无法区分首帧先后，
        // 上面的"武器首帧中文"就不再有证明力。
        for (int variant = 0; variant < 4; variant++)
        {
            Grid root = new Grid();
            for (int branch = 0; branch < 2; branch++)
            {
                StackPanel busy = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = 0; i < 3000; i++) busy.Children.Add(new TextBlock { Text = "UnknownWeaponsControlFixture" });
                root.Children.Add(busy);
            }
            UnitWeapons_WPF weapons = new UnitWeapons_WPF();
            StackPanel plainHost = new StackPanel();
            root.Children.Insert(1, weapons);
            root.Children.Insert(2, plainHost);
            Window window = new Window { Content = root, Width = 300, Height = 150,
                Left = -2000, Top = -2000, ShowActivated = false, ShowInTaskbar = false };
            List<string> order = new List<string>();
            string firstFrameText = null;
            TextBlock target = new TextBlock { Text = "WPF safety ready" };
            target.Loaded += delegate
            {
                order.Add("loaded");
                if (variant == 3) DiagnosticCall("TranslateWpfNode", target);
            };
            DependencyPropertyDescriptor text = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler changed = delegate { order.Add("text:" + target.Text); };
            text.AddValueChanged(target, changed);
            EventHandler observe = delegate { if (firstFrameText == null) { firstFrameText = target.Text; order.Add("render:" + target.Text); } };
            try
            {
                window.Show();
                Pump();
                DiagnosticCall("InstallWpfRenderHook");
                DiagnosticCall("QueueWpfElement", root, false);
                System.Windows.Media.CompositionTarget.Rendering += observe;
                if (variant == 0) plainHost.Children.Add(target);
                else weapons.Children.Add(target);
                if (variant == 1) DiagnosticCall("TranslateWpfNode", target);
                if (variant == 2) DiagnosticCall("TranslateWpfElement", target);
                System.Diagnostics.Stopwatch wait = System.Diagnostics.Stopwatch.StartNew();
                while (firstFrameText == null && wait.ElapsedMilliseconds < 2000) Pump();
                Console.WriteLine("ORDER variant=" + variant + " firstFrame='" + firstFrameText + "' sequence=" + string.Join(",", order.ToArray()));
                Require(firstFrameText != null, "No first frame observed for variant " + variant);
                // 变体 0 走队列（非武器子树），必须仍是英文：这是"探针能看见英文首帧"的负向对照。
                // 变体 1/2/3 都在武器子树内，现在都由 Loaded 同步路径翻成中文；
                // 变体 2 不再能代表队列路径，如实记录。
                Require(variant == 0 ? firstFrameText == "WPF safety ready" : firstFrameText == "安全就绪",
                    "Variant " + variant + " produced unexpected first frame: " + firstFrameText);
            }
            finally
            {
                System.Windows.Media.CompositionTarget.Rendering -= observe;
                text.RemoveValueChanged(target, changed);
                window.Close();
                object queue = DiagnosticCall("WpfQueueFor", Dispatcher.CurrentDispatcher);
                queue.GetType().GetMethod("CancelRoot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Invoke(queue, new object[] { root });
            }
            Pump();
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            var exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            exact["WPF safety ready"] = "安全就绪";
            exact["WPF safety cancel"] = "安全取消";
            exact["Unassigned"] = "未分配任务";
            exact["Israel"] = "以色列";
            exact["Active"] = "主动 (Active)";
            exact["Runway"] = "跑道";
            if (args[1] == "headers") Headers();
            else if (args[1] == "editable") Editable();
            else if (args[1] == "collection") Collection();
            else if (args[1] == "lifecycle") { Lifecycle(); MixedUnboundUpdates(); MixedInlines(); MixedValueUpdates(); TrackedRunLifecycle(); }
            else if (args[1] == "dispatchers") Dispatchers();
            else if (args[1] == "weapons-diagnostic") WeaponsDiagnostics();
            else if (args[1] == "weapons-negative-controls") WeaponsNegativeControls();
            else if (args[1] == "weapons-bound-run-sync") WeaponsBoundRunSync();
            else if (args[1] == "weapons-binding-projection") WeaponsBindingProjection();
            else if (args[1] == "weapons-first-frame-order") WeaponsFirstFrameOrder();
            else throw new ArgumentException("Unknown mode " + args[1]);
            Console.WriteLine("PASS " + args[1] + " checks=" + checks);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
