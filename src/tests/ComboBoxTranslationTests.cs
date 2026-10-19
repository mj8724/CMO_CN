using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Windows.Forms;

// Independent STA controls only. Does not initialize an AppDomain or load the game.
class ComboBoxTranslationTests
{
    private static Type engine;
    private static int checks;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Action<Control> RaiseLostFocus = CreateLostFocusEvent();

    private static Action<Control> CreateLostFocusEvent()
    {
        // ComboBox's native-focus override ignores artificial hidden focus.
        // Raise Control's event directly, without acquiring real input focus.
        DynamicMethod method = new DynamicMethod("FixtureLostFocus", typeof(void), new Type[] { typeof(Control) }, typeof(ComboBoxTranslationTests), true);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, typeof(EventArgs).GetField("Empty"));
        il.Emit(OpCodes.Call, typeof(Control).GetMethod("OnLostFocus", BindingFlags.Instance | BindingFlags.NonPublic));
        il.Emit(OpCodes.Ret);
        return (Action<Control>)method.CreateDelegate(typeof(Action<Control>));
    }

    private class FixtureCombo : ComboBox
    {
        internal bool InputFocused;
        internal bool PopupOpen;
        internal int RedrawMessages;
        public override bool Focused { get { return InputFocused; } }
        internal void SetInputFocus(bool value)
        {
            bool lost = InputFocused && !value;
            InputFocused = value;
            if (lost) RaiseLostFocus(this);
        }
        internal void SetPopup(bool value)
        {
            PopupOpen = value;
            if (!value) OnDropDownClosed(EventArgs.Empty);
        }
        internal void CommitSelection() { OnSelectionChangeCommitted(EventArgs.Empty); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x000B) RedrawMessages++; // WM_SETREDRAW
            if (message.Msg == 0x0157) // CB_GETDROPPEDSTATE: no real popup is shown.
            {
                message.Result = PopupOpen ? new IntPtr(1) : IntPtr.Zero;
                return;
            }
            base.WndProc(ref message);
        }
    }

    private sealed class RecreatedCombo : FixtureCombo
    {
        internal void Recreate() { RecreateHandle(); }
    }

    private static object Call(string method, params object[] args)
    {
        MethodInfo found = engine.GetMethod(method, PrivateStatic);
        return found == null ? null : found.Invoke(null, args);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static void Pump(int milliseconds)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do { Application.DoEvents(); Thread.Sleep(1); } while (elapsed.ElapsedMilliseconds < milliseconds);
    }

    private static double WaitUntil(Func<bool> done)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!done() && elapsed.ElapsedMilliseconds < 350) Pump(1);
        return elapsed.Elapsed.TotalMilliseconds;
    }

    private static Form Window(ComboBox combo, out TextBox editor)
    {
        Form form = new Form();
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-1800, -1800);
        form.Size = new Size(320, 200);
        editor = new TextBox();
        editor.Top = 80;
        form.Controls.Add(editor);
        combo.Width = 280;
        form.Controls.Add(combo);
        IntPtr formHandle = form.Handle;
        IntPtr comboHandle = combo.Handle;
        Require(!form.Visible && !combo.Focused, "Fixture must stay invisible and inactive");
        Pump(10);
        Call("HookControlTextChanged", combo);
        // Also supports testing the independent partial before main integration.
        Call("AttachComboTranslation", combo);
        Pump(10);
        return form;
    }

    private static void Refill()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                int textEvents = 0, selectionEvents = 0;
                combo.TextChanged += delegate { textEvents++; };
                combo.SelectedIndexChanged += delegate { selectionEvents++; };
                for (int i = 0; i < 60; i++) combo.Items.Add("Combo fixture " + i);
                Require(combo.SelectedIndex == -1, "Fixture unexpectedly selected an item");
                Require(textEvents == 0 && selectionEvents == 0, "Refill unexpectedly raised the existing managed hooks");
                double latency = WaitUntil(delegate { return (string)combo.Items[59] == "组合条目59"; });
                Require((string)combo.Items[59] == "组合条目59", "Native Items refill remained English without a managed text/selection event");
                Require(combo.SelectedIndex == -1, "Translation changed the unselected index");
                combo.Items.Insert(5, "Combo fixture 60");
                combo.Items.RemoveAt(1);
                Pump(30);
                Require((string)combo.Items[4] == "组合条目60", "Insert/delete refill was missed");
                combo.Items.Clear();
                combo.Items.Add("Combo fixture 61");
                Pump(30);
                Require(combo.Items.Count == 1 && (string)combo.Items[0] == "组合条目61", "Reset/refill was missed");
                Console.WriteLine("REFILL_DISPATCH_MS=" + latency.ToString("F3"));
            }
        }
    }

    private static void Protection()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ((FixtureCombo)combo).SetInputFocus(true);
                Require(combo.Focused, "Synthetic editable-focus state was not set");
                combo.Text = "typed text";
                combo.Items.Add("Combo fixture 0");
                Call("LocalizeComboBoxSafe", combo);
                Pump(30);
                Require((string)combo.Items[0] == "Combo fixture 0" && combo.Text == "typed text", "Focused editable input was rewritten");
                int lostEvents = 0;
                combo.LostFocus += delegate { lostEvents++; };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(30);
                Require((string)combo.Items[0] == "组合条目0", "LostFocus did not flush deferred input work: "
                    + combo.Items[0] + " focused=" + combo.Focused + " dropped=" + combo.DroppedDown + " lostEvents=" + lostEvents
                    + " handle=" + combo.IsHandleCreated);
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.Clear();
                combo.Items.Add("Combo fixture 1");
                Call("LocalizeComboBoxSafe", combo);
                Require((string)combo.Items[0] == "Combo fixture 1", "Synchronous path relaxed focused-input protection");
                Pump(30);
                Require((string)combo.Items[0] == "组合条目1", "Queued read-only combo work remained blocked by focus");
                ((FixtureCombo)combo).SetPopup(true);
                combo.Items.Add("Combo fixture 2");
                Pump(30);
                Require((string)combo.Items[1] == "Combo fixture 2", "Open dropdown was modified");
                ((FixtureCombo)combo).SetPopup(false);
                Pump(30);
                Require((string)combo.Items[1] == "组合条目2", "DropDownClosed did not flush deferred work");
            }
        }
    }

    private static void Lifecycle()
    {
        using (RecreatedCombo combo = new RecreatedCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                combo.Items.Add("Combo fixture 3");
                combo.Recreate();
                combo.Items.Add("Combo fixture 4");
                Pump(40);
                Require((string)combo.Items[0] == "组合条目3" && (string)combo.Items[1] == "组合条目4", "Handle recreation lost queued items");
                Call("AttachComboTranslation", combo);
                combo.Items.Add("Combo fixture 5");
                combo.Dispose();
                Pump(30);
                FieldInfo tableField = engine.GetField("comboTranslations", PrivateStatic);
                Require(tableField != null, "Combo attachment table is missing");
                object table = tableField.GetValue(null);
                object[] lookup = { combo, null };
                Require(!(bool)table.GetType().GetMethod("TryGetValue").Invoke(table, lookup), "Disposed combo attachment was retained");
            }
        }
    }

    private static void ReentrantRefill()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.AddRange(new object[] { "Combo fixture 0", "Combo fixture 1", "Combo fixture 2" });
                combo.SelectedIndex = 0;
                bool rebuilt = false;
                combo.SelectedIndexChanged += delegate
                {
                    if (rebuilt) return;
                    rebuilt = true;
                    // A game handler may refill the list while a translated
                    // selected item is being assigned by the localizer.
                    combo.Items.Clear();
                    combo.Items.AddRange(new object[] { "Replacement alpha", "Replacement beta", "Replacement gamma" });
                };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(40);
                Require(rebuilt, "Fixture did not trigger the game's selection/refill callback");
                Require(combo.Items.Count == 3 && (string)combo.Items[0] == "Replacement alpha"
                    && (string)combo.Items[1] == "Replacement beta" && (string)combo.Items[2] == "Replacement gamma",
                    "An old translation plan overwrote the game's replacement items");
            }
        }
    }

    private static void QueuedRetry()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                Require(object.Equals(Call("TryBeginComboTranslation", combo), true), "Retry fixture could not enter gate");
                combo.Items.Add("Combo fixture 10");
                Call("QueueComboTranslation", combo);
                Call("EndComboTranslation", combo);
                Pump(30);
                Require((string)combo.Items[0] == "组合条目10", "End discarded a retry queued by a game refill");
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.Clear();
                combo.Items.AddRange(new object[] { "Combo fixture 0", "Combo fixture 1", "Combo fixture 2" });
                combo.SelectedIndex = 0;
                bool rebuilt = false;
                combo.SelectedIndexChanged += delegate
                {
                    if (rebuilt) return;
                    rebuilt = true;
                    combo.Items.Clear();
                    combo.Items.AddRange(new object[] { "Combo fixture 10", "Combo fixture 11", "Combo fixture 12" });
                };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(40);
                Require(rebuilt, "Retry fixture did not rebuild the selected items");
                Require(combo.Items.Count == 3 && (string)combo.Items[0] == "组合条目10"
                    && (string)combo.Items[1] == "组合条目11" && (string)combo.Items[2] == "组合条目12",
                    "A game refill inside translation was not reconciled by the deferred retry");
            }
        }
    }

    private static void SingleItemRetry()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.Add("Combo fixture 0");
                combo.SelectedIndex = 0;
                bool rebuilt = false;
                combo.SelectedIndexChanged += delegate
                {
                    if (rebuilt) return;
                    rebuilt = true;
                    combo.Items.Clear();
                    combo.Items.Add("Combo fixture 10");
                };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(40);
                Require(rebuilt, "Single-item fixture did not rebuild the list");
                Require(combo.Items.Count == 1 && (string)combo.Items[0] == "组合条目10",
                    "The last planned write lost a game-handler reset/refill retry");
            }
        }
    }

    private static void RecreatedHandleRetry(bool recreate)
    {
        using (RecreatedCombo combo = new RecreatedCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.Add("Combo fixture 0");
                combo.SelectedIndex = 0;
                bool rebuilt = false;
                combo.SelectedIndexChanged += delegate
                {
                    if (rebuilt) return;
                    rebuilt = true;
                    if (recreate) combo.Recreate();
                    combo.Items.Add("Combo fixture 10");
                };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(40);
                Require(rebuilt, "Append fixture did not run the selection callback");
                Require(combo.Items.Count == 2 && (string)combo.Items[1] == "组合条目10",
                    recreate ? "Handle recreation during the last write lost new-item translation"
                        : "A game-handler append during the last write lost new-item translation");
            }
        }
    }

    private static void BindingRetryBeforeHandle()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            int bindingEvents = 0;
            combo.DisplayMemberChanged += delegate { bindingEvents++; };
            Call("AttachComboTranslation", combo);
            Require(object.Equals(Call("TryBeginComboTranslation", combo), true), "Binding fixture could not enter gate");
            combo.DisplayMember = "Display";
            Require(bindingEvents > 0, "No-handle binding fixture did not raise DisplayMemberChanged");
            Call("EndComboTranslation", combo);
            object table = engine.GetField("comboTranslations", PrivateStatic).GetValue(null);
            object[] lookup = { combo, null };
            Require((bool)table.GetType().GetMethod("TryGetValue").Invoke(table, lookup), "Binding attachment is missing");
            Require((bool)lookup[1].GetType().GetField("Dirty", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(lookup[1]),
                "A display-member change during translation discarded pending work before handle creation");
        }
    }

    private static void Gate()
    {
        using (ComboBox first = new FixtureCombo())
        using (ComboBox second = new FixtureCombo())
        {
            Call("AttachComboTranslation", first);
            Call("AttachComboTranslation", second);
            Require(object.Equals(Call("TryBeginComboTranslation", first), true), "First gate entry failed");
            Require(object.Equals(Call("TryBeginComboTranslation", first), false), "Reentrant combo write was accepted");
            Require(object.Equals(Call("TryBeginComboTranslation", second), true), "One combo blocked another combo");
            Call("EndComboTranslation", first);
            Call("EndComboTranslation", second);
            Require(object.Equals(Call("TryBeginComboTranslation", first), true), "Gate did not recover after End");
            Call("EndComboTranslation", first);
        }
    }

    private static void BoundValues()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            using (DataTable data = new DataTable())
            {
                data.Columns.Add("Display", typeof(string));
                data.Columns.Add("Key", typeof(int));
                data.Rows.Add("Combo fixture 64", 10);
                data.Rows.Add("Combo fixture 65", 20);
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.DisplayMember = "Display";
                combo.ValueMember = "Key";
                combo.DataSource = data;
                combo.SelectedValue = 20;
                int formats = 0;
                combo.FormattingEnabled = true;
                combo.Format += delegate { formats++; };
                ((FixtureCombo)combo).SetInputFocus(false);
                Pump(40);
                Require((string)data.Rows[0]["Display"] == "组合条目64"
                    && (string)data.Rows[1]["Display"] == "组合条目65", "Bound display values were not translated");
                Require(object.ReferenceEquals(combo.DataSource, data) && (int)combo.SelectedValue == 20
                    && (int)data.Rows[0]["Key"] == 10 && (int)data.Rows[1]["Key"] == 20,
                    "Bound localization changed the source, selected value, or keys");
                Require(formats <= 40, "A bound refresh did not settle after its retry: " + formats);
                Console.WriteLine("BOUND_ITEMS=2 FORMAT_CALLS_WITH_REFRESH=" + formats);
            }
        }
    }

    private static void Benchmark()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                int formats = 0;
                combo.FormattingEnabled = true;
                combo.Format += delegate { formats++; };
                for (int i = 0; i < 60; i++) combo.Items.Add("Combo fixture " + i);
                int before = formats;
                Pump(30);
                int flush = formats - before;
                Require((string)combo.Items[59] == "组合条目59", "Benchmark refill did not translate");
                Require(flush <= 240, "Burst refill rescanned the entire item list per item: " + flush);
                int after = formats;
                for (int i = 0; i < 40; i++) { combo.Invalidate(); combo.Update(); }
                Pump(20);
                int paints = formats - after;
                Require(paints == 0, "Paint caused extra item-list translation scans: " + paints);
                Console.WriteLine("BULK_ITEMS=60 FLUSH_FORMAT_CALLS=" + flush + " REPAINT_FORMAT_CALLS=" + paints);
            }
        }
    }

    private static void RefillFight()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ((FixtureCombo)combo).SetInputFocus(true);
                combo.Items.AddRange(new object[] { "Combo fixture 0", "Combo fixture 1", "Combo fixture 2" });
                combo.SelectedIndex = 0;
                bool inside = false;
                int stopped = 0, refills = 0;
                combo.SelectedIndexChanged += delegate
                {
                    if (Thread.VolatileRead(ref stopped) != 0 || inside) return;
                    inside = true;
                    try
                    {
                        refills++;
                        combo.Items.Clear();
                        combo.Items.AddRange(new object[] { "Combo fixture 0", "Combo fixture 1", "Combo fixture 2" });
                        combo.SelectedIndex = 0;
                    }
                    finally { inside = false; }
                };
                Thread stop = new Thread(delegate() { Thread.Sleep(400); Interlocked.Exchange(ref stopped, 1); });
                stop.IsBackground = true;
                stop.Start();
                TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime;
                ((FixtureCombo)combo).SetInputFocus(false);
                // Lifecycle and periodic scans in the same cycle must share
                // the existing chain's budget rather than replenish it.
                for (int i = 0; i < 20; i++)
                {
                    Call("AttachComboTranslation", combo);
                    Call("LocalizeComboBoxCore", combo);
                    Pump(1);
                }
                Pump(450);
                stop.Join();
                Console.WriteLine("FIGHT_REFILLS=" + refills + " CPU_MS="
                    + (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds.ToString("F3"));
                Require(refills > 0 && refills <= 3, "A selection/refill callback created an unbounded asynchronous retry chain");
                combo.Items.Clear();
                combo.Items.Add("Combo fixture 10");
                Pump(30);
                Require((string)combo.Items[0] == "组合条目10", "A fresh external refill did not recover after the retry limit");
            }
        }
    }

    private static void NoopRepaint()
    {
        using (FixtureCombo combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                combo.Items.AddRange(new object[] { "组合条目", "Unknown unmatched fixture" });
                Pump(30);
                int invalidations = 0;
                combo.Invalidated += delegate { invalidations++; };
                combo.RedrawMessages = 0;
                for (int i = 0; i < 40; i++) Call("LocalizeComboBoxSafe", combo);
                Require(combo.RedrawMessages == 0 && invalidations == 0,
                    "An empty translation plan changed redraw state: messages=" + combo.RedrawMessages + " invalidations=" + invalidations);
                Console.WriteLine("NOOP_SCANS=40 SETREDRAW=0 INVALIDATIONS=0");
                using (DataTable data = new DataTable())
                {
                    data.Columns.Add("Display", typeof(string));
                    data.Columns.Add("Key", typeof(int));
                    data.Rows.Add("已译内容", 42);
                    combo.DisplayMember = "Display";
                    combo.ValueMember = "Key";
                    combo.DataSource = data;
                    Pump(30);
                    combo.RedrawMessages = invalidations = 0;
                    for (int i = 0; i < 40; i++) Call("LocalizeComboBoxSafe", combo);
                    Require(combo.RedrawMessages == 0 && invalidations == 0, "An empty bound plan changed redraw state");
                }
            }
        }
    }

    private static void ExhaustRetries(ComboBox combo)
    {
        for (int i = 0; i < 3; i++)
        {
            Require(object.Equals(Call("TryBeginComboTranslation", combo), true), "Retry budget ended too soon");
            Call("QueueComboTranslation", combo);
            Call("EndComboTranslation", combo);
        }
        Require(object.Equals(Call("TryBeginComboTranslation", combo), false), "Synchronous scan bypassed exhausted retry cooldown");
    }

    private static void Recovery()
    {
        using (FixtureCombo combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            {
                ExhaustRetries(combo);
                Pump(10);
                combo.Items.Add("Combo fixture 10");
                Pump(20);
                Require((string)combo.Items[0] == "组合条目10", "External item change did not clear the cooldown");
                combo.SetInputFocus(true);
                combo.Items[0] = "Combo fixture 11";
                ExhaustRetries(combo);
                combo.SetInputFocus(false);
                combo.CommitSelection();
                Pump(20);
                Require((string)combo.Items[0] == "组合条目11", "User selection commit did not clear the cooldown");
                ExhaustRetries(combo);
                using (DataTable data = new DataTable())
                {
                    data.Columns.Add("Display", typeof(string));
                    data.Columns.Add("Key", typeof(int));
                    data.Rows.Add("Combo fixture 12", 42);
                    combo.DisplayMember = "Display";
                    combo.ValueMember = "Key";
                    combo.DataSource = data;
                    Pump(20);
                    Require((string)data.Rows[0]["Display"] == "组合条目12" && (int)data.Rows[0]["Key"] == 42,
                        "Binding change did not recover safely after the cooldown");
                }
            }
        }
        using (FixtureCombo combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            using (DataTable data = new DataTable())
            {
                data.Columns.Add("Token", typeof(string));
                data.Rows.Add("Combo fixture 12");
                combo.DisplayMember = combo.ValueMember = "Token";
                combo.DataSource = data;
                Pump(20);
                Require((string)data.Rows[0]["Token"] == "Combo fixture 12", "DisplayMember alias rewrote the binding key");
            }
        }
    }

    private static void BindingTableSwap()
    {
        using (FixtureCombo combo = new FixtureCombo())
        {
            TextBox editor;
            using (Form form = Window(combo, out editor))
            using (DataTable first = new DataTable())
            using (DataTable second = new DataTable())
            using (BindingSource source = new BindingSource())
            {
                foreach (DataTable table in new DataTable[] { first, second })
                {
                    table.Columns.Add("Display", typeof(string));
                    table.Columns.Add("Key", typeof(int));
                }
                first.Rows.Add("Combo fixture 0", 1);
                first.Rows.Add("Combo fixture 1", 2);
                second.Rows.Add("Combo fixture 10", 10);
                second.Rows.Add("Combo fixture 11", 20);
                bool swapped = false;
                first.ColumnChanged += delegate
                {
                    if (swapped) return;
                    swapped = true;
                    source.DataSource = second;
                };
                combo.SetInputFocus(true);
                source.DataSource = first;
                combo.DisplayMember = "Display";
                combo.ValueMember = "Key";
                combo.DataSource = source;
                combo.SetInputFocus(false);
                Pump(40);
                Require(swapped && object.ReferenceEquals(combo.DataSource, source), "BindingSource fixture did not swap its inner table");
                Require((string)first.Rows[1]["Display"] == "Combo fixture 1", "An obsolete table's remaining row was translated after rebinding");
                Require((string)second.Rows[0]["Display"] == "组合条目10" && (string)second.Rows[1]["Display"] == "组合条目11",
                    "The new BindingSource table was not translated by its deferred retry");
                Require((int)first.Rows[0]["Key"] == 1 && (int)first.Rows[1]["Key"] == 2
                    && (int)second.Rows[0]["Key"] == 10 && (int)second.Rows[1]["Key"] == 20,
                    "Rebinding changed a table's value keys");
            }
        }
    }

    private sealed class DisplayItem
    {
        public string Label { get; set; }
        public int Key { get; set; }
    }

    private static void ObjectIdentity()
    {
        using (ComboBox combo = new FixtureCombo())
        {
            DisplayItem item = new DisplayItem { Label = "Combo fixture 0", Key = 42 };
            combo.DisplayMember = "Label";
            combo.ValueMember = "Key";
            combo.Items.Add(item);
            combo.Items.Add("Combo fixture 1");
            combo.SelectedIndex = 0;
            Call("LocalizeComboBoxSafe", combo);
            Require(object.ReferenceEquals(combo.Items[0], item) && object.ReferenceEquals(combo.SelectedItem, item),
                "Unbound display item or selected object identity was replaced");
            Require(item.Label == "Combo fixture 0" && item.Key == 42, "Unbound business object was mutated");
            Require((string)combo.Items[1] == "组合条目1", "A neighboring string item stopped translating");
        }
        using (ComboBox combo = new FixtureCombo { FormattingEnabled = true, DisplayMember = "Label" })
        {
            DisplayItem item = new DisplayItem { Label = "Combo fixture 0", Key = 43 };
            combo.Items.Add("Combo fixture 0");
            int formats = 0;
            combo.Format += delegate(object sender, ListControlConvertEventArgs args)
            {
                if (++formats == 2)
                {
                    combo.Items[0] = item;
                    args.Value = item.Label;
                }
            };
            Call("LocalizeComboBoxSafe", combo);
            Require(formats >= 2, "Format callback did not run during source revalidation");
            Require(object.ReferenceEquals(combo.Items[0], item), "A Format callback's replacement business object was overwritten");
        }
        using (ComboBox combo = new FixtureCombo { FormattingEnabled = true })
        {
            combo.Items.Add("business-ready-key");
            combo.Format += delegate(object sender, ListControlConvertEventArgs args)
            {
                if (object.Equals(args.ListItem, "business-ready-key")) args.Value = "Combo fixture 0";
            };
            combo.SelectedIndex = 0;
            Call("LocalizeComboBoxSafe", combo);
            Require((string)combo.Items[0] == "business-ready-key" && (string)combo.SelectedItem == "business-ready-key",
                "A formatted display label replaced its underlying string token");
            Require(combo.GetItemText(combo.Items[0]) == "Combo fixture 0", "The business Format mapping stopped working");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: ComboBoxTranslationTests <engine.dll> <mode>");
            engine = Assembly.LoadFrom(System.IO.Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            IDictionary exact = (IDictionary)engine.GetField("exactMap", PrivateStatic).GetValue(null);
            for (int i = 0; i < 70; i++) exact["Combo fixture " + i] = "组合条目" + i;
            Control.CheckForIllegalCrossThreadCalls = true;
            if (args[1] == "refill") Refill();
            else if (args[1] == "protection") Protection();
            else if (args[1] == "lifecycle") Lifecycle();
            else if (args[1] == "reentrant-refill") ReentrantRefill();
            else if (args[1] == "queued-retry") { BindingRetryBeforeHandle(); QueuedRetry(); SingleItemRetry(); RecreatedHandleRetry(true); }
            else if (args[1] == "append-retry") RecreatedHandleRetry(false);
            else if (args[1] == "gate") Gate();
            else if (args[1] == "bound") BoundValues();
            else if (args[1] == "benchmark") Benchmark();
            else if (args[1] == "fight") RefillFight();
            else if (args[1] == "noop") NoopRepaint();
            else if (args[1] == "recovery") Recovery();
            else if (args[1] == "binding-swap") BindingTableSwap();
            else if (args[1] == "object-identity") ObjectIdentity();
            else throw new ArgumentException("Unknown mode: " + args[1]);
            Console.WriteLine("PASS " + args[1] + " checks=" + checks);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex); return 1; }
    }
}
