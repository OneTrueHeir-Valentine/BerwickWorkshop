using System.Buffers.Binary;
using System.Text;

namespace Berwick.Core;

/// <summary>One copy of an event on the disc: a top-level archive, and the archive inside it when the
/// event is stored one level deeper.</summary>
public sealed record StableCopy(int Top, string? Parent);

/// <summary>Prices returned by an event script: each price is a number record that follows a comparison with its key
/// (a horse's item number, a piece of furniture's number). Every copy of the event is rewritten in place.</summary>
public sealed class ScriptPrices(int eventNumber, string key, string label, string description, (int Item, int Offset)[] slots)
{
    public int EventNumber { get; } = eventNumber;
    public string FileName { get; } = $"e{eventNumber:D7}.dat";
    public const int Minimum = 1, Maximum = 99999;
    public TableField Field { get; } = new(key, label, 0, 0, 32, Minimum, Maximum);
    /// <summary>Key and the offset of its price number in the decoded event.</summary>
    public IReadOnlyList<(int Item, int Offset)> Slots { get; } = slots;
    private string Unexpected => $"The {description} event has an unexpected layout.";

    /// <summary>The key each price belongs to is read from the comparison just before it. Throws when the event does not
    /// have the expected layout.</summary>
    public IReadOnlyDictionary<int, int> Read(ReadOnlySpan<byte> decoded)
    {
        var prices = new Dictionary<int, int>();
        Span<byte> pattern = stackalloc byte[12];
        foreach (var (item, offset) in Slots)
        {
            if (offset < 8 || offset + 4 > decoded.Length) throw new InvalidDataException(Unexpected);
            // The price is a number record: size 12, command 1, value.
            if (BinaryPrimitives.ReadInt32LittleEndian(decoded[(offset - 8)..]) != 12 || BinaryPrimitives.ReadInt32LittleEndian(decoded[(offset - 4)..]) != 1)
                throw new InvalidDataException(Unexpected);
            // The key appears as a number record in the comparison shortly before the price.
            var before = decoded[Math.Max(0, offset - 100)..(offset - 8)];
            BinaryPrimitives.WriteInt32LittleEndian(pattern, 12); BinaryPrimitives.WriteInt32LittleEndian(pattern[4..], 1); BinaryPrimitives.WriteInt32LittleEndian(pattern[8..], item);
            if (before.IndexOf(pattern) < 0) throw new InvalidDataException(Unexpected);
            prices[item] = BinaryPrimitives.ReadInt32LittleEndian(decoded[offset..]);
        }
        ScriptParser.Parse(decoded);
        return prices;
    }

    public byte[] Write(ReadOnlySpan<byte> decoded, IReadOnlyDictionary<int, int> prices)
    {
        Read(decoded);
        var edited = decoded.ToArray();
        foreach (var (item, price) in prices)
        {
            int slot = Slots.ToList().FindIndex(s => s.Item == item);
            if (slot < 0) throw new EditException($"There is no {description} for this entry.");
            if (price < Minimum || price > Maximum) throw new EditException($"{Field.Label} must be between {Minimum} and {Maximum}.");
            BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(Slots[slot].Offset), price);
        }
        return edited;
    }

    /// <summary>Every copy of the event: each top-level archive that holds it directly, and each ev_all.ar.</summary>
    public IReadOnlyList<StableCopy> Locate(GameArchives archives)
    {
        var copies = new List<StableCopy>();
        for (int i = 0; i < archives.Count; i++)
        {
            var header = archives.TopRange(i, 0, 16);
            if (!Tarc.IsArchive(header)) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int names = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (count <= 0 || count > 20000 || names <= 0) continue;
            var list = archives.TopRange(i, names, count * 40);
            var found = new List<string>();
            for (int p = 0, k = 0; k < count && p < list.Length; k++)
            {
                int end = Array.IndexOf(list, (byte)0, p);
                if (end < 0) break;
                found.Add(Encoding.ASCII.GetString(list, p, end - p)); p = end + 1;
            }
            if (found.Contains(FileName)) copies.Add(new(i, null));
            // ev_all.ar holds events 1 to 1000.
            if (EventNumber <= 1000 && found.Contains("ev_all.ar")) copies.Add(new(i, "ev_all.ar"));
        }
        return copies;
    }

    /// <summary>The decoded event from one copy.</summary>
    public byte[] Extract(byte[] top, StableCopy copy)
    {
        var archive = top;
        if (copy.Parent is { } parent) archive = Tarc.Extract(top, Single(Tarc.Parse(top), parent));
        return Tarc.Extract(archive, Single(Tarc.Parse(archive), FileName));
    }

    /// <summary>The top-level archive with the event replaced; same length, only the event's bytes and its stored size change.</summary>
    public byte[] Replace(byte[] top, StableCopy copy, byte[] edited)
    {
        if (copy.Parent is not { } parent) return ArchiveEditing.Replace(top, FileName, edited);
        var archive = Tarc.Extract(top, Single(Tarc.Parse(top), parent));
        return ArchiveEditing.Replace(top, parent, ArchiveEditing.Replace(archive, FileName, edited));
    }

    private ArchiveEntry Single(IReadOnlyList<ArchiveEntry> entries, string name)
    {
        var matches = entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException($"The {description} event could not be found in its archive.");
    }

    /// <summary>Read all copies, require them to agree, and return the prices and the decoded event.</summary>
    public (IReadOnlyDictionary<int, int> Prices, byte[] Decoded, IReadOnlyList<StableCopy> Copies) Load(GameArchives archives)
    {
        var copies = Locate(archives);
        if (copies.Count == 0) throw new InvalidDataException($"The {description} event was not found on this disc.");
        byte[]? first = null;
        foreach (var copy in copies)
        {
            var decoded = Extract(archives.Top(copy.Top), copy);
            if (first is null) first = decoded;
            else if (!first.AsSpan().SequenceEqual(decoded)) throw new InvalidDataException($"The copies of the {description} event differ.");
        }
        return (Read(first!), first!, copies);
    }

    /// <summary>Byte patches that write the edited prices into every copy.</summary>
    public IReadOnlyList<DiscPatch> Patches(GameArchives archives, IReadOnlyList<StableCopy> copies, byte[] edited) =>
        Combined(archives, [(this, copies, edited)]);

    /// <summary>Byte patches for several edited events at once. Events that share an archive are written into it together,
    /// so the archive is rewritten once and the patches never overlap.</summary>
    public static IReadOnlyList<DiscPatch> Combined(GameArchives archives, IReadOnlyList<(ScriptPrices Table, IReadOnlyList<StableCopy> Copies, byte[] Edited)> events)
    {
        var patches = new List<DiscPatch>();
        foreach (var group in events.SelectMany(e => e.Copies.Select(c => (e.Table, Copy: c, e.Edited))).GroupBy(x => x.Copy.Top).OrderBy(g => g.Key))
        {
            var original = archives.Top(group.Key);
            var updated = original;
            foreach (var (table, copy, edited) in group) updated = table.Replace(updated, copy, edited);
            if (updated.Length != original.Length) throw new InvalidDataException("An archive changed size.");
            long at = archives.TopOffset(group.Key);
            for (int i = 0; i < updated.Length; i++)
            {
                if (updated[i] == original[i]) continue;
                int start = i;
                while (i < updated.Length && updated[i] != original[i]) i++;
                patches.Add(new(at + start, updated.AsSpan(start, i - start).ToArray(), original.AsSpan(start, i - start).ToArray()));
            }
        }
        return patches;
    }
}

