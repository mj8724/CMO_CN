using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly ConditionalWeakTable<Control, DarkTreeTranslationState> darkTreeTranslations =
            new ConditionalWeakTable<Control, DarkTreeTranslationState>();

        private static void TranslateDarkTreeIncremental(Control tree)
        {
            DarkTreeTranslationState state;
            if (!darkTreeTranslations.TryGetValue(tree, out state))
            {
                state = new DarkTreeTranslationState(tree);
                darkTreeTranslations.Add(tree, state);
            }
            // Keep the existing periodic entry point as reconciliation for
            // silent Nodes property replacements and other unreported changes.
            state.Refresh();
        }

        private sealed class DarkTreeReferenceComparer : IEqualityComparer<object>
        {
            bool IEqualityComparer<object>.Equals(object x, object y) { return object.ReferenceEquals(x, y); }
            int IEqualityComparer<object>.GetHashCode(object value) { return RuntimeHelpers.GetHashCode(value); }
        }

        private sealed class DarkTreeSubscription
        {
            private readonly object source;
            private readonly EventInfo eventInfo;
            private readonly Delegate handler;

            internal DarkTreeSubscription(object source, string name, Action<object, object> callback)
            {
                this.source = source;
                eventInfo = source.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                if (eventInfo == null) return;
                // Actual DarkUI uses EventHandler<ObservableListModified<T>>
                // for additions/removals and EventHandler<object> for clears.
                // Relaxed reference-parameter binding covers both without
                // compiling a DynamicMethod for every node or collection.
                handler = Delegate.CreateDelegate(eventInfo.EventHandlerType, callback.Target, callback.Method);
                eventInfo.AddEventHandler(source, handler);
            }

            internal void Detach()
            {
                if (handler != null) eventInfo.RemoveEventHandler(source, handler);
            }
        }

        private sealed class DarkTreeCollection
        {
            internal object Source;
            internal DarkTreeNodeState Parent;
            internal bool Attached = true;
            internal readonly HashSet<DarkTreeNodeState> Children = new HashSet<DarkTreeNodeState>();
            internal readonly List<DarkTreeSubscription> Subscriptions = new List<DarkTreeSubscription>();
        }

        private sealed class DarkTreeNodeState
        {
            internal object Node;
            internal DarkTreeCollection Owner;
            internal DarkTreeCollection Children;
            internal PropertyInfo Text;
            internal PropertyInfo Nodes;
            internal PropertyInfo Expanded;
            internal PropertyInfo FullArea;
            internal DarkTreeSubscription TextChanged;
            internal DarkTreeSubscription NodeExpanded;
            internal DarkTreeSubscription NodeCollapsed;
            internal bool Attached = true;
            internal bool Writing;
            internal bool CheckChildren;
            internal int WorkPriority = 3;
        }

        private sealed class DarkTreeTranslationState
        {
            private readonly Control tree;
            private readonly PropertyInfo nodesProperty;
            private readonly PropertyInfo viewportProperty;
            private readonly DarkTreeSubscription viewportChanged;
            private readonly UiIdleContinuation continuation;
            private readonly Dictionary<object, DarkTreeNodeState> nodes =
                new Dictionary<object, DarkTreeNodeState>(new DarkTreeReferenceComparer());
            private readonly Dictionary<DarkTreeNodeState, bool> pending = new Dictionary<DarkTreeNodeState, bool>();
            private readonly HashSet<DarkTreeCollection> dirtyCollections = new HashSet<DarkTreeCollection>();
            private readonly Queue<DarkTreeNodeState>[] work = { new Queue<DarkTreeNodeState>(),
                new Queue<DarkTreeNodeState>(), new Queue<DarkTreeNodeState>() };
            private readonly HashSet<DarkTreeNodeState> queued = new HashSet<DarkTreeNodeState>();
            private DarkTreeCollection roots;
            private bool scheduled;
            private bool flushing;
            private bool disposed;
            private bool prioritiesChanged;
            private int generation;
            private int workTurn;
            private Rectangle lastViewport;
            private bool hasViewport;
            internal double LastSliceMs;
            internal int LastSliceProcessedNodes;
            internal int QueuedNodes { get { return queued.Count + pending.Count; } }

            internal DarkTreeTranslationState(Control tree)
            {
                this.tree = tree;
                nodesProperty = Reflect.Prop(tree.GetType(), "Nodes", BindingFlags.Public | BindingFlags.Instance);
                viewportProperty = Reflect.Prop(tree.GetType(), "Viewport", BindingFlags.Public | BindingFlags.Instance);
                ViewportMoved();
                viewportChanged = new DarkTreeSubscription(tree, "ViewportChanged", delegate
                {
                    // Ignore identical layout notifications, but retain an
                    // actual programmatic scroll even inside TextChanged.
                    if (ViewportMoved()) { prioritiesChanged = true; Schedule(); }
                });
                continuation = new UiIdleContinuation(Flush);
                tree.Invalidated += OnInvalidated;
                tree.HandleCreated += OnHandleCreated;
                tree.HandleDestroyed += OnHandleDestroyed;
                tree.Disposed += OnDisposed;
            }

            internal void Refresh()
            {
                if (disposed || tree.IsDisposed || nodesProperty == null) return;
                try
                {
                    ReconcileRoots();
                    if (roots != null)
                        foreach (DarkTreeNodeState root in roots.Children) Queue(root);
                    if (tree.IsHandleCreated) Flush();
                }
                catch (Exception ex) { Log("DarkTree refresh failed: " + ex.Message); }
            }

            private void ReconcileRoots()
            {
                object current = nodesProperty.GetValue(tree, null);
                if (roots != null && object.ReferenceEquals(roots.Source, current)) return;
                if (roots != null) DetachCollection(roots);
                roots = AttachCollection(current, null);
                if (roots != null)
                    foreach (DarkTreeNodeState root in roots.Children) Queue(root);
            }

            private DarkTreeCollection AttachCollection(object source, DarkTreeNodeState parent)
            {
                IEnumerable values = source as IEnumerable;
                if (values == null) return null;
                DarkTreeCollection collection = new DarkTreeCollection { Source = source, Parent = parent };
                collection.Subscriptions.Add(new DarkTreeSubscription(source, "ItemsAdded", delegate { CollectionChanged(collection); }));
                collection.Subscriptions.Add(new DarkTreeSubscription(source, "ItemsRemoved", delegate { CollectionChanged(collection); }));
                collection.Subscriptions.Add(new DarkTreeSubscription(source, "ItemsCleared", delegate { CollectionChanged(collection); }));
                foreach (object node in Snapshot(values)) AttachNode(node, collection);
                return collection;
            }

            private void CollectionChanged(DarkTreeCollection collection)
            {
                if (disposed || !collection.Attached) return;
                // Earlier game handlers may clear/refill or remove/readd before
                // this callback runs. Reconcile current membership once per batch,
                // instead of applying an event payload from before those handlers.
                dirtyCollections.Add(collection);
                Schedule();
            }

            private void ReconcileCollections()
            {
                List<DarkTreeCollection> batch = new List<DarkTreeCollection>(dirtyCollections);
                dirtyCollections.Clear();
                foreach (DarkTreeCollection collection in batch)
                {
                    if (!collection.Attached) continue;
                    List<object> current = Snapshot(collection.Source as IEnumerable);
                    HashSet<object> members = new HashSet<object>(current, new DarkTreeReferenceComparer());
                    foreach (DarkTreeNodeState child in new List<DarkTreeNodeState>(collection.Children))
                        if (!members.Contains(child.Node)) DetachNode(child);
                    foreach (object node in current)
                    {
                        DarkTreeNodeState existing;
                        bool attached = nodes.TryGetValue(node, out existing) && existing.Owner == collection;
                        DarkTreeNodeState child = AttachNode(node, collection);
                        if (!attached) Queue(child);
                    }
                }
            }

            private static List<object> Snapshot(IEnumerable values)
            {
                List<object> result = new List<object>();
                if (values != null) foreach (object value in values) if (value != null) result.Add(value);
                return result;
            }

            private DarkTreeNodeState AttachNode(object node, DarkTreeCollection owner)
            {
                DarkTreeNodeState entry;
                if (nodes.TryGetValue(node, out entry))
                {
                    if (entry.Owner == owner) return entry;
                    DetachNode(entry);
                }
                Type type = node.GetType();
                entry = new DarkTreeNodeState { Node = node, Owner = owner,
                    Text = Reflect.Prop(type, "Text", BindingFlags.Public | BindingFlags.Instance),
                    Nodes = Reflect.Prop(type, "Nodes", BindingFlags.Public | BindingFlags.Instance),
                    Expanded = Reflect.Prop(type, "Expanded", BindingFlags.Public | BindingFlags.Instance),
                    FullArea = Reflect.Prop(type, "FullArea", BindingFlags.Public | BindingFlags.Instance) };
                nodes.Add(node, entry);
                owner.Children.Add(entry);
                DarkTreeNodeState captured = entry;
                entry.TextChanged = new DarkTreeSubscription(node, "TextChanged", delegate
                {
                    if (!captured.Writing) Queue(captured, false);
                });
                entry.NodeExpanded = new DarkTreeSubscription(node, "NodeExpanded", delegate
                {
                    prioritiesChanged = true;
                    Queue(captured);
                });
                entry.NodeCollapsed = new DarkTreeSubscription(node, "NodeCollapsed", delegate
                {
                    prioritiesChanged = true;
                    Schedule();
                });
                if (entry.Nodes != null) entry.Children = AttachCollection(entry.Nodes.GetValue(node, null), entry);
                return entry;
            }

            private void DetachNode(DarkTreeNodeState entry)
            {
                entry.Attached = false;
                pending.Remove(entry);
                queued.Remove(entry);
                entry.WorkPriority = 3;
                if (queued.Count == 0) foreach (Queue<DarkTreeNodeState> queue in work) queue.Clear();
                if (entry.Children != null) DetachCollection(entry.Children);
                if (entry.TextChanged != null) entry.TextChanged.Detach();
                if (entry.NodeExpanded != null) entry.NodeExpanded.Detach();
                if (entry.NodeCollapsed != null) entry.NodeCollapsed.Detach();
                entry.Owner.Children.Remove(entry);
                nodes.Remove(entry.Node);
            }

            private void DetachCollection(DarkTreeCollection collection)
            {
                collection.Attached = false;
                dirtyCollections.Remove(collection);
                foreach (DarkTreeSubscription subscription in collection.Subscriptions) subscription.Detach();
                foreach (DarkTreeNodeState child in new List<DarkTreeNodeState>(collection.Children)) DetachNode(child);
            }

            private void Queue(DarkTreeNodeState node, bool subtree = true)
            {
                if (disposed || !node.Attached) return;
                bool previous;
                pending.TryGetValue(node, out previous);
                pending[node] = subtree || previous;
                Schedule();
            }

            private void Schedule(bool yield = false)
            {
                if (disposed || flushing || scheduled || (pending.Count == 0 && queued.Count == 0 && dirtyCollections.Count == 0) || !tree.IsHandleCreated) return;
                scheduled = true;
                if (yield) { continuation.Schedule(tree); return; }
                int queuedGeneration = generation;
                try
                {
                    tree.BeginInvoke((MethodInvoker)delegate
                    {
                        if (disposed || queuedGeneration != generation) return;
                        scheduled = false;
                        Flush();
                    });
                }
                catch (InvalidOperationException) { scheduled = false; }
            }

            private int Priority(DarkTreeNodeState entry)
            {
                try
                {
                    for (DarkTreeNodeState parent = entry.Owner.Parent; parent != null; parent = parent.Owner.Parent)
                        if (parent.Expanded != null && object.Equals(parent.Expanded.GetValue(parent.Node, null), false)) return 2;
                    if (viewportProperty != null && entry.FullArea != null)
                    {
                        object viewValue = viewportProperty.GetValue(tree, null);
                        object areaValue = entry.FullArea.GetValue(entry.Node, null);
                        if (viewValue is Rectangle && areaValue is Rectangle)
                        {
                            Rectangle view = (Rectangle)viewValue, area = (Rectangle)areaValue;
                            if (view.Height > 0 && area.Height > 0)
                                return area.Bottom > view.Top && area.Top < view.Bottom ? 0 : 1;
                        }
                    }
                }
                catch { } // Missing/unfinished geometry uses expanded-first fallback.
                return 1;
            }

            private bool ViewportMoved()
            {
                if (viewportProperty == null) return false;
                object value = viewportProperty.GetValue(tree, null);
                if (!(value is Rectangle)) return false;
                Rectangle current = (Rectangle)value;
                bool moved = !hasViewport || current.Top != lastViewport.Top || current.Height != lastViewport.Height;
                lastViewport = current;
                hasViewport = true;
                return moved;
            }

            private void AddWork(DarkTreeNodeState entry, bool subtree, HashSet<DarkTreeNodeState> visited)
            {
                if (!entry.Attached || !visited.Add(entry)) return;
                entry.CheckChildren |= subtree;
                int priority = Priority(entry);
                if (priority < entry.WorkPriority)
                {
                    entry.WorkPriority = priority;
                    queued.Add(entry);
                    work[priority].Enqueue(entry);
                }
                if (subtree && entry.Children != null)
                    foreach (DarkTreeNodeState child in entry.Children.Children) AddWork(child, true, visited);
            }

            private void PrepareWork()
            {
                Dictionary<DarkTreeNodeState, bool> batch = new Dictionary<DarkTreeNodeState, bool>(pending);
                pending.Clear();
                HashSet<DarkTreeNodeState> visited = new HashSet<DarkTreeNodeState>();
                foreach (KeyValuePair<DarkTreeNodeState, bool> item in batch)
                {
                    bool covered = false;
                    for (DarkTreeNodeState parent = item.Key.Owner.Parent; parent != null; parent = parent.Owner.Parent)
                    {
                        bool subtree;
                        if (batch.TryGetValue(parent, out subtree) && subtree) { covered = true; break; }
                    }
                    if (!covered) AddWork(item.Key, item.Value, visited);
                }
                if (prioritiesChanged)
                {
                    prioritiesChanged = false;
                    List<DarkTreeNodeState> snapshot = new List<DarkTreeNodeState>(queued);
                    foreach (Queue<DarkTreeNodeState> queue in work) queue.Clear();
                    foreach (DarkTreeNodeState entry in snapshot)
                    {
                        entry.WorkPriority = Priority(entry);
                        work[entry.WorkPriority].Enqueue(entry);
                    }
                }
            }

            private DarkTreeNodeState TakeWork()
            {
                // Mostly visible work, but every 13th turn gives folded branches
                // a chance to finish even if the user keeps exposing new rows.
                int turn = workTurn++ % 13;
                int preferred = turn < 8 ? 0 : turn < 12 ? 1 : 2;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    int priority = attempt == 0 ? preferred : attempt - 1;
                    while (work[priority].Count > 0)
                    {
                        DarkTreeNodeState entry = work[priority].Dequeue();
                        if (!entry.Attached || entry.WorkPriority != priority) continue;
                        entry.WorkPriority = 3;
                        queued.Remove(entry);
                        return entry;
                    }
                }
                return null;
            }

            private void Flush()
            {
                if (disposed || tree.IsDisposed || flushing) return;
                continuation.Cancel();
                generation++;
                scheduled = false;
                flushing = true;
                long started = Stopwatch.GetTimestamp();
                int processed = 0;
                try
                {
                    ReconcileRoots();
                    ReconcileCollections();
                    PrepareWork();
                    bool changed = false;
                    // A callback may remove queued siblings. Reconcile that
                    // collection before writing any further node in a new slice.
                    while (queued.Count > 0 && dirtyCollections.Count == 0 && !prioritiesChanged && (processed == 0
                        || Stopwatch.GetTimestamp() - started < Stopwatch.Frequency / 250))
                    {
                        DarkTreeNodeState entry = TakeWork();
                        if (entry == null) break;
                        bool checkChildren = entry.CheckChildren;
                        entry.CheckChildren = false;
                        processed++;
                        try { changed |= TranslateNode(entry, checkChildren); }
                        catch (Exception ex)
                        {
                            // A faulty game callback must not block its siblings.
                            Log("DarkTree node translation failed: " + ex.Message);
                        }
                    }
                    if (changed && !tree.IsDisposed) tree.Invalidate();
                }
                catch (Exception ex) { Log("DarkTree incremental translation failed: " + ex.Message); }
                finally
                {
                    LastSliceMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    LastSliceProcessedNodes = processed;
                    if (queued.Count == 0) foreach (Queue<DarkTreeNodeState> queue in work) queue.Clear();
                    flushing = false;
                    // Continue after queued input/paint, with a timer fallback
                    // for hosts that do not raise Application.Idle.
                    Schedule(true);
                }
            }

            private bool TranslateNode(DarkTreeNodeState entry, bool checkChildren)
            {
                if (!entry.Attached) return false;
                bool changed = false;
                try
                {
                    if (entry.Text != null)
                    {
                        string raw = entry.Text.GetValue(entry.Node, null) as string;
                        if (!string.IsNullOrEmpty(raw))
                        {
                            string translated = TranslateString(raw);
                            if (string.IsNullOrEmpty(translated) || translated == raw) translated = TranslateMilitaryUnitName(raw);
                            if (!string.IsNullOrEmpty(translated) && translated != raw)
                            {
                                entry.Writing = true;
                                try { entry.Text.SetValue(entry.Node, translated, null); changed = true; }
                                finally { entry.Writing = false; }
                            }
                        }
                    }
                    return changed;
                }
                finally
                {
                    // A TextChanged callback may replace Nodes and then throw.
                    // Its healthy new children still need to enter the queue.
                    if (entry.Attached && checkChildren)
                    {
                        object current = entry.Nodes == null ? null : entry.Nodes.GetValue(entry.Node, null);
                        if (entry.Children == null || !object.ReferenceEquals(entry.Children.Source, current))
                        {
                            if (entry.Children != null) DetachCollection(entry.Children);
                            entry.Children = AttachCollection(current, entry);
                            if (entry.Children != null)
                                foreach (DarkTreeNodeState child in entry.Children.Children) Queue(child);
                        }
                    }
                }
            }

            private void OnInvalidated(object sender, InvalidateEventArgs args)
            {
                // Only compare the root collection identity here. Do not walk
                // nodes on paint/hover invalidations; events handle additions.
                if (!disposed && nodesProperty != null) ReconcileRoots();
            }

            private void OnHandleCreated(object sender, EventArgs args) { scheduled = false; Schedule(); }
            private void OnHandleDestroyed(object sender, EventArgs args) { generation++; scheduled = false; continuation.Cancel(); }

            private void OnDisposed(object sender, EventArgs args)
            {
                disposed = true;
                continuation.Dispose();
                viewportChanged.Detach();
                tree.Invalidated -= OnInvalidated;
                tree.HandleCreated -= OnHandleCreated;
                tree.HandleDestroyed -= OnHandleDestroyed;
                tree.Disposed -= OnDisposed;
                if (roots != null) DetachCollection(roots);
                pending.Clear();
                dirtyCollections.Clear();
                queued.Clear();
                foreach (Queue<DarkTreeNodeState> queue in work) queue.Clear();
                darkTreeTranslations.Remove(tree);
            }
        }
    }
}
