using Berwick.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void AddPromotionWindowEditor(PromotionPath path)
    {
        if (session is null) return;
        AddSection(PromotionDetails, "Campaign availability");
        (int Start, int End) window;
        try { window = PromotionQuestWindows.Read(session, path.UnitId); }
        catch (Exception ex) when (ex is EditException or IOException)
        {
            AddText(PromotionDetails, ex.Message);
            return;
        }
        var rule = PromotionQuestWindows.RuleFor(path.UnitId);
        AddText(PromotionDetails, $"Quest {PromotionQuestWindows.QuestFor(path.UnitId)}. Original campaign counters: {GameRules.RangeLow(rule.Default)} to {GameRules.RangeHigh(rule.Default)}, inclusive.");
        AddText(PromotionDetails, "Campaign counter is separate from chapter and battle turn. Story gates, phase, quest history and offer priority are unchanged. Existing saves may retain a previously adopted quest.");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        row.Children.Add(new TextBlock { Text = "Start", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var start = new TextBox { Text = window.Start.ToString(CultureInfo.InvariantCulture), Width = 55 };
        row.Children.Add(start);
        row.Children.Add(new TextBlock { Text = "End", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
        var end = new TextBox { Text = window.End.ToString(CultureInfo.InvariantCulture), Width = 55 };
        row.Children.Add(end);
        var apply = new Button { Content = "Apply", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) =>
        {
            if (session is null || loading is not null) return;
            if (!int.TryParse(start.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)
                || !int.TryParse(end.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int last))
            {
                Status.Text = "Enter whole campaign counters from 0 to 63.";
                return;
            }
            try
            {
                bool changed = PromotionQuestWindows.SetWindow(session, path.UnitId, first, last);
                if (changed) RefreshDocument();
                Status.Text = changed ? "Promotion campaign window changed." : "The promotion campaign window is unchanged.";
            }
            catch (Exception ex) when (ex is EditException or IOException) { Status.Text = ex.Message; }
        };
        row.Children.Add(apply);
        PromotionDetails.Children.Add(row);
    }
}
