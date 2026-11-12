using Berwick.Core;

namespace Berwick.Workshop;
public sealed record CharacterRow(CharacterRecord Character, IReadOnlyList<ItemDifference> Differences, bool HasComparison)
{
    public string Comparison => HasComparison ? Differences.Count == 0 ? "Same" : "Different" : "";
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record ClassRow(ClassRecord Class, IReadOnlyList<ItemDifference> Differences, bool HasComparison, IReadOnlyList<string> StartingCharacters)
{
    public string Comparison => HasComparison ? Differences.Count == 0 ? "Same" : "Different" : "";
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}
