namespace Berwick.Core;

public enum RelatedRecord { Character, Item, Class }
public sealed record RelatedEvent(int Event, string Reason);

/// <summary>Literal typed references only. A matching untyped number is not a record reference.</summary>
public static class RelatedEvents
{
    public static IReadOnlyList<RelatedEvent> Discover(RelatedRecord record, int id, ScriptDependencyIndex index)
    {
        int opcode = record switch
        {
            RelatedRecord.Character => 0x1E,
            RelatedRecord.Class => 0x1F,
            RelatedRecord.Item => 0x20,
            _ => throw new ArgumentOutOfRangeException(nameof(record))
        };
        bool FirstLiteral(ScriptDependencySite site) => site.Arguments.Any(a => a.Argument == 1 && a.SourceNumber && a.SourceType == "literal" && a.Value == id);
        return index.Sites.Where(site => (site.Opcode == opcode && FirstLiteral(site)) ||
                (record == RelatedRecord.Item && (site.Opcode is Shops.AddItem or Shops.AddItemSpecial or Shops.AddItemStock) && FirstLiteral(site)))
            .GroupBy(site => site.Event).OrderBy(group => group.Key)
            .Select(group => new RelatedEvent(group.Key, string.Join("; ", group.Select(site =>
                site.Opcode == opcode ? "Literal typed record reference" : "Literal shop item reference").Distinct()) +
                (group.Any(site => site.Conditional) ? " (includes conditional references)" : ""))).ToArray();
    }

    /// <summary>Call only for a verified original promotion route. Edited scripts may have changed the relationship.</summary>
    public static IReadOnlyList<RelatedEvent> Promotion(PromotionPath path)
    {
        var result = new List<RelatedEvent>();
        void Add(int? number, string reason) { if (number is int n) result.Add(new(n, reason)); }
        Add(path.CallerEvent, "Original eligibility caller");
        Add(path.CheckEvent, "Original quest check");
        Add(path.ApplicationEvent, "Original application");
        Add(path.TownRoot, "Original town entry");
        if (NativePromotionRoutes.ForUnit(path.UnitId) is { } native)
        {
            Add(native.CheckEvent, "Native quest check");
            Add(native.ApplicationEvent, "Native application");
            Add(native.IntroEvent, "Quest introduction (not the application pointer)");
            Add(native.ConversionEvent, "Conversion request");
            foreach (int n in native.SceneEvents) Add(n, "Ceremony branch");
            foreach (int n in native.RelatedItemEvents) Add(n, "Item or horse event");
        }
        return result.GroupBy(r => r.Event).Select(g => new RelatedEvent(g.Key, string.Join("; ", g.Select(r => r.Reason).Distinct()))).ToArray();
    }
}
