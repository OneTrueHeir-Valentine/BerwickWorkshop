namespace Berwick.Core;

public sealed record RandomizeOptions(int Seed)
{
    /// <summary>Weapons, orbs, staves and shields.</summary>
    public bool Weapons { get; init; } = true;
    /// <summary>Arrows and bolts.</summary>
    public bool Ammunition { get; init; } = true;
    /// <summary>Accessories and consumables.</summary>
    public bool Other { get; init; }
    public bool Bags { get; init; } = true;
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

    /// <summary>Items that can be handed out: real, sold in the game (price above 0), not tied to one person or class, and not a bag.</summary>
    public static IReadOnlyList<ItemRecord> Pool(EditSession session) =>
        session.EditedItems().Where(i => !i.IsUnused && i.Price > 0 && !i.Name.Equals("Bag", StringComparison.OrdinalIgnoreCase) && !Restricted(session, i)).ToList();

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
            for (int slot = 0; slot < CharacterFields.ItemSlots; slot++)
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
        return edits;
    }
}
