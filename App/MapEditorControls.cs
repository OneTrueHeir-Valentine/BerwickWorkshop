using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

internal static class MapEditorControls
{
    public static void Arrival(Panel panel, Reinforcement arrival, bool editable, Action<int,int> setHex,
        Action<int> setTurn, Action pickHex, Action<string> status)
    {
        MapText.Section(panel,"Arrival");
        string when = arrival.Turn is int t ? arrival.LastTurn is int last && last != t ? $"turns {t} to {last}" : $"turn {t}" : "when an event sets it off";
        if (arrival.Spot is { } spot) when = $"after a unit uses {spot.Name} at {spot.X}, {spot.Y}" + (arrival.Turn is not null ? $" ({when} at most)" : "");
        else if (arrival.Talk is not null) when = "after a conversation" + (arrival.Turn is not null ? $" ({when} at most)" : "");
        MapText.Text(panel,$"Arrives at {arrival.X}, {arrival.Y}{(arrival.FromX != arrival.X || arrival.FromY != arrival.Y ? $", marching in from {arrival.FromX}, {arrival.FromY}" : "")}, {when}{(arrival.Conditional ? ", if its other conditions are met" : "")}.");
        if (!editable) return;
        if (arrival.Places is null) { MapText.Text(panel,"This arrival's hex comes from a calculation in its event, so it is changed on the Events page."); return; }
        var row = Row();
        var x = Input(row,"Column",arrival.X,"Arrival column");
        var y = Input(row,"Row",arrival.Y,"Arrival row");
        Button(row,"Set",() =>
        {
            if (int.TryParse(x.Text.Trim(),out int px) && int.TryParse(y.Text.Trim(),out int py)) setHex(px,py);
            else status("Enter a column and a row.");
        },panel);
        Button(row,"Pick on map",pickHex,panel); panel.Children.Add(row);
        if (arrival.TurnPlace is not null && arrival.Turn is int first)
        {
            var turnRow = Row();
            var turn = Input(turnRow,"First turn",first,"Arrival turn");
            Button(turnRow,"Set turn",() =>
            {
                if (int.TryParse(turn.Text.Trim(),out int value) && value >= 1 && value <= 99) setTurn(value);
                else status("Enter a turn from 1 to 99.");
            },panel);
            panel.Children.Add(turnRow); MapText.Text(panel,"Units brought in by the same turn check change turn together.");
        }
    }
    public static void Deployment(Panel panel, (ScriptNumber Number, bool Own)? limit, int hexCount, bool editable,
        Action<int,bool> setLimit, Action<string> status)
    {
        if (limit is not { } found) { MapText.Text(panel,"The most units you can deploy is not set by this battle's events."); return; }
        int value = found.Number.Value;
        MapText.Text(panel,found.Own ? $"Most units you can deploy: {value} (set by this battle, event {found.Number.Event})"
            : $"Most units you can deploy: {value} (the game's default for every battle that does not set its own, event {Berwick.Core.Deployment.DefaultEvent})");
        if (hexCount < value) MapText.Text(panel,$"Only {hexCount} hexes are marked, so no more than {hexCount} units can be placed.");
        if (hexCount > value) MapText.Text(panel,$"The game marks only the first {value} hexes, in the order they were added; the other {hexCount-value} (dashed) are not used.");
        if (!editable) return;
        var row = Row();
        var box = new TextBox { Width = 50, Text = value.ToString(), Margin = new(0,0,6,0) };
        System.Windows.Automation.AutomationProperties.SetName(box,"Most units you can deploy"); row.Children.Add(box);
        void Set(bool own)
        {
            if (int.TryParse(box.Text.Trim(),out int n) && n >= 1 && n <= 63) setLimit(n,own);
            else status("Enter a number from 1 to 63.");
        }
        Button(row,found.Own ? "Set" : "Set for every battle",() => Set(false),panel);
        if (!found.Own) Button(row,"Set for this battle only",() => Set(true),panel);
        panel.Children.Add(row);
    }
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Margin = new(0,4,0,4) };
    private static TextBox Input(Panel row, string label, int value, string accessibleName)
    {
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new(0,0,4,0) });
        var box = new TextBox { Width = 40, Text = value.ToString(), Margin = new(0,0,8,0) };
        System.Windows.Automation.AutomationProperties.SetName(box,accessibleName); row.Children.Add(box); return box;
    }
    private static void Button(Panel row, string label, Action click, FrameworkElement resources)
    {
        var button = new Button { Content = label, Style = (Style)resources.FindResource("SmallButton"), Margin = new(0,0,6,0) };
        button.Click += (_,_) => click(); row.Children.Add(button);
    }
}
