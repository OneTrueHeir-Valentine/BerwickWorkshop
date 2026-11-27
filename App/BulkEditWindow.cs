using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace Berwick.Workshop;

/// <summary>
/// Changes many records at once with a recipe: choose records by name, type or group, add steps, preview every change, then
/// apply it as one undo step. Recipes and selections can be saved and reused.
/// </summary>
internal sealed class BulkEditWindow : Window
{
    private readonly EditSession session;
    private readonly Action applied;
    private BulkPreview? preview;

    // Screen readers announce list entries by their text, so each choice reads as its label rather than as raw data.
    private sealed record TableChoice(string Table, string Label) { public override string ToString() => Label; }
    private sealed record RecordChoice(int Id, string Name)
    {
        public string Label => $"{Name} ({Id})";
        public override string ToString() => Label;
    }
    private sealed record FieldChoice(TableField Field, string Unit)
    {
        public string Label => Unit.Length > 0 ? $"{Field.Label} ({Unit})" : Field.Label;
        public override string ToString() => Label;
    }
    private sealed record OperationChoice(BulkOperation Operation, string Label) { public override string ToString() => Label; }
    private sealed record StepChoice(BulkStep Step, string Label) { public override string ToString() => Label; }

    private static readonly TableChoice[] Tables =
    [
        new(EditSession.Items, "Items"),
        new(EditSession.Characters, "Characters: level, stat bonuses and weapon levels"),
        new(EditSession.Growths, "Characters: growth rates and weapon growth"),
        new(EditSession.Classes, "Classes: base stats and experience"),
        new(EditSession.ArmyUnits, "Army units: level and stat bonuses"),
    ];
    private static readonly OperationChoice[] Operations =
    [
        new(BulkOperation.Set, "Set to"), new(BulkOperation.Add, "Add"), new(BulkOperation.Multiply, "Multiply by"), new(BulkOperation.Clamp, "Keep between"),
    ];

    private readonly ComboBox table = new() { DisplayMemberPath = "Label", MinWidth = 320 };
    private readonly TextBox search = new() { MinWidth = 220 };
    private readonly ComboBox armyNameFilter = new() { ItemsSource = new[] { "All army records", "Named records", "Records not marked named" }, SelectedIndex = 0 };
    private readonly StackPanel armyPicker = new() { Visibility = Visibility.Collapsed };
    private readonly ListBox records = new() { DisplayMemberPath = "Label", SelectionMode = SelectionMode.Extended, Height = 170 };
    private readonly ListBox included = new() { DisplayMemberPath = "Label", SelectionMode = SelectionMode.Extended, Height = 75 };
    private readonly ListBox excluded = new() { DisplayMemberPath = "Label", SelectionMode = SelectionMode.Extended, Height = 75 };
    private readonly StackPanel groups = new() { Margin = new(0, 6, 0, 0) };
    private readonly ListBox types = new() { SelectionMode = SelectionMode.Multiple, Height = 75 };
    private readonly CheckBox includeUnused = new() { Content = "Also include spare records and broken-weapon placeholders found by type or group", Margin = new(0, 6, 0, 0) };
    private readonly CheckBox includeSpecial = new() { Content = new TextBlock { Text = "Also include personal weapons, and items whose range differs from the rest, found by type or group", TextWrapping = TextWrapping.Wrap }, Margin = new(0, 4, 0, 0) };
    private readonly StackPanel itemFilters = new();

    private readonly ComboBox field = new() { DisplayMemberPath = "Label", MinWidth = 240 };
    private readonly ComboBox operation = new() { DisplayMemberPath = "Label", ItemsSource = Operations, SelectedIndex = 1, MinWidth = 120 };
    private readonly TextBox amount = new() { MinWidth = 70 };
    private readonly TextBlock andText = new() { Text = "and", VerticalAlignment = VerticalAlignment.Center, Margin = new(6, 0, 6, 0) };
    private readonly TextBox upper = new() { MinWidth = 70 };
    private readonly ListBox steps = new() { DisplayMemberPath = "Label", Height = 75 };
    private readonly ComboBox rounding = new() { ItemsSource = new[] { "Round to the nearest value", "Round down", "Round up" }, SelectedIndex = 0, MinWidth = 200 };

