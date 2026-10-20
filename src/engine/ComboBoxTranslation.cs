using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly ConditionalWeakTable<ComboBox, ComboTranslationState> comboTranslations =
            new ConditionalWeakTable<ComboBox, ComboTranslationState>();

        private static ComboTranslationState AttachComboTranslation(ComboBox combo)
        {
            if (combo == null || combo.IsDisposed) return null;
            return comboTranslations.GetValue(combo, delegate(ComboBox owner) { return new ComboTranslationState(owner); });
        }

        private static bool TryBeginComboTranslation(ComboBox combo)
        {
            ComboTranslationState state = AttachComboTranslation(combo);
            return state != null && state.TryBegin();
        }

        private static void EndComboTranslation(ComboBox combo)
        {
            ComboTranslationState state;
            if (!comboTranslations.TryGetValue(combo, out state)) return;
            state.End();
        }

        private static void QueueComboTranslation(ComboBox combo)
        {
            ComboTranslationState state = AttachComboTranslation(combo);
            if (state != null) state.RequestRetry();
        }

        private static void BeginComboItemWrite(ComboBox combo, int index)
        {
            ComboTranslationState state;
            if (comboTranslations.TryGetValue(combo, out state)) state.BeginItemWrite(index);
        }

        private static void EndComboItemWrite(ComboBox combo)
        {
            ComboTranslationState state;
            if (comboTranslations.TryGetValue(combo, out state)) state.EndItemWrite();
        }

        // All state and native callbacks belong to the control's UI thread.
        // Native item messages only mark a burst; Items must never be inspected
        // while WinForms is still adding/deleting the corresponding item.
        private sealed class ComboTranslationState : NativeWindow
        {
            private const int AddString = 0x0143;
            private const int DeleteString = 0x0144;
            private const int InsertString = 0x014A;
            private const int ResetContent = 0x014B;
            private readonly ComboBox combo;
            private bool scheduled;
            private bool flushing;
            private bool disposed;
            private int generation;
            private const int MaximumRetries = 2;
            private int retriesLeft = MaximumRetries;
            private bool retriesExhausted;
            private int nativeDepth;
            private int ownWriteIndex = -1;
            private int ownMessageDepth;
            private bool ownDeleteExpected;
            private bool ownInsertExpected;
            internal bool Translating;
            internal bool Dirty;
            internal bool Retry;

            internal ComboTranslationState(ComboBox combo)
            {
                this.combo = combo;
                combo.HandleCreated += OnHandleCreated;
                combo.HandleDestroyed += OnHandleDestroyed;
                combo.Disposed += OnDisposed;
                combo.LostFocus += OnInputFinished;
                combo.DropDownClosed += OnInputFinished;
                combo.DataSourceChanged += OnDataChanged;
                combo.DisplayMemberChanged += OnDataChanged;
                combo.BindingContextChanged += OnDataChanged;
                combo.SelectionChangeCommitted += OnDataChanged;
                if (combo.IsHandleCreated) AssignHandle(combo.Handle);
                ExternalChange();
            }

            internal bool TryBegin()
            {
                if (disposed || Translating || retriesExhausted) return false;
                Translating = true;
                return true;
            }

            internal void End()
            {
                Translating = false;
                Dirty = Retry && retriesLeft > 0;
                if (Retry)
                {
                    if (retriesLeft > 0) retriesLeft--;
                    else retriesExhausted = true;
                }
                else retriesLeft = MaximumRetries;
                Retry = false;
                Schedule();
            }

            internal void BeginItemWrite(int index)
            {
                ownWriteIndex = index;
                ownMessageDepth = nativeDepth + 1;
                ownDeleteExpected = ownInsertExpected = true;
            }

            internal void EndItemWrite()
            {
                ownWriteIndex = -1;
                ownDeleteExpected = ownInsertExpected = false;
            }

            protected override void WndProc(ref Message message)
            {
                int id = message.Msg;
                if (id != AddString && id != InsertString && id != DeleteString && id != ResetContent)
                {
                    base.WndProc(ref message);
                    return;
                }
                bool own = false;
                nativeDepth++;
                try
                {
                    // Suppress only the setter's expected outer delete/insert.
                    // Nested game callbacks and their reset/add messages remain visible.
                    if (nativeDepth == ownMessageDepth && message.WParam.ToInt64() == ownWriteIndex)
                    {
                        if (id == DeleteString && ownDeleteExpected) { ownDeleteExpected = false; own = true; }
                        else if (id == InsertString && ownInsertExpected) { ownInsertExpected = false; own = true; }
                    }
                    base.WndProc(ref message);
                }
                finally { nativeDepth--; }
                if (!own) ExternalChange();
            }

            private void ExternalChange()
            {
                if (disposed) return;
                if (Translating || flushing) { RequestRetry(); return; }
                // Real external input starts a new chain. Repeated discovery and
                // synchronous scans never replenish an unfinished retry budget.
                retriesLeft = MaximumRetries;
                retriesExhausted = false;
                Dirty = true;
                Schedule();
            }

            internal void RequestRetry()
            {
                if (disposed) return;
                Dirty = true;
                if (Translating || flushing) Retry = true;
                else Schedule();
            }

            internal void Schedule()
            {
                if (disposed || !Dirty || scheduled || Translating || flushing || !combo.IsHandleCreated) return;
                scheduled = true;
                int queuedGeneration = generation;
                try
                {
                    combo.BeginInvoke((MethodInvoker)delegate
                    {
                        // An old callback must not clear the new handle's queue.
                        if (disposed || queuedGeneration != generation) return;
                        scheduled = false;
                        Flush();
                    });
                }
                catch (InvalidOperationException)
                {
                    // Handle destruction cancels dispatch; HandleCreated retries.
                    scheduled = false;
                }
            }

            private void Flush()
            {
                if (disposed || combo.IsDisposed || !Dirty || Translating || flushing) return;
                if (combo.DroppedDown || (combo.Focused && combo.DropDownStyle != ComboBoxStyle.DropDownList))
                    return; // Keep the work until LostFocus / DropDownClosed.
                Dirty = false;
                flushing = true;
                try
                {
                    // Focused read-only selectors cannot accept typed input.
                    // This queued path runs after the game's native item stack;
                    // the synchronous entry point retains its stricter guard.
                    if (combo.Focused && combo.DropDownStyle == ComboBoxStyle.DropDownList)
                    {
                        if (!IsProtectedDBViewerCombo(combo)) LocalizeComboBoxCore(combo);
                    }
                    else LocalizeComboBoxSafe(combo);
                }
                finally
                {
                    flushing = false;
                    Schedule();
                }
            }

            private void OnInputFinished(object sender, EventArgs args) { Schedule(); }
            private void OnDataChanged(object sender, EventArgs args) { ExternalChange(); }

            private void OnHandleCreated(object sender, EventArgs args)
            {
                if (disposed) return;
                AssignHandle(combo.Handle);
                ExternalChange();
            }

            private void OnHandleDestroyed(object sender, EventArgs args)
            {
                generation++;
                scheduled = false;
                Dirty = true;
                ReleaseHandle();
            }

            private void OnDisposed(object sender, EventArgs args)
            {
                disposed = true;
                generation++;
                scheduled = false;
                combo.HandleCreated -= OnHandleCreated;
                combo.HandleDestroyed -= OnHandleDestroyed;
                combo.Disposed -= OnDisposed;
                combo.LostFocus -= OnInputFinished;
                combo.DropDownClosed -= OnInputFinished;
                combo.DataSourceChanged -= OnDataChanged;
                combo.DisplayMemberChanged -= OnDataChanged;
                combo.BindingContextChanged -= OnDataChanged;
                combo.SelectionChangeCommitted -= OnDataChanged;
                ReleaseHandle();
                comboTranslations.Remove(combo);
            }
        }
    }
}