/// <summary>Stable horse prices. They are not the items' base prices: event 602 returns the price for a horse,
/// and the stable menu and the payment both use it.</summary>
public static class StablePrices
{
    public static ScriptPrices Table { get; } = new(602, "stablePrice", "Stable price", "stable price",
    [
        (0x205, 0xAC), (0x206, 0x110), (0x207, 0x174), (0x208, 0x1D8), (0x209, 0x23C), (0x20A, 0x2A0),
        (0x20B, 0x304), (0x20C, 0x368), (0x20D, 0x3CC), (0x20E, 0x430), (0x20F, 0x494),
    ]);
    public const int EventNumber = 602;
    public const string FileName = "e0000602.dat";
    public const int Minimum = ScriptPrices.Minimum, Maximum = ScriptPrices.Maximum;
    public static TableField Field => Table.Field;
    public static IReadOnlyList<(int Item, int Offset)> Slots => Table.Slots;
    public static IReadOnlyDictionary<int, int> Read(ReadOnlySpan<byte> decoded) => Table.Read(decoded);
    public static byte[] Write(ReadOnlySpan<byte> decoded, IReadOnlyDictionary<int, int> prices) => Table.Write(decoded, prices);
    public static IReadOnlyList<StableCopy> Locate(GameArchives archives) => Table.Locate(archives);
    public static byte[] Extract(byte[] top, StableCopy copy) => Table.Extract(top, copy);
    public static byte[] Replace(byte[] top, StableCopy copy, byte[] edited) => Table.Replace(top, copy, edited);
    public static (IReadOnlyDictionary<int, int> Prices, byte[] Decoded, IReadOnlyList<StableCopy> Copies) Load(GameArchives archives) => Table.Load(archives);
    public static IReadOnlyList<DiscPatch> Patches(GameArchives archives, IReadOnlyList<StableCopy> copies, byte[] edited) => Table.Patches(archives, copies, edited);
}

/// <summary>Furniture shop prices. Event 727 returns the price of each piece by its number; the furniture shop uses it.</summary>
public static class FurniturePrices
{
    public static ScriptPrices Table { get; } = new(727, "furniturePrice", "Price", "furniture price",
        [(1, 0xA0), (2, 0xF8), (3, 0x150), (4, 0x1A8), (5, 0x200), (7, 0x258), (8, 0x2B0), (9, 0x308), (10, 0x360), (11, 0x3B8), (12, 0x410), (13, 0x468), (14, 0x4C0)]);

    /// <summary>The name of each piece: piece n is the game's event item n + 1 (evitem002 onward), matching the shop's own list.</summary>
    public static IReadOnlyDictionary<int, string> Names { get; } = new Dictionary<int, string>
    {
        [1] = "Sword Mount", [2] = "Big Rug", [3] = "Porcelain Vase", [4] = "Deer Mount", [5] = "Coat of Arms", [7] = "Coffers", [8] = "Tapestry",
        [9] = "Bird Statue", [10] = "Art: \"Revival\"", [11] = "Maiden Statue", [12] = "Treasure Chest", [13] = "Art: \"Hope\"", [14] = "Luxury Bed",
    };
}
