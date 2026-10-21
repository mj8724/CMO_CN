using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Independent STA controls only. Never initializes the game or its domain manager.
class RichTextTranslationTests
{
    private static Type engine;
    private static int checks;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Help = "Rich help ready";
    private const string HelpChinese = "富文本帮助就绪";
    private const string Changed = "Rich help refreshed";
    private const string ChangedChinese = "富文本帮助已刷新";

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollPoint { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ScrollPoint point);

    private sealed class Host : IDisposable
    {
        internal readonly Form Form;
        internal readonly TextBox FocusSink;

        internal Host(bool hotkeys) : this(hotkeys ? (Form)new Command.Hotkeys() : new Form()) { }

        internal Host(Form form)
        {
            Form = form;
            Form.Text = "Rich text translation fixture";
            Form.StartPosition = FormStartPosition.Manual;
            Form.Location = new Point(-2000, -2000);
            Form.ClientSize = new Size(500, 220);
            Form.ShowInTaskbar = false;
            FocusSink = new TextBox { Name = "FixtureFocusSink", Width = 200, Top = 190 };
            Form.Controls.Add(FocusSink);
        }

        internal void Show(RichTextBox box)
        {
            box.Size = new Size(480, 170);
            Form.Controls.Add(box);
            Form.Show();
            Form.ActiveControl = FocusSink;
            FocusSink.Focus();
            Application.DoEvents();
            Require(!box.Focused && !box.ContainsFocus, "Fixture help surface unexpectedly has input focus");
        }

        public void Dispose() { Form.Dispose(); }
    }

    private static object Call(string name, params object[] arguments)
    {
        MethodInfo method = engine.GetMethod(name, PrivateStatic | BindingFlags.Public);
        if (method == null) throw new InvalidOperationException("Missing existing engine method: " + name);
        return method.Invoke(null, arguments);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }

    private static RichTextBox Rich(string name, bool readOnly, string text)
    {
        return new DarkUI.Controls.DarkRichTextBox {
            Name = name, ReadOnly = readOnly, Text = text, DetectUrls = false,
            WordWrap = false, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Both
        };
    }

    private static string Delimiters(string text)
    {
        return Regex.Replace(text, "[^\\t\\r\\n]", "");
    }

