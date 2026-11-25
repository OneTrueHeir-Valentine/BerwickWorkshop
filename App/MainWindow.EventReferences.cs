using Berwick.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private EventReferenceIndex? eventReferenceIndex;
    private EventLibrary? eventReferenceSource;

    private async void EventTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScriptTabs is null || !ReferenceEquals(e.Source, ScriptTabs)
            || ScriptTabs.SelectedIndex is not (1 or 2 or 3) || events is not { } library
            || EventList.SelectedValue is not int selected) return;
        if (ScriptTabs.SelectedIndex == 3) await ShowEventDependencies(library);
        else await ShowEventReferences(library, selected);
    }

    private async Task ShowEventReferences(EventLibrary library, int number)
    {
        if (eventReferenceSource != library || eventReferenceIndex is null)
        {
            EventReferencesStatus.Text = "Indexing exact event calls from this project...";
            // Take a snapshot of the project's overrides on the UI thread.
            // A project rewrite is never performed by this read-only index.
            var overrides = new Dictionary<int, byte[]>();
            if (session is not null)
            {
                foreach (int edited in session.EditedRecords
                    .Where(x => x.Table is EditSession.Scripts or EditSession.ScriptValues)
                    .Select(x => x.Table == EditSession.ScriptValues
                        ? Berwick.Core.ScriptValues.Split(x.Id).Event : x.Id).Distinct())
                {
                    if (session.EventScript(edited) is { } bytes) overrides[edited] = bytes;
                }
            }
            EventReferenceIndex? index = null;
            try
            {
                index = await Task.Run(() => EventReferenceIndex.Build(library.Numbers, n =>
                {
                    if (overrides.TryGetValue(n, out var bytes)) return bytes;
                    lock (library) return library.Bytes(n);
                }));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                EventReferencesStatus.Text = "References unavailable: " + ex.Message;
                return;
            }
            if (events != library || index is null) return;
            eventReferenceIndex = index;
            eventReferenceSource = library;
            RefreshEventCommandList(library);
        }
        if (EventList.SelectedValue is not int current || current != number || events != library) return;
        var found = eventReferenceIndex;
        EventCallersList.ItemsSource = found.Callers(number).Select(n => new Choice(n, $"Event {n}")).ToList();
        EventCalleesList.ItemsSource = found.Calls(number).Select(n => new Choice(n, $"Event {n}")).ToList();
        EventReferencesStatus.Text = $"Direct literal calls only: {found.Callers(number).Count} incoming, "
            + $"{found.Calls(number).Count} outgoing. {found.IndexedEvents:N0} events indexed. "
            + $"{found.DynamicCallers.Count} events have unresolved computed calls; "
            + $"{found.UnreadableEvents.Count} could not be parsed. Conditional calls are not guaranteed to execute.";
    }

    private void EventCommandSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (EventCommandList is not null && events is { } library && eventReferenceIndex is not null)
            RefreshEventCommandList(library);
    }

    private void RefreshEventCommandList(EventLibrary library)
    {
        if (eventReferenceIndex is null || EventCommandList is null || EventCommandSearch is null) return;
        string query = EventCommandSearch.Text.Trim();
        var rows = eventReferenceIndex.IndexedCommands.Select(command =>
            new Choice(command, $"{ScriptText.Name(command, library.CommandNames)} (0x{command:X}) · "
                + $"{eventReferenceIndex.EventsUsingCommand(command).Count} events"))
            .Where(row => query.Length == 0 || row.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int? previous = EventCommandList.SelectedValue as int?;
        EventCommandList.ItemsSource = rows;
        if (previous is int id) EventCommandList.SelectedValue = id;
        EventCommandStatus.Text = $"{rows.Count} commands displayed; source-observed and not proof of behavior. Double-click a listed event to inspect it.";
    }

    private void EventCommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventCommandList is null || EventCommandSiteList is null || eventReferenceIndex is null
            || EventCommandList.SelectedValue is not int command) return;
        var found = eventReferenceIndex.EventsUsingCommand(command);
        EventCommandSiteList.ItemsSource = found.Select(n => new Choice(n, $"Event {n}")).ToList();
        EventCommandStatus.Text = $"Command 0x{command:X}: {found.Count} source occurrences by event. Original execution conditions are unverified.";
    }

    private void EventReferenceNavigate(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBox { SelectedValue: int number } || events is not { } library) return;
        if (!library.Numbers.Contains(number))
        {
            Status.Text = $"Event {number} is referenced but is not present on this disc.";
            return;
        }
        ShowEventList(library.Numbers, $"{library.Numbers.Count:N0} events");
        EventList.SelectedValue = number;
        EventList.ScrollIntoView(EventList.SelectedItem);
    }
}
