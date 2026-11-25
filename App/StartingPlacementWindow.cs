using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

internal sealed class StartingPlacementWindow : Window
{
    private readonly ComboBox units = new() { DisplayMemberPath = "Label", MinWidth = 440 };
    private readonly CheckBox showAll = new() { Content = "Show units on the map and units that cannot be restored", Margin = new(0, 8, 0, 0) };
    private readonly IReadOnlyList<StartingUnitCandidate> candidates;
    private readonly TextBlock details = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 12) };
    private readonly CheckBox acknowledge = new() { Content = "I understand that the listed battles share these positions.", Margin = new(0, 10, 0, 10) };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly Button place = new() { Content = "Restore original position", Margin = new(0, 0, 8, 0), MinWidth = 100 };
    private readonly Button remove = new() { Content = "Remove starting position", Margin = new(0, 0, 8, 0) };
    private readonly Func<StartingUnitCandidate, bool, int, int, bool, bool> applyChange;
    private sealed record UnitChoice(StartingUnitCandidate Candidate)
    {
        public string Label => $"{Candidate.Unit.Name}, army {Candidate.Assignment.Army:000}, record {Candidate.Unit.Index}, group {Candidate.Assignment.Group}, slot {Candidate.Slot}" +
            (Candidate.Problem is not null ? " (unavailable)" : Candidate.Occupied ? " (on the map)" :
                Candidate.PlacementProblem is not null ? " (cannot be restored)" : " (removed; can be restored)");
    }

    public StartingPlacementWindow(int battle, IReadOnlyList<StartingUnitCandidate> candidates, int? selectedKey,
        Func<StartingUnitCandidate, bool, int, int, bool, bool> apply)
    {
        this.candidates = candidates;
        applyChange = apply;
        Title = $"Battle {battle} starting positions";
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
        panel.Children.Add(new TextBlock { Text = "Restore original starting positions", FontSize = 22 });
        panel.Children.Add(new TextBlock
        {
            Text = "Put back a unit whose starting position was removed in this project, at the hex it has on the original disc. Only units the battle already loads are listed. This does not add new units. Removing a starting position never deletes the army record.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new(0, 10, 0, 12)
        });
        panel.Children.Add(units);
        panel.Children.Add(showAll);
        panel.Children.Add(details);
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
        if (!removing)
        {
            if (choice.Candidate.OriginalPosition is not { } original)
            {
                error.Text = "This unit has no original starting position to restore.";
                return;
            }
            (x, y) = original;
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
                : "No removed starting positions can be restored in this battle. Show the other units to see their positions and why they cannot be restored.";
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
            (candidate.Problem ?? (candidate.Occupied ? "This unit is on the map. Removal clears only this map slot." +
                (candidate.PlacementProblem is null ? "" : "\n" + candidate.PlacementProblem) :
                candidate.PlacementProblem ?? (candidate.OriginalPosition is { } hex
                    ? $"This starting position was removed in this project. It will be restored at column {hex.X}, row {hex.Y}, where the original disc places it."
                    : "This unit has no original starting position to restore."))) +
            "\nThe army record and later arrival commands remain unchanged. Restoring a unit does not cancel its later arrivals.";
        acknowledge.Visibility = candidate.MapBattles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var candidate = (units.SelectedItem as UnitChoice)?.Candidate;
        bool confirmed = candidate is not null && (candidate.MapBattles.Count == 1 || acknowledge.IsChecked == true);
        place.IsEnabled = confirmed && candidate?.CanPlace == true && candidate.OriginalPosition is not null;
        remove.IsEnabled = confirmed && candidate?.CanRemove == true;
    }
}