    private static void ReadOnlyHelp()
    {
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, "Ctrl+F9\t" + Help + "\n"))
        {
            host.Show(box);
            string before = box.Text;
            Call("TranslateControlTree", host.Form);
            Require(box.Text == before.Replace(Help, HelpChinese), "Read-only WinForms rich text help stayed English");
            Require(Delimiters(box.Text) == Delimiters(before), "Translation changed Tab or newline delimiters");
        }
    }

    private static void HotkeysHelp()
    {
        string[] names = { "RichTextBox1", "RichTextBox2", "RichTextBox3", "RichTextBox4", "RichTextBox5",
            "RichTextBox6", "RichTextBox7", "RichTextBox8", "RichTextBoxL_AMP" };
        foreach (string name in names)
            using (Host host = new Host(true))
            using (RichTextBox box = Rich(name, false, "Insert\t\t" + Help + "\nDel\t \t" + Changed + "\n"))
            {
                host.Show(box);
                string before = box.Text;
                Call("TranslateControlTree", host.Form);
                Require(box.Text == before.Replace(Help, HelpChinese).Replace(Changed, ChangedChinese),
                    "Hotkeys descriptions stayed English or shortcut columns changed: " + name);
                Require(!box.ReadOnly, "Translation changed the game's ReadOnly property: " + name);
            }
    }

    private static void EditableProtection()
    {
        foreach (bool hotkeys in new bool[] { false, true })
            using (Host host = new Host(hotkeys))
            using (RichTextBox box = Rich(hotkeys ? "UserNotes" : "RichTextBox2", false, "Rich help: Ready\n"))
            {
                host.Show(box);
                string before = box.Rtf;
                Call("TranslateControlTree", host.Form);
                Require(box.Rtf == before, "Scan rewrote editable rich text outside a verified help role");
                Call("HookControlTextChanged", box);
                box.Text = Changed;
                Require(box.Text == Changed, "TextChanged rewrote editable rich text outside a verified help role");
            }
    }

    private static void FocusProtection()
    {
        foreach (bool hotkeys in new bool[] { false, true })
            using (Host host = new Host(hotkeys))
            using (RichTextBox box = Rich(hotkeys ? "RichTextBox2" : "ReadOnlyHelp", !hotkeys, "Rich help: Ready\n"))
            {
                host.Show(box);
                host.Form.ActiveControl = box;
                box.Focus();
                Application.DoEvents();
                Require(box.Focused, "Fixture could not establish real focus on the rich text control");
                string before = box.Rtf;
                Call("TranslateControlText", box);
                Require(box.Rtf == before, "Direct translation rewrote focused rich text");
                Call("TranslateControlTree", host.Form);
                Require(box.Rtf == before, "Tree translation rewrote focused rich text");
                Call("HookControlTextChanged", box);
                box.Text = "Rich help: Ready\n" + Changed;
                Require(box.Text == "Rich help: Ready\n" + Changed, "TextChanged rewrote focused rich text");
            }
    }

    private static void ApplyStyle(RichTextBox box, string text, Font font, Color color, Color background)
    {
        int start = box.Text.IndexOf(text, StringComparison.Ordinal);
        Require(start >= 0, "Fixture style anchor missing: " + text);
        box.Select(start, text.Length);
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.SelectionBackColor = background;
    }

    private static void AssertStyle(RichTextBox box, string text, Font font, Color color, Color background)
    {
        int start = box.Text.IndexOf(text, StringComparison.Ordinal);
        Require(start >= 0, "Translated style anchor missing: " + text);
        box.Select(start, text.Length);
        Font actual = box.SelectionFont;
        Require(actual != null && actual.FontFamily.Name == font.FontFamily.Name
            && Math.Abs(actual.SizeInPoints - font.SizeInPoints) < 0.1 && actual.Style == font.Style,
            "Translation changed font family, size or style for: " + text);
        Require(box.SelectionColor.ToArgb() == color.ToArgb(), "Translation changed text color for: " + text);
        Require(box.SelectionBackColor.ToArgb() == background.ToArgb(), "Translation changed background color for: " + text);
    }

    private static void Formatting(bool colon)
    {
        string source = colon ? "Rich help: Ready" : Help;
        string translated = colon ? "富文本说明: 就绪" : HelpChinese;
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, source + "\nKEEP\tCtrl+F9\n"))
        // The translated font fixture must contain CJK glyphs; Latin-only
        // Consolas is linked to a Chinese font by RichEdit itself.
        using (Font helpFont = new Font("Microsoft YaHei", 14, FontStyle.Bold))
        using (Font keyFont = new Font("Consolas", 11, FontStyle.Italic))
        using (Font keepFont = new Font("Consolas", 10, FontStyle.Underline))
        {
            host.Show(box);
            box.SelectAll();
            box.SelectionTabs = new int[] { 180, 300 };
            ApplyStyle(box, source, helpFont, Color.Red, Color.Gold);
            ApplyStyle(box, "Ctrl+F9", keyFont, Color.Blue, Color.White);
            ApplyStyle(box, "KEEP", keepFont, Color.Green, Color.White);
            string before = box.Text;
            Call("TranslateControlTree", host.Form);
            Require(box.Text == before.Replace(source, translated), "Formatted rich text help did not translate");
            Require(Delimiters(box.Text) == Delimiters(before), "Formatted translation changed Tab or newline delimiters");
            AssertStyle(box, translated, helpFont, Color.Red, Color.Gold);
            AssertStyle(box, "Ctrl+F9", keyFont, Color.Blue, Color.White);
            AssertStyle(box, "KEEP", keepFont, Color.Green, Color.White);
            box.Select(0, 1);
            int[] tabs = box.SelectionTabs;
            Require(tabs.Length == 2 && tabs[0] == 180 && tabs[1] == 300, "Translation changed paragraph Tab stops");
        }
    }

    private static void SelectionAndScroll()
    {
        StringBuilder text = new StringBuilder(Help + "\tCtrl+F9\n");
        for (int i = 0; i < 100; i++) text.Append("KEEP ROW ").Append(i).Append("\tCtrl+F9\n");
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, text.ToString()))
        {
            host.Show(box);
            string before = box.Text;
            const string selection = "KEEP ROW 60";
            box.Select(box.Text.IndexOf(selection, StringComparison.Ordinal), selection.Length);
            ScrollPoint wanted = new ScrollPoint { X = 0, Y = 300 };
            SendMessage(box.Handle, 0x04DE, IntPtr.Zero, ref wanted); // EM_SETSCROLLPOS
            ScrollPoint scroll = new ScrollPoint();
            SendMessage(box.Handle, 0x04DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS
            Require(scroll.Y > 0, "Fixture did not establish a nonzero rich text scroll position");
            box.Modified = false;
            Call("TranslateControlTree", host.Form);
            Require(box.Text == before.Replace(Help, HelpChinese), "State fixture rich text did not translate");
            Require(box.SelectedText == selection, "Translation moved the selection away from unchanged source text");
            Require(box.SelectionStart == box.Text.IndexOf(selection, StringComparison.Ordinal)
                && box.SelectionLength == selection.Length, "Translation did not map selection offsets after an earlier replacement");
            ScrollPoint after = new ScrollPoint();
            SendMessage(box.Handle, 0x04DD, IntPtr.Zero, ref after);
            Require(after.X == scroll.X && after.Y == scroll.Y, "Translation moved the rich text viewport");
            Require(!box.Modified, "Read-only translation marked the document as user-modified");
        }
    }

    private static void HookRefresh()
    {
        foreach (bool hotkeys in new bool[] { false, true })
            using (Host host = new Host(hotkeys))
            using (RichTextBox box = Rich(hotkeys ? "RichTextBox2" : "ReadOnlyHelp", !hotkeys, Help))
            {
                host.Show(box);
                Call("HookControlTextChanged", box);
                string key = hotkeys ? "Insert" : "Ctrl+F9";
                box.Text = key + "\t" + Changed + "\n";
                Require(box.Text == key + "\t" + ChangedChinese + "\n", "TextChanged left refreshed help English or changed a shortcut");
            }
    }

    private static void Idempotency()
    {
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, Help + "\tCtrl+F9\n"))
        {
            host.Show(box);
            Call("TranslateControlTree", host.Form);
            Require(box.Text == HelpChinese + "\tCtrl+F9\n", "Idempotency fixture never translated its source");
            string once = box.Rtf;
            int writes = 0;
            box.TextChanged += delegate { writes++; };
            Call("TranslateControlTree", host.Form);
            Call("TranslateControlText", box);
            Require(box.Rtf == once && writes == 0, "Unchanged translated rich text was written again");
        }
    }

    private static void SeedDictionary()
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
        exact[Help] = HelpChinese;
        exact[Changed] = ChangedChinese;
        exact["Rich help"] = "富文本说明";
        exact["Ready"] = "就绪";
        exact["Rich help: Ready"] = "富文本说明: 就绪";
        // Exact dictionary hits are a deliberate trap: these are keyboard keys
        // in Hotkeys' first column, although they are translatable UI captions.
        exact["Insert"] = "插入";
        exact["Del"] = "删除";
    }

    private static void CompleteDump()
    {
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, new string('x', 300) + "\n" + Help))
        using (RichTextBox editor = Rich("UserNotes", false, "private editor sentinel"))
        {
            host.Form.Name = "OtherHelp";
            host.Form.Controls.Add(editor);
            host.Show(box);
            string before = box.Rtf;
            string response = (string)Call("ExecuteTestCommand", "RICH_TEXT_DUMP OtherHelp");
            Dictionary<string, object> dump = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response);
            object[] nodes = (object[])dump["nodes"];
            Require(nodes.Length == 1, "Command filter was lost or editable input was included in the help dump");
            Dictionary<string, object> node = (Dictionary<string, object>)nodes[0];
            Require((string)node["text"] == box.Text, "Help dump truncated or changed the complete document");
            Require((string)node["n"] == box.Name, "Help dump lost the control identity");
            Require(box.Rtf == before, "Read-only help dump mutated the document");
        }
    }

    private static void MixedBaseline()
    {
        using (Host host = new Host(false))
        using (RichTextBox box = Rich("ReadOnlyHelp", true, Help))
        {
            host.Show(box);
            box.Select(Help.IndexOf("ready", StringComparison.Ordinal), "ready".Length);
            box.SelectionCharOffset = 5;
            string before = box.Rtf;
            Call("TranslateControlTree", host.Form);
            Require(box.Rtf == before, "A mixed-baseline cell must not be flattened to one character offset");
        }
    }

    private static void PlainReadOnly()
    {
        using (Host host = new Host(false))
        using (TextBox box = new TextBox { ReadOnly = true, Multiline = true, Text = Help })
        {
            host.Form.Controls.Add(box);
            host.Form.Show();
            host.FocusSink.Focus();
            Call("TranslateControlTree", host.Form);
            Require(box.Text == HelpChinese, "Read-only plain text help stayed English");
        }
    }

    private static string NormalizeLines(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string ReviewedHelp(string prefix)
    {
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
        string source = "";
        foreach (string key in exact.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > source.Length) source = key;
        Require(source.Length > prefix.Length, "Official dictionary is missing the complete reviewed help: " + prefix);
        return source;
    }

    private static void KnownStaticHelp()
    {
        string[][] fixtures = {
            new string[] { "Command.LuaEditorOperationPlanner", "RichTextBox1", "-The script MUST return a boolean." },
            new string[] { "Command.DebugTool", "TB_DetailedSensorReport", "A message in the \"Debug\" tab" },
            new string[] { "Command.CopyOverSimilarityPrompt", "TB_InfoUseSelected", "The selected candidate will be used as a new reference" },
            new string[] { "Command.IconeCustomizer", "TB_Synthax", "Each line starting with #" },
            new string[] { "Command.IconeCustomizer", "TB_Structure", "This is a runtime global modification" },
            new string[] { "Command.IconeCustomizer", "TB_Examples", "Example 1 :" }
        };
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
        foreach (string[] fixture in fixtures)
        {
            string source = ReviewedHelp(fixture[2]);
            string expected = NormalizeLines(exact[source]);
            Form form = (Form)Activator.CreateInstance(Assembly.GetExecutingAssembly().GetType(fixture[0]), true);
            using (Host host = new Host(form))
            using (RichTextBox box = Rich(fixture[1], false, source))
            {
                foreach (string name in new string[] { "TBScriptPanel", "TBDescription", "TBError" })
                    form.Controls.Add(Rich(name, false, source));
                host.Show(box);
                box.Focus();
                Application.DoEvents();
                Require(box.Focused, "Static-help fixture could not establish focus");
                string focused = box.Rtf;
                Call("TranslateControlText", box);
                Require(box.Rtf == focused, "Focused static help was rewritten: " + fixture[1]);
                host.FocusSink.Focus();
                Application.DoEvents();
                Call("TranslateControlTree", form);
                Require(NormalizeLines(box.Text) == expected, "Reviewed static help differs from its official full-body translation: " + fixture[1]);
                foreach (string line in NormalizeLines(source).Split('\n'))
                    if (line.StartsWith("#", StringComparison.Ordinal))
                        Require(Array.IndexOf(NormalizeLines(box.Text).Split('\n'), line) >= 0, "Static help changed a literal code example: " + line);
                Require(!box.ReadOnly, "Static help localization changed the game's ReadOnly property");
                foreach (Control child in form.Controls)
                    if (child.Name == "TBScriptPanel" || child.Name == "TBDescription" || child.Name == "TBError")
                        Require(NormalizeLines(child.Text) == NormalizeLines(source), "Adjacent editable input was rewritten: " + child.Name);
                string once = box.Rtf;
                Call("TranslateControlText", box);
                Require(box.Rtf == once, "Known static help was written again after translation");
                string response = (string)Call("ExecuteTestCommand", "RICH_TEXT_DUMP " + form.GetType().Name);
                object[] nodes = (object[])((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response))["nodes"];
                Require(nodes.Length == 1 && (string)((Dictionary<string, object>)nodes[0])["text"] == box.Text,
                    "Static help dump omitted the known translation or exposed neighboring editors");
                box.Text = source;
                Require(NormalizeLines(box.Text) == expected, "TextChanged did not localize refreshed static help");
                box.Text = Help;
                string edited = box.Rtf;
                Call("TranslateControlTree", form);
                Require(box.Rtf == edited && box.Text == Help, "Static-help name allowed a changed user body to be rewritten");
                response = (string)Call("ExecuteTestCommand", "RICH_TEXT_DUMP " + form.GetType().Name);
                nodes = (object[])((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response))["nodes"];
                Require(nodes.Length == 0, "Static help dump exposed a changed user body");
            }
            using (Host host = new Host(false))
            using (RichTextBox box = Rich(fixture[1], false, source))
            {
                host.Show(box);
                string before = box.Rtf;
                Call("TranslateControlTree", host.Form);
                Require(box.Rtf == before, "A static-help control name was accepted under the wrong form type");
            }
        }
    }

    private static void MixedStyle()
    {
        foreach (string style in new string[] { "font", "foreground", "background" })
            using (Host host = new Host(false))
            using (RichTextBox box = Rich("ReadOnlyHelp", true, Help))
            using (Font font = new Font(box.Font, FontStyle.Bold))
            {
                host.Show(box);
                box.Select(5, 4);
                if (style == "font") box.SelectionFont = font;
                if (style == "foreground") box.SelectionColor = Color.Red;
                if (style == "background") box.SelectionBackColor = Color.Gold;
                string before = box.Rtf;
                Call("TranslateControlTree", host.Form);
                Require(box.Rtf == before, "Mixed " + style + " cell was flattened or changed");
            }
    }

    private static void ReentrantBodyProtection()
    {
        foreach (string role in new string[] { "readonly", "hotkeys", "known-static" })
            foreach (string callback in new string[] { "text", "selection", "restore-selection" })
            {
                Form form = role == "known-static" ? (Form)new Command.LuaEditorOperationPlanner()
                    : role == "hotkeys" ? (Form)new Command.Hotkeys() : new Form();
                string source = role == "known-static" ? ReviewedHelp("-The script MUST return a boolean.")
                    : role == "hotkeys" ? "Insert\t" + Help + "\nDel\t" + Changed : Help + "\n" + Changed;
                Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
                string translated = role == "known-static" ? NormalizeLines(exact[source])
                    : role == "hotkeys" ? "Insert\t" + HelpChinese + "\nDel\t" + ChangedChinese : HelpChinese + "\n" + ChangedChinese;
                using (Host host = new Host(form))
                using (RichTextBox box = Rich(role == "readonly" ? "ReadOnlyHelp" : "RichTextBox1", role == "readonly", source))
                {
                    host.Show(box);
                    string replacement = new string('u', 2000);
                    bool swapped = false;
                    string callbackRtf = null;
                    Action swap = delegate {
                        if (swapped) return;
                        swapped = true;
                        box.Text = replacement;
                        box.Select(12, 9);
                        box.Modified = true;
                        callbackRtf = box.Rtf;
                    };
                    if (callback == "text") box.TextChanged += delegate { swap(); };
                    else box.SelectionChanged += delegate {
                        if (callback == "selection" || (NormalizeLines(box.Text) == translated
                            && box.SelectionStart == 0 && box.SelectionLength == 0)) swap();
                    };
                    Call("TranslateControlText", box);
                    Require(swapped, "Reentrant fixture did not invoke the external callback");
                    Require(box.Text == replacement && box.Rtf == callbackRtf,
                        "Old offsets or formatting rewrote the callback's new body: " + role + "/" + callback);
                    Require(box.SelectionStart == 12 && box.SelectionLength == 9 && box.Modified,
                        "Translation overwrote callback-owned selection or Modified state");
                    if (role == "known-static")
                    {
                        string response = (string)Call("ExecuteTestCommand", "RICH_TEXT_DUMP LuaEditorOperationPlanner");
                        object[] nodes = (object[])((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response))["nodes"];
                        Require(nodes.Length == 0, "A reentrant user body was registered as static help");
                    }
                }
            }
    }

    private static void CodeExampleProtection()
    {
        const string example = "#ship;*;Ships.png";
        Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
        string previous;
        bool existed = exact.TryGetValue(example, out previous);
        exact[example] = "代码示例不得改写";
        try
        {
            using (Host host = new Host(false))
            using (RichTextBox box = Rich("ReadOnlyHelp", true, example + "\n" + Help))
            {
                host.Show(box);
                Call("TranslateControlText", box);
                Require(box.Text == example + "\n" + HelpChinese, "A dictionary hit rewrote a literal code example");
            }
        }
        finally { if (existed) exact[example] = previous; else exact.Remove(example); }
    }

    private static void Corpus(string path)
    {
        Dictionary<string, object> corpus = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
        object[] controls = (object[])corpus["controls"];
        Require(controls.Length == 9, "Corpus must cover all nine actual Hotkeys help controls");
        int lineCount = 0;
        HashSet<string> abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "ATO", "BOL", "EMCON", "WRA", "RTB", "OoB", "Ctrl", "Shift", "Alt", "Lua", "RPs"
        };
        foreach (object item in controls)
        {
            Dictionary<string, object> control = (Dictionary<string, object>)item;
            string name = (string)control["controlName"];
            using (Host host = new Host(true))
            using (RichTextBox box = Rich(name, false, (string)control["rawText"]))
            {
                host.Show(box);
                string before = box.Text;
                Call("TranslateControlTree", host.Form);
                Dictionary<string, string> exact = (Dictionary<string, string>)engine.GetField("exactMap", PrivateStatic).GetValue(null);
                Require(NormalizeLines(box.Text) == NormalizeLines(exact[(string)control["rawText"]]),
                    name + ": displayed complete body differs from the official dictionary translation");
                string[] original = Regex.Split(before, "\\r\\n|\\r|\\n");
                string[] translated = Regex.Split(box.Text, "\\r\\n|\\r|\\n");
                Require(original.Length == translated.Length, name + ": corpus line count changed");
                Require(Delimiters(before) == Delimiters(box.Text), name + ": corpus Tabs or newlines changed");
                for (int i = 0; i < original.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(original[i])) continue;
                    int boundary = original[i].LastIndexOf('\t') + 1;
                    string prefix = original[i].Substring(0, boundary);
                    Require(translated[i].StartsWith(prefix, StringComparison.Ordinal),
                        name + ": shortcut/layout prefix changed on line " + (i + 1));
                    string source = original[i].Substring(boundary).Trim();
                    string expected = (string)Call("TranslateString", source);
                    string description = translated[i].Substring(boundary).Trim();
                    Require(description == expected, name + ": displayed description differs from real dictionary translation: " + source);
                    Require(Regex.IsMatch(description, "[\\u3400-\\u9fff]"), name + ": description stayed English: " + source);
                    foreach (Match token in Regex.Matches(description, "[A-Za-z][A-Za-z0-9-]*"))
                        Require(abbreviations.Contains(token.Value), name + ": unexplained English residue '" + token.Value + "' in " + description);
                    lineCount++;
                }
            }
        }
        Require(lineCount == 89, "Corpus did not execute all 89 actual Hotkeys help lines");
        Console.WriteLine("CORPUS controls=" + controls.Length + " lines=" + lineCount);
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args.Length > 4) throw new ArgumentException("Expected engine DLL, dictionary path, optional mode and corpus path");
            engine = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            Call("LoadDictionaries", Path.GetFullPath(args[1]));
            string mode = args.Length >= 3 ? args[2] : "all";
            if (mode != "corpus") SeedDictionary();
            if (mode == "corpus") Corpus(args.Length == 4 ? args[3]
                : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(args[1]))), "build", "hotkeys-corpus.json"));
            if (mode == "readonly" || mode == "all") ReadOnlyHelp();
            if (mode == "hotkeys" || mode == "all") HotkeysHelp();
            if (mode == "editable" || mode == "all") EditableProtection();
            if (mode == "focused" || mode == "all") FocusProtection();
            if (mode == "formatting" || mode == "all") Formatting(false);
            if (mode == "colon-format" || mode == "all") Formatting(true);
            if (mode == "state" || mode == "all") SelectionAndScroll();
            if (mode == "hook" || mode == "all") HookRefresh();
            if (mode == "idempotent" || mode == "all") Idempotency();
            if (mode == "dump" || mode == "all") CompleteDump();
            if (mode == "mixed-baseline" || mode == "all") MixedBaseline();
            if (mode == "plain-readonly" || mode == "all") PlainReadOnly();
            if (mode == "known-static" || mode == "all") KnownStaticHelp();
            if (mode == "mixed-style" || mode == "all") MixedStyle();
            if (mode == "reentrant-body" || mode == "all") ReentrantBodyProtection();
            if (mode == "code-example" || mode == "all") CodeExampleProtection();
            if (mode != "all" && mode != "readonly" && mode != "hotkeys" && mode != "editable"
                && mode != "focused" && mode != "formatting" && mode != "colon-format" && mode != "state"
                && mode != "hook" && mode != "idempotent" && mode != "corpus" && mode != "dump"
                && mode != "mixed-baseline" && mode != "plain-readonly" && mode != "known-static"
                && mode != "mixed-style" && mode != "reentrant-body" && mode != "code-example") throw new ArgumentException("Unknown mode " + mode);
            Console.WriteLine("PASS richtext/" + mode + " checks=" + checks);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
}

namespace Command
{
    internal sealed class Hotkeys : Form { }
    internal sealed class LuaEditorOperationPlanner : Form { }
    internal sealed class DebugTool : Form { }
    internal sealed class CopyOverSimilarityPrompt : Form { }
    internal sealed class IconeCustomizer : Form { }
}

namespace DarkUI.Controls
{
    internal sealed class DarkRichTextBox : RichTextBox { }
}
