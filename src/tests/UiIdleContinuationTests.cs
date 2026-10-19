using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// No Form.Show, focus or activation. Real Application.Run uses a hidden context.
class UiIdleContinuationTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type continuationType;
    private static int assertions;
    private const int MarkerMessage = 0x8000 + 197;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private sealed class HiddenOwner : Control
    {
        internal int Markers;
        internal HiddenOwner() { Visible = false; }
        internal void Recreate() { RecreateHandle(); }
        internal void PostMarker()
        {
            Require(PostMessage(Handle, MarkerMessage, IntPtr.Zero, IntPtr.Zero), "Could not post native UI marker");
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == MarkerMessage) Markers++;
            base.WndProc(ref message);
        }
    }

    private sealed class Continuation : IDisposable
    {
        internal object Value;
        internal Continuation(Action action)
        {
            ConstructorInfo constructor = continuationType.GetConstructor(Instance, null, new Type[] { typeof(Action) }, null);
            Require(constructor != null, "UiIdleContinuation(Action) constructor missing");
            Value = constructor.Invoke(new object[] { action });
        }
        private void Invoke(string name, params object[] arguments)
        {
            MethodInfo method = continuationType.GetMethod(name, Instance);
            Require(method != null, "UiIdleContinuation." + name + " missing");
            method.Invoke(Value, arguments);
        }
        internal void Schedule(Control owner) { Invoke("Schedule", owner); }
        internal void Cancel() { Invoke("Cancel"); }
        public void Dispose() { Invoke("Dispose"); }
    }

    private static void Require(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    private static void RunUntil(Func<bool> complete, int timeout)
    {
        using (ApplicationContext context = new ApplicationContext())
        using (System.Windows.Forms.Timer watchdog = new System.Windows.Forms.Timer { Interval = 5 })
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool timedOut = false;
            watchdog.Tick += delegate
            {
                if (complete()) context.ExitThread();
                else if (elapsed.ElapsedMilliseconds >= timeout) { timedOut = true; context.ExitThread(); }
            };
            watchdog.Start();
            Application.Run(context);
            watchdog.Stop();
            Require(!timedOut && complete(), "Real Application.Run did not complete before deadline");
        }
    }

    private static void RunFor(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        RunUntil(delegate { return elapsed.ElapsedMilliseconds >= milliseconds; }, milliseconds + 1500);
    }

    private static void IdleSlices()
    {
        using (HiddenOwner owner = new HiddenOwner())
        {
            IntPtr handle = owner.Handle;
            int slices = 0, idleSlices = 0, depth = 0, maxDepth = 0;
            bool inIdle = false;
            EventHandler before = delegate { inIdle = true; };
            EventHandler after = delegate { inIdle = false; };
            Application.Idle += before;
            Continuation next = null;
            next = new Continuation(delegate
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
                if (inIdle) idleSlices++;
                try
                {
                    Require(owner.Markers >= slices,
                        "Continuation drained again before a separately posted native UI message");
                    slices++;
                    if (slices < 12)
                    {
                        owner.PostMarker();
                        next.Schedule(owner);
                        // Keep the test observer after the newly armed Idle callback.
                        Application.Idle -= after;
                        Application.Idle += after;
                    }
                }
                finally { depth--; }
            });
            try
            {
                for (int i = 0; i < 50; i++) next.Schedule(owner);
                Application.Idle += after;
                RunUntil(delegate { return slices >= 12; }, 2000);
                Require(slices == 12, "Repeated Schedule requests were not coalesced");
                Require(idleSlices > 0, "No continuation ran within a real Application.Idle invocation");
                Require(maxDepth == 1, "Continuation recursively invoked itself");
                Require(!owner.Visible && !owner.Focused, "Owner unexpectedly became visible or focused");
                Console.WriteLine("IDLE slices=" + slices + " via_idle=" + idleSlices
                    + " native_markers=" + owner.Markers + " max_depth=" + maxDepth);
            }
            finally
            {
                next.Dispose();
                Application.Idle -= before;
                Application.Idle -= after;
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleased(Control owner, bool dispose, Action unexpected)
    {
        Continuation helper = new Continuation(unexpected);
        helper.Schedule(owner);
        if (dispose) helper.Dispose();
        else helper.Cancel();
        return new WeakReference(helper.Value);
    }

    private static void CancelAndDispose()
    {
        using (HiddenOwner owner = new HiddenOwner())
        {
            IntPtr handle = owner.Handle;
            int calls = 0, idleEvents = 0;
            EventHandler observer = delegate { idleEvents++; };
            Application.Idle += observer;
            WeakReference cancelled = CreateReleased(owner, false, delegate { calls++; });
            WeakReference disposed = CreateReleased(owner, true, delegate { calls++; });
            try
            {
                RunFor(90);
                Require(idleEvents > 0, "Cancellation test never reached real Idle");
                Require(calls == 0, "A cancelled/disposed idle or timer callback still invoked Action");
                for (int i = 0; i < 4 && (cancelled.IsAlive || disposed.IsAlive); i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                Require(!cancelled.IsAlive && !disposed.IsAlive,
                    "Cancelled/disposed continuation was retained by Idle or timer subscription");
                Console.WriteLine("CANCEL calls=" + calls + " collected_cancelled=" + !cancelled.IsAlive
                    + " collected_disposed=" + !disposed.IsAlive);
                using (HiddenOwner snapshotOwner = new HiddenOwner())
                using (Continuation snapshot = new Continuation(delegate { calls++; }))
                {
                    handle = snapshotOwner.Handle;
                    snapshot.Schedule(snapshotOwner);
                    FieldInfo idleField = continuationType.GetField("idle", Instance);
                    Require(idleField != null, "Cannot capture helper's Idle callback for generation regression");
                    EventHandler staleIdle = (EventHandler)idleField.GetValue(snapshot.Value);
                    Require(staleIdle != null, "Schedule did not arm an Idle callback");
                    snapshot.Cancel();
                    snapshot.Schedule(snapshotOwner);
                    staleIdle(null, EventArgs.Empty);
                    Require(calls == 0, "A stale Idle invocation consumed a newly scheduled generation");
                    RunUntil(delegate { return calls == 1; }, 1500);
                    Require(calls == 1, "The new generation did not run exactly once");
                    Console.WriteLine("STALE_IDLE ignored=true new_generation_actions=" + calls);
                }
            }
            finally { Application.Idle -= observer; }
        }
    }

    private static void TimerFallback()
    {
        using (HiddenOwner owner = new HiddenOwner())
        {
            IntPtr handle = owner.Handle;
            int calls = 0, idleEvents = 0;
            EventHandler observer = delegate { idleEvents++; };
            Application.Idle += observer;
            using (Continuation next = new Continuation(delegate { calls++; }))
            {
                try
                {
                    next.Schedule(owner);
                    Stopwatch elapsed = Stopwatch.StartNew();
                    while (calls == 0 && elapsed.ElapsedMilliseconds < 1500)
                    {
                        Application.DoEvents();
                        Thread.Sleep(1);
                    }
                    Require(calls == 1, "Timer fallback did not finish with DoEvents only");
                    Require(idleEvents == 0, "DoEvents fixture unexpectedly raised Idle; fallback was not isolated");
                    Console.WriteLine("FALLBACK actions=" + calls + " idle_events=" + idleEvents);
                }
                finally { Application.Idle -= observer; }
            }
        }
    }

    private static void ReentrantSchedule()
    {
        using (HiddenOwner owner = new HiddenOwner())
        {
            IntPtr handle = owner.Handle;
            int calls = 0, depth = 0, maxDepth = 0;
            Continuation next = null;
            next = new Continuation(delegate
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
                try
                {
                    int current = ++calls;
                    if (current < 6)
                    {
                        next.Schedule(owner);
                        // Deliberately enter a nested pump while the timer is due.
                        Thread.Sleep(18);
                        Application.DoEvents();
                        Require(calls == current, "Action reentered while an earlier slice was still running");
                    }
                }
                finally { depth--; }
            });
            try
            {
                next.Schedule(owner);
                RunUntil(delegate { return calls >= 6; }, 2000);
                Require(calls == 6 && maxDepth == 1, "Self-scheduling lost work or recursed");
                Console.WriteLine("REENTRANT actions=" + calls + " max_depth=" + maxDepth);
            }
            finally { next.Dispose(); }
        }
    }

    private static void OwnerLifecycle()
    {
        using (HiddenOwner owner = new HiddenOwner())
        {
            IntPtr handle = owner.Handle;
            int calls = 0;
            using (Continuation next = new Continuation(delegate { calls++; }))
            using (System.Windows.Forms.Timer phases = new System.Windows.Forms.Timer { Interval = 70 })
            {
                next.Schedule(owner);
                owner.Recreate();
                next.Cancel();
                int stage = 0;
                phases.Tick += delegate
                {
                    if (stage == 0)
                    {
                        Require(calls == 0, "A callback survived handle recreation followed by Cancel");
                        handle = owner.Handle;
                        next.Schedule(owner);
                    }
                    else if (stage == 1)
                    {
                        Require(calls == 1, "A fresh schedule did not work after recreation and Cancel");
                        next.Schedule(owner);
                        owner.Dispose();
                        next.Cancel();
                    }
                    stage++;
                };
                phases.Start();
                RunUntil(delegate { return stage >= 3; }, 1500);
                phases.Stop();
                Require(calls == 1, "A late callback ran after owner disposal and Cancel");
                Console.WriteLine("LIFECYCLE actions=" + calls + " recreated_cancelled=true disposed_cancelled=true");
            }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: UiIdleContinuationTests <engine.dll> <idle|cancel|fallback|reentrant|lifecycle>");
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Type engine = Assembly.LoadFrom(args[0]).GetType("CMOLocalizer.CustomAppDomainManager", true);
            continuationType = engine.GetNestedType("UiIdleContinuation", BindingFlags.NonPublic);
            Require(continuationType != null, "Required helper UiIdleContinuation is missing");
            if (args[1] == "idle") IdleSlices();
            else if (args[1] == "cancel") CancelAndDispose();
            else if (args[1] == "fallback") TimerFallback();
            else if (args[1] == "reentrant") ReentrantSchedule();
            else if (args[1] == "lifecycle") OwnerLifecycle();
            else throw new ArgumentException("Unknown mode " + args[1]);
            Console.WriteLine("PASS " + args[1] + " assertions=" + assertions + " visible_forms=" + Application.OpenForms.Count);
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error);
            return 1;
        }
    }
}
