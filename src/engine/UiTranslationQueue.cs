using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        [ThreadStatic] private static UiTranslationQueue uiTranslationQueue;

        private static void QueueUiTranslation(Control control, bool firstText)
        {
            if (control == null || control.IsDisposed) return;
            if (control.InvokeRequired)
            {
                try { control.BeginInvoke((MethodInvoker)delegate { QueueUiTranslation(control, firstText); }); }
                catch (InvalidOperationException) { }
                return;
            }
            if (uiTranslationQueue == null) uiTranslationQueue = new UiTranslationQueue();
            uiTranslationQueue.Request(control, firstText);
        }

        private static void DrainUiLifecycleQueue()
        {
            if (uiTranslationQueue != null) uiTranslationQueue.Drain();
        }

        private static bool IsVisibleUiTranslation(Control control)
        {
            if (!control.Visible) return false;
            Rectangle region = control.ClientRectangle;
            // Zero-size pre-layout controls have no reliable clipping information.
            if (region.Width <= 0 || region.Height <= 0) return true;
            for (Control current = control; current.Parent != null; current = current.Parent)
            {
                region.Offset(current.Left, current.Top);
                Rectangle clip = current.Parent.ClientRectangle;
                if (clip.Width > 0 && clip.Height > 0)
                {
                    region.Intersect(clip);
                    if (region.IsEmpty) return false;
                }
            }
            return true;
        }

        // One queue per owning UI thread. No control is processed twice in a
        // cycle merely because both an ancestor and its descendant were queued.
        private sealed class UiTranslationQueue
        {
            private sealed class Work
            {
                internal Control Control;
                internal bool Visible;
                internal bool FirstTextDone;
            }

            private readonly Dictionary<Control, Work> pending = new Dictionary<Control, Work>();
            private readonly Queue<Work> visible = new Queue<Work>();
            private readonly Queue<Work> background = new Queue<Work>();
            private readonly HashSet<Control> visited = new HashSet<Control>();
            private readonly HashSet<Control> nextCycle = new HashSet<Control>();
            private bool draining;
            private bool firstTextBusy;
            private bool scheduled;
            private int ticket;
            private int visibleSinceBackground;
            private Control dispatchOwner;
            private UiIdleContinuation continuation;

            internal void Request(Control control, bool firstText)
            {
                AttachUiLifecycle(control);
                if (visited.Contains(control))
                {
                    // An actual event after this control ran may introduce new
                    // descendants. Reconcile it next cycle, without a live loop.
                    nextCycle.Add(control);
                    Schedule(false);
                    return;
                }
                Work item = Enqueue(control);
                try
                {
                    if (firstText && !draining && !firstTextBusy && item != null
                        && !item.FirstTextDone && IsVisibleUiTranslation(control))
                    {
                        item.FirstTextDone = true;
                        firstTextBusy = true;
                        try
                        {
                            HookControlTextChanged(control);
                            // Only the root's simple text is synchronous. Items,
                            // grids and descendants remain in the queued pass.
                            TranslateControlText(control);
                        }
                        finally { firstTextBusy = false; }
                    }
                }
                finally { Schedule(false); }
            }

            private Work Enqueue(Control control)
            {
                if (control == null || control.IsDisposed || visited.Contains(control)) return null;
                bool high = IsVisibleUiTranslation(control);
                Work item;
                if (pending.TryGetValue(control, out item))
                {
                    if (high && !item.Visible) { item.Visible = true; visible.Enqueue(item); }
                    return item;
                }
                item = new Work { Control = control, Visible = high };
                pending.Add(control, item);
                (high ? visible : background).Enqueue(item);
                return item;
            }

            private Work Take(Queue<Work> queue, bool high)
            {
                while (queue.Count != 0)
                {
                    Work item = queue.Dequeue();
                    Work current;
                    if (item.Visible != high || !pending.TryGetValue(item.Control, out current)
                        || !object.ReferenceEquals(current, item)) continue;
                    pending.Remove(item.Control);
                    if (!item.Control.IsDisposed) return item;
                }
                return null;
            }

            private Work Next()
            {
                // Visible work wins initially; one background node per 64
                // visible nodes prevents starvation during continuous updates.
                Work item = visibleSinceBackground >= 64 ? Take(background, false) : null;
                if (item != null) { visibleSinceBackground = 0; return item; }
                item = Take(visible, true);
                if (item != null) { visibleSinceBackground++; return item; }
                visibleSinceBackground = 0;
                return Take(background, false);
            }

            internal void Drain()
            {
                if (draining) return;
                ticket++;
                scheduled = false;
                dispatchOwner = null;
                if (continuation != null) continuation.Cancel();
                draining = true;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    Work item;
                    while ((item = Next()) != null)
                    {
                        Control control = item.Control;
                        visited.Add(control);
                        try
                        {
                            TranslateControlNode(control);
                        }
                        catch (Exception ex) { Log("Queued UI node translation failed: " + ex.Message); }
                        // A failing parent's business callback must not prevent
                        // its otherwise healthy descendants from being visited.
                        try
                        {
                            if (!control.IsDisposed && control.HasChildren)
                            {
                                Control[] children = new Control[control.Controls.Count];
                                control.Controls.CopyTo(children, 0);
                                foreach (Control child in children) Enqueue(child);
                            }
                        }
                        catch (Exception ex) { Log("Queued UI child discovery failed: " + ex.Message); }
                        if (Stopwatch.GetTimestamp() - started >= Stopwatch.Frequency / 250) break;
                    }
                }
                finally { draining = false; }
                if (pending.Count == 0) CompleteCycle();
                if (pending.Count == 0) StopIdle();
                else Schedule(true);
            }

            private void CompleteCycle()
            {
                visited.Clear();
                visible.Clear();
                background.Clear();
                if (nextCycle.Count != 0)
                {
                    Control[] fresh = new Control[nextCycle.Count];
                    nextCycle.CopyTo(fresh);
                    nextCycle.Clear();
                    foreach (Control control in fresh) Enqueue(control);
                }
            }

            private void Schedule(bool later)
            {
                if (scheduled || draining || pending.Count == 0) return;
                foreach (Control control in pending.Keys)
                {
                    Control owner = control;
                    while (owner != null && !owner.IsHandleCreated) owner = owner.Parent;
                    if (owner == null || owner.IsDisposed) continue;
                    dispatchOwner = owner;
                    scheduled = true;
                    if (later)
                    {
                        if (continuation == null) continuation = new UiIdleContinuation(Drain);
                        continuation.Schedule(owner);
                        return;
                    }
                    int queuedTicket = ++ticket;
                    try
                    {
                        owner.BeginInvoke((MethodInvoker)delegate
                        {
                            if (queuedTicket == ticket && scheduled) Drain();
                        });
                    }
                    catch (InvalidOperationException) { scheduled = false; dispatchOwner = null; }
                    return;
                }
                // HandleCreated will retry. Do not create handles for discovery.
            }

            internal void OwnerUnavailable(Control control, bool disposed)
            {
                if (disposed)
                {
                    pending.Remove(control);
                    visited.Remove(control);
                    nextCycle.Remove(control);
                }
                if (object.ReferenceEquals(dispatchOwner, control))
                {
                    ticket++;
                    scheduled = false;
                    dispatchOwner = null;
                    if (continuation != null) continuation.Cancel();
                }
                if (pending.Count == 0 && !draining) CompleteCycle();
                if (pending.Count == 0 && !draining) StopIdle();
                else Schedule(true);
            }

            private void StopIdle()
            {
                ticket++;
                scheduled = false;
                dispatchOwner = null;
                visited.Clear();
                visible.Clear();
                background.Clear();
                if (continuation != null)
                {
                    continuation.Dispose();
                    continuation = null;
                }
            }
        }
    }
}
