using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly ConditionalWeakTable<Dispatcher, WpfTranslationQueue> wpfQueues =
            new ConditionalWeakTable<Dispatcher, WpfTranslationQueue>();
        private static readonly ConditionalWeakTable<FrameworkElement, WpfLayoutRequest> wpfLayoutRequests =
            new ConditionalWeakTable<FrameworkElement, WpfLayoutRequest>();
        [ThreadStatic] private static List<WeakReference> registeredElementHosts;

        private static WpfTranslationQueue WpfQueueFor(Dispatcher dispatcher)
        {
            return wpfQueues.GetValue(dispatcher, delegate(Dispatcher owner) { return new WpfTranslationQueue(owner); });
        }

        private static void QueueWpfSource(System.Windows.Interop.HwndSource source)
        {
            if (source == null || source.Dispatcher == null || source.Dispatcher.HasShutdownStarted) return;
            WpfQueueFor(source.Dispatcher).RequestSource(source);
        }

        private static void QueueWpfElement(DependencyObject root, bool immediate)
        {
            if (root == null || root.Dispatcher.HasShutdownStarted) return;
            WpfTranslationQueue queue = WpfQueueFor(root.Dispatcher);
            queue.RequestRoot(root);
            if (immediate && root.Dispatcher.CheckAccess()) queue.Drain();
        }

        private static void RegisterElementHost(System.Windows.Forms.Control host)
        {
            if (host == null || host.IsDisposed || host.InvokeRequired) return;
            if (registeredElementHosts == null) registeredElementHosts = new List<WeakReference>();
            for (int i = registeredElementHosts.Count - 1; i >= 0; i--)
            {
                System.Windows.Forms.Control old = registeredElementHosts[i].Target as System.Windows.Forms.Control;
                if (old == null || old.IsDisposed) registeredElementHosts.RemoveAt(i);
                else if (object.ReferenceEquals(old, host)) return;
            }
            registeredElementHosts.Add(new WeakReference(host));
        }

        private static void RequestElementHost(System.Windows.Forms.Control host)
        {
            if (host == null || host.IsDisposed || host.InvokeRequired) return;
            RegisterElementHost(host);
            PropertyInfo child = Reflect.Prop(host.GetType(), "Child", BindingFlags.Public | BindingFlags.Instance);
            DependencyObject root = child != null ? child.GetValue(host, null) as DependencyObject : null;
            if (root == null) return;
            if (root.Dispatcher.CheckAccess()) HookElementHostLayout(root);
            QueueWpfElement(root, false);
        }

        private sealed class WpfLayoutRequest
        {
            private readonly FrameworkElement root;
            private bool attached;
            private long lastRequest;

            internal WpfLayoutRequest(FrameworkElement root)
            {
                this.root = root;
                root.Loaded += OnLoaded;
                root.Unloaded += OnUnloaded;
                if (root.IsLoaded) Attach();
            }

            private void Attach()
            {
                if (attached) return;
                attached = true;
                root.LayoutUpdated += OnLayout;
                root.Dispatcher.ShutdownStarted += OnUnloaded;
            }

            private void OnLoaded(object sender, RoutedEventArgs args) { Attach(); QueueWpfElement(root, false); }

            private void OnUnloaded(object sender, EventArgs args)
            {
                if (!attached) return;
                attached = false;
                root.LayoutUpdated -= OnLayout;
                root.Dispatcher.ShutdownStarted -= OnUnloaded;
                WpfQueueFor(root.Dispatcher).CancelRoot(root);
            }

            private void OnLayout(object sender, EventArgs args)
            {
                if (!root.IsLoaded || PresentationSource.FromDependencyObject(root) == null) return;
                long now = Stopwatch.GetTimestamp();
                if (lastRequest != 0 && now - lastRequest < Stopwatch.Frequency / 25) return;
                lastRequest = now;
                // Layout is a discovery hint; all actual work runs on the queue so
                // it can be sliced and cannot starve input or paint.
                QueueWpfElement(root, false);
            }
        }

        private sealed class WpfTranslationQueue
        {
            private const int MaxRoots = 64;
            private const int SliceSteps = 96;
            private const int SliceBudgetMs = 4;
            private const int StarvationMs = 2000;

            private readonly Dispatcher dispatcher;
            private readonly object gate = new object();
            private readonly Dictionary<object, Work> pending = new Dictionary<object, Work>();
            private readonly Queue<Work> ready = new Queue<Work>();
            private DispatcherOperation operation;
            private UiPerfScope posted;
            private bool draining;
            private bool shutdown;

            private sealed class Work
            {
                internal object Key;
                internal System.Windows.Interop.HwndSource Source;
                internal DependencyObject Root;
                internal PresentationSource Presentation;
                internal DependencyObject Parent;
                internal Stack<Frame> Frames;
                internal HashSet<DependencyObject> Visited;
                internal int SlicesRun;
                internal long EnqueuedAt;
            }

            // Document children are tracked with sibling references (NextBlock /
            // NextInline). Writing Run.Text mutates the collection and invalidates
            // any IEnumerator, which is why an enumerator cannot span slices.
            internal sealed class Frame
            {
                internal DependencyObject Node;
                internal bool Visited;
                internal int VisualIndex;
                internal int ExtraIndex;
                internal DependencyObject[] Extra;
                internal Block BlockCursor;
                internal Inline InlineCursor;
                internal bool LogicalStarted;
                internal bool LogicalDone;
            }

            internal WpfTranslationQueue(Dispatcher dispatcher)
            {
                this.dispatcher = dispatcher;
            }

            internal void RequestSource(System.Windows.Interop.HwndSource source)
            {
                Request(source, source, null);
            }

            internal void RequestRoot(DependencyObject root)
            {
                Request(root, null, root);
            }

            private void Request(object key, System.Windows.Interop.HwndSource source, DependencyObject root)
            {
                lock (gate)
                {
                    if (shutdown || dispatcher.HasShutdownStarted || pending.ContainsKey(key)) return;
                    if (root != null && dispatcher.CheckAccess() && PresentationSource.FromDependencyObject(root) != null)
                    {
                        // A descendant never needs its own pass while an ancestor is queued.
                        foreach (Work current in pending.Values)
                        {
                            DependencyObject ancestor = root;
                            for (int depth = 0; ancestor != null && depth < 128; depth++, ancestor = ParentOf(ancestor))
                                if (object.ReferenceEquals(ancestor, current.Root)) return;
                        }
                    }
                    if (pending.Count >= MaxRoots) return;
                    Work work = new Work
                    {
                        Key = key, Source = source, Root = root,
                        EnqueuedAt = Stopwatch.GetTimestamp()
                    };
                    pending.Add(key, work);
                    System.Threading.Interlocked.Increment(ref _wpfSweepWindowCount);
                    ready.Enqueue(work);
                    Schedule();
                }
            }

            private void Schedule()
            {
                if (operation != null || draining || shutdown || ready.Count == 0) return;
                DispatcherPriority priority = DispatcherPriority.Background;
                long now = Stopwatch.GetTimestamp();
                foreach (Work item in pending.Values)
                {
                    // A newly discovered root must reach Chinese before its first
                    // paint; everything else yields to input and rendering.
                    if (item.SlicesRun == 0) { priority = DispatcherPriority.Render; break; }
                    long waitedMs = (now - item.EnqueuedAt) * 1000 / Stopwatch.Frequency;
                    if (waitedMs >= StarvationMs) { priority = DispatcherPriority.Input; break; }
                }
                UiPerfScope ticket = new UiPerfScope("wpfQueue", true);
                posted = ticket;
                try
                {
                    operation = dispatcher.BeginInvoke(priority, new Action(delegate
                    {
                        ticket.Start();
                        using (ticket) Drain();
                    }));
                    operation.Aborted += delegate
                    {
                        ticket.Dispose();
                        lock (gate)
                        {
                            if (object.ReferenceEquals(posted, ticket)) { posted = null; operation = null; }
                        }
                    };
                    // A/B: watchdog disabled to isolate the startup exit
                }
                catch
                {
                    ticket.Dispose();
                    posted = null;
                    operation = null;
                    throw;
                }
            }

            private static DependencyObject ParentOf(DependencyObject node)
            {
                DependencyObject parent = LogicalTreeHelper.GetParent(node);
                if (parent == null && node is Visual) parent = VisualTreeHelper.GetParent(node);
                return parent;
            }

            private static bool IsCurrent(Work work)
            {
                if (work.Source != null)
                {
                    if (work.Source.IsDisposed || !IsWindowVisible(work.Source.Handle)
                        || !object.ReferenceEquals(work.Source.RootVisual, work.Root)) return false;
                }
                if (work.Presentation != null)
                    return object.ReferenceEquals(PresentationSource.FromDependencyObject(work.Root), work.Presentation);
                return object.ReferenceEquals(ParentOf(work.Root), work.Parent);
            }

            private static bool Initialize(Work work)
            {
                if (work.Source != null)
                {
                    if (work.Source.IsDisposed || !IsWindowVisible(work.Source.Handle)) return false;
                    work.Root = work.Source.RootVisual;
                }
                if (work.Root == null || IsEditableWpfTarget(work.Root)) return false;
                work.Presentation = PresentationSource.FromDependencyObject(work.Root);
                work.Parent = ParentOf(work.Root);
                work.Visited = new HashSet<DependencyObject>(ReferenceComparer.Instance);
                work.Frames = new Stack<Frame>();
                work.Frames.Push(new Frame { Node = work.Root });
                InstallWpfRenderHook();
                FrameworkElement element = work.Root as FrameworkElement;
                if (element != null) HookElementHostLayout(element);
                return true;
            }

            private static DependencyObject NextChild(Frame frame)
            {
                if (frame.Extra != null && frame.ExtraIndex < frame.Extra.Length)
                {
                    DependencyObject extra = frame.Extra[frame.ExtraIndex++];
                    return extra;
                }
                int count = 0;
                if (frame.Node is Visual) count = VisualTreeHelper.GetChildrenCount(frame.Node);
                if (frame.VisualIndex < count) return VisualTreeHelper.GetChild(frame.Node, frame.VisualIndex++);
                if (frame.LogicalDone) return null;

                BlockCollection blocks = null;
                FlowDocument document = frame.Node as FlowDocument;
                Section section = frame.Node as Section;
                if (document != null) blocks = document.Blocks;
                else if (section != null) blocks = section.Blocks;
                if (blocks != null)
                {
                    if (!frame.LogicalStarted)
                    {
                        frame.LogicalStarted = true;
                        frame.BlockCursor = blocks.FirstBlock;
                    }
                    else if (frame.BlockCursor != null)
                    {
                        frame.BlockCursor = frame.BlockCursor.NextBlock;
                    }
                    if (frame.BlockCursor == null) { frame.LogicalDone = true; return null; }
                    return frame.BlockCursor;
                }

                InlineCollection inlines = null;
                Paragraph paragraph = frame.Node as Paragraph;
                Span span = frame.Node as Span;
                TextBlock text = frame.Node as TextBlock;
                if (paragraph != null) inlines = paragraph.Inlines;
                else if (span != null) inlines = span.Inlines;
                else if (text != null
                         && !System.Windows.Data.BindingOperations.IsDataBound(text, TextBlock.TextProperty))
                    inlines = text.Inlines;
                if (inlines != null)
                {
                    if (!frame.LogicalStarted)
                    {
                        frame.LogicalStarted = true;
                        frame.InlineCursor = inlines.FirstInline;
                    }
                    else if (frame.InlineCursor != null)
                    {
                        frame.InlineCursor = frame.InlineCursor.NextInline;
                    }
                    if (frame.InlineCursor == null) { frame.LogicalDone = true; return null; }
                    return frame.InlineCursor;
                }
                frame.LogicalDone = true;
                return null;
            }

            private static bool Step(Work work)
            {
                Frame frame = work.Frames.Peek();
                if (!frame.Visited)
                {
                    frame.Visited = true;
                    if (IsEditableWpfTarget(frame.Node))
                    {
                        work.Frames.Pop();
                        return work.Frames.Count != 0;
                    }
                    work.Visited.Add(frame.Node);
                    long nodeStarted = Stopwatch.GetTimestamp();
                    try { TranslateWpfNode(frame.Node); }
                    catch (Exception error) { Log("WPF node skipped: " + error.Message); }
                    long nodeTicks = Stopwatch.GetTimestamp() - nodeStarted;
                    if (nodeTicks > System.Threading.Interlocked.Read(ref _wpfMaxStepTicks))
                    {
                        System.Threading.Interlocked.Exchange(ref _wpfMaxStepTicks, nodeTicks);
                        _wpfMaxStepNode = frame.Node.GetType().Name
                            + (string.IsNullOrEmpty(GetWpfElementName(frame.Node)) ? "" : "#" + GetWpfElementName(frame.Node));
                    }
                    try { frame.Extra = WpfNodeExtras(frame.Node); }
                    catch (Exception error) { frame.Extra = null; Log("WPF node extras skipped: " + error.Message); }
                    return true;
                }
                DependencyObject child = null;
                try { child = NextChild(frame); }
                catch (Exception error) { frame.LogicalDone = true; Log("WPF child enumeration stopped: " + error.Message); }
                if (child != null && !work.Visited.Contains(child)) work.Frames.Push(new Frame { Node = child });
                else if (child == null) work.Frames.Pop();
                // A visited child is skipped without changing the cursor: the
                // parent continues with the next sibling on the following step.
                return work.Frames.Count != 0;
            }

            internal void Drain()
            {
                if (!dispatcher.CheckAccess()) return;
                lock (gate)
                {
                    if (draining || shutdown) return;
                    if (operation != null && operation.Status == DispatcherOperationStatus.Pending)
                    {
                        DispatcherOperation old = operation;
                        operation = null;
                        if (posted != null) posted.Dispose();
                        posted = null;
                        old.Abort();
                    }
                    operation = null;
                    posted = null;
                    draining = true;
                }
                long started = Stopwatch.GetTimestamp();
                int steps = 0;
                bool hooked = false;
                using (new UiPerfScope("wpfSlice", false))
                try
                {
                    while (true)
                    {
                        if (!hooked)
                        {
                            hooked = true;
                            dispatcher.ShutdownStarted -= OnShutdown;
                            dispatcher.ShutdownStarted += OnShutdown;
                        }
                        Work work;
                        lock (gate) { if (ready.Count == 0) break; work = ready.Dequeue(); }
                        bool again = false;
                        try
                        {
                            if (work.Frames == null && !Initialize(work)) { }
                            else if (IsCurrent(work)) again = Step(work);
                        }
                        catch (Exception error)
                        {
                            Log("WPF queued translation failed: " + error.Message);
                            again = false;
                        }
                        work.SlicesRun++;
                        lock (gate)
                        {
                            if (again) ready.Enqueue(work);
                            else
                            {
                                if (pending.Remove(work.Key)) System.Threading.Interlocked.Decrement(ref _wpfSweepWindowCount);
                                DisposeWork(work);
                            }
                        }
                        steps++;
                        if (steps >= SliceSteps
                            || (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency >= SliceBudgetMs) break;
                    }
                }
                finally
                {
                    long elapsedTicks = Stopwatch.GetTimestamp() - started;
                    lock (traceLock)
                    {
                        _sweepCalls++;
                        long elapsedMs = elapsedTicks * 1000 / Stopwatch.Frequency;
                        _sweepTotalMs += elapsedMs;
                        if (elapsedMs > _sweepMaxMs) _sweepMaxMs = elapsedMs;
                        _sweepWindowsVisited += steps;
                        if (pending.Count == 0) _sweepFullPasses++;
                    }
                    lock (gate)
                    {
                        draining = false;
                        if (dispatcher.HasShutdownStarted) CancelAll();
                        else
                        {
                            if (dispatcher.CheckAccess())
                            {
                                dispatcher.ShutdownStarted -= OnShutdown;
                                dispatcher.ShutdownStarted += OnShutdown;
                            }
                            Schedule();
                        }
                    }
                }
            }

            private static void DisposeWork(Work work)
            {
                if (work.Frames == null) return;
                work.Frames.Clear();
            }

            internal void CancelRoot(DependencyObject root)
            {
                lock (gate)
                {
                    int count = ready.Count;
                    while (count-- > 0)
                    {
                        Work work = ready.Dequeue();
                        if (object.ReferenceEquals(work.Root, root))
                        {
                            if (pending.Remove(work.Key)) System.Threading.Interlocked.Decrement(ref _wpfSweepWindowCount);
                            DisposeWork(work);
                        }
                        else ready.Enqueue(work);
                    }
                }
            }

            private void OnShutdown(object sender, EventArgs args)
            {
                lock (gate) CancelAll();
            }

            private void CancelAll()
            {
                shutdown = true;
                foreach (Work work in pending.Values) DisposeWork(work);
                System.Threading.Interlocked.Add(ref _wpfSweepWindowCount, -pending.Count);
                pending.Clear(); ready.Clear();
                if (posted != null) posted.Dispose();
                posted = null; operation = null;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<DependencyObject>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(DependencyObject left, DependencyObject right) { return object.ReferenceEquals(left, right); }
            public int GetHashCode(DependencyObject value) { return RuntimeHelpers.GetHashCode(value); }
        }
    }
}
