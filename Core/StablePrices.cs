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
    public IReadOnlyList<StableCopy> Locate(GameArchives archives) => EventCopies.Locate(archives, EventNumber);

    /// <summary>The decoded event from one copy.</summary>
    public byte[] Extract(byte[] top, StableCopy copy) => EventCopies.Extract(top, EventNumber, copy);

    /// <summary>The top-level archive with the event replaced; same length, only the event's bytes and its stored size change.</summary>
    public byte[] Replace(byte[] top, StableCopy copy, byte[] edited) => EventCopies.Replace(top, EventNumber, copy, edited);

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
    public static IReadOnlyList<DiscPatch> Combined(GameArchives archives, IReadOnlyList<(ScriptPrices Table, IReadOnlyList<StableCopy> Copies, byte[] Edited)> events,
        IReadOnlyList<(int Top, Func<byte[], byte[]> Edit)>? other = null)
    {
        var patches = new List<DiscPatch>();
        var edits = events.SelectMany(e => e.Copies.Select(c => (c.Top, Edit: (Func<byte[], byte[]>)(top => e.Table.Replace(top, c, e.Edited)))))
            .Concat(other ?? []);
        foreach (var group in edits.GroupBy(x => x.Top).OrderBy(g => g.Key))
        {
            var original = archives.Top(group.Key);
            var updated = original;
            foreach (var (_, edit) in group) updated = edit(updated);
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

/// <summary>The names in a top-level archive's file table, or none when it is not an archive.</summary>
internal static class TopNames
{
    public static List<string> Read(GameArchives archives, int index)
    {
        var found = new List<string>();
        var header = archives.TopRange(index, 0, 16);
        if (!Tarc.IsArchive(header)) return found;
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        int names = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (count <= 0 || count > 20000 || names <= 0) return found;
        var list = archives.TopRange(index, names, count * 40);
        for (int p = 0, k = 0; k < count && p < list.Length; k++)
        {
            int end = Array.IndexOf(list, (byte)0, p);
            if (end < 0) break;
            found.Add(Encoding.ASCII.GetString(list, p, end - p)); p = end + 1;
        }
        return found;
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

/// <summary>
/// The furniture shop's list of pieces. Its lines are fixed menu texts 91 to 103 in smenui.ar, one per piece in order of price,
/// with the price written into the text as 16-bit glyph numbers, so the list does not ask event 727. The English translation
/// puts the price first ("14500 - Art: Hope"); the Japanese release puts it last, right-aligned after spaces.
/// </summary>
public static class FurnitureList
{
    public const string ArchiveName = "smenui.ar";
    public const int FirstText = 91, Digits = 5;
    private sealed record Layout(int[] DigitGlyphs, int Space, bool PriceFirst);
    // Glyph numbers of the digits 0 to 9 and of a space, in the menu font of each edition.
    private static readonly Layout[] Layouts =
    [
        new([62, 53, 54, 55, 56, 57, 58, 59, 60, 61], 4, true),
        new([47, 1, 7, 12, 17, 22, 27, 32, 37, 42], 0, false),
    ];

    /// <summary>The top-level archives that hold smenui.ar.</summary>
    public static IReadOnlyList<int> Locate(GameArchives archives) =>
        Enumerable.Range(0, archives.Count).Where(i => TopNames.Read(archives, i).Contains(ArchiveName)).ToList();

    private static ArchiveEntry Entry(byte[] archive, string name) =>
        Tarc.Parse(archive).Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int[] Glyphs(byte[] text) =>
        Enumerable.Range(0, text.Length / 2).Select(i => (int)BinaryPrimitives.ReadUInt16LittleEndian(text.AsSpan(i * 2))).ToArray();

    private static int? Price(int[] glyphs, Layout layout)
    {
        int start = layout.PriceFirst ? 0 : glyphs.Length - Digits;
        if (start < 0 || glyphs.Length < Digits) return null;
        int value = 0; bool digits = false;
        for (int i = start; i < start + Digits; i++)
        {
            int d = Array.IndexOf(layout.DigitGlyphs, glyphs[i]);
            if (d >= 0) { value = value * 10 + d; digits = true; }
            else if (glyphs[i] != layout.Space || digits) return null;
        }
        return digits ? value : null;
    }

    /// <summary>
    /// smenui.ar with each piece's line showing its new price, or null when the lines are not the original list (such as on a
    /// disc changed by another tool). Pieces are matched to lines by their original prices, which are all different.
    /// </summary>
    public static byte[]? Write(byte[] smenui, IReadOnlyDictionary<int, int> original, IReadOnlyDictionary<int, int> prices)
    {
        var order = original.OrderBy(p => p.Value).ToList();
        if (order.Select(p => p.Value).Distinct().Count() != order.Count) return null;
        string[] names = order.Select((_, k) => (FirstText + k).ToString()).ToArray();
        int[][] lines;
        try { lines = names.Select(n => Glyphs(Tarc.Extract(smenui, Entry(smenui, n)))).ToArray(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return null; }
        var layout = Layouts.FirstOrDefault(l => lines.Select((g, k) => Price(g, l) == order[k].Value).All(ok => ok));
        if (layout is null) return null;
        var updated = smenui;
        for (int k = 0; k < order.Count; k++)
        {
            int price = prices.TryGetValue(order[k].Key, out var p) ? p : order[k].Value;
            if (price == order[k].Value) continue;
            if (price < ScriptPrices.Minimum || price > ScriptPrices.Maximum) throw new EditException($"Furniture prices must be between {ScriptPrices.Minimum} and {ScriptPrices.Maximum}.");
            var glyphs = lines[k].ToArray();
            int start = layout.PriceFirst ? 0 : glyphs.Length - Digits;
            string text = price.ToString().PadLeft(Digits);
            for (int i = 0; i < Digits; i++) glyphs[start + i] = text[i] == ' ' ? layout.Space : layout.DigitGlyphs[text[i] - '0'];
            var bytes = new byte[glyphs.Length * 2];
            for (int i = 0; i < glyphs.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)glyphs[i]);
            updated = ArchiveEditing.Replace(updated, names[k], bytes);
        }
        return updated;
    }

    /// <summary>The price each line of the list shows, by piece, or null when the lines are not the original list.</summary>
    public static IReadOnlyDictionary<int, int>? Read(byte[] smenui, IReadOnlyDictionary<int, int> original)
    {
        var order = original.OrderBy(p => p.Value).ToList();
        int[][] lines;
        try { lines = order.Select((_, k) => Glyphs(Tarc.Extract(smenui, Entry(smenui, (FirstText + k).ToString())))).ToArray(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return null; }
        foreach (var layout in Layouts)
        {
            var shown = lines.Select(g => Price(g, layout)).ToArray();
            if (shown.All(v => v is not null)) return order.Select((p, k) => (p.Key, shown[k]!.Value)).ToDictionary(x => x.Key, x => x.Value);
        }
        return null;
    }

    /// <summary>An edit for every top-level archive holding smenui.ar that writes the new prices into the list.</summary>
    public static IReadOnlyList<(int Top, Func<byte[], byte[]> Edit)> Edits(GameArchives archives, IReadOnlyDictionary<int, int> original, IReadOnlyDictionary<int, int> prices) =>
        Locate(archives).Select(top => (top, (Func<byte[], byte[]>)(data =>
        {
            var archive = Tarc.Extract(data, Entry(data, ArchiveName));
            var written = Write(archive, original, prices);
            return written is null ? data : ArchiveEditing.Replace(data, ArchiveName, written);
        }))).ToList();
}
