using Berwick.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private ScriptDependencyIndex? eventDependencyIndex;
    private EventLibrary? eventDependencySource;
    private sealed record DependencyRow(ScriptDependencySite Site, string Text);

    private async Task ShowEventDependencies(EventLibrary library)
    {
        if (eventDependencySource != library || eventDependencyIndex is null)
        {
            EventDependencyStatus.Text = "Indexing source operations from this disc and project...";
            var overrides = new Dictionary<int, byte[]>();
            if (session is not null)
            {
                foreach (int number in session.EditedRecords
                    .Where(r => r.Table is EditSession.Scripts or EditSession.ScriptValues)
                    .Select(r => r.Table == EditSession.Scripts ? r.Id : ScriptValues.Split(r.Id).Event)
                    .Distinct())
                {
                    if (session.EventScript(number) is { } bytes) overrides[number] = bytes;
                }
            }
            ScriptDependencyIndex? index;
            try
            {
                index = await Task.Run(() => ScriptDependencyIndex.Build(library.Numbers, n =>
                {
                    if (overrides.TryGetValue(n, out var bytes)) return bytes;
                    lock (library) return library.Bytes(n);
                }, library.CommandNames));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                EventDependencyStatus.Text = "Source dependency index unavailable: " + ex.Message;
                return;
            }
            if (events != library) return;
            eventDependencyIndex = index;
            eventDependencySource = library;
        }
        RefreshEventDependencies();
    }

    /// <summary>
    /// Obtain a fresh script-item reference snapshot for the randomizer even when the
    /// Events page has never been opened. Read-only, and includes project overrides.
    /// </summary>
    private async Task<ScriptDependencyIndex> DependencyIndexForRandomizerAsync(bool reopenDisc = false)
    {
        // Rebuild on each randomizer preview. A cached event index may precede project script edits.
        if (session is null) throw new InvalidOperationException("There is no active project.");
        var overrides = new Dictionary<int, byte[]>();
        foreach (int number in session.EditedRecords
            .Where(r => r.Table is EditSession.Scripts or EditSession.ScriptValues)
            .Select(r => r.Table == EditSession.Scripts ? r.Id : ScriptValues.Split(r.Id).Event).Distinct())
            if (session.EventScript(number) is { } bytes) overrides[number] = bytes;
        if (!reopenDisc && events is { } opened)
        {
            var built = await Task.Run(() => ScriptDependencyIndex.Build(opened.Numbers, n =>
            {
                if (overrides.TryGetValue(n, out var updated)) return updated;
                lock (opened) return opened.Bytes(n);
            }, opened.CommandNames));
            if (events == opened) { eventDependencyIndex = built; eventDependencySource = opened; }
            return built;
        }
        string path = session.Disc.Path;
        return await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var library = new EventLibrary(new GameArchives(new IsoImage(stream)));
            return ScriptDependencyIndex.Build(library.Numbers, n => overrides.TryGetValue(n, out var updated)
                ? updated : library.Bytes(n), library.CommandNames);
        });
    }

    private static readonly ScriptDependencyKind?[] DependencyFamilies =
    [
        null, ScriptDependencyKind.Flags, ScriptDependencyKind.Quests,
        ScriptDependencyKind.Classes, ScriptDependencyKind.Skills,
        ScriptDependencyKind.InventoryOperations, ScriptDependencyKind.ItemReferences,
        ScriptDependencyKind.Shops, ScriptDependencyKind.Horses, ScriptDependencyKind.Units
    ];

    private void EventDependencyCategoryChanged(object sender, SelectionChangedEventArgs e) => RefreshEventDependencies();
    private void EventDependencySearchChanged(object sender, TextChangedEventArgs e) => RefreshEventDependencies();
    private void EventDependencyVerifiedChanged(object sender, RoutedEventArgs e) => RefreshEventDependencies();

    private void RefreshEventDependencies()
    {
        if (EventDependencyList is null || EventDependencyStatus is null || EventDependencySearch is null
            || EventDependencyKind is null || EventDependencyVerifiedOnly is null
            || eventDependencyIndex is not { } index) return;
        int selection = EventDependencyKind.SelectedIndex;
        ScriptDependencyKind? family = selection >= 0 && selection < DependencyFamilies.Length
            ? DependencyFamilies[selection] : null;
        string query = EventDependencySearch.Text.Trim();
        bool verifiedOnly = EventDependencyVerifiedOnly.IsChecked == true;
        var rows = index.Filter(family, verifiedOnly)
            .Where(site => query.Length == 0 || site.Event.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
                || site.Command.Contains(query, StringComparison.OrdinalIgnoreCase)
                || site.Arguments.Any(a => a.Value?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .Take(6000)
            .Select(site => new DependencyRow(site, $"Event {site.Event}: {site.Command} "
                + $"({string.Join(", ", site.Arguments.Select(a => a.IsResolved ? a.Value!.Value.ToString() : "?"))})"
                + (site.Conditional ? " [conditional]" : "")
                + " · Source observed"))
            .ToArray();
        EventDependencyList.ItemsSource = rows;
        EventDependencyStatus.Text = $"{rows.Length:N0} source operation sites displayed"
            + (rows.Length == 6000 ? " (display limit 6,000; narrow the filter)" : "")
            + $". {index.IndexedEvents:N0} events indexed; {index.UnreadableEvents.Count} unreadable. "
            + "Visible by default, but execution and argument semantics remain unverified. "
            + "Direct numeric literals can be reviewed in the existing Numbers editor; computed operands are not safely bound.";
    }

    private void EventDependencySelected(object sender, SelectionChangedEventArgs e)
    {
        if (EventDependencyList?.SelectedItem is not DependencyRow row) return;
        var site = row.Site;
        EventDependencyStatus.Text = $"Event {site.Event}, {site.Command} (0x{site.Opcode:X}): "
            + $"{site.Arguments.Count} argument(s), {site.EditableNumberIndices.Count} directly editable number(s); "
            + (site.Conditional ? "inside a conditional branch; " : "")
            + "source observed, untested. Double-click to inspect; changes need separate gameplay validation.";
    }

    private async void EventDependencyNavigate(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (EventDependencyList?.SelectedItem is not DependencyRow row || events is not { } library) return;
        int target = row.Site.Event;
        if (!library.Numbers.Contains(target)) return;
        ShowEventList(library.Numbers, $"{library.Numbers.Count:N0} events");
        EventList.SelectedValue = target;
        EventList.ScrollIntoView(EventList.SelectedItem);
        ScriptTabs.SelectedIndex = 0;
        await ShowEvent(library, target, scrollHome: true);
        if (events != library || EventList.SelectedValue is not int selected || selected != target) return;
        if (row.Site.EditableNumberIndices.FirstOrDefault(-1) is int place && place >= 0)
        {
            int key = ScriptValues.Key(target, place);
            ScriptNumberList.SelectedValue = key;
            if (ScriptNumberList.SelectedItem is not null)
                ScriptNumberList.ScrollIntoView(ScriptNumberList.SelectedItem);
        }
    }

    private void ExportEventDependencyIndex(object sender, RoutedEventArgs e)
    {
        if (eventDependencyIndex is not { } index || primary is null)
        {
            EventDependencyStatus.Text = "Open Dependencies first to build a source index before exporting.";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export source dependency index for research or randomizer",
            Filter = "JSON source index (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "berwick-source-dependencies.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        // This is an evidence/data export only. It does not patch game data or randomize anything.
        var snapshot = new
        {
            Schema = "berwick-workshop.source-dependencies",
            SchemaVersion = 1,
            SourceEdition = primary.Edition,
            SourceSha256 = primary.Sha256,
            Evidence = "SourceObserved; behavior untested; conditional scripts may never execute",
            IndexCoverage = new { index.IndexedEvents, index.UnreadableEvents },
            SourceItemReferences = index.ItemReferences,
            Sites = index.Sites
        };
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true, Converters =
                { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            EventDependencyStatus.Text = "Dependency research index exported. Original game and project unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EventDependencyStatus.Text = "Export failed: " + ex.Message;
        }
    }
}
