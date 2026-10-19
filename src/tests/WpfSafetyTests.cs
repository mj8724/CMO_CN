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
            set { text = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Text")); }
        }
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

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            var exact = (Dictionary<string, string>)engine.GetField("exactMap", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            exact["WPF safety ready"] = "安全就绪";
            exact["WPF safety cancel"] = "安全取消";
            if (args[1] == "headers") Headers();
            else if (args[1] == "editable") Editable();
            else if (args[1] == "collection") Collection();
            else if (args[1] == "lifecycle") Lifecycle();
            else if (args[1] == "dispatchers") Dispatchers();
            else throw new ArgumentException("Unknown mode " + args[1]);
            Console.WriteLine("PASS " + args[1] + " checks=" + checks);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
