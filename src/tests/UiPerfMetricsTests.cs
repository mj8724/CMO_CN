using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

class UiPerfMetricsTests
{
    static Type engine, scope;
    static object NewScope(string kind, bool queued)
    {
        return Activator.CreateInstance(scope, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { kind, queued }, CultureInfo.InvariantCulture);
    }
    static object Metric(string kind)
    {
        IDictionary table = (IDictionary)engine.GetField("uiPerfMetrics", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        return table[kind];
    }
    static long Field(object metric, string name)
    {
        return Convert.ToInt64(metric.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(metric));
    }
    static bool Reset()
    {
        return (bool)engine.GetMethod("ResetUiPerfMetrics", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }
    static void Require(bool value, string name)
    {
        if (!value) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }
    sealed class ClickCommand : ICommand
    {
        internal int Calls;
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { Calls++; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
    static void CommandClick()
    {
        ClickCommand command = new ClickCommand();
        Button button = new Button { Name = "CampaignFixture", Content = "Campaign fixture", Command = command };
        StackPanel root = new StackPanel();
        root.Children.Add(button);
        HwndSourceParameters parameters = new HwndSourceParameters("Semantic button fixture");
        parameters.WindowStyle = unchecked((int)0x90000000);
        parameters.PositionX = -3000; parameters.PositionY = -3000;
        parameters.Width = 240; parameters.Height = 100;
        using (HwndSource source = new HwndSource(parameters))
        {
            source.RootVisual = root;
            root.UpdateLayout();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(command.Calls == 0, "old_raise_event_skips_command_negative");
            int clicked = (int)engine.GetMethod("ClickWpfButton", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { root, "CampaignFixture" });
            Require(clicked == 1 && command.Calls == 1, "semantic_button_executes_command");
            button.IsEnabled = false;
            clicked = (int)engine.GetMethod("ClickWpfButton", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { root, "CampaignFixture" });
            Require(clicked == 0 && command.Calls == 1, "disabled_button_does_not_execute");
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            scope = engine.GetNestedType("UiPerfScope", BindingFlags.NonPublic);
            Require(Reset(), "idle_reset");
            object ticket = NewScope("fixture", true);
            object metric = Metric("fixture");
            Thread.Sleep(25);
            Require(Field(metric, "Posted") == 1 && Field(metric, "Pending") == 1,
                "queued_work_is_pending");
            Require(Field(metric, "Completed") == 0 && Field(metric, "TotalTicks") == 0,
                "enqueue_is_not_execution");
            Require(!Reset(), "pending_work_cannot_reset");
            scope.GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ticket, null);
            Require(!Reset(), "active_work_cannot_reset");
            Thread.Sleep(25);
            ((IDisposable)ticket).Dispose();
            ((IDisposable)ticket).Dispose();
            Require(Field(metric, "Completed") == 1 && Field(metric, "Pending") == 0 && Field(metric, "Active") == 0,
                "completed_work_counted_once");
            Require(Field(metric, "MaxTicks") * 1000.0 / Stopwatch.Frequency > 16 && Field(metric, "OverFrame") == 1,
                "slow_execution_exceeds_frame");
            Require(Field(metric, "MaxQueueTicks") * 1000.0 / Stopwatch.Frequency >= 20,
                "queued_wait_is_separate");
            object cancel = NewScope("cancel", true);
            ((IDisposable)cancel).Dispose();
            Require(Field(Metric("cancel"), "Pending") == 0 && Field(Metric("cancel"), "Completed") == 0,
                "cancel_not_completed");
            StringBuilder json = new StringBuilder();
            engine.GetMethod("AppendUiPerfMetrics", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { json });
            Require(json.ToString().Contains("\"uiPerfVersion\":1") && json.ToString().Contains("\"over16ms\":1"),
                "wire_metrics_include_actual_work");
            Console.WriteLine(json.ToString());
            Require(Reset(), "finished_reset");
            CommandClick();
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error);
            return 1;
        }
    }
}
