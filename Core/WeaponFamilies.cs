namespace Berwick.Core;

/// <summary>
/// Which starting weapon level the game reads for each item type. Read from the executable in both editions: the item type
/// selects the level through the jump table at 0x004A3F40 (reached from 0x0017AD70). Blade and Mace use the sword level,
/// Lance and Fork the spear level, Ballista the crossbow level, healing staves the holy level and dark healing the dark level.
/// Shields, ammunition, accessories and consumables have no weapon level here.
/// </summary>
public static class WeaponFamilies
{
    private static readonly IReadOnlyDictionary<string, string> Levels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Knife"] = "knifeLevel",
        ["Sword"] = "swordLevel", ["Blade"] = "swordLevel", ["Mace"] = "swordLevel",
        ["Spear"] = "spearLevel", ["Lance"] = "spearLevel", ["Fork"] = "spearLevel",
        ["Axe"] = "axeLevel",
        ["Bow"] = "bowLevel",
        ["Crossbow"] = "crossbowLevel", ["Ballista"] = "crossbowLevel",
        ["Fire"] = "fireLevel", ["Thunder"] = "thunderLevel", ["Wind"] = "windLevel",
        ["Holy"] = "holyLevel", ["Healing"] = "holyLevel",
        ["Dark"] = "darkLevel", ["Dark healing"] = "darkLevel",
    };

    /// <summary>The starting weapon level field for an item type, or null when the type has none.</summary>
    public static string? LevelFor(string itemType) => Levels.GetValueOrDefault(itemType);

    /// <summary>Item types that heal rather than attack. A character carrying one is a support character, not an unarmed one.</summary>
    public static bool IsSupport(string itemType) => itemType is "Healing" or "Dark healing";

    /// <summary>
    /// The ammunition an item type fires: bows need arrows and crossbows need bolts, and neither can attack without them (observed
    /// in game). Ballista ammunition is not known, so ballistae are not judged on it.
    /// </summary>
    public static string? AmmunitionFor(string itemType) => itemType switch { "Bow" => "Arrow", "Crossbow" => "Bolt", _ => null };

    /// <summary>The class weapon level cap field that decides whether a class can use an item type at all.</summary>
    public static string? CapFor(string itemType) => LevelFor(itemType) is { } level ? level[..^5] + "Cap" : null;
}

/// <summary>What a carried item lets a character do, judged from the game's data.</summary>
public enum LoadoutUse
{
    /// <summary>Not a weapon or staff (ammunition, accessories, consumables, shields).</summary>
    Other,
    /// <summary>A weapon the character's class can use; a weapon level of zero still attacks, with very low accuracy.</summary>
    Attack,
    /// <summary>A healing staff the class can use.</summary>
    Heal,
    /// <summary>The class has no weapon level cap for this weapon family, so it cannot use it.</summary>
    ClassCannotUse,
    /// <summary>A bow or crossbow carried without its ammunition.</summary>
    NoAmmunition,
}

/// <summary>One carried item and what it lets the character do.</summary>
public sealed record LoadoutSlot(int Slot, int ItemId, string Item, LoadoutUse Use)
{
    public bool Viable => Use is LoadoutUse.Attack or LoadoutUse.Heal;

    public string Reason => Use switch
    {
        LoadoutUse.ClassCannotUse => "the class cannot use this weapon type",
        LoadoutUse.NoAmmunition => "no ammunition for it is carried",
        _ => "",
    };
}

public static class Loadouts
{
    /// <summary>
    /// What each carried item lets a character do, from the class's weapon caps, the item types and the ammunition carried. Equipment
    /// failure chance, attack animations and promotion are not judged here.
    /// </summary>
    public static IReadOnlyList<LoadoutSlot> Read(EditSession session, int unitId, Func<string, string, int>? value = null)
    {
        value ??= (table, field) => session.Value(table, unitId, field);
        int classId = value(EditSession.Characters, "class");
        var carried = Enumerable.Range(1, CharacterFields.ItemSlots).Select(slot => (Slot: slot, Item: value(EditSession.Characters, $"item{slot}")))
            .Where(s => s.Item > 0).ToList();
        string Type(int item) => ItemDecoder.Types[session.Value(EditSession.Items, item, "type")];
        var types = carried.Select(c => Type(c.Item)).ToHashSet(StringComparer.Ordinal);
        return carried.Select(c =>
        {
            string type = Type(c.Item);
            LoadoutUse use = WeaponFamilies.CapFor(type) is not { } cap ? LoadoutUse.Other
                : session.Value(EditSession.Classes, classId, cap) == 0 ? LoadoutUse.ClassCannotUse
                : WeaponFamilies.IsSupport(type) ? LoadoutUse.Heal
                : WeaponFamilies.AmmunitionFor(type) is { } ammo && !types.Contains(ammo) ? LoadoutUse.NoAmmunition
                : LoadoutUse.Attack;
            return new LoadoutSlot(c.Slot, c.Item, session.EditedItem(c.Item).Name, use);
        }).ToList();
    }
}
