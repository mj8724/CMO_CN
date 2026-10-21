using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Real WinForms ToolTip components; never loads or initializes the game.
internal static class HelpTooltipTests
{
    private static Type engine;
    private static int checks;
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Source = "The current phase of the mission in the Operation Planner\r\n\r\nWaiting for trigger:\r\nThe mission has not yet started, the multi-mission units assigned to the mission, which aren't mobilized won’t execute the mission.\r\n\r\nSatisfied:\r\nThe mission is considered to have achieved enough of its objective for assigned units to consider other missions, but a satisfied mission does not necessarily end. This tag is used for mission triggers and for multi-mission priority.\r\n\r\nTriggered: \r\nThe mission will have its assigned units to mobilise and execute the mission, depending on its priority.";

    private static object Call(string name, params object[] args)
    {
        MethodInfo method = engine.GetMethod(name, PrivateStatic | BindingFlags.Public);
        if (method == null) throw new InvalidOperationException("Missing engine method: " + name);
        return method.Invoke(null, args);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static ComboBox PhaseCombo(string name)
    {
        ComboBox combo = new ComboBox { Name = name, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.Add("Waiting for trigger");
        combo.Items.Add("Satisfied");
        combo.SelectedIndex = 1;
        combo.Tag = "MISSION_PHASE_SATISFIED";
        return combo;
    }

    private static void MissionPhase(string expected)
    {
        using (Command.MissionEditor form = new Command.MissionEditor())
        using (Panel panel = new Panel())
        using (ComboBox combo = PhaseCombo("Combo_MissionPhase"))
        using (ComboBox adjacent = PhaseCombo("Combo_MissionPhase_Other"))
        {
            form.Controls.Add(panel);
            panel.Controls.Add(combo);
            panel.Controls.Add(adjacent);
            ToolTip tooltip = form.TooltipComponent;
            tooltip.SetToolTip(combo, Source);
            tooltip.SetToolTip(adjacent, Source);
            string text = combo.Text;
            object first = combo.Items[0];
            object second = combo.Items[1];
            object selected = combo.SelectedItem;
            object key = combo.Tag;
            int textChanges = 0;
            int selectionChanges = 0;
            combo.TextChanged += delegate { textChanges++; };
            combo.SelectedIndexChanged += delegate { selectionChanges++; };

            Call("TranslateControlText", combo);
            Require(tooltip.GetToolTip(combo) == expected, "Actual mission-phase ToolTip stayed English");
            Require(tooltip.GetToolTip(adjacent) == Source, "Adjacent control's ToolTip was changed");
            for (int i = 0; i < 5; i++) Call("TranslateControlText", combo);
            Require(tooltip.GetToolTip(combo) == expected, "Repeated tooltip refresh was not idempotent");

            tooltip.SetToolTip(combo, Source);
            Call("TranslateControlText", combo);
            Require(tooltip.GetToolTip(combo) == expected, "Game-refreshed tooltip was not translated again");
            Require(combo.Text == text && combo.SelectedIndex == 1, "Tooltip translation changed ComboBox text or selection");
            Require(combo.Items.Count == 2 && object.ReferenceEquals(first, combo.Items[0])
                && object.ReferenceEquals(second, combo.Items[1]), "Tooltip translation replaced ComboBox items");
            Require(object.ReferenceEquals(selected, combo.SelectedItem) && object.ReferenceEquals(key, combo.Tag),
                "Tooltip translation changed selected item or business key");
            Require(textChanges == 0 && selectionChanges == 0, "Tooltip translation raised ComboBox input events");

            tooltip.SetToolTip(combo, "");
            Call("TranslateControlText", combo);
            Require(tooltip.GetToolTip(combo) == "", "An absent tooltip was invented");
        }
    }

    private static void WrongTargets()
    {
        using (OtherCommand.MissionEditor form = new OtherCommand.MissionEditor())
        using (ComboBox combo = PhaseCombo("Combo_MissionPhase"))
        {
            form.Name = "MissionEditor";
            form.Controls.Add(combo);
            form.TooltipComponent.SetToolTip(combo, Source);
            Call("TranslateControlText", combo);
            Require(form.TooltipComponent.GetToolTip(combo) == Source, "Wrong form type's tooltip was changed");
        }
        using (Command.MissionEditor form = new Command.MissionEditor())
        using (ComboBox combo = PhaseCombo("Combo_MissionPhase_Other"))
        {
            form.Controls.Add(combo);
            form.TooltipComponent.SetToolTip(combo, Source);
            Call("TranslateControlText", combo);
            Require(form.TooltipComponent.GetToolTip(combo) == Source, "Wrong control name's tooltip was changed");
        }
        using (ComboBox orphan = PhaseCombo("Combo_MissionPhase"))
        {
            Call("TranslateControlText", orphan);
            Require(orphan.Text == "Satisfied", "Orphan input was changed");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected engine DLL and official dictionary path");
            string dictionary = Path.GetFullPath(args[1]);
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(dictionary));
            Dictionary<string, object> exact = (Dictionary<string, object>)data["exact"];
            string expected = (string)exact[Source];
            Require(expected != Source && Regex.IsMatch(expected, "[\\u3400-\\u9fff]"), "Official tooltip translation is missing");
            engine = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("CMOLocalizer.CustomAppDomainManager", true);
            Call("LoadDictionaries", dictionary);
            MissionPhase(expected);
            WrongTargets();
            Console.WriteLine("PASS helptooltip checks=" + checks);
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL " + error);
            return 1;
        }
    }
}

internal abstract class TooltipFixtureForm : Form
{
    private readonly IContainer components = new Container();
    private readonly ToolTip phaseTooltip;

    protected TooltipFixtureForm() { phaseTooltip = new ToolTip(components); }
    internal ToolTip TooltipComponent { get { return phaseTooltip; } }

    protected override void Dispose(bool disposing)
    {
        if (disposing) components.Dispose();
        base.Dispose(disposing);
    }
}

namespace Command
{
    internal sealed class MissionEditor : TooltipFixtureForm
    {
        private ToolTip GlobalTooltip { get { return TooltipComponent; } }
    }
}

namespace OtherCommand
{
    internal sealed class MissionEditor : TooltipFixtureForm
    {
        private ToolTip GlobalTooltip { get { return TooltipComponent; } }
    }
}
