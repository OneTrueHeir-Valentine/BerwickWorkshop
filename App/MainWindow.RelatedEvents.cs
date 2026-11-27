using Berwick.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void AddRecordEvents(Panel panel, View page, RelatedRecord record, int id)
    {
        if (session is null) return;
        AddSection(panel, "Related events");
        AddText(panel, "Find literal references in the current project. Conditional and indirect relationships may be incomplete.");
        if (record == RelatedRecord.Character && PromotionCatalogue.Read(primary) is { OriginalSourceVerified: true } catalogue &&
            catalogue.Paths.FirstOrDefault(p => p.UnitId == id) is { } promotion)
        {
            AddText(panel, "Original promotion route (may differ from edited scripts):");
            foreach (var link in RelatedEvents.Promotion(promotion)) AddRelatedEventButton(panel, link.Event, link.Reason);
        }
        AddRelatedEventSearch(panel, page, "Find related event references", index => RelatedEvents.Discover(record, id, index));
    }

    private void AddRelatedEventSearch(Panel panel, View page, string caption, Func<ScriptDependencyIndex, IEnumerable<RelatedEvent>> discover)
    {
        var scan = new Button { Content = caption, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
        var results = new StackPanel();
        panel.Children.Add(scan);
        panel.Children.Add(results);
        scan.Click += async (_, _) =>
        {
            if (session is null || loading is not null) return;
            if (editing is not null) { Status.Text = "Apply or cancel the current edit first."; return; }
            var source = session;
            scan.IsEnabled = false;
            results.Children.Clear();
            AddText(results, "Reading current event scripts...");
            try
            {
                var index = await DependencyIndexForRandomizerAsync(reopenDisc: true);
                if (session != source || view != page || !panel.Children.Contains(results)) return;
                results.Children.Clear();
                AddText(results, $"{index.IndexedEvents:N0} events indexed; {index.UnreadableEvents.Count:N0} unreadable. Computed IDs and indirect calls may be missed.");
                var links = discover(index).ToArray();
                if (links.Length == 0) AddText(results, "No matching direct references found. This is not proof of absence.");
                foreach (var link in links) AddRelatedEventButton(results, link.Event, link.Reason);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or EditException)
            {
                if (session != source || view != page || !panel.Children.Contains(results)) return;
                results.Children.Clear();
                AddText(results, "Search unavailable: " + ex.Message);
            }
            finally { scan.IsEnabled = true; }
        };
    }
}
