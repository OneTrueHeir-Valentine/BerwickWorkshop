using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private sealed record RandomizerReviewRow(string Character, string Change, string Before, string After)
    {
        // Read aloud where a grid announces the whole row, instead of the raw record.
        public override string ToString() => $"{Character}, {Change}: {Before} to {After}";
    }

    private static DataGrid RandomizerReviewGrid(IEnumerable<RandomizerReviewRow> rows, string changeHeader)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = rows.ToArray(),
            Margin = new Thickness(0, 0, 0, 12) };
        void Add(string header, string property, double width)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            grid.Columns.Add(new DataGridTextColumn { Header = header,
                Binding = new System.Windows.Data.Binding(property), ElementStyle = style,
                Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = 100 });
        }
        Add("Character", nameof(RandomizerReviewRow.Character), 1);
        Add(changeHeader, nameof(RandomizerReviewRow.Change), 1.2);
        Add("Before", nameof(RandomizerReviewRow.Before), 1.5);
        Add("After", nameof(RandomizerReviewRow.After), 1.5);
        return grid;
    }

    private static string RandomizerFieldName(string key)
    {
        string name = key.EndsWith("Growth", StringComparison.Ordinal) ? key[..^6]
            : key.EndsWith("Bonus", StringComparison.Ordinal) || key.EndsWith("Level", StringComparison.Ordinal) ? key[..^5] : key;
        return name switch
        {
            "hp" => "HP", "smallshield" => "Small Shield", "mediumshield" => "Medium Shield",
            "largeshield" => "Large Shield", "shield" => "Shield (shared growth)",
            "crossbow" => "Crossbow", "" => key,
            _ => char.ToUpperInvariant(name[0]) + name[1..]
        };
    }

    // Empty IDs in existing profiles mean all characters, never no characters.
    private sealed class RandomizerCharacterSelection
    {
        private readonly (int Id, string Name)[] roster;
        private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8) };
        private int[] ids = [];
        public StackPanel View { get; } = new();

        public RandomizerCharacterSelection(EditSession source, Window owner)
        {
            roster = source.Disc.Characters.Select(c => (c.Id, c.Name))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToArray();
            var choose = new Button { Content = "Choose characters…", HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(9, 4, 9, 4) };
            choose.Click += (_, _) => Choose(owner);
            View.Children.Add(choose);
            View.Children.Add(summary);
            UpdateSummary();
        }

        public int[] GetIds() => (int[])ids.Clone();

        public void SetIds(int[] value)
        {
            if (value.Any(id => !roster.Any(c => c.Id == id)))
                throw new EditException("The saved selection contains a character not found on this disc. The selection was not changed.");
            ids = (int[])value.Clone();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            summary.Text = ids.Length == 0 ? $"All {roster.Length} characters" :
                $"{ids.Length} of {roster.Length} characters selected: "
                + string.Join(", ", roster.Where(c => ids.Contains(c.Id)).Take(5).Select(c => c.Name))
                + (ids.Length > 5 ? ", and others" : "");
        }

        private void Choose(Window owner)
        {
            var selected = (ids.Length == 0 ? roster.Select(c => c.Id) : ids.AsEnumerable()).ToHashSet();
            var dialog = new Window { Owner = owner, Title = "Choose characters", Width = 460, Height = 570,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var layout = new DockPanel { Margin = new Thickness(14) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
            top.Children.Add(new TextBlock { Text = "Search by name, then tick the characters to change. Select all and Clear selection affect the entire roster. Searching does not change your selection.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 7), ToolTip = "Search character names" };
            System.Windows.Automation.AutomationProperties.SetName(search, "Search character names");
            top.Children.Add(search);
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var all = new Button { Content = "Select all", Margin = new Thickness(0, 0, 8, 0) };
            var none = new Button { Content = "Clear selection" };
            tools.Children.Add(all); tools.Children.Add(none); top.Children.Add(tools);
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            var count = new TextBlock { Margin = new Thickness(0, 7, 0, 7) }; footer.Children.Add(count);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 4) };
            var use = new Button { Content = "Use selection", IsDefault = true, Margin = new Thickness(0, 0, 0, 4) };
            buttons.Children.Add(cancel); buttons.Children.Add(use); footer.Children.Add(buttons);
            var list = new StackPanel();
            layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            void UpdateCount() { count.Text = $"{selected.Count} of {roster.Length} selected"; use.IsEnabled = selected.Count > 0; }
            void Refresh()
            {
                list.Children.Clear();
                foreach (var c in roster.Where(c => c.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    string label = roster.Count(x => x.Name == c.Name) > 1 ? $"{c.Name} ({c.Id:X})" : c.Name;
                    var check = new CheckBox { Content = label, IsChecked = selected.Contains(c.Id), Margin = new Thickness(3, 3, 0, 3) };
                    check.Checked += (_, _) => { selected.Add(c.Id); UpdateCount(); };
                    check.Unchecked += (_, _) => { selected.Remove(c.Id); UpdateCount(); };
                    list.Children.Add(check);
                }
                if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "No matching characters." });
                UpdateCount();
            }
            search.TextChanged += (_, _) => Refresh();
            all.Click += (_, _) => { selected.UnionWith(roster.Select(c => c.Id)); Refresh(); };
            none.Click += (_, _) => { selected.Clear(); Refresh(); };
            int[]? result = null;
            use.Click += (_, _) => { if (selected.Count > 0) { result = selected.Count == roster.Length ? [] : selected.OrderBy(id => id).ToArray(); dialog.DialogResult = true; } };
            Refresh(); dialog.Content = layout;
            if (dialog.ShowDialog() == true && result is not null) SetIds(result);
        }
    }

    private bool ConfirmR0Preview(Window owner, R0StartingItemPreview preview, string? auditError,
        out bool permitReferences, out bool permitIncomplete)
    {
        permitReferences = false;
        permitIncomplete = false;
        if (preview.Replacements.Count == 0)
        {
            MessageBox.Show(owner, "No starting items would change with these options and seed.",
                "Starting-item preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var dialog = new Window { Title = "Review proposed starting-item changes", Owner = owner,
            Width = 940, Height = 690, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false };
        var root = new DockPanel { Margin = new Thickness(17) };
        var footer = new StackPanel { Orientation = Orientation.Vertical };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        if (session is null) return false;
        var names = session.Disc.Characters.ToDictionary(c => c.Id, c => c.Name);
        var items = session.EditedItems().ToDictionary(i => i.Id);
        string ItemName(int id) => items.TryGetValue(id, out var item)
            ? item.DisplayName : $"Unknown item (ID {id})";
        var headline = new TextBlock {
            Text = $"{preview.Replacements.Count} starting-item changes for {preview.Replacements.Select(c => c.UnitId).Distinct().Count()} characters. Seed: {preview.Options.Seed}. "
                + $"Inventory scope: {(preview.Options.ExtendedInventory ? "slots 1–8" : "slots 1–5")}. "
                + "Compare the original and replacement items below. The number in parentheses is the item-use value recorded by the game.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(headline, Dock.Top); root.Children.Add(headline);
        var warn = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.DarkOrange, Text =
                "Nothing has been changed yet. Apply adds these changes to the project; it does not write to the original disc. Gameplay compatibility still needs checking." };
        DockPanel.SetDock(warn, Dock.Top); root.Children.Add(warn);
        root.Children.Add(RandomizerReviewGrid(preview.Replacements.Select(c => new RandomizerReviewRow(
            names.TryGetValue(c.UnitId, out var name) ? name : $"Unknown character (ID {c.UnitId})",
            c.Table == EditSession.Bags ? $"Bag slot {c.Slot + 1}" : $"Inventory slot {c.Slot + 1}",
            $"{ItemName(c.FromItem)} (uses: {c.FromUses})", $"{ItemName(c.ToItem)} (uses: {c.ToUses})")), "Location"));
        var ackReferences = new CheckBox { Margin = new Thickness(0, 7, 0, 4),
            Content = $"I understand that {preview.ScriptReferences.Count} of these items are mentioned in game events." };
        if (preview.ScriptReferences.Count > 0) footer.Children.Add(ackReferences);
        var ackIncomplete = new CheckBox { Margin = new Thickness(0, 7, 0, 4),
            Content = "I understand that some game events could not be checked." };
        if (!preview.ScriptAuditComplete)
        {
            footer.Children.Add(new TextBlock { Text = auditError is not null
                    ? "Script audit unavailable; this preview may be saved but cannot be applied: " + auditError
                    : $"{preview.UnreadableEvents.Count} event scripts were unreadable; some references may be missing.",
                Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0) });
            footer.Children.Add(ackIncomplete);
        }
        if (preview.ScriptReferences.Count > 0)
        {
            footer.Children.Add(new TextBlock { Text =
                "Referenced items: " + string.Join("; ", preview.ScriptReferences.Take(12).Select(r =>
                    $"{ItemName(r.ItemId)} (events {string.Join(", ", r.Events.Take(6))})"))
                + (preview.ScriptReferences.Count > 12 ? "; ..." : ""),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) });
        }
        var error = new TextBlock { Foreground = Brushes.DarkRed, Margin = new Thickness(0, 6, 0, 6),
            TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(error);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Save detailed report (JSON)", Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Title = "Save starting-item preview", FileName = "berwick-r0-preview.json",
                Filter = "JSON files|*.json" };
            if (picker.ShowDialog(dialog) == true)
            {
                try { File.WriteAllText(picker.FileName, preview.ToJson()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { error.Text = "Could not save preview: " + ex.Message; }
            }
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply to project", IsDefault = true,
            IsEnabled = auditError is null, Style = (Style)FindResource("PrimaryButton") };
        apply.Click += (_, _) =>
        {
            if (preview.ScriptReferences.Count > 0 && ackReferences.IsChecked != true)
            { error.Text = "Acknowledge the script-reference warning or Cancel."; return; }
            if (!preview.ScriptAuditComplete && ackIncomplete.IsChecked != true)
            { error.Text = "Acknowledge the incomplete audit or Cancel."; return; }
            dialog.DialogResult = true;
        };
        bar.Children.Add(save); bar.Children.Add(cancel); bar.Children.Add(apply);
        footer.Children.Add(bar);
        dialog.Content = root;
        if (dialog.ShowDialog() != true) return false;
        permitReferences = ackReferences.IsChecked == true;
        permitIncomplete = ackIncomplete.IsChecked == true;
        return true;
    }
}
