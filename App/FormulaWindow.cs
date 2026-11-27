using Berwick.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Berwick.Workshop;

/// <summary>
/// Read-only Formula Editor: each formula's parts, what kind of part each is, the rule that holds each editable part (with its original
/// and current setting), and a calculator comparing the original disc with the project. Changes are made on the Game rules or Skills page.
/// </summary>
public sealed class FormulaWindow : Window
{
    private readonly EditSession session;
    public int? SelectedRule { get; private set; }
    private readonly ListBox list = new() { Width = 200, Margin = new(0, 0, 14, 0) };
    private readonly StackPanel details = new();

    public FormulaWindow(EditSession session)
    {
        this.session = session;
        Title = "Formulas";
        Width = 980; Height = 760; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        AutomationProperties.SetName(list, "Formulas");
        foreach (var formula in Formulas.All) list.Items.Add(new ListBoxItem { Content = formula.Name, Tag = formula });
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: Formula f }) Show(f); };
        var layout = new DockPanel { Margin = new(16) };
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12), Foreground = Brushes.DimGray,
            Text = "How the game works out these values, using the same settings as the Game rules and Skills pages. Nothing here changes the project: open an editable part using its rule link, change it, then reopen the calculator. The calculator is a guide, not a replacement for testing in the game." };
        DockPanel.SetDock(intro, Dock.Top); layout.Children.Add(intro);
        DockPanel.SetDock(list, Dock.Left); layout.Children.Add(list);
        layout.Children.Add(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
        list.SelectedIndex = 0;
    }

    private static TextBlock Text(string text, double size = 13, bool bold = false, Brush? colour = null) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new(0, 0, 0, 6), Foreground = colour ?? Brushes.Black,
    };

    private static string KindText(FormulaTermKind kind) => kind switch
    {
        FormulaTermKind.Input => "From the situation",
        FormulaTermKind.Fixed => "Fixed by the game",
        FormulaTermKind.Editable => "Editable rule (see evidence)",
        FormulaTermKind.Derived => "Worked out",
        FormulaTermKind.Conditional => "Only sometimes",
        FormulaTermKind.Limit => "Limit",
        _ => "Not known yet",
    };

    private void Show(Formula formula)
    {
        details.Children.Clear();
        details.Children.Add(Text(formula.Name, 20, true));
        details.Children.Add(Text(formula.Expression, 15));
        details.Children.Add(Text("Parts", 15, true));
        foreach (var term in formula.Terms)
        {
            string rule = "";
            if (term.RuleId is int id)
            {
                var r = GameRules.ById(id);
                if (session.IsRuleAvailable(id))
                {
                    int original = session.OriginalValue(EditSession.Rules, id, r.Key), current = session.Value(EditSession.Rules, id, r.Key);
                    rule = $" Rule “{r.Name}”: original {r.Describe(original)}, project {r.Describe(current)}{(original == current ? "" : " (changed)")}.";
                    if (r.ExperimentalEditor) rule += " Experimental; gameplay verification pending.";
                }
                else rule = $" Rule “{r.Name}”: unavailable for this disc.";
            }
            string unit = term.Unit.Length > 0 ? $" ({term.Unit})" : "";
            details.Children.Add(Text($"{term.Name}{unit}: {KindText(term.Kind)}. {term.Explanation}{rule}"));
            if (term.RuleId is int linked && session.IsRuleAvailable(linked))
            {
                var edit = new Button { Content = "Open rule", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 8) };
                edit.Click += (_, _) => { SelectedRule = linked; Close(); };
                details.Children.Add(edit);
            }
        }
        details.Children.Add(Text("Limits of what is known", 15, true));
        foreach (string limit in formula.Limits) details.Children.Add(Text(limit, 13, false, Brushes.DimGray));
        details.Children.Add(Text("Evidence: " + formula.Evidence, 12, false, Brushes.DimGray));
        details.Children.Add(Text("Try it", 15, true));
        details.Children.Add(Calculator(formula));
    }

    private UIElement Calculator(Formula formula)
    {
        var panel = new StackPanel();
        var boxes = new Dictionary<string, TextBox>();
        var checks = new Dictionary<string, CheckBox>();
        void Box(string key, string label, string value)
        {
            var heading = Text(label, 13, true);
            var box = new TextBox { Text = value, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 6) };
            AutomationProperties.SetLabeledBy(box, heading);
            panel.Children.Add(heading); panel.Children.Add(box); boxes[key] = box;
        }
        void Tick(string key, string label) { var c = new CheckBox { Content = label, Margin = new(0, 0, 0, 6) }; panel.Children.Add(c); checks[key] = c; }
        switch (formula.Key)
        {
            case "hit":
                Box("acc", "Weapon accuracy (as on the Items page)", "70"); Box("lvl", "Weapon level (for example 8 or 0.5)", "8");
                Box("bonus", "Other hit bonuses", "0"); Box("speed", "Target's attack speed", "0"); Box("terrain", "Target's terrain avoid", "0");
                Box("avoid", "Target's other avoid", "0"); Tick("ranged", "The attack comes from 2 or more hexes away");
                break;
            case "failure":
                Box("rank", "Item rank", "12"); Box("level", "Level it is compared with", "9");
                break;
            default:
                Box("acc", "Shield accuracy", "30"); Box("lvl", "Shield level (for example 12.5)", 12.5m.ToString(System.Globalization.CultureInfo.CurrentCulture)); Tick("faire", "The unit has Shieldfaire"); Tick("eligible", "A shield block roll is eligible for this attack"); checks["eligible"].IsChecked = true;
                break;
        }
        var result = Text("", 14, true);
        var steps = Text("", 13, false, Brushes.DimGray);
        var calc = new Button { Content = "Calculate", HorizontalAlignment = HorizontalAlignment.Left, Padding = new(12, 4, 12, 4), Margin = new(0, 4, 0, 8) };
        calc.Click += (_, _) =>
        {
            int N(string key) => int.TryParse(boxes[key].Text.Trim(), out int v) ? v : throw new FormatException($"{key}");
            int Tenths(string key)
            {
                if (!decimal.TryParse(boxes[key].Text.Trim(), System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.CurrentCulture, out decimal v) || v < 0 || v > 102.3m || v * 10 != decimal.Truncate(v * 10))
                    throw new EditException("Weapon and shield levels must be between 0 and 102.3, with at most one decimal place.");
                return (int)(v * 10);
            }
            try
            {
                var r = formula.Key switch
                {
                    "hit" => Formulas.Hit(session, new(N("acc"), Tenths("lvl"), N("bonus"), N("speed"), N("terrain"), N("avoid"), checks["ranged"].IsChecked == true)),
                    "failure" => Formulas.Failure(session, new(N("rank"), N("level"))),
                    _ => Formulas.Block(session, new(N("acc"), Tenths("lvl"), checks["faire"].IsChecked == true, checks["eligible"].IsChecked == true)),
                };
                string Value(int? value) => value?.ToString() ?? "Not calculated";
                result.Text = $"Raw estimate: Original disc {Value(r.Original)}. This project {Value(r.Current)}.";
                steps.Text = $"Original: {r.OriginalSteps}\nProject: {r.CurrentSteps}";
            }
            catch (FormatException) { result.Text = "Enter whole numbers (weapon and shield levels may have one decimal place)."; steps.Text = ""; }
            catch (EditException ex) { result.Text = ex.Message; steps.Text = ""; }
            catch (OverflowException) { result.Text = "The inputs are too large to calculate."; steps.Text = ""; }
        };
        void ClearResult() { result.Text = ""; steps.Text = ""; }
        foreach (var box in boxes.Values) box.TextChanged += (_, _) => ClearResult();
        foreach (var check in checks.Values) { check.Checked += (_, _) => ClearResult(); check.Unchecked += (_, _) => ClearResult(); }
        panel.Children.Add(calc); panel.Children.Add(result); panel.Children.Add(steps);
        return panel;
    }
}
