using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

// No windows shown or activated. Exercises real controls and the engine queue.
class UiSchedulingTests
{
    private static Type engine;
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static int assertions;

    private sealed class CountingLabel : Label
    {
        internal int Reads;
        internal int Delay;
        internal string Raw { get { return base.Text; } }
        public override string Text
        {
            get
            {
                Reads++;
                long start = Stopwatch.GetTimestamp();
                while (Delay > 0 && Stopwatch.GetTimestamp() - start < Stopwatch.Frequency * Delay / 1000)
                    Thread.SpinWait(32);
                return base.Text;
            }
            set { base.Text = value; }
        }
    }

    private sealed class RecreatedForm : Form
    {
        internal void Recreate() { RecreateHandle(); }
    }

    private static object Call(string name, params object[] args)
    {
        MethodInfo method = engine.GetMethod(name, Flags);
        return method == null ? null : method.Invoke(null, args);
    }

    private static void Require(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> finished, int limit)
    {
        Stopwatch watch = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(1); }
        while (!finished() && watch.ElapsedMilliseconds < limit);
        Require(finished(), "Queued UI work did not finish before deadline");
    }

    private static void Coalesce()
    {
        using (Form form = new Form())
        using (Panel root = new Panel())
        {
            IntPtr unused = form.Handle;
            form.Controls.Add(root);
            Panel parent = root;
            for (int i = 0; i < 8; i++) { Panel next = new Panel(); parent.Controls.Add(next); parent = next; }
            CountingLabel label = new CountingLabel { Text = "已经翻译" };
            parent.Controls.Add(label);
            Call("TranslateControlTree", form);
            label.Reads = 0;
            for (int i = 0; i < 50; i++)
            {
                Call("TranslateUiLifecycleControl", root);
                Call("TranslateUiLifecycleControl", parent);
                Call("TranslateUiLifecycleControl", label);
            }
            Require(label.Reads <= 4, "Repeated lifecycle requests synchronously rescanned a subtree: " + label.Reads);
            Pump(delegate { return label.Reads > 0; }, 1000);
            Application.DoEvents();
            Require(label.Reads <= 4, "Parent/child requests were not deduplicated: " + label.Reads);
            Require(!form.Visible && !label.Focused, "Diagnostic unexpectedly became visible or focused");
            Console.WriteLine("COALESCED requests=150 leaf_reads=" + label.Reads);
        }
    }

    private static void VisibleFirst()
    {
        using (Panel hidden = new Panel { Visible = false })
        using (Panel visible = new Panel())
        {
            CountingLabel hiddenLabel = new CountingLabel { Text = "Schedule hidden" };
            CountingLabel visibleLabel = new CountingLabel { Text = "Schedule visible" };
            hidden.Controls.Add(hiddenLabel);
            visible.Controls.Add(visibleLabel);
            List<string> order = new List<string>();
            hiddenLabel.TextChanged += delegate { if (hiddenLabel.Raw == "后台标签") order.Add("hidden"); };
            visibleLabel.TextChanged += delegate { if (visibleLabel.Raw == "可见标签") order.Add("visible"); };
            Call("TranslateUiLifecycleControl", hidden);
            Call("TranslateUiLifecycleControl", visible);
            for (int i = 0; i < 20 && order.Count < 2; i++) Call("DrainUiLifecycleQueue");
            Require(order.Count == 2, "Visible or hidden controls never completed");
            Require(order[0] == "visible", "Hidden content ran before queued visible content");
            Console.WriteLine("PRIORITY=" + string.Join(",", order.ToArray()));
        }
    }

    private static void YieldBetweenSlices()
    {
        using (Form form = new Form())
        {
            IntPtr unused = form.Handle;
            List<CountingLabel> labels = new List<CountingLabel>();
            for (int i = 0; i < 80; i++)
            {
                CountingLabel label = new CountingLabel { Text = "Schedule visible", Delay = 1 };
                labels.Add(label);
                form.Controls.Add(label);
            }
            bool marker = false;
            int englishAtMarker = 0;
            Call("TranslateUiLifecycleControl", form);
            form.BeginInvoke((MethodInvoker)delegate
            {
                foreach (CountingLabel label in labels) if (label.Raw == "Schedule visible") englishAtMarker++;
                marker = true;
            });
            Pump(delegate { return marker; }, 1500);
            Console.WriteLine("YIELD_MARKER untranslated=" + englishAtMarker);
            Require(englishAtMarker > 0, "A complete slow tree blocked the next UI message");
            try { Pump(delegate { return labels.TrueForAll(delegate(CountingLabel label) { return label.Raw == "可见标签"; }); }, 5000); }
            catch
            {
                Console.WriteLine("YIELD_FINAL translated=" + labels.FindAll(delegate(CountingLabel label) { return label.Raw == "可见标签"; }).Count);
                throw;
            }
            Console.WriteLine("YIELD untranslated_at_next_message=" + englishAtMarker + " final_translated=" + labels.Count);
        }
    }

    private static void ReentrantAddition()
    {
        using (Form form = new Form())
        {
            IntPtr unused = form.Handle;
            CountingLabel first = new CountingLabel { Text = "Schedule visible" };
            CountingLabel added = null;
            form.Controls.Add(first);
            Call("AttachUiLifecycle", form);
            first.TextChanged += delegate
            {
                if (first.Raw != "可见标签" || added != null) return;
                added = new CountingLabel { Text = "Schedule hidden" };
                form.Controls.Add(added);
            };
            Call("TranslateUiLifecycleControl", form);
            Pump(delegate { return added != null && added.Raw == "后台标签"; }, 1500);
            Require(first.Raw == "可见标签", "Original work was lost during reentrant ControlAdded");
        }
    }

    private static void DisposeAndRecreate()
    {
        Form closed = new Form();
        IntPtr unused = closed.Handle;
        CountingLabel disposedLabel = new CountingLabel { Text = "Schedule hidden" };
        closed.Controls.Add(disposedLabel);
        Call("TranslateUiLifecycleControl", closed);
        closed.Dispose();
        using (RecreatedForm current = new RecreatedForm())
        {
            unused = current.Handle;
            CountingLabel live = new CountingLabel { Text = "Schedule visible" };
            current.Controls.Add(live);
            Call("TranslateUiLifecycleControl", current);
            current.Recreate();
            Pump(delegate { return live.Raw == "可见标签"; }, 1500);
            Require(disposedLabel.Raw == "Schedule hidden", "Canceled work wrote a disposed child");
            Require(!current.Visible, "Handle recreation fixture was shown");
        }
        DetachedWork();
        DetachedRetention();
        VisitedRetention();
        UnobservedOwner();
    }

    private static void UnobservedOwner()
    {
        using (Form source = new Form())
        using (Form target = new Form())
        using (Form healthy = new Form())
        {
            IntPtr unused = source.Handle;
            unused = target.Handle;
            unused = healthy.Handle;
            Call("AttachUiLifecycle", source);
            Panel moved = new Panel();
            source.Controls.Add(moved);
            Call("DrainUiLifecycleQueue");
            target.Controls.Add(moved); // Only the moved child was observed before dispatch.
            Require(!moved.IsHandleCreated, "Dispatch owner fixture created a child handle");

            Call("AttachUiLifecycle", healthy);
            Panel liveRoot = new Panel();
            Label live = new Label { Text = "Schedule hidden" };
            liveRoot.Controls.Add(live);
            healthy.Controls.Add(liveRoot);
            target.Dispose();
            Pump(delegate { return live.Text == "后台标签"; }, 1000);
            Require(QueueCount("pending") == 0, "A closed dispatch owner stalled another window's queued work");

            Label late = new Label { Text = "Schedule hidden" };
            healthy.Controls.Add(late);
            Pump(delegate { return late.Text == "后台标签"; }, 1000);
            Require(QueueCount("pending") == 0, "A closed dispatch owner stalled later translation requests");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PrepareDetachedRetention(Panel waiting)
    {
        Form form = new Form();
        IntPtr unused = form.Handle;
        Call("AttachUiLifecycle", form);
        Panel panel = new Panel();
        panel.Controls.Add(new Label { Text = "Schedule visible" });
        form.Controls.Add(panel);
        Require(!panel.IsHandleCreated, "Retention fixture unexpectedly created a child handle");
        if (waiting != null) Call("QueueUiTranslation", waiting, false);
        form.Controls.Remove(panel);
        form.Dispose();
        return new WeakReference[] { new WeakReference(form), new WeakReference(panel) };
    }

    private static int QueueCount(string field)
    {
        object queue = engine.GetField("uiTranslationQueue", Flags).GetValue(null);
        object collection = queue.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(queue);
        return (int)collection.GetType().GetProperty("Count").GetValue(collection, null);
    }

    private static void CollectDetached()
    {
        for (int i = 0; i < 5; i++)
        {
            Application.DoEvents();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(5);
        }
    }

    private static void DetachedRetention()
    {
        WeakReference[] removed = PrepareDetachedRetention(null);
        CollectDetached();
        Require(QueueCount("pending") == 0 && QueueCount("visible") + QueueCount("background") == 0,
            "A closed owner retained stale work without a future drain");
        Require(!removed[0].IsAlive && !removed[1].IsAlive, "A closed owner retained a detached form or panel");
        using (Panel waiting = new Panel())
        {
            Label label = new Label { Text = "Schedule hidden" };
            waiting.Controls.Add(label);
            removed = PrepareDetachedRetention(waiting);
            CollectDetached();
            Require(QueueCount("pending") == 1 && QueueCount("visible") + QueueCount("background") == 1,
                "Stale work was retained or valid handleless work was removed");
            Require(!removed[0].IsAlive && !removed[1].IsAlive,
                "Buffered work retained a detached tree alongside valid handleless work");
            Require(!waiting.IsHandleCreated && label.Text == "Schedule hidden", "Handleless work ran before an owner existed");
            IntPtr unused = waiting.Handle;
            Pump(delegate { return label.Text == "后台标签"; }, 1000);
            Require(QueueCount("pending") == 0, "Retained handleless work did not complete after handle creation");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PrepareVisitedRetention(Panel waiting)
    {
        Form form = new Form();
        IntPtr unused = form.Handle;
        Call("AttachUiLifecycle", form);
        Panel panel = new Panel();
        CountingLabel label = new CountingLabel { Text = "Schedule visible", Delay = 7 };
        panel.Controls.Add(label);
        form.Controls.Add(panel);
        unused = panel.Handle;
        label.TextChanged += delegate
        {
            if (label.Raw == "可见标签") Call("QueueUiTranslation", waiting, false);
        };
        for (int i = 0; i < 8 && label.Raw != "可见标签"; i++) Call("DrainUiLifecycleQueue");
        Require(panel.IsHandleCreated && label.Raw == "可见标签" && QueueCount("visited") >= 2,
            "Visited retention fixture did not translate a real tree before detachment");
        Require(QueueCount("pending") == 1 && !waiting.IsHandleCreated,
            "Visited retention fixture did not leave valid handleless work pending");
        form.Controls.Remove(panel);
        form.Dispose();
        return new WeakReference[] { new WeakReference(form), new WeakReference(panel), new WeakReference(label) };
    }

    private static void VisitedRetention()
    {
        using (Panel waiting = new Panel())
        {
            Label next = new Label { Text = "Schedule hidden" };
            waiting.Controls.Add(next);
            WeakReference[] removed = PrepareVisitedRetention(waiting);
            CollectDetached();
            Require(QueueCount("pending") == 1 && QueueCount("visited") == 0,
                "Visited work retained a detached tree while valid handleless work waited");
            Require(!removed[0].IsAlive && !removed[1].IsAlive && !removed[2].IsAlive,
                "A previously translated detached form, panel or label remained rooted");
            Require(!waiting.IsHandleCreated && next.Text == "Schedule hidden",
                "Visited cleanup ran valid handleless work without a dispatch owner");
            IntPtr unused = waiting.Handle;
            Pump(delegate { return next.Text == "后台标签"; }, 1000);
            Require(QueueCount("pending") == 0, "Valid work did not resume after visited cleanup");
        }
    }

    private static void DetachedWork()
    {
        using (Form first = new Form())
        using (Form second = new Form())
        using (Label label = new Label { Text = "Schedule visible" })
        using (Panel detached = new Panel())
        using (Label nested = new Label { Text = "Schedule hidden" })
        {
            IntPtr unused = first.Handle;
            unused = second.Handle;
            Call("AttachUiLifecycle", first);
            first.Controls.Add(label);
            first.Controls.Remove(label);
            int detachedWrites = 0;
            label.TextChanged += delegate { if (label.Parent == null) detachedWrites++; };
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(1); }
            Require(label.Text == "Schedule visible" && detachedWrites == 0,
                "Stale queued work translated a control removed from its original tree");
            first.Controls.Add(label);
            Pump(delegate { return label.Text == "可见标签"; }, 1000);

            using (Label moved = new Label { Text = "Schedule visible" })
            {
                first.Controls.Add(moved);
                second.Controls.Add(moved); // The destination need not have been discovered yet.
                Pump(delegate { return moved.Text == "可见标签"; }, 1000);
                Require(moved.Parent == second, "Cross-form reparenting changed the destination");
            }
            first.Controls.Add(detached);
            detached.Controls.Add(nested);
            first.Controls.Remove(detached);
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(1); }
            Require(nested.Text == "Schedule hidden", "An ancestor removal left queued descendants writable");
            Call("QueueUiTranslation", detached, false); // A fresh, explicit parentless root is legitimate.
            Call("DrainUiLifecycleQueue");
            Require(nested.Text == "后台标签", "Explicit translation of a parentless root was rejected");
        }
        using (Form form = new Form())
        using (Panel parent = new Panel { Text = "Schedule visible" })
        using (Label child = new Label { Text = "Schedule hidden" })
        {
            IntPtr unused = form.Handle;
            parent.Controls.Add(child);
            Call("AttachUiLifecycle", form);
            parent.TextChanged += delegate { if (parent.Text == "可见标签") form.Controls.Remove(parent); };
            form.Controls.Add(parent);
            for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(1); }
            Require(parent.Parent == null && child.Text == "Schedule hidden",
                "A parent removed during translation still queued its detached descendants");
        }
    }

    private static void FailingParent()
    {
        using (Panel parent = new Panel { Visible = false, Text = "Schedule visible" })
        {
            CountingLabel child = new CountingLabel { Text = "Schedule hidden" };
            parent.Controls.Add(child);
            parent.TextChanged += delegate { throw new InvalidOperationException("Deliberate business callback failure"); };
            Call("TranslateUiLifecycleControl", parent);
            for (int i = 0; i < 20 && child.Raw != "后台标签"; i++) Call("DrainUiLifecycleQueue");
            Require(child.Raw == "后台标签", "A failing parent prevented healthy descendant translation");
        }
    }

    private static void NestedCreation()
    {
        using (Panel root = new Panel())
        {
            int depth = 0, maximum = 0;
            List<CountingLabel> labels = new List<CountingLabel>();
            Action add = null;
            add = delegate
            {
                CountingLabel label = new CountingLabel { Text = "Schedule visible" };
                labels.Add(label);
                label.TextChanged += delegate
                {
                    if (label.Raw != "可见标签") return;
                    depth++;
                    maximum = Math.Max(maximum, depth);
                    try { if (labels.Count < 12) add(); }
                    finally { depth--; }
                };
                root.Controls.Add(label);
            };
            add();
            Call("TranslateUiLifecycleControl", root);
            for (int i = 0; i < 30 && (labels.Count < 12 || labels[11].Raw != "可见标签"); i++)
                Call("DrainUiLifecycleQueue");
            Require(labels.Count == 12 && labels[11].Raw == "可见标签", "Reentrant additions lost queued descendants");
            Require(maximum == 1, "First-text path caused recursive business callbacks: " + maximum);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            Call("LoadDictionaries", args[1]);
            IDictionary exact = (IDictionary)engine.GetField("exactMap", Flags).GetValue(null);
            exact["Schedule visible"] = "可见标签";
            exact["Schedule hidden"] = "后台标签";
            string mode = args[2];
            if (mode == "coalesce") Coalesce();
            else if (mode == "visible") VisibleFirst();
            else if (mode == "yield") YieldBetweenSlices();
            else if (mode == "reentrant") ReentrantAddition();
            else if (mode == "lifecycle") DisposeAndRecreate();
            else if (mode == "failure") FailingParent();
            else if (mode == "nested") NestedCreation();
            else throw new ArgumentException(mode);
            Console.WriteLine("PASS " + mode + " assertions=" + assertions);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetBaseException().Message); return 1; }
    }
}
