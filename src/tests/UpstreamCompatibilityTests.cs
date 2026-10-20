using System;
using System.Collections.Generic;
using System.Data;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

// R39 must survive the incremental item plan and the WPF subscription lifecycle.
class UpstreamCompatibilityTests
{
    private static Type engine;
    private static int checks;
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static object Call(string name, params object[] args)
    {
        return engine.GetMethod(name, Private | BindingFlags.Public).Invoke(null, args);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }
    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
        Forms.Application.DoEvents();
    }
    private static HwndSource HiddenSource(FrameworkElement content)
    {
        HwndSourceParameters options = new HwndSourceParameters("Upstream compatibility fixture");
        options.WindowStyle = unchecked((int)0x80000000); // WS_POPUP, without WS_VISIBLE
        options.Width = 200;
        options.Height = 100;
        HwndSource source = new HwndSource(options);
        source.RootVisual = content;
        Pump();
        Require(content.IsLoaded, "Hidden WPF content did not load");
        return source;
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            var exact = (Dictionary<string, string>)engine.GetField("exactMap", Private).GetValue(null);
            exact["Compatibility ready"] = "兼容就绪";
            const string mixed = "Inherited, 始终开火";
            const string translated = "继承自上级，始终开火";
            Require((string)Call("TranslateString", mixed) == translated, "R39 string translation regressed");
            Require((string)Call("TranslateComboItemText", mixed) == translated, "R39 item helper regressed");
            using (Forms.ComboBox combo = new Forms.ComboBox())
            {
                combo.Items.Add(mixed);
                combo.SelectedIndex = 0;
                Call("LocalizeComboBoxSafe", combo);
                Require((string)combo.Items[0] == translated, "R39 mixed item was filtered out of the write plan");
                Require(combo.SelectedIndex == 0, "R39 changed selected index");
            }
            using (Forms.ComboBox combo = new Forms.ComboBox())
            using (DataTable table = new DataTable())
            {
                table.Columns.Add("Id", typeof(int));
                table.Columns.Add("Label", typeof(string));
                table.Rows.Add(42, mixed);
                combo.BindingContext = new Forms.BindingContext();
                combo.DisplayMember = "Label";
                combo.ValueMember = "Id";
                combo.DataSource = table;
                Call("LocalizeComboBoxSafe", combo);
                Require((string)table.Rows[0]["Label"] == translated, "R39 bound mixed display was filtered out");
                Require((int)table.Rows[0]["Id"] == 42, "R39 changed the bound business key");
            }
            TextBlock label = new TextBlock { Text = "Compatibility ready" };
            using (HwndSource source = HiddenSource(label))
            {
                Call("TranslateWpfElement", label);
                label.Text = mixed;
                Require(label.Text == translated, "R39 loaded TextBlock callback regressed");
                source.RootVisual = null;
                Pump();
            }
            ContentControl content = new ContentControl { Content = "Compatibility ready" };
            using (HwndSource source = HiddenSource(content))
            {
                Call("TranslateWpfElement", content);
                content.Content = mixed;
                Require((string)content.Content == translated, "R39 loaded ContentControl callback regressed");
                source.RootVisual = null;
                Pump();
            }
            Console.WriteLine("PASS upstream R39 compatibility checks=" + checks);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}
