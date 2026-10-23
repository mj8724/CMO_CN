using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Threading;

class WpfSchedulingTests
{
    static Type engine;
    static int checks;

    static object Call(string name, params object[] args)
    {
        MethodInfo method = engine.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (method == null) throw new InvalidOperationException("missing engine method " + name);
        return method.Invoke(null, args);
    }

    static long Counter(string name)
    {
        return Convert.ToInt64(engine.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }

    static object Queue(Dispatcher dispatcher) { return Call("WpfQueueFor", dispatcher); }

    static int Pending(object queue)
    {
        IDictionary pending = (IDictionary)queue.GetType()
            .GetField("pending", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(queue);
        return pending.Count;
    }

    static long MaxQueueTicks()
    {
        object table = engine.GetField("uiPerfMetrics", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        object metric = ((IDictionary)table)["wpfSlice"];
        if (metric == null) return 0;
        return Convert.ToInt64(metric.GetType().GetField("MaxQueueTicks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(metric));
    }

    static void Drain(object queue)
    {
        queue.GetType().GetMethod("Drain", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(queue, null);
    }

    static HwndSource Source(FrameworkElement root)
    {
        HwndSourceParameters parameters = new HwndSourceParameters("CMO queue fixture");
        parameters.WindowStyle = unchecked((int)0x90000000);
        parameters.PositionX = -3000;
        parameters.PositionY = -3000;
        parameters.Width = 220;
        parameters.Height = 100;
        HwndSource source = new HwndSource(parameters);
        source.RootVisual = root;
        return source;
    }

    static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            System.Windows.Forms.Application.DoEvents();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(delegate { }));
            Thread.Sleep(1);
        }
    }

    // Negative control, kept in the test only: the old implementation used a live
    // IEnumerator as its cross-slice cursor, which writing Run.Text invalidates.
    static void LegacyEnumeratorTraversal(Paragraph paragraph)
    {
        IEnumerator cursor = ((IEnumerable)paragraph.Inlines).GetEnumerator();
        while (cursor.MoveNext())
        {
            Run run = cursor.Current as Run;
            if (run != null && run.Text == "Ready") run.Text = "就绪";
        }
    }

    static void DocumentYield()
    {
        // Warm the translation/JIT path first: the very first slice of a session
        // legitimately pays one-time JIT cost, and that is not steady-state lag.
        FlowDocument warmDocument = new FlowDocument();
        Paragraph warmParagraph = new Paragraph();
        for (int i = 0; i < 200; i++) warmParagraph.Inlines.Add(new Run("Ready"));
        warmDocument.Blocks.Add(warmParagraph);
        FlowDocumentScrollViewer warmViewer = new FlowDocumentScrollViewer { Document = warmDocument };
        object warmQueue = Queue(Dispatcher.CurrentDispatcher);
        Call("QueueWpfElement", warmViewer, false);
        for (int i = 0; Pending(warmQueue) != 0 && i < 200; i++) Drain(warmQueue);

        FlowDocument document = new FlowDocument();
        Paragraph paragraph = new Paragraph();
        List<Run> runs = new List<Run>();
        for (int i = 0; i < 6000; i++)
        {
            Run run = new Run("Ready");
            runs.Add(run);
            paragraph.Inlines.Add(run);
        }
        document.Blocks.Add(paragraph);
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer { Document = document };

        bool legacyThrew = false;
        try { LegacyEnumeratorTraversal(paragraph); }
        catch (InvalidOperationException) { legacyThrew = true; }
        Console.WriteLine("METRIC legacy_enumerator_translated=" + CountTranslated(runs));
        Require(legacyThrew && CountTranslated(runs) == 1, "negative_control_legacy_enumerator_breaks");

        foreach (Run run in runs) run.Text = "Ready";
        object queue = Queue(Dispatcher.CurrentDispatcher);
        Call("QueueWpfElement", viewer, false);
        List<double> sliceTimes = new List<double>();
        int slices = 0;
        while (Pending(queue) != 0 && slices < 400)
        {
            Stopwatch next = Stopwatch.StartNew();
            Drain(queue);
            next.Stop();
            sliceTimes.Add(next.Elapsed.TotalMilliseconds);
            slices++;
        }
        double max = 0;
        foreach (double value in sliceTimes) if (value > max) max = value;
        Console.WriteLine("METRIC runs=6000 slices=" + slices + " max_slice_ms="
            + max.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            + " first_slice_ms=" + (sliceTimes.Count > 0 ? sliceTimes[0].ToString("F3",
                System.Globalization.CultureInfo.InvariantCulture) : "n/a")
            + " max_queue_ms=" + (MaxQueueTicks() * 1000.0 / Stopwatch.Frequency).ToString("F1",
                System.Globalization.CultureInfo.InvariantCulture));
        Require(Pending(queue) == 0 && CountTranslated(runs) == runs.Count, "large_document_eventually_finishes");
        Require(slices > 1, "large_document_yields_before_all_runs");
        // Each slice must stay inside a frame; the assertion is what makes the
        // budget observable instead of a printed metric only.
        Require(max <= 16.7, "slice_within_one_frame");
    }

    static int CountTranslated(List<Run> runs)
    {
        int translated = 0;
        foreach (Run run in runs) if (run.Text != "Ready") translated++;
        return translated;
    }

    static void MutationBetweenSlices()
    {
        FlowDocument document = new FlowDocument();
        Paragraph paragraph = new Paragraph();
        List<Run> runs = new List<Run>();
        for (int i = 0; i < 3000; i++)
        {
            Run run = new Run("Ready");
            runs.Add(run);
            paragraph.Inlines.Add(run);
        }
        document.Blocks.Add(paragraph);
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer { Document = document };
        object queue = Queue(Dispatcher.CurrentDispatcher);
        Call("QueueWpfElement", viewer, false);
        Drain(queue);
        Require(Pending(queue) > 0, "mutation_case_has_remaining_work");
        // The game rebuilds panels while we scan; appending must not throw and
        // must not lose the work already queued.
        for (int i = 0; i < 50; i++) paragraph.Inlines.Add(new Run("Ready"));
        bool threw = false;
        try { for (int i = 0; Pending(queue) != 0 && i < 200; i++) Drain(queue); }
        catch (Exception error) { threw = true; Console.WriteLine("MUTATION ERROR " + error.Message); }
        Require(!threw && Pending(queue) == 0 && CountTranslated(runs) > 0, "mutation_between_slices_is_safe");
    }

    static void RootChange()
    {
        TextBlock old = new TextBlock { Text = "Ready" };
        using (HwndSource source = Source(old))
        {
            object queue = Queue(Dispatcher.CurrentDispatcher);
            Call("QueueWpfSource", source);
            TextBlock replacement = new TextBlock { Text = "Ready" };
            source.RootVisual = replacement;
            for (int i = 0; Pending(queue) != 0 && i < 50; i++) Drain(queue);
            Require(old.Text == "Ready" && replacement.Text != "Ready", "source_request_reads_current_root_not_old_tree");
        }
    }

    static void RemoteCoalescing()
    {
        Dispatcher remote = null;
        HwndSource source = null;
        ManualResetEvent created = new ManualResetEvent(false);
        ManualResetEvent release = new ManualResetEvent(false);
        ManualResetEvent closed = new ManualResetEvent(false);
        Thread thread = new Thread(delegate()
        {
            remote = Dispatcher.CurrentDispatcher;
            TextBlock root = new TextBlock { Text = "Ready" };
            using (source = Source(root))
            {
                created.Set();
                release.WaitOne();
                Dispatcher.Run();
            }
            closed.Set();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(created.WaitOne(5000), "remote_dispatcher_fixture_created");
        try
        {
            object queue = Queue(remote);
            long before = Counter("_sweepCalls");
            for (int i = 0; i < 12; i++) Call("QueueWpfSource", source);
            Require(Pending(queue) == 1, "remote_requests_coalesce_one_root");
            Require(Counter("_sweepCalls") == before, "remote_enqueue_is_not_completed_scan");
            release.Set();
            remote.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            Require(Pending(queue) == 0, "remote_work_finishes_after_unblocking");
        }
        finally
        {
            release.Set();
            if (remote != null) remote.BeginInvokeShutdown(DispatcherPriority.Send);
            Require(closed.WaitOne(5000), "remote_dispatcher_cleanly_closed");
        }
    }

    // Continuation slices must yield to input: an Input-priority callback queued
    // while work remains has to run before the next slice finishes the tree.
    static void InputPriority()
    {
        FlowDocument document = new FlowDocument();
        Paragraph paragraph = new Paragraph();
        List<Run> runs = new List<Run>();
        for (int i = 0; i < 4000; i++)
        {
            Run run = new Run("Ready");
            runs.Add(run);
            paragraph.Inlines.Add(run);
        }
        document.Blocks.Add(paragraph);
        FlowDocumentScrollViewer viewer = new FlowDocumentScrollViewer { Document = document };
        object queue = Queue(Dispatcher.CurrentDispatcher);
        long inputRanAt = -1;
        long translatedAt = -1;
        Call("QueueWpfElement", viewer, false);

        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
        {
            inputRanAt = CountTranslated(runs);
        }));
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 3000 && (inputRanAt < 0 || Pending(queue) != 0))
        {
            Pump(5);
            if (inputRanAt < 0 && CountTranslated(runs) > 0) { }
            if (inputRanAt >= 0 && translatedAt < 0) translatedAt = CountTranslated(runs);
        }
        Console.WriteLine("METRIC input_ran_after_translated=" + inputRanAt + " total=" + runs.Count);
        Require(inputRanAt >= 0 && inputRanAt < runs.Count, "input_runs_before_all_work_finishes");
        Pump(50);
        while (Pending(queue) != 0) Pump(20);
        Require(CountTranslated(runs) == runs.Count, "work_converges_despite_input_pressure");
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            if (args.Length > 3)
                engine.GetField("logPath", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, args[3]);
            Call("LoadDictionaries", args[1]);
            string mode = args[2];
            if (mode == "document") DocumentYield();
            else if (mode == "mutation") MutationBetweenSlices();
            else if (mode == "root") RootChange();
            else if (mode == "remote") RemoteCoalescing();
            else if (mode == "input") InputPriority();
            else throw new Exception("unknown mode " + mode);
            Console.WriteLine("ALL PASS mode=" + mode + " checks=" + checks);
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error);
            return 1;
        }
    }
}