    private readonly DataGrid grid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 220, HeadersVisibility = DataGridHeadersVisibility.Column };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly Button apply = new() { Content = "Apply", MinWidth = 90, IsEnabled = false, Margin = new(8, 0, 0, 0) };

    public BulkEditWindow(EditSession session, Action applied)
    {
        this.session = session;
        this.applied = applied;
        Title = "Bulk edit";
        Width = 940;
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        MinWidth = 720;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Build();
        table.ItemsSource = Tables;
        table.SelectionChanged += (_, _) => TableChanged();
        search.TextChanged += (_, _) => FillRecords();
        armyNameFilter.SelectionChanged += (_, _) => FillRecords();
        operation.SelectionChanged += (_, _) => upper.Visibility = andText.Visibility = Clamp ? Visibility.Visible : Visibility.Collapsed;
        rounding.SelectionChanged += (_, _) => Stale();
        includeUnused.Checked += (_, _) => Stale();
        includeUnused.Unchecked += (_, _) => Stale();
        includeSpecial.Checked += (_, _) => Stale();
        includeSpecial.Unchecked += (_, _) => Stale();
        System.Windows.Automation.AutomationProperties.SetName(includeSpecial, "Also include personal weapons, and items whose range differs from the rest, found by type or group");
        types.SelectionChanged += (_, _) => Stale();
        apply.Click += (_, _) => Apply();
        table.SelectedIndex = 0;
        upper.Visibility = andText.Visibility = Visibility.Collapsed;
    }

    private bool Clamp => (operation.SelectedItem as OperationChoice)?.Operation == BulkOperation.Clamp;
    private string Table => ((TableChoice)table.SelectedItem).Table;

    private ScrollViewer Build()
    {
        var panel = new StackPanel { Margin = new(20) };
        panel.Children.Add(new TextBlock { Text = "Bulk edit", FontSize = 22 });
        panel.Children.Add(Wrap("Change the same fields on many records at once. Choose the records, add the steps, and preview every change before applying it. Applying is one step you can undo. Nothing is written to a disc image until you export the project.", new(0, 6, 0, 10)));
        panel.Children.Add(Labeled("Records to change", table));

        // Selection: search and named records on the left, included and excluded lists on the right.
        var selection = new Grid { Margin = new(0, 8, 0, 0) };
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Labeled("Search by name or number", search));
        armyPicker.Children.Add(Labeled("Show army records", armyNameFilter));
        armyPicker.Children.Add(Wrap("This filters the picker only. A named record is not necessarily a boss; other records are not necessarily ordinary enemies. Included and excluded records stay selected.", new(0, 4, 0, 4)));
        left.Children.Add(armyPicker);
        Label(records, "Records found");
        left.Children.Add(records);
        left.Children.Add(Row(Button("Include selected", () => Move(records, included)), Button("Exclude selected", () => Move(records, excluded))));
        Grid.SetColumn(left, 0);
        var right = new StackPanel();
        right.Children.Add(Bold("Included by name"));
        Label(included, "Included by name");
        right.Children.Add(included);
        right.Children.Add(Row(Button("Remove", () => Remove(included))));
        right.Children.Add(Bold("Always left out"));
        Label(excluded, "Always left out");
        right.Children.Add(excluded);
        right.Children.Add(Row(Button("Remove", () => Remove(excluded))));
        Grid.SetColumn(right, 2);
        selection.Children.Add(left);
        selection.Children.Add(right);
        panel.Children.Add(selection);

        itemFilters.Children.Add(Bold("Item groups (worked out from each item's data)"));
        itemFilters.Children.Add(groups);
        itemFilters.Children.Add(Bold("Item types"));
        Label(types, "Item types");
        types.ItemsSource = ItemDecoder.Types;
        itemFilters.Children.Add(types);
        itemFilters.Children.Add(includeUnused);
        itemFilters.Children.Add(includeSpecial);
        foreach (string group in BulkGroups.All)
        {
            var box = new CheckBox { Content = group, Tag = group, Margin = new(0, 2, 0, 0) };
            box.Checked += (_, _) => Stale();
            box.Unchecked += (_, _) => Stale();
            groups.Children.Add(box);
        }
        panel.Children.Add(itemFilters);
        panel.Children.Add(Row(Button("Save selection...", SaveSelection), Button("Load selection...", LoadSelection)));

        // Steps.
        panel.Children.Add(new TextBlock { Text = "Steps, in order", FontWeight = FontWeights.SemiBold, Margin = new(0, 14, 0, 2) });
        panel.Children.Add(Wrap("Steps run in this order on the value as shown. Only the final value has to fit the field; Keep between limits it. Examples: Add -5 to Accuracy lowers it by 5; Multiply Price by 1.1 raises it by 10%; Keep Maximum range between 1 and 3.", new(0, 0, 0, 4)));
        Label(field, "Field"); Label(operation, "Operation"); Label(amount, "Amount"); Label(upper, "Highest value");
        panel.Children.Add(Row(field, operation, amount, andText, upper, Button("Add step", AddStep)));
        Label(steps, "Steps, in order");
        panel.Children.Add(steps);
        panel.Children.Add(Row(Button("Move up", MoveStepUp), Button("Remove step", () => { if (steps.SelectedItem is { } s) { steps.Items.Remove(s); Stale(); } })));
        Label(rounding, "Rounding");
        panel.Children.Add(Labeled("When the result falls between two values the field can hold", rounding));

        // Preview.
        panel.Children.Add(Row(Button("Preview", Preview), apply, Button("Save recipe...", SaveRecipe), Button("Load recipe...", LoadRecipe)));
        panel.Children.Add(summary);
        foreach (var (header, path) in new[] { ("Record", "Record"), ("Field", "FieldLabel"), ("Unit", "Unit"), ("Original", "Original"), ("Current", "Current"), ("New", "Proposed"), ("Result", "Status"), ("Reason", "Reason") })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(path) });
        Label(grid, "Preview of every change");
        panel.Children.Add(grid);
        panel.Children.Add(message);
        var close = new Button { Content = "Close", MinWidth = 90, IsCancel = true, Margin = new(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void Label(UIElement element, string name) => AutomationProperties.SetName(element, name);
    private static TextBlock Wrap(string text, Thickness margin) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
    private static TextBlock Bold(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 2) };

    private static StackPanel Labeled(string label, Control control)
    {
        Label(control, label);
        var panel = new StackPanel { Margin = new(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0) };
        foreach (var item in items)
        {
            if (item is FrameworkElement f) f.Margin = new(0, 0, 8, 0);
            row.Children.Add(item);
        }
        return row;
    }

    private Button Button(string text, Action click)
    {
        var button = new Button { Content = text, MinWidth = 90, Padding = new(10, 3, 10, 3) };
        button.Click += (_, _) => click();
        return button;
    }

    private void TableChanged()
    {
        bool items = Table == EditSession.Items;
        itemFilters.Visibility = items ? Visibility.Visible : Visibility.Collapsed;
        armyPicker.Visibility = Table == EditSession.ArmyUnits ? Visibility.Visible : Visibility.Collapsed;
        var allowed = BulkEdit.SupportedFields[Table];
        var fields = session.Table(Table).Fields.Where(f => allowed.Contains(f.Key)).Select(f => new FieldChoice(f, BulkEdit.Unit(Table, f))).ToList();
        field.ItemsSource = fields;
        field.SelectedIndex = 0;
        included.Items.Clear();
        excluded.Items.Clear();
        steps.Items.Clear();
        foreach (CheckBox box in groups.Children) box.IsChecked = false;
        types.SelectedItems.Clear();
        FillRecords();
        Stale();
    }

    private void FillRecords()
    {
        string text = search.Text.Trim();
        var found = text.Length > 0 ? BulkEdit.Search(session, Table, text)
            : Table == EditSession.Items ? session.EditedItems().Select(i => (i.Id, i.Name)).ToList()
            : Table is EditSession.Classes or EditSession.ArmyUnits ? BulkEdit.Records(session, Table)
            : session.EditedCharacters().Select(c => (c.Id, c.Name)).ToList();
        if (Table == EditSession.ArmyUnits && armyNameFilter.SelectedIndex > 0)
        {
            var ids = session.Disc.Armies.SelectMany(a => a.Units)
                .Where(u => BulkEdit.MatchesArmyNameFilter(u, armyNameFilter.SelectedIndex)).Select(u => u.Key).ToHashSet();
            found = found.Where(r => ids.Contains(r.Id)).ToList();
        }
        records.ItemsSource = found.Select(r => new RecordChoice(r.Id, r.Name)).ToList();
    }

    private void Move(ListBox from, ListBox to)
    {
        foreach (var r in from.SelectedItems.Cast<RecordChoice>().ToList())
            if (!to.Items.Cast<RecordChoice>().Any(x => x.Id == r.Id)) to.Items.Add(r);
        Stale();
    }

    private void Remove(ListBox list)
    {
        foreach (var r in list.SelectedItems.Cast<object>().ToList()) list.Items.Remove(r);
        Stale();
    }

    private void AddStep()
    {
        if (field.SelectedItem is not FieldChoice f || operation.SelectedItem is not OperationChoice op) return;
        if (!decimal.TryParse(amount.Text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal value) &&
            !TryChoice(f.Field, amount.Text, out value))
        { message.Text = f.Field.Choices is { } c ? $"{f.Field.Label} is a choice: type one of {string.Join(", ", c)}." : "Type a number, for example 5, -5 or 1.5."; return; }
        decimal? high = null;
        if (op.Operation == BulkOperation.Clamp)
        {
            if (!decimal.TryParse(upper.Text.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal u))
            { message.Text = "Type the highest value as a number."; return; }
            high = u;
        }
        if (f.Field.Choices is { } choices && (op.Operation != BulkOperation.Set || value != decimal.Truncate(value) || value < 0 || value >= choices.Count))
        { message.Text = $"For {f.Field.Label}, choose Set to and enter one of {string.Join(", ", choices)}."; return; }
        if (high is { } limit && limit < value)
        { message.Text = "The highest value cannot be below the lowest value."; return; }
        var step = new BulkStep(f.Field.Key, op.Operation, value, high);
        steps.Items.Add(new StepChoice(step, Describe(step, f)));
        message.Text = "";
        Stale();
    }

    private static bool TryChoice(TableField field, string text, out decimal index)
    {
        index = 0;
        if (field.Choices is not { } choices) return false;
        int i = choices.ToList().FindIndex(c => c.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        index = i;
        return true;
    }

    private static string Describe(BulkStep step, FieldChoice f) => step.Operation switch
    {
        BulkOperation.Set when f.Field.Choices is { } c && step.Amount == decimal.Truncate(step.Amount) && step.Amount >= 0 && step.Amount < c.Count => $"Set {f.Field.Label} to {c[(int)step.Amount]}",
        BulkOperation.Set => $"Set {f.Field.Label} to {step.Amount}",
        BulkOperation.Add => $"Add {step.Amount} to {f.Field.Label}",
        BulkOperation.Multiply => $"Multiply {f.Field.Label} by {step.Amount}",
        _ => $"Keep {f.Field.Label} between {step.Amount} and {step.Upper}",
    } + (f.Unit.Length > 0 ? $" ({f.Unit})" : "");

    private void MoveStepUp()
    {
        int i = steps.SelectedIndex;
        if (i <= 0) return;
        var item = steps.Items[i];
        steps.Items.RemoveAt(i);
        steps.Items.Insert(i - 1, item);
        steps.SelectedIndex = i - 1;
        Stale();
    }

    private BulkSelection Selection() => new(
        included.Items.Cast<RecordChoice>().Select(r => r.Id).ToList(),
        Table == EditSession.Items ? types.SelectedItems.Cast<string>().ToList() : [],
        Table == EditSession.Items ? groups.Children.Cast<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList() : [],
        excluded.Items.Cast<RecordChoice>().Select(r => r.Id).ToList(),
        includeUnused.IsChecked == true, includeSpecial.IsChecked == true);

    private BulkRecipe Recipe() => new(BulkRecipe.CurrentVersion, "Recipe", Table, Selection(), steps.Items.Cast<StepChoice>().Select(s => s.Step).ToList(),
        (BulkRounding)rounding.SelectedIndex) { Schema = BulkEdit.Schema(Table) };

    /// <summary>Anything that changes the recipe makes the shown preview out of date.</summary>
    private void Stale()
    {
        if (preview is null) return;
        preview = null;
        apply.IsEnabled = false;
        summary.Text = "The recipe changed. Preview it again before applying.";
    }

    private void Preview()
    {
        try
        {
            preview = BulkEdit.Preview(session, Recipe());
        }
        catch (EditException ex) { preview = null; grid.ItemsSource = null; apply.IsEnabled = false; summary.Text = ex.Message; return; }
        grid.ItemsSource = preview.Rows;
        int unchanged = preview.Rows.Count(r => r.Status == BulkRowStatus.Unchanged);
        int selected = preview.Rows.Select(r => r.Id).Distinct().Count();
        summary.Text = $"{selected} {(selected == 1 ? "record" : "records")} selected: {preview.ChangedRecords} change, {preview.BlockedRecords} blocked, " +
                       $"{unchanged} {(unchanged == 1 ? "field is" : "fields are")} already at the new value. {preview.Skipped.Count} left out." +
                       (preview.Warnings.Count > 0 ? "\nWarnings: " + string.Join(" ", preview.Warnings) : "") +
                       (preview.Skipped.Count > 0 ? "\nLeft out: " + string.Join("; ", preview.Skipped.Select(s => $"{s.Record} ({s.Id}): {(s.Reason.StartsWith("Left out: ", StringComparison.Ordinal) ? s.Reason[10..] : s.Reason)}")) : "") +
                       (preview.BlockedRecords > 0 ? "\nBlocked records must be left out or the steps changed before applying." : "");
        apply.IsEnabled = preview.CanApply;
        message.Text = "";
    }

    private void Apply()
    {
        if (preview is null) return;
        try
        {
            bool changed = BulkEdit.Apply(session, preview);
            message.Text = changed ? "Applied as one step. Use Undo in the main window to take it back." : "Nothing needed to change.";
            applied();
        }
        catch (EditException ex) { message.Text = ex.Message; }
        preview = null;
        apply.IsEnabled = false;
    }

    private void SaveRecipe()
    {
        var picker = new SaveFileDialog { Title = "Save recipe", Filter = "Bulk edit recipes (*.bwrecipe.json)|*.bwrecipe.json", DefaultExt = ".bwrecipe.json", AddExtension = true, OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var recipe = Recipe() with { Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(picker.FileName)) };
            BulkEdit.ValidateRecipe(session, recipe);
            SaveNewFile(picker.FileName, BulkEdit.Save(recipe));
            message.Text = "Recipe saved.";
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { message.Text = ex.Message; }
    }

    private void LoadRecipe()
    {
        var picker = new OpenFileDialog { Title = "Load recipe", Filter = "Bulk edit recipes (*.bwrecipe.json)|*.bwrecipe.json|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            ShowRecipe(BulkEdit.Load(File.ReadAllBytes(picker.FileName)));
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { message.Text = ex.Message; }
    }

    internal void ShowRecipe(BulkRecipe recipe)
    {
        // Validate the saved schema and selections before replacing any controls.
        BulkEdit.ValidateRecipe(session, recipe);
        var target = Tables.Single(t => t.Table == recipe.Table);
        var newSteps = recipe.Steps.Select(step =>
        {
            var f = session.Table(recipe.Table).Field(step.Field);
            return new StepChoice(step, Describe(step, new FieldChoice(f, BulkEdit.Unit(recipe.Table, f))));
        }).ToList();
        Stale();
        table.SelectedItem = target;
        ShowSelection(recipe.Selection);
        steps.Items.Clear();
        foreach (var step in newSteps) steps.Items.Add(step);
        rounding.SelectedIndex = (int)recipe.Rounding;
        message.Text = $"Loaded {recipe.Name}. Preview it to see what it changes in this project.";
    }

    private void SaveSelection()
    {
        var picker = new SaveFileDialog { Title = "Save selection", Filter = "Saved selections (*.bwselection.json)|*.bwselection.json", DefaultExt = ".bwselection.json", AddExtension = true, OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var selection = Selection();
            BulkEdit.ValidateSelection(session, Table, selection);
            SaveNewFile(picker.FileName, BulkEdit.SaveSelection(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(picker.FileName)), Table, selection));
            message.Text = "Selection saved.";
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { message.Text = ex.Message; }
    }

    private void LoadSelection()
    {
        var picker = new OpenFileDialog { Title = "Load selection", Filter = "Saved selections (*.bwselection.json)|*.bwselection.json|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var saved = BulkEdit.LoadSelection(File.ReadAllBytes(picker.FileName));
            BulkEdit.ValidateSelection(session, saved.Table, saved.Selection);
            if (saved.Table != Table) table.SelectedItem = Tables.First(t => t.Table == saved.Table);
            ShowSelection(saved.Selection);
            message.Text = $"Loaded the selection {saved.Name}.";
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { message.Text = ex.Message; }
    }

    private static void SaveNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private void ShowSelection(BulkSelection selection)
    {
        string NameOf(int id) => BulkEdit.Search(session, Table, id.ToString()).FirstOrDefault(r => r.Id == id).Name ?? $"Record {id}";
        included.Items.Clear();
        excluded.Items.Clear();
        foreach (int id in selection.Ids) included.Items.Add(new RecordChoice(id, NameOf(id)));
        foreach (int id in selection.Excluded) excluded.Items.Add(new RecordChoice(id, NameOf(id)));
        foreach (CheckBox box in groups.Children) box.IsChecked = selection.Groups.Contains((string)box.Tag);
        types.SelectedItems.Clear();
        foreach (string type in selection.ItemTypes) types.SelectedItems.Add(ItemDecoder.Types.First(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)));
        includeUnused.IsChecked = selection.IncludeUnused;
        includeSpecial.IsChecked = selection.IncludeSpecial;
        Stale();
    }
}
