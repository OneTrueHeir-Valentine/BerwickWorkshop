using System.Text.Json.Serialization;

namespace Berwick.Core;

public sealed record RandomizeOptions(int Seed)
{
    /// <summary>
    /// The characters whose starting items may change; empty means every character. Each draw is keyed by character and slot, so
    /// leaving characters out never changes anyone else's result. Not written when empty, so older files read and save as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? UnitIds { get; init; }

    public bool Equals(RandomizeOptions? other) => other is not null && Seed == other.Seed && Weapons == other.Weapons &&
        Ammunition == other.Ammunition && Other == other.Other && Bags == other.Bags && LimitLevel == other.LimitLevel && ExtendedInventory == other.ExtendedInventory &&
        (UnitIds ?? []).SequenceEqual(other.UnitIds ?? []);

    public override int GetHashCode() => HashCode.Combine(Seed, Weapons, Ammunition, Other, Bags, LimitLevel, ExtendedInventory, (UnitIds ?? []).Length);

    /// <summary>Weapons, orbs, staves and shields.</summary>
    public bool Weapons { get; init; } = true;
    /// <summary>Arrows and bolts.</summary>
    public bool Ammunition { get; init; } = true;
    /// <summary>Accessories and consumables.</summary>
    public bool Other { get; init; }
    public bool Bags { get; init; } = true;
    /// <summary>Include already occupied starting slots 6–8. Empty slots are never filled.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ExtendedInventory { get; init; }
    /// <summary>Only pick items whose level is no higher than the item they replace.</summary>
    public bool LimitLevel { get; init; } = true;
}

/// <summary>Randomizes the items characters join with. The result is a list of ordinary field edits, applied as one undoable step.</summary>
public static class Randomizer
{
    // Effect flags that tie an item to one person, class or group (see ItemEncoder.Effects).
    private static readonly int[] RestrictedFlags = [32, 1, 29, 26, 69, 72, 79, 80];
    private const int Arrow = 21, Bolt = 22, Accessory = 23, Consumable = 24;

    private static bool Restricted(EditSession session, ItemRecord item)
    {
        var bytes = session.EditedBytes(EditSession.Items, item.Id);
        return RestrictedFlags.Any(bit => (bytes[28 + bit / 8] >> (bit % 8) & 1) != 0);
    }

    private static bool InScope(RandomizeOptions options, ItemRecord item) => item.Type switch
    {
        Arrow or Bolt => options.Ammunition,
        Accessory or Consumable => options.Other,
        < Arrow => options.Weapons,
        _ => false,
    };

    /// <summary>Regular mounted horses and map-pickup horse records are not ordinary starting items.</summary>
    public static bool IsHorseRecord(int itemId) =>
        itemId is (>= 0x205 and <= 0x214) or (>= 0x264 and <= 0x274);

    /// <summary>Items that can be handed out: real, sold in the game (price above 0), not tied to one person or class, and not a bag.</summary>
    public static IReadOnlyList<ItemRecord> Pool(EditSession session) =>
        session.EditedItems().Where(i => !i.IsUnused && i.Price > 0
            && !IsHorseRecord(i.Id)
            && !i.Name.Equals("Bag", StringComparison.OrdinalIgnoreCase)
            && !Restricted(session, i)).ToList();

    /// <summary>
    /// Highlight original or replacement items mentioned in event scripts. This never
    /// marks them progression-critical or changes randomizer choices on its own.
    /// </summary>
    public static IReadOnlyList<RandomizerItemReference> ScriptImpact(EditSession session,
        IEnumerable<FieldEdit> edits, ScriptDependencyIndex index)
    {
        var replacements = edits.Where(e => (e.Table is EditSession.Characters or EditSession.Bags)
                && e.Field.StartsWith("item", StringComparison.Ordinal)
                && !e.Field.EndsWith("Uses", StringComparison.Ordinal)
                && session.Value(e.Table, e.Id, e.Field) != e.Value)
            .Select(e => (OldItem: session.Value(e.Table, e.Id, e.Field), NewItem: e.Value));
        return RandomizerDependencyAudit.Inspect(index.ItemReferences, replacements);
    }

    public static IReadOnlyList<FieldEdit> StartingItems(EditSession session, RandomizeOptions options)
    {
        var random = new Random(options.Seed);
        var pool = Pool(session);
        var poolIds = pool.Select(i => i.Id).ToHashSet();
        var items = session.EditedItems().ToDictionary(i => i.Id);
        var edits = new List<FieldEdit>();

        // Returns the new item and uses for one slot, or null to leave the slot as it is.
        (int Item, int Uses)? Pick(int current, int uses)
        {
            if (current == 0 || !items.TryGetValue(current, out var old) || !poolIds.Contains(current) || !InScope(options, old)) return null;
            var choices = pool.Where(i => i.Type == old.Type && (!options.LimitLevel || i.Level <= old.Level)).ToList();
            if (choices.Count == 0) return null;
            var picked = choices[random.Next(choices.Count)];
            // A full item stays full; a used one keeps its uses, up to what the new item holds.
            int newUses = uses == old.Uses ? picked.Uses : Math.Min(uses, picked.Uses);
            return (picked.Id, newUses);
        }

        foreach (var character in session.Disc.Characters.OrderBy(c => c.Id))
        {
            for (int slot = 0; slot < CharacterFields.VerifiedItemSlots; slot++)
            {
                string itemKey = $"item{slot + 1}", usesKey = $"item{slot + 1}Uses";
                if (Pick(session.Value(EditSession.Characters, character.Id, itemKey), session.Value(EditSession.Characters, character.Id, usesKey)) is { } result)
                {
                    edits.Add(new(EditSession.Characters, character.Id, itemKey, result.Item));
                    edits.Add(new(EditSession.Characters, character.Id, usesKey, result.Uses));
                }
            }
            if (!options.Bags || !session.Disc.BagBytes.ContainsKey(character.Id)) continue;
            int count = session.Value(EditSession.Bags, character.Id, "bagCount");
            for (int slot = 0; slot < count; slot++)
            {
                if (Pick(session.Value(EditSession.Bags, character.Id, BagData.ItemKey(slot)), session.Value(EditSession.Bags, character.Id, BagData.UsesKey(slot))) is { } result)
                {
                    edits.Add(new(EditSession.Bags, character.Id, BagData.ItemKey(slot), result.Item));
                    edits.Add(new(EditSession.Bags, character.Id, BagData.UsesKey(slot), result.Uses));
                }
            }
        }
        // Keep the old sequential random draws (including bags) intact. Additional slots
        // are processed only after the legacy pass, so opting in cannot change its results.
        if (options.ExtendedInventory)
            foreach (var character in session.Disc.Characters.OrderBy(c => c.Id))
                for (int slot = CharacterFields.VerifiedItemSlots; slot < CharacterFields.ItemSlots; slot++)
                {
                    string itemKey = $"item{slot + 1}", usesKey = $"item{slot + 1}Uses";
                    if (Pick(session.Value(EditSession.Characters, character.Id, itemKey), session.Value(EditSession.Characters, character.Id, usesKey)) is { } result)
                    {
                        edits.Add(new(EditSession.Characters, character.Id, itemKey, result.Item));
                        edits.Add(new(EditSession.Characters, character.Id, usesKey, result.Uses));
                    }
                }
        return edits;
    }
}
