using Berwick.Core;

namespace Berwick.Workshop;
public sealed record CharacterRow(CharacterRecord Character, IReadOnlyList<ItemDifference> Differences, bool HasComparison)
{
    public string Comparison => HasComparison ? Differences.Count == 0 ? "Same" : "Different" : "";
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record ArmyUnitRow(Army Army, ArmyUnit Unit, CharacterRecord Record, string ClassName, string Drops)
{
    public string ArmyLabel => Army.Label;
    public string Name => Unit.Name;
    public int Level => Record.Level;
    public bool Edited { get; init; }
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record TextRow(int Id, string Name, string Kind, string Text, bool Edited)
{
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record DishRow(int Id, string Name, int Price, int Tier, string Liked, bool Edited)
{
    public string PriceText => Price.ToString("N0");
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record FurnitureRow(int Id, string Name, int Price, int Original)
{
    public string PriceText => Price.ToString("N0");
    public bool Edited => Price != Original;
    public string EditedText => Edited ? "Edited" : "";
}

public sealed record LiveUnitRow(LiveUnit Unit, string ClassName)
{
    public string Side => Unit.Army switch { 0 => "Yours", 1 => "Enemy", 2 => "Ally", _ => "Other" };
    public string Position => $"{Unit.X}, {Unit.Y}";
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
