using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly Regex richTextCells = new Regex(@"[^\t\r\n]+", RegexOptions.Compiled);
        private static readonly ConditionalWeakTable<RichTextBox, StaticHelpTranslation> staticHelpTranslations =
            new ConditionalWeakTable<RichTextBox, StaticHelpTranslation>();

        private sealed class StaticHelpTranslation { internal string Text; }

        private sealed class RichTextEdit
        {
            internal int Start;
            internal int Length;
            internal string Text;
        }

        private static bool IsHotkeysHelp(Control control)
        {
            Form form = control.FindForm();
            if (form == null || form.GetType().Name != "Hotkeys") return false;
            if (control.Name == "RichTextBoxL_AMP") return true;
            return control.Name.Length == 12 && control.Name.StartsWith("RichTextBox", StringComparison.Ordinal)
                && control.Name[11] >= '1' && control.Name[11] <= '8';
        }

        private static bool CanTranslateTextBoxBody(TextBoxBase box)
        {
            return box != null && !box.IsDisposed && !box.Focused && !box.ContainsFocus
                && (box.ReadOnly || (box is RichTextBox
                    && (IsHotkeysHelp(box) || IsKnownStaticHelp((RichTextBox)box, false))));
        }

        private static string StaticHelpSourceHash(Control control)
        {
            Form form = control.FindForm();
            if (form == null) return null;
            // Fixed designer help, not adjacent script/description/error inputs.
            // Hashes use the reviewed resource text with LF line endings.
            switch (form.GetType().FullName + "." + control.Name)
            {
                case "Command.LuaEditorOperationPlanner.RichTextBox1": return "a1dae8f20459bf9c7e95a9961701816edbd320946ed0f5696a1ad896b3122696";
                case "Command.DebugTool.TB_DetailedSensorReport": return "4e98369e3523c0f3504371bf33696e3cefaff5322ddd393340f1c9c5675e221d";
                case "Command.CopyOverSimilarityPrompt.TB_InfoUseSelected": return "4ca413fd193f51fcaed23d6b16751b77ab7f78f8d64af098b82b55bbbab2d41c";
                case "Command.IconeCustomizer.TB_Synthax": return "ba79356d0372481fc5c1528f68ee7b0e7d7e9de1c6f5a21480d7132c88237370";
                case "Command.IconeCustomizer.TB_Structure": return "7e30d5da4cb5e3b3c4cd81e5228dc11ad3e69d174c9fc75c7aec50727dbb3c72";
                case "Command.IconeCustomizer.TB_Examples": return "a2040bccd9b71ab3f5ce133fdbf685c051a5a41b595f7240b2f018aeaeaa329d";
                default: return null;
            }
        }

        private static bool IsKnownStaticHelp(RichTextBox box, bool includeTranslated)
        {
            string expected = StaticHelpSourceHash(box);
            if (expected == null) return false;
            string text = box.Text;
            StaticHelpTranslation state;
            if (includeTranslated && staticHelpTranslations.TryGetValue(box, out state) && state.Text == text) return true;
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant() == expected;
        }

        private static List<RichTextEdit> FindRichTextEdits(string text, bool shortcutColumn)
        {
            List<RichTextEdit> edits = new List<RichTextEdit>();
            int lineStart = 0;
            foreach (Match cell in richTextCells.Matches(text))
            {
                // The first column of a Tab-separated row is a keyboard/mouse
                // binding. Never send it to the dictionary (Insert is a key).
                int previousLine = text.LastIndexOf('\n', cell.Index);
                lineStart = previousLine < 0 ? 0 : previousLine + 1;
                int lineEnd = text.IndexOf('\n', cell.Index);
                if (lineEnd < 0) lineEnd = text.Length;
                int tab = text.IndexOf('\t', lineStart, lineEnd - lineStart);
                if (shortcutColumn && tab >= 0 && cell.Index < tab) continue;

                int start = cell.Index;
                int end = start + cell.Length;
                while (start < end && char.IsWhiteSpace(text[start])) start++;
                while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
                if (start == end) continue;
                string source = text.Substring(start, end - start);
                if (source.StartsWith("#", StringComparison.Ordinal)) continue; // Literal icon/script examples.
                string translated = TranslateString(source);
                if (translated == source) translated = TranslateLoadoutDetailText(source);
                if (translated == source || !ContainsChinese(translated)
                    || translated.IndexOfAny(new char[] { '\t', '\r', '\n' }) >= 0) continue;
                edits.Add(new RichTextEdit { Start = start, Length = end - start, Text = translated });
            }
            return edits;
        }

        private static int MapRichTextSelection(int position, RichTextEdit edit)
        {
            if (position <= edit.Start) return position;
            if (position >= edit.Start + edit.Length) return position + edit.Text.Length - edit.Length;
            return edit.Start + Math.Min(position - edit.Start, edit.Text.Length);
        }

        private static bool IsCurrentRichTextBody(RichTextBox box, string expectedText, string staticHelpHash)
        {
            return !box.IsDisposed && !box.Focused && !box.ContainsFocus && box.Text == expectedText
                && (box.ReadOnly || IsHotkeysHelp(box)
                    || (staticHelpHash != null && StaticHelpSourceHash(box) == staticHelpHash));
        }

        private static bool SelectCurrentRichText(RichTextBox box, int start, int length, string expectedText, string staticHelpHash)
        {
            if (!IsCurrentRichTextBody(box, expectedText, staticHelpHash)) return false;
            box.Select(start, length);
            return IsCurrentRichTextBody(box, expectedText, staticHelpHash);
        }

        private static void TranslateRichTextBox(RichTextBox box)
        {
            if (!CanTranslateTextBoxBody(box)) return;
            string staticHelpHash = IsKnownStaticHelp(box, false) ? StaticHelpSourceHash(box) : null;
            string expectedText = box.Text;
            List<RichTextEdit> edits = FindRichTextEdits(expectedText, IsHotkeysHelp(box));
            if (edits.Count == 0 || !EnterMutation("RichTextBody")) return;
            int selectionStart = box.SelectionStart;
            int selectionEnd = selectionStart + box.SelectionLength;
            bool modified = box.Modified;
            bool previousHook = _inControlTextChanged;
            bool redraw = box.Visible;
            IntPtr handle = box.Handle;
            RichTextNative.ScrollPoint scroll = new RichTextNative.ScrollPoint();
            RichTextNative.SendMessage(handle, 0x04DD, IntPtr.Zero, ref scroll); // EM_GETSCROLLPOS
            _inControlTextChanged = true;
            if (redraw) SendMessage(handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW
            try
            {
                for (int i = edits.Count - 1; i >= 0; i--)
                {
                    RichTextEdit edit = edits[i];
                    if (!SelectCurrentRichText(box, edit.Start, edit.Length, expectedText, staticHelpHash))
                    { expectedText = null; break; }
                    RichTextNative.CharFormat format = new RichTextNative.CharFormat();
                    format.Size = (uint)Marshal.SizeOf(typeof(RichTextNative.CharFormat));
                    RichTextNative.SendMessage(handle, 0x043A, new IntPtr(1), ref format); // EM_GETCHARFORMAT
                    // EM_REPLACESEL inherits the first character's format. A
                    // mixed-style cell must not be flattened by that operation.
                    // RichTextBox.SelectionFont/BackColor do not reliably signal
                    // mixed values; native masks expose every supported attribute.
                    if (!SelectCurrentRichText(box, edit.Start, 1, expectedText, staticHelpHash))
                    { expectedText = null; break; }
                    RichTextNative.CharFormat firstCharacter = format;
                    RichTextNative.SendMessage(handle, 0x043A, new IntPtr(1), ref firstCharacter);
                    if ((format.Mask & firstCharacter.Mask) != firstCharacter.Mask) continue;
                    if (!SelectCurrentRichText(box, edit.Start, edit.Length, expectedText, staticHelpHash))
                    { expectedText = null; break; }
                    expectedText = expectedText.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
                    box.SelectedText = edit.Text;
                    if (!SelectCurrentRichText(box, edit.Start, edit.Text.Length, expectedText, staticHelpHash))
                    { expectedText = null; break; }
                    RichTextNative.SendMessage(handle, 0x0444, new IntPtr(1), ref format); // EM_SETCHARFORMAT
                    if (!IsCurrentRichTextBody(box, expectedText, staticHelpHash))
                    { expectedText = null; break; }
                    selectionStart = MapRichTextSelection(selectionStart, edit);
                    selectionEnd = MapRichTextSelection(selectionEnd, edit);
                }
            }
            finally
            {
                // If a game callback took ownership of the body, its selection,
                // formatting, Modified flag and viewport must remain intact too.
                if (expectedText != null && !box.IsDisposed)
                {
                    selectionStart = Math.Min(selectionStart, box.TextLength);
                    selectionEnd = Math.Min(selectionEnd, box.TextLength);
                    if (SelectCurrentRichText(box, selectionStart, Math.Max(0, selectionEnd - selectionStart), expectedText, staticHelpHash))
                    {
                        box.Modified = modified;
                        RichTextNative.SendMessage(handle, 0x04DE, IntPtr.Zero, ref scroll); // EM_SETSCROLLPOS
                    }
                    else expectedText = null;
                }
                if (redraw)
                {
                    SendMessage(handle, 0x000B, new IntPtr(1), IntPtr.Zero);
                    box.Invalidate();
                }
                _inControlTextChanged = previousHook;
                ExitMutation();
            }
            // Register only our verified result, after the final Select callback.
            if (staticHelpHash != null && expectedText != null && IsCurrentRichTextBody(box, expectedText, staticHelpHash))
                staticHelpTranslations.GetValue(box, delegate(RichTextBox owner) { return new StaticHelpTranslation(); }).Text = expectedText;
        }

        private static void AppendRichTextHelp(Control control, StringBuilder json, ref bool first)
        {
            RichTextBox box = control as RichTextBox;
            if (box != null && (box.ReadOnly || IsHotkeysHelp(box) || IsKnownStaticHelp(box, true)))
            {
                if (!first) json.Append(',');
                first = false;
                json.Append("{\"n\":\"").Append(JsonEsc(box.Name))
                    .Append("\",\"t\":\"").Append(JsonEsc(box.GetType().FullName))
                    .Append("\",\"readOnly\":").Append(box.ReadOnly ? "true" : "false")
                    .Append(",\"focused\":").Append(box.ContainsFocus ? "true" : "false")
                    .Append(",\"eligible\":").Append(CanTranslateTextBoxBody(box) ? "true" : "false")
                    .Append(",\"text\":\"").Append(JsonEsc(box.Text)).Append("\"}");
            }
            foreach (Control child in control.Controls) AppendRichTextHelp(child, json, ref first);
        }

        // CTRL_TREE deliberately truncates each caption to 120 characters.
        // Help coverage needs the complete document, without exposing editors.
        private static string HandleRichTextDump(string command)
        {
            string filter = command.Substring("RICH_TEXT_DUMP".Length).Trim();
            if (filter.Length == 0) filter = "Hotkeys";
            StringBuilder json = new StringBuilder("{\"status\":\"ok\",\"nodes\":[");
            bool first = true;
            foreach (Form form in Application.OpenForms)
            {
                if (form.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && form.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                AppendRichTextHelp(form, json, ref first);
            }
            return json.Append("]}").ToString();
        }

        private static class RichTextNative
        {
            [StructLayout(LayoutKind.Sequential)]
            internal struct ScrollPoint { internal int X; internal int Y; }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct CharFormat
            {
                internal uint Size;
                internal uint Mask;
                internal uint Effects;
                internal int Height;
                internal int Offset;
                internal int TextColor;
                internal byte CharacterSet;
                internal byte PitchAndFamily;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                internal string FaceName;
                internal ushort Weight;
                internal short Spacing;
                internal int BackColor;
                internal uint Locale;
                internal uint Reserved;
                internal short Style;
                internal ushort Kerning;
                internal byte UnderlineType;
                internal byte Animation;
                internal byte RevisionAuthor;
                internal byte UnderlineColor;
            }

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref ScrollPoint point);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, ref CharFormat format);
        }
    }
}
