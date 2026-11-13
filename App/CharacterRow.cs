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

public sealed record TerrainKindRow(TerrainKindRecord Kind)
{
    public string Name => Kind.Name;
    public int Avoid => Kind.Avoid;
    public string Recovery => Kind.RecoveryText;
    public string Flyers => Kind.FlyersText;
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record MovementRow(MovementTypeRecord Type, IReadOnlyList<string> Classes)
{
    public int ClassCount => Classes.Count;
    public string Plain => Type.Cost(0);
    public string Forest => Type.Cost(4);
    public string MountainFoot => Type.Cost(6);
    public string Sand => Type.Cost(3);
    public string SnowForest => Type.Cost(50);
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}
