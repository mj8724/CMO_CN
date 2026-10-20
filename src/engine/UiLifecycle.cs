using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private sealed class UiLifecycleAttachment { }

        // Weak keys do not keep closed windows and their control trees alive.
        private static readonly ConditionalWeakTable<Control, UiLifecycleAttachment> uiLifecycleAttached =
            new ConditionalWeakTable<Control, UiLifecycleAttachment>();
        private static readonly object uiLifecycleLock = new object();
        private static readonly Dictionary<IntPtr, Control> uiLifecyclePending = new Dictionary<IntPtr, Control>();
        private static readonly HashSet<IntPtr> uiLifecyclePendingWpf = new HashSet<IntPtr>();
        private static readonly ManualResetEvent uiLifecycleReady = new ManualResetEvent(false);
        private static readonly UiLifecycleNative.WinEventProc uiLifecycleCallback = OnUiLifecycleWinEvent;
        private static Thread uiLifecycleThread;
        private static uint uiLifecycleThreadId;
        private static IntPtr uiLifecycleHook;

        private static void StartUiLifecycleWatcher()
        {
            lock (uiLifecycleLock)
            {
                if (uiLifecycleThread != null) return;
                uiLifecycleReady.Reset();
                uiLifecycleThread = new Thread(RunUiLifecycleWatcher);
                uiLifecycleThread.Name = "CMO Chinese UI lifecycle";
                uiLifecycleThread.IsBackground = true;
                uiLifecycleThread.SetApartmentState(ApartmentState.STA);
                AppDomain.CurrentDomain.ProcessExit += OnUiLifecycleProcessExit;
                uiLifecycleThread.Start();
            }
        }

        private static void RunUiLifecycleWatcher()
        {
            try
            {
                // Create the message queue before publishing the thread ID, so
                // shutdown can always post WM_QUIT once startup is complete.
                UiLifecycleNative.Message message;
                UiLifecycleNative.PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                uiLifecycleThreadId = UiLifecycleNative.GetCurrentThreadId();
                uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                uiLifecycleHook = UiLifecycleNative.SetWinEventHook(
                    UiLifecycleNative.EventObjectCreate, UiLifecycleNative.EventObjectShow,
                    IntPtr.Zero, uiLifecycleCallback, pid, 0, 0);
                if (uiLifecycleHook == IntPtr.Zero)
                {
                    Log("UI lifecycle WinEvent hook unavailable: " + Marshal.GetLastWin32Error()
                        + "; periodic UI scan remains active.");
                    return;
                }

                // Cover a window created while the dedicated hook thread was
                // starting. Native callbacks themselves never touch UI state.
                EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
                {
                    uint owner;
                    GetWindowThreadProcessId(hwnd, out owner);
                    if (owner == pid) QueueUiLifecycleWindow(hwnd);
                    return true;
                }, IntPtr.Zero);
                uiLifecycleReady.Set();
                Log("UI lifecycle watcher installed (same-process CREATE/SHOW + control events)");
                int result;
                while ((result = UiLifecycleNative.GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
                {
                    if (message.message == UiLifecycleNative.TranslateWpfMessage)
                    {
                        lock (uiLifecyclePendingWpf) { uiLifecyclePendingWpf.Clear(); }
                        TranslateWpfWindows();
                        continue;
                    }
                    UiLifecycleNative.TranslateMessage(ref message);
                    UiLifecycleNative.DispatchMessage(ref message);
                }
                if (result < 0) Log("UI lifecycle message loop failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log("UI lifecycle watcher failed: " + ex);
            }
            finally
            {
                if (uiLifecycleHook != IntPtr.Zero)
                {
                    UiLifecycleNative.UnhookWinEvent(uiLifecycleHook);
                    uiLifecycleHook = IntPtr.Zero;
                }
                uiLifecycleThreadId = 0;
                lock (uiLifecyclePending) { uiLifecyclePending.Clear(); }
                lock (uiLifecyclePendingWpf) { uiLifecyclePendingWpf.Clear(); }
                uiLifecycleReady.Set();
            }
        }

        private static void OnUiLifecycleWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
            int objectId, int childId, uint eventThread, uint eventTime)
        {
            if (eventType == UiLifecycleNative.EventObjectDestroy && objectId == 0 && childId == 0)
            {
                lock (uiLifecyclePending) { uiLifecyclePending.Remove(hwnd); }
                lock (uiLifecyclePendingWpf) { uiLifecyclePendingWpf.Remove(hwnd); }
                return;
            }
            // Accessibility creates many child objects unrelated to windows.
            // Subscribe only to root HWNDs; ControlAdded handles their children.
            if ((eventType != UiLifecycleNative.EventObjectCreate && eventType != UiLifecycleNative.EventObjectShow)
                || hwnd == IntPtr.Zero || objectId != 0 || childId != 0
                || UiLifecycleNative.GetAncestor(hwnd, 2) != hwnd) return;
            try { QueueUiLifecycleWindow(hwnd); }
            catch (Exception ex) { Log("UI lifecycle native callback failed: " + ex.Message); }
        }

        private static void QueueUiLifecycleWindow(IntPtr hwnd)
        {
            Control control = Control.FromHandle(hwnd);
            if (control == null)
            {
                System.Text.StringBuilder className = new System.Text.StringBuilder(128);
                UiLifecycleNative.GetClassName(hwnd, className, className.Capacity);
                if (className.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal))
                {
                    lock (uiLifecyclePendingWpf)
                    {
                        if (uiLifecyclePendingWpf.Add(hwnd) && uiLifecyclePendingWpf.Count == 1)
                            UiLifecycleNative.PostThreadMessage(uiLifecycleThreadId,
                                UiLifecycleNative.TranslateWpfMessage, UIntPtr.Zero, IntPtr.Zero);
                    }
                }
                return;
            }
            // CREATE can precede NativeWindow's managed association. SHOW and
            // the existing periodic scan cover that case without a timer retry.
            if (control == null || control.IsDisposed || !control.IsHandleCreated) return;
            lock (uiLifecyclePending)
            {
                if (uiLifecyclePending.ContainsKey(hwnd)) return;
                uiLifecyclePending.Add(hwnd, control);
            }
            try
            {
                control.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (!control.IsDisposed && control.IsHandleCreated && control.Handle == hwnd)
                            QueueUiTranslation(control, true);
                    }
                    catch (Exception ex) { Log("UI lifecycle first translation failed: " + ex.Message); }
                    finally
                    {
                        RemoveUiLifecyclePending(hwnd, control);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                RemoveUiLifecyclePending(hwnd, control);
            }
        }

        private static void RemoveUiLifecyclePending(IntPtr hwnd, Control control)
        {
            lock (uiLifecyclePending)
            {
                Control pending;
                if (uiLifecyclePending.TryGetValue(hwnd, out pending) && object.ReferenceEquals(pending, control))
                    uiLifecyclePending.Remove(hwnd);
            }
        }

        private static void AttachUiLifecycle(Control control)
        {
            if (control == null || control.IsDisposed) return;
            UiLifecycleAttachment attached;
            if (uiLifecycleAttached.TryGetValue(control, out attached)) return;
            uiLifecycleAttached.Add(control, new UiLifecycleAttachment());
            control.ControlAdded += OnUiLifecycleControlAdded;
            control.ParentChanged += OnUiLifecycleParentChanged;
            control.VisibleChanged += OnUiLifecycleVisibleChanged;
            control.HandleCreated += OnUiLifecycleReady;
            control.HandleDestroyed += OnUiLifecycleHandleDestroyed;
            control.Disposed += OnUiLifecycleDisposed;
            Form form = control as Form;
            if (form != null) form.Load += OnUiLifecycleReady;
        }

        private static void OnUiLifecycleControlAdded(object sender, ControlEventArgs e)
        {
            // Runs on the owning UI thread, before the newly added subtree
            // needs to paint. Reuse the input/token protections of the engine.
            TranslateUiLifecycleControl(e.Control);
        }

        private static void OnUiLifecycleVisibleChanged(object sender, EventArgs e)
        {
            Control control = sender as Control;
            if (control != null && control.Visible) TranslateUiLifecycleControl(control);
        }

        private static void OnUiLifecycleParentChanged(object sender, EventArgs e)
        {
            Control control = sender as Control;
            // Moving into a new tree renews queued ownership, even when the new
            // parent has not been discovered. Removal alone must not do so.
            if (control != null && control.Parent != null) TranslateUiLifecycleControl(control);
        }

        private static void OnUiLifecycleReady(object sender, EventArgs e)
        {
            TranslateUiLifecycleControl(sender as Control);
        }

        private static void OnUiLifecycleHandleDestroyed(object sender, EventArgs e)
        {
            if (uiTranslationQueue != null) uiTranslationQueue.OwnerUnavailable(sender as Control, false);
        }

        private static void TranslateUiLifecycleControl(Control control)
        {
            if (control == null || control.IsDisposed) return;
            try { QueueUiTranslation(control, true); }
            catch (Exception ex) { Log("UI lifecycle control translation failed: " + ex.Message); }
        }

        private static void OnUiLifecycleDisposed(object sender, EventArgs e)
        {
            Control control = sender as Control;
            if (control == null) return;
            control.ControlAdded -= OnUiLifecycleControlAdded;
            control.ParentChanged -= OnUiLifecycleParentChanged;
            control.VisibleChanged -= OnUiLifecycleVisibleChanged;
            control.HandleCreated -= OnUiLifecycleReady;
            control.HandleDestroyed -= OnUiLifecycleHandleDestroyed;
            control.Disposed -= OnUiLifecycleDisposed;
            Form form = control as Form;
            if (form != null) form.Load -= OnUiLifecycleReady;
            uiLifecycleAttached.Remove(control);
            if (uiTranslationQueue != null) uiTranslationQueue.OwnerUnavailable(control, true);
        }

        private static void OnUiLifecycleProcessExit(object sender, EventArgs e)
        {
            StopUiLifecycleWatcher();
        }

        private static void StopUiLifecycleWatcher()
        {
            Thread thread;
            lock (uiLifecycleLock) { thread = uiLifecycleThread; }
            if (thread == null) return;
            if (uiLifecycleReady.WaitOne(2000))
            {
                uint threadId = uiLifecycleThreadId;
                if (threadId != 0) UiLifecycleNative.PostThreadMessage(threadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
            }
            if (thread != Thread.CurrentThread && !thread.Join(2000))
            {
                Log("UI lifecycle watcher did not stop within its shutdown budget.");
                return;
            }
            lock (uiLifecycleLock)
            {
                if (uiLifecycleThread == thread) uiLifecycleThread = null;
                AppDomain.CurrentDomain.ProcessExit -= OnUiLifecycleProcessExit;
            }
        }

        private static class UiLifecycleNative
        {
            internal const uint EventObjectCreate = 0x8000;
            internal const uint EventObjectDestroy = 0x8001;
            internal const uint EventObjectShow = 0x8002;
            internal const uint TranslateWpfMessage = 0x8001;
            internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
                int objectId, int childId, uint eventThread, uint eventTime);

            [StructLayout(LayoutKind.Sequential)]
            internal struct Message
            {
                internal IntPtr hwnd;
                internal uint message;
                internal UIntPtr wParam;
                internal IntPtr lParam;
                internal uint time;
                internal int x;
                internal int y;
                internal uint privateValue;
            }

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
                WinEventProc callback, uint processId, uint threadId, uint flags);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool UnhookWinEvent(IntPtr hook);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int count);
            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentThreadId();
            [DllImport("user32.dll")]
            internal static extern int GetMessage(out Message message, IntPtr hwnd, uint min, uint max);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool TranslateMessage(ref Message message);
            [DllImport("user32.dll")]
            internal static extern IntPtr DispatchMessage(ref Message message);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
        }
    }
}
