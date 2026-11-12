using Berwick.Core;

namespace Berwick.Workshop;
public sealed record ItemRow(ItemRecord Item, IReadOnlyList<ItemDifference> Differences, bool HasComparison)
{
    public string Comparison => HasComparison ? Differences.Count == 0 ? "Same" : "Different" : "";
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}
