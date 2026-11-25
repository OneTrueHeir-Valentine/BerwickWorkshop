using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Berwick.Core;
using Microsoft.Win32;

namespace Berwick.Workshop;

internal sealed class ChangeReviewWindow : Window
{
    private readonly TextBlock summary = new() { Text = "Checking project changes...", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) };
    private readonly TextBox search = new() { Margin = new(0, 0, 8, 8), ToolTip = "Search changes, checks and files" };
    private readonly ComboBox category = new() { MinWidth = 150, Margin = new(0, 0, 0, 8) };
    private readonly DataGrid changes = Grid(), checks = Grid(), files = Grid();
    private readonly TextBox original = Detail(), current = Detail();
    private readonly Button save = new() { Content = "Save manifest", IsEnabled = false, Margin = new(0, 0, 8, 0) };
    private readonly Button proceed = new() { Content = "Continue to export", IsEnabled = false, Margin = new(0, 0, 8, 0) };
    private ProjectReview? report;
    private bool busy = true;

    public ChangeReviewWindow(EditSession session, bool exporting)
    {
        Title = "Review changes"; Width = 1080; Height = 760; MinWidth = 760; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new DockPanel { Margin = new(24) }; Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Review changes", FontSize = 26, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(summary);
        heading.Children.Add(new TextBlock { Text = "Checks cover known data dependencies and patch conflicts. Script behavior, learned skill unlocks and saved units still need playtesting. Export verifies the source disc and original bytes.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 0, 0, 12) });
        var filter = new DockPanel(); heading.Children.Add(filter);
        DockPanel.SetDock(category, Dock.Right); filter.Children.Add(category); filter.Children.Add(search);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(save); if (exporting) footer.Children.Add(proceed);
        var close = new Button { Content = exporting ? "Back" : "Close", IsCancel = true };
        close.Click += (_, _) => Close(); footer.Children.Add(close);
        var details = new System.Windows.Controls.Grid { Height = 190, Margin = new(0, 12, 0, 0) };
        details.ColumnDefinitions.Add(new()); details.ColumnDefinitions.Add(new());
        AddDetail(details, "Original", original, 0); AddDetail(details, "Current", current, 1);
        DockPanel.SetDock(details, Dock.Bottom); root.Children.Add(details);
        var tabs = new TabControl(); root.Children.Add(tabs);
        tabs.Items.Add(new TabItem { Header = "Changes", Content = changes });
        tabs.Items.Add(new TabItem { Header = "Checks", Content = checks });
        tabs.Items.Add(new TabItem { Header = "Files", Content = files });
        Column(changes, "Table", "Table", 115); Column(changes, "Record", "Record", 175); Column(changes, "Field", "Field", 180);
        Column(changes, "Original", "Original", 130); Column(changes, "Current", "Current", 130); Column(changes, "File", "File", 120);
        Column(changes, "Evidence", "Evidence.Label", 215);
        Column(checks, "Result", "Severity", 85); Column(checks, "Record", "Id", 65); Column(checks, "Details", "Message", 760);
        Column(files, "File", "File", 230); Column(files, "Disc offset", "Offset", 160); Column(files, "Bytes", "Length", 100);
        changes.SelectionChanged += (_, _) => { if (changes.SelectedItem is ChangeEntry row) Show(row.Original, row.Current); };
        checks.SelectionChanged += (_, _) => { if (checks.SelectedItem is ReviewIssue row) Show("", row.Message); };
        files.SelectionChanged += (_, _) => { if (files.SelectedItem is FileChange row) Show(row.Original, row.Current); };
        tabs.SelectionChanged += (sender, e) => { if (ReferenceEquals(e.Source, tabs)) Show("", ""); };
        search.TextChanged += (_, _) => Filter(); category.SelectionChanged += (_, _) => Filter();
        save.Click += (_, _) => Save(session);
        proceed.Click += (_, _) => { if (report is { CanExport: true } && report.Patches.Count > 0) DialogResult = true; };
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Loaded += async (_, _) =>
        {
            try
            {
                report = await Task.Run(() => ProjectReview.Build(session));
                int errors = report.Issues.Count(i => i.Severity == "Error"), warnings = report.Issues.Count - errors;
                summary.Text = $"{report.Changes.Count} changes · {report.Files.Select(f => f.File).Distinct().Count()} disc files · {errors} errors · {warnings} to review" +
                    (report.Patches.Count == 0 ? "\nNo disc changes to export. Project names are saved in the project file." : "");
                category.ItemsSource = new[] { "All tables" }.Concat(report.Changes.Select(c => c.Table).Distinct()).ToArray(); category.SelectedIndex = 0;
                save.IsEnabled = true; proceed.IsEnabled = report.CanExport && report.Patches.Count > 0;
                Filter(); if (errors > 0) tabs.SelectedIndex = 1;
            }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException)
            { summary.Text = "The review could not be completed. " + ex.Message; }
            finally { busy = false; }
        };
    }

    private void Filter()
    {
        if (report is null) return;
        string term = search.Text.Trim(), table = category.SelectedItem as string ?? "All tables";
        bool Matches(params string[] values) => term.Length == 0 || values.Any(v => v.Contains(term, StringComparison.OrdinalIgnoreCase));
        changes.ItemsSource = report.Changes.Where(c => (table == "All tables" || c.Table == table) && Matches(c.Table, c.Record, c.Field, c.Original, c.Current, c.File)).ToList();
        checks.ItemsSource = report.Issues.Where(i => (table == "All tables" || i.Table.Length == 0 || i.Table == table) && Matches(i.Severity, i.Message, i.Code)).ToList();
        files.ItemsSource = report.Files.Where(f => Matches(f.File, f.Offset.ToString())).ToList();
    }
    private void Show(string before, string after) { original.Text = before; current.Text = after; }
    private void Save(EditSession session)
    {
        if (report is null) return;
        var picker = new SaveFileDialog { Title = "Save change manifest", Filter = "JSON manifest (*.json)|*.json", DefaultExt = ".json", FileName = "Berwick changes.json", OverwritePrompt = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var document = new { Format = "Berwick Workshop change manifest", Version = 2, session.Disc.Edition, SourceSha256 = session.Disc.Sha256,
                report.CanExport, report.Changes, report.Issues, report.Files };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
            using var stream = new FileStream(picker.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, "The manifest could not be saved. Choose a new file name in a folder you can write to.", "Save manifest", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private static DataGrid Grid() => new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false };
    private static TextBox Detail() => new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private static void Column(DataGrid grid, string title, string property, double width) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = width, MinWidth = 60 });
    private static void AddDetail(System.Windows.Controls.Grid grid, string title, TextBox box, int column)
    {
        var panel = new DockPanel { Margin = new(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 0) };
        var label = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 5) };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(box);
        System.Windows.Controls.Grid.SetColumn(panel, column); grid.Children.Add(panel);
    }
}
