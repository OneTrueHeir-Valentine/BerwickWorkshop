using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void AddFurnitureEvents(int piece)
    {
        AddPair(FurnitureFields, "Related events", "Open a known event, or scan the current disc and project for literal furniture flag references. References may be conditional.");
        foreach (var reference in FurnitureEvents.Known(piece))
            AddRelatedEventButton(FurnitureFields, reference.Event, reference.Reason);
        if (FurnitureEvents.EffectFlag(piece) is int flag)
            AddPair(FurnitureFields, "Original effect flag", flag.ToString());
        else
            AddPair(FurnitureFields, "Original effect flag", "Not mapped for this piece.");
        AddRelatedEventSearch(FurnitureFields, View.Furniture, "Find related flag references",
            index => FurnitureEvents.Discover(piece, index).Select(link => new RelatedEvent(link.Event, link.Reason)));
    }

    private void AddRelatedEventButton(Panel panel, int eventNumber, string reason)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = $"Event {eventNumber}: {reason}", TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Open event {eventNumber}: {reason}");
        button.Click += async (_, _) =>
        {
            // Do not discard an unfinished editor draft to navigate.
            if (loading is not null || session is null) return;
            if (editing is not null) { Status.Text = "Apply or cancel the current edit first."; return; }
            var source = session;
            button.IsEnabled = false;
            try
            {
                view = View.Events;
                UpdatePage();
                await OpenEvents();
                if (view != View.Events || session != source) return;
                if (events is not { } library || !library.Numbers.Contains(eventNumber))
                { Status.Text = $"Event {eventNumber} is not available on this disc."; return; }
                ShowEventList(library.Numbers, $"{library.Numbers.Count:N0} events");
                EventList.SelectedValue = eventNumber;
                if (EventList.SelectedItem is not null) EventList.ScrollIntoView(EventList.SelectedItem);
            }
            finally { button.IsEnabled = true; }
        };
        panel.Children.Add(button);
    }
}
