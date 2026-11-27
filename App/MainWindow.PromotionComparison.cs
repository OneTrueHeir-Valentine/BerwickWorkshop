using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void AddPromotionClassComparison(PromotionPath path)
    {
        AddSection(PromotionDetails, "Class comparison");
        var character = session is null ? primary?.Characters.FirstOrDefault(c => c.Id == path.UnitId)
            : session.EditedCharacters().FirstOrDefault(c => c.Id == path.UnitId);
        var classes = session is null ? primary?.Classes : session.EditedClasses();
        var source = classes?.FirstOrDefault(c => c.Id == character?.ClassId);
        int targetId = path.ProjectDestinationClassId ?? path.DestinationClassId;
        var target = classes?.FirstOrDefault(c => c.Id == targetId);
        if (source is null || target is null)
        {
            AddText(PromotionDetails, "Comparison unavailable: the starting or destination class record is missing.");
            return;
        }
        AddText(PromotionDetails, $"Project starting class: {source.Name} ({source.IdText}). Destination: {target.Name} ({target.IdText}).");
        AddText(PromotionDetails, "These are differences between class records, not guaranteed stat gains at promotion. Current character growth, learned and personal skills, equipment, and scripted rewards are not included. Weapon caps do not prove weapon permission.");
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
        foreach (double weight in new[] { 2.2, 1.2, 1.2, 1.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
        void Row(string[] values, bool header = false)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < values.Length; i++)
            {
                var cell = new TextBlock { Text = values[i], TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 8, 3), FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
                Grid.SetRow(cell, row); Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
        }
        Row(["Field", "Starting", "Destination", "Change"], true);
        var grants = new Dictionary<string, int?>();
        foreach (var (skill, id) in new[] { ("Pitchfork", 239), ("Lance", 240), ("Blade", 241) })
            grants[skill] = session is not null && session.IsRuleAvailable(id)
                ? session.Value(EditSession.Rules, id, GameRules.ById(id).Key) : null;
        AddText(PromotionDetails, "Weapon permission here means the class record and its configured class skill grants. Personal or learned skills, food, item restrictions, ammunition and animations may change actual usability.");
        foreach (var item in PromotionClassComparison.Compare(source, target, grants))
            Row([item.Field, item.Source, item.Destination, item.Difference]);
        PromotionDetails.Children.Add(grid);
        if (character is not null)
        {
            AddSection(PromotionDetails, "Starting equipment and destination class");
            AddText(PromotionDetails, "Starting inventory is a project reference, not the inventory at the time of promotion. The original ceremony and reward information remains in the event route below.");
            var items = session?.EditedItems() ?? primary?.Items;
            if (NativePromotionRoutes.ForUnit(path.UnitId)?.KnownItemRequest is { } request)
            {
                var reward = items?.FirstOrDefault(i => i.Id == request.ItemId);
                if (reward is not null && reward.Type is >= 0 and <= 20)
                {
                    var allowed = PromotionClassComparison.ClassAllows(target, reward.Type, grants);
                    AddText(PromotionDetails, $"Original event {request.Event} requests {reward.DisplayName}: {PromotionClassComparison.PermissionText(allowed)} at destination. The event itself has not been changed or re-read by this comparison.");
                }
            }
            foreach (var carried in character.Items)
            {
                var equipment = items?.FirstOrDefault(i => i.Id == carried.ItemId);
                if (equipment is null)
                {
                    AddText(PromotionDetails, $"{carried.Name}: item record unavailable.");
                    continue;
                }
                if (equipment.Type is < 0 or > 20) continue;
                var before = PromotionClassComparison.ClassAllows(source, equipment.Type, grants);
                var after = PromotionClassComparison.ClassAllows(target, equipment.Type, grants);
                AddText(PromotionDetails, $"{equipment.DisplayName}: {PromotionClassComparison.PermissionText(before)} → {PromotionClassComparison.PermissionText(after)}.");
                if (before == true && after == false)
                    AddText(PromotionDetails, "Review: this equipment loses class permission. Unit-specific grants may still allow it.");
            }
        }
        if (target.Move < source.Move)
            AddText(PromotionDetails, "Movement decreases in the destination class. Review terrain costs and mount changes before altering the route.");
    }
}
