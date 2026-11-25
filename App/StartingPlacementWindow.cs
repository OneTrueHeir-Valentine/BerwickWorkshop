using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

internal sealed class StartingPlacementWindow : Window
{
    private readonly ComboBox units = new() { DisplayMemberPath = "Label", MinWidth = 440 };
    private readonly CheckBox showAll = new() { Content = "Show placed and unsupported records", Margin = new(0, 8, 0, 0) };
    private readonly IReadOnlyList<StartingUnitCandidate> candidates;
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly TextBox column = new() { Text = "1", Width = 60 };
    private readonly TextBox row = new() { Text = "1", Width = 60 };
    private readonly CheckBox acknowledge = new() { Content = "I understand that the listed battles share these positions.", Margin = new(0, 10, 0, 10) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button place = new() { Content = "Restore placement", Margin = new(0, 0, 8, 0), MinWidth = 100 };
    private readonly Button remove = new() { Content = "Remove starting placement", Margin = new(0, 0, 8, 0) };
    private readonly Func<StartingUnitCandidate, bool, int, int, bool, bool> applyChange;
    private sealed record UnitChoice(StartingUnitCandidate Candidate)
    {
        public string Label => $"{Candidate.Unit.Name}, army {Candidate.Assignment.Army:000}, record {Candidate.Unit.Index}, group {Candidate.Assignment.Group}, slot {Candidate.Slot}" +
            (Candidate.Problem is not null ? " (unsupported)" : Candidate.PlacementProblem is not null ? " (unverified)" :
                Candidate.Occupied ? " (placed)" : " (removed starting placement)");
    }

    public StartingPlacementWindow(int battle, IReadOnlyList<StartingUnitCandidate> candidates, int? selectedKey,
        Func<StartingUnitCandidate, bool, int, int, bool, bool> apply)
    {
        this.candidates = candidates;
        applyChange = apply;
        Title = $"Battle {battle} starting unit placements";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);
        Content = BuildContent();
        units.SelectionChanged += (_, _) => UpdateSelection();
        showAll.Checked += (_, _) => RefreshChoices();
        showAll.Unchecked += (_, _) => RefreshChoices();
        acknowledge.Checked += (_, _) => UpdateButtons();
        acknowledge.Unchecked += (_, _) => UpdateButtons();
        place.Click += (_, _) => Apply(false);
        remove.Click += (_, _) => Apply(true);
        RefreshChoices(selectedKey);
    }

    private ScrollViewer BuildContent()
    {
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Place an existing army unit", FontSize = 22 });
        panel.Children.Add(new TextBlock
        {
            Text = "Only units already in this battle's assigned armies are listed. Team and group come from the opening event. Removing a starting placement never deletes the army record.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 10, 0, 12)
        });
        panel.Children.Add(units);
        panel.Children.Add(showAll);
        panel.Children.Add(details);
        var coordinates = new StackPanel { Orientation = Orientation.Horizontal };
        AddCoordinate(coordinates, "Column", column);
        AddCoordinate(coordinates, "Row", row);
        panel.Children.Add(coordinates);
        panel.Children.Add(acknowledge);
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", MinWidth = 70, IsCancel = true };
        buttons.Children.Add(place);
        buttons.Children.Add(remove);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void RefreshChoices(int? selectedKey = null)
    {
        selectedKey ??= (units.SelectedItem as UnitChoice)?.Candidate.Unit.Key;
        var choices = candidates.Where(candidate => showAll.IsChecked == true || candidate.CanPlace)
            .Select(candidate => new UnitChoice(candidate)).ToList();
        units.ItemsSource = choices;
        units.SelectedItem = choices.FirstOrDefault(choice => choice.Candidate.Unit.Key == selectedKey);
        if (units.SelectedIndex < 0 && choices.Count > 0)
            units.SelectedIndex = 0;
        UpdateSelection();
    }

    private void Apply(bool removing)
    {
        if (units.SelectedItem is not UnitChoice choice)
            return;
        int x = 0;
        int y = 0;
        if (!removing && (!int.TryParse(column.Text, out x) || !int.TryParse(row.Text, out y)))
        {
            error.Text = "Enter a column and a row.";
            return;
        }
        try
        {
            if (!applyChange(choice.Candidate, removing, x, y, acknowledge.IsChecked == true))
            {
                error.Text = "No changes were made. Close and reopen starting placements to refresh the available records.";
                return;
            }
            DialogResult = true;
        }
        catch (Exception exception) when (exception is EditException or System.IO.IOException or System.IO.InvalidDataException)
        {
            error.Text = exception.Message;
        }
    }

    private void UpdateSelection()
    {
        error.Text = "";
        acknowledge.IsChecked = false;
        if (units.SelectedItem is not UnitChoice choice)
        {
            details.Text = showAll.IsChecked == true
                ? "No existing army records can be identified for this battle."
                : "No eligible unused starting slots are available. Show placed and unsupported records to inspect existing placements and refusal reasons.";
            acknowledge.Visibility = Visibility.Collapsed;
            UpdateButtons();
            return;
        }
        var candidate = choice.Candidate;
        var source = candidate.Assignment;
        details.Text = $"Army ud{source.Army:000}, record {candidate.Unit.Index} (key 0x{candidate.Unit.Key:X}, offset 0x{candidate.Unit.RecordOffset:X}).\n" +
            $"Opening event {source.OpeningEvent}, source event {source.Event} at 0x{source.Offset:X}. Team {source.Team}, group {source.Group}, slot {candidate.Slot}.\n" +
            $"Known army battles: {string.Join(", ", candidate.ArmyBattles)}. Known army event references: {string.Join(", ", candidate.Events)}.\n" +
            $"Battles sharing this map's positions: {string.Join(", ", candidate.MapBattles)}.\n" +
            (candidate.Problem ?? (candidate.Occupied ? "This record has coordinates. Removal clears only this map slot." +
                (candidate.PlacementProblem is null ? "" : "\n" + candidate.PlacementProblem) :
                candidate.PlacementProblem ?? "This starting placement was removed from the source map. Enter the hex where it should start.")) +
            "\nThe army record and later arrival commands remain unchanged. Placing a unit now does not cancel its later arrivals.";
        acknowledge.Visibility = candidate.MapBattles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var candidate = (units.SelectedItem as UnitChoice)?.Candidate;
        bool confirmed = candidate is not null && (candidate.MapBattles.Count == 1 || acknowledge.IsChecked == true);
        place.IsEnabled = confirmed && candidate?.CanPlace == true;
        remove.IsEnabled = confirmed && candidate?.CanRemove == true;
        column.IsEnabled = candidate?.CanPlace == true;
        row.IsEnabled = column.IsEnabled;
    }

    private static void AddCoordinate(Panel panel, string label, TextBox input)
    {
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) });
        input.Margin = new(0, 0, 16, 0);
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(input);
    }
}
