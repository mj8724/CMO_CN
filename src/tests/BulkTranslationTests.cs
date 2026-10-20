using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Standalone synthetic WinForms contracts with the real dictionary and engine.
// Never initializes the domain manager or loads Command.exe. All logs stay next
// to this test executable. Counts verify traversal and business-event behavior.
public static class BulkTranslationTests
{
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static Type engine;
    private static Func<string, bool> forbidden;
    private static Action<Control> tree;
    private static Action<Control> hook;
    private static Action<ComboBox> safeCombo;
    private static Dictionary<string, string> exact;
    private static readonly List<object> results = new List<object>();
    private static readonly List<string> failures = new List<string>();

    private static void Check(bool value, string message)
    {
        if (!value) failures.Add(message);
    }

    private sealed class CountingLabel : Label
    {
        public int Reads;
        public int Writes;
        public override string Text
        {
            get { Reads++; return base.Text; }
            set { Writes++; base.Text = value; }
        }
    }

    private sealed class CountingCombo : ComboBox
    {
        public int Formats;
        public int Changes;
        public int Selections;
        public int Depth;
        public int MaxDepth;
        public int EventFormatReads;
        public CountingCombo()
        {
            FormattingEnabled = true;
            Format += delegate { Formats++; };
        }
        protected override void OnTextChanged(EventArgs e)
        {
            Changes++;
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
            int before = Formats;
            try { base.OnTextChanged(e); }
            finally { EventFormatReads += Formats - before; Depth--; }
        }
        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            Selections++;
            Depth++;
            MaxDepth = Math.Max(MaxDepth, Depth);
            int before = Formats;
            try { base.OnSelectedIndexChanged(e); }
            finally { EventFormatReads += Formats - before; Depth--; }
        }
        public void ResetCounts() { Formats = Changes = Selections = Depth = MaxDepth = EventFormatReads = 0; }
    }

    private static T Bind<T>(string name) where T : class
    {
        return Delegate.CreateDelegate(typeof(T), engine.GetMethod(name, Static)) as T;
    }

    private static T Field<T>(string name)
    {
        return (T)engine.GetField(name, Static).GetValue(null);
    }

    private static void Reset(string[] preseen)
    {
        Field<Dictionary<string, string>>("dynamicCache").Clear();
        HashSet<string> seen = Field<HashSet<string>>("liveAuditSeen");
        seen.Clear();
        foreach (string text in preseen) seen.Add(text);
    }

    private static bool Suitable(KeyValuePair<string, string> item)
    {
        string s = item.Key;
        return s != item.Value && s.Length > 3 && s.Length <= 240
            && s[0] >= 'A' && s[0] <= 'z' && s.IndexOf(' ') >= 0
            && s.IndexOfAny(new char[] { '\r', '\n', '\\', '<', '>', '{', '}', '[', ']', '\t' }) < 0
            && s.IndexOf("[LOADDOC]", StringComparison.OrdinalIgnoreCase) < 0
            && s.IndexOf("://", StringComparison.Ordinal) < 0
            && s.IndexOf(".scen", StringComparison.OrdinalIgnoreCase) < 0
            && s.All(c => c >= ' ' && c <= '~')
            && !forbidden(s)
            && item.Value.Any(c => c >= '\u4e00' && c <= '\u9fff');
    }

    private static string[] SelectExact(int min, int max, int count)
    {
        return exact.Where(Suitable).Where(p => p.Key.Length >= min && p.Key.Length <= max && p.Key == p.Key.Trim())
            .OrderBy(p => p.Key, StringComparer.Ordinal).Take(count).Select(p => p.Key).ToArray();
    }

    private static void ProfileTabs()
    {
        for (int depth = 0; depth <= 6; depth++)
            using (Panel root = new Panel())
            {
                Control parent = root;
                for (int i = 0; i < depth; i++)
                {
                    TabControl tabs = new TabControl();
                    TabPage page = new TabPage("Unknown tab fixture");
                    tabs.TabPages.Add(page);
                    parent.Controls.Add(tabs);
                    parent = page;
                }
                CountingLabel leaf = new CountingLabel { Text = "Unmapped fixture alpha tab leaf" };
                parent.Controls.Add(leaf);
                Reset(new string[] { "Unmapped fixture alpha tab leaf", "Unknown tab fixture" });
                for (int pass = 0; pass < 2; pass++)
                {
                    leaf.Reads = leaf.Writes = 0;
                    tree(root);
                    results.Add(new { kind = "tabs", depth = depth, phase = pass == 0 ? "cold" : "warm",
                        leafTextReads = leaf.Reads, leafTextWrites = leaf.Writes });
                    Check(leaf.Reads == 2, "Tab depth " + depth + " pass " + pass
                        + " visited leaf more than once: " + leaf.Reads + " Text reads, expected 2");
                }
            }
    }

    private static void ProfileCombos(string mode, int count, int items, bool selected, bool bound)
    {
        string[] options = SelectExact(5, 48, items);
        if (options.Length != items) throw new Exception("Not enough real dictionary options");
        Reset(options);
        using (Form form = new Form())
        {
            form.Text = "Bulk fixture";
            form.BindingContext = new BindingContext();
            form.SuspendLayout();
            TextBox input = new TextBox { Text = "MISSION" };
            RichTextBox richInput = new RichTextBox { Text = "SITUATION" };
            form.Controls.Add(input);
            form.Controls.Add(richInput);
            List<CountingCombo> combos = new List<CountingCombo>();
            int rowWrites = 0;
            for (int c = 0; c < count; c++)
            {
                CountingCombo cb = new CountingCombo { Name = "FixtureCombo" + c, DropDownStyle = ComboBoxStyle.DropDownList };
                form.Controls.Add(cb);
                if (bound)
                {
                    DataTable dt = new DataTable();
                    dt.Columns.Add("Label", typeof(string));
                    dt.Columns.Add("Code", typeof(int));
                    for (int i = 0; i < items; i++) dt.Rows.Add(options[i], i);
                    cb.DisplayMember = "Label";
                    cb.ValueMember = "Code";
                    cb.DataSource = dt;
                    dt.ColumnChanged += delegate { rowWrites++; };
                }
                else foreach (string value in options) cb.Items.Add(value);
                IntPtr unused = cb.Handle;
                cb.SelectedIndex = selected ? 0 : -1;
                if (mode == "safe-hooked") hook(cb);
                combos.Add(cb);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (CountingCombo cb in combos) cb.ResetCounts();
                rowWrites = 0;
                if (mode == "tree") tree(form);
                else foreach (CountingCombo cb in combos) safeCombo(cb);
                int translated = 0;
                bool keysIntact = true;
                bool selectionIntact = true;
                foreach (CountingCombo cb in combos)
                {
                    if (cb.SelectedIndex != (selected ? 0 : -1)) selectionIntact = false;
                    if (bound)
                    {
                        DataTable dt = (DataTable)cb.DataSource;
                        for (int i = 0; i < dt.Rows.Count; i++)
                        {
                            if ((string)dt.Rows[i]["Label"] == exact[options[i]]) translated++;
                            if ((int)dt.Rows[i]["Code"] != i) keysIntact = false;
                        }
                    }
                    else for (int i = 0; i < cb.Items.Count; i++)
                        if ((string)cb.Items[i] == exact[options[i]]) translated++;
                }
                results.Add(new { kind = "combos", mode = mode, comboCount = count, itemsEach = items,
                    selected = selected, bound = bound, phase = pass == 0 ? "cold" : "warm",
                    formats = combos.Sum(c => c.Formats),
                    textChanged = combos.Sum(c => c.Changes), selectedChanged = combos.Sum(c => c.Selections),
                    itemReadsInsideEvents = combos.Sum(c => c.EventFormatReads),
                    maxEventDepth = combos.Max(c => c.MaxDepth), displayColumnWrites = rowWrites,
                    translated = translated, keysIntact = keysIntact, selectionIntact = selectionIntact,
                    inputIntact = input.Text == "MISSION" && richInput.Text == "SITUATION",
                    anyFocused = combos.Any(c => c.Focused) });
                string description = mode + ", items=" + items + ", bound=" + bound + ", pass=" + pass;
                Check(translated == count * items, description + ": not all eligible real dictionary labels translated");
                Check(keysIntact && selectionIntact, description + ": selection or data key changed");
                Check(input.Text == "MISSION" && richInput.Text == "SITUATION", description + ": editable input changed");
                if (!bound)
                    Check(combos.Sum(c => c.EventFormatReads) == 0,
                        description + ": translating items synchronously rescanned items inside native change events: "
                        + combos.Sum(c => c.EventFormatReads) + " Format reads");
                if (pass == 1 && !bound)
                    Check(combos.Sum(c => c.Formats) <= count * items,
                        description + ": hot Combo item pass repeated, Format events=" + combos.Sum(c => c.Formats));
            }
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length > 3 || (args.Length == 3 && args[2] != "contracts"))
        {
            Console.Error.WriteLine("Usage: BulkTranslationTests <engine.dll> <dictionary.json> [contracts]");
            return 2;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        engine = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
        engine.GetField("auditLogPath", Static).SetValue(null, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bulk-audit.log"));
        engine.GetField("logPath", Static).SetValue(null, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bulk-engine.log"));
        engine.GetMethod("LoadDictionaries", Static).Invoke(null, new object[] { Path.GetFullPath(args[1]) });
        exact = Field<Dictionary<string, string>>("exactMap");
        if (exact.Count < 10000) throw new Exception("Real dictionary did not load");
        forbidden = Bind<Func<string, bool>>("IsForbiddenCodeOrEnum");
        tree = Bind<Action<Control>>("TranslateControlTree");
        hook = Bind<Action<Control>>("HookControlTextChanged");
        safeCombo = Bind<Action<ComboBox>>("LocalizeComboBoxSafe");
        ProfileTabs();
        foreach (int items in new int[] { 12, 60 })
        {
            ProfileCombos("safe-unhooked", 40, items, true, false);
            ProfileCombos("safe-hooked", 40, items, true, false);
            ProfileCombos("tree", 40, items, true, false);
            ProfileCombos("tree", 40, items, false, false);
            ProfileCombos("tree", 40, items, true, true);
        }
        JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        Console.WriteLine(json.Serialize(new { engine = Path.GetFullPath(args[0]), dictionary = Path.GetFullPath(args[1]),
            exactEntries = exact.Count, framework = Environment.Version.ToString(),
            mode = "contracts", results = results, contractFailures = failures }));
        return failures.Count == 0 ? 0 : 1;
    }
}
