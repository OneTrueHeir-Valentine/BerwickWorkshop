namespace Berwick.Core;

public sealed record FurnitureEventReference(int Event, string Reason);

/// <summary>Known furniture script relationships and literal flag references. A reference is not proof that a branch executes.</summary>
public static class FurnitureEvents
{
    public static IReadOnlyList<FurnitureEventReference> Known(int piece)
    {
        if (!FurniturePrices.Names.ContainsKey(piece)) return [];
        var result = new List<FurnitureEventReference>
        {
            new(727, "Shared shop prices"),
            new(1013, "Shared room display and effect flags")
        };
        if (piece == 3)
        {
            result.Add(new(136, "Porcelain Vase favor effect"));
            result.Add(new(152, "Porcelain Vase favor effect"));
        }
        return result;
    }

    public static int? EffectFlag(int piece) => piece == 14 ? 3707 : GameRules.OriginalFurnitureEffectFlag(piece);

    // Display flag mapping is not yet established per piece. Show shared flag mentions separately.
    public static IReadOnlyList<FurnitureEventReference> Discover(int piece, ScriptDependencyIndex index)
    {
        if (!FurniturePrices.Names.ContainsKey(piece)) return [];
        int? flag = EffectFlag(piece);
        var hits = index.Filter(ScriptDependencyKind.Flags)
            .SelectMany(site => site.Arguments.Where(arg => arg.SourceNumber && arg.Value.HasValue)
                .Select(arg => (site.Event, Flag: arg.Value!.Value)))
            .Where(x => (flag.HasValue && x.Flag == flag.Value) || x.Flag is >= 3750 and <= 3763)
            .Distinct().GroupBy(x => x.Event).OrderBy(g => g.Key);
        return hits.Select(g => new FurnitureEventReference(g.Key,
            "Literal flag reference: " + string.Join(", ", g.Select(x => x.Flag).Distinct().Order()) +
            (g.Any(x => x.Flag is >= 3750 and <= 3763) ? " (includes shared display flags)" : ""))).ToArray();
    }
}
