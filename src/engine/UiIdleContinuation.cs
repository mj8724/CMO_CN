using System;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        // Created, armed and canceled on the owning UI thread. Idle runs only
        // after queued input/paint; the timer also supports modal/DoEvents hosts.
        private sealed class UiIdleContinuation : IDisposable
        {
            private readonly Action action;
            private readonly Timer fallback;
            private EventHandler idle;
            private Control owner;
            private bool pending;
            private bool running;
            private bool disposed;
            private int generation;

            internal UiIdleContinuation(Action action)
            {
                this.action = action;
                fallback = new Timer { Interval = 1 };
                fallback.Tick += OnTimer;
            }

            internal void Schedule(Control control)
            {
                if (disposed || pending) return;
                owner = control;
                pending = true;
                int armedGeneration = ++generation;
                idle = delegate { if (armedGeneration == generation) Run(); };
                Application.Idle += idle;
                fallback.Start();
                // Wake the pump, but never drain work in a marshaled callback:
                // BeginInvoke can consume newly posted callbacks in one message.
                try { control.BeginInvoke((MethodInvoker)delegate { }); }
                catch (InvalidOperationException) { }
            }

            internal void Cancel()
            {
                generation++;
                pending = false;
                owner = null;
                if (idle != null) { Application.Idle -= idle; idle = null; }
                fallback.Stop();
            }

            private void OnTimer(object sender, EventArgs args) { Run(); }

            private void Run()
            {
                if (!pending || running) return;
                Control current = owner;
                Cancel();
                if (current.IsDisposed || !current.IsHandleCreated) return;
                running = true;
                try { action(); }
                finally { running = false; }
            }

            public void Dispose()
            {
                if (disposed) return;
                Cancel();
                disposed = true;
                fallback.Tick -= OnTimer;
                fallback.Dispose();
            }
        }
    }
}
