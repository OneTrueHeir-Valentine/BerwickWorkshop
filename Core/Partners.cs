using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>
/// Partners (ufavor.dat). Each row belongs to one character and lists up to four partners, each with a bond value and flags.
/// When a partner stands within 2 spaces, the unit gains bond ÷ 10 hit for that partner, and the total of those amounts ÷ 5 as critical.
/// The game copies the row into the unit each time units are loaded, so the table is the only place to change.
/// </summary>
public static class PartnerData
{
    public const int RowLength = 34, Rows = 17, Slots = 4, MaxBond = 1000;
    public const int TableLength = RowLength * Rows;

    private static readonly Lazy<IReadOnlyList<CharacterEntry>> roster = new(CharacterDecoder.LoadRoster);
    private static readonly Lazy<IReadOnlyList<TableField>> fields = new(BuildFields);

    public static IReadOnlyList<TableField> Fields => fields.Value;
    public static IReadOnlyList<TableField> Editable => Fields.Where(f => f.Group.Length > 0).ToList();

    public static string PartnerKey(int slot) => $"partner{slot + 1}";
    public static string BondKey(int slot) => $"partner{slot + 1}Bond";

    private static IReadOnlyList<TableField> BuildFields()
    {
        var people = roster.Value.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new List<TableField> { new("character", "Character", 0, 0, 16, 0, 0xFFFF) };
        for (int slot = 0; slot < Slots; slot++)
        {
            list.Add(new(PartnerKey(slot), $"Partner {slot + 1}", 2 + slot * 8, 0, 16, 0, 0xFFFF)
            { Group = "Partners", Choices = ["None", .. people.Select(c => c.Name)], ChoiceValues = [0, .. people.Select(c => c.Id)] });
            list.Add(new(BondKey(slot), $"Partner {slot + 1} bond", 4 + slot * 8, 0, 16, 0, MaxBond) { Group = "Partners" });
        }
        return list.AsReadOnly();
    }

    /// <summary>Rows that belong to known characters. Returns nothing when the table does not have the expected layout.</summary>
    public static IReadOnlyList<(int Id, int Row, byte[] Bytes)> Read(ReadOnlySpan<byte> table, IReadOnlySet<int> characters)
    {
        if (table.Length < TableLength) return [];
        var rows = new List<(int, int, byte[])>();
        var seen = new HashSet<int>();
        for (int row = 0; row < Rows; row++)
        {
            var bytes = table.Slice(row * RowLength, RowLength);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            if (id == 0) continue;
            if (!characters.Contains(id) || !seen.Add(id)) return [];
            for (int slot = 0; slot < Slots; slot++)
            {
                int partner = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 + slot * 8)..]);
                int bond = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(4 + slot * 8)..]);
                if (partner != 0 && !characters.Contains(partner)) return [];
                if (bond > MaxBond) return [];
            }
            rows.Add((id, row, bytes.ToArray()));
        }
        return rows;
    }

    public static string Validate(IReadOnlyDictionary<string, int> values)
    {
        var ids = roster.Value.Select(c => c.Id).ToHashSet();
        var chosen = new HashSet<int>();
        for (int slot = 0; slot < Slots; slot++)
        {
            int partner = values.TryGetValue(PartnerKey(slot), out int p) ? p : 0;
            if (partner == 0) continue;
            if (!ids.Contains(partner)) return $"Partner {slot + 1} is not a character.";
            if (values.TryGetValue("character", out int self) && partner == self) return "A character cannot be their own partner.";
            if (!chosen.Add(partner)) return "Each partner can be listed only once.";
        }
        return "";
    }
}

/// <summary>
/// Starting bag contents. In the starting unit data a character's record can be followed by a SACK block:
/// the inventory slot that holds the bag, the number of items, then one 8-byte entry per item (the same layout as inventory slots).
/// The block's size is fixed on the disc, so entries can be changed or emptied but not added.
/// </summary>
public static class BagData
{
    public const int OffsetFromRecord = 0xFC, HeaderLength = 12, EntryLength = 8, MaxSlots = 8;
    public const int RecordLength = HeaderLength + MaxSlots * EntryLength;
    public const int ReadLength = RecordLength + 4;
    private static readonly byte[] Tag = "SACK"u8.ToArray();

    public static string ItemKey(int slot) => $"bagItem{slot + 1}";
    public static string UsesKey(int slot) => $"bagItem{slot + 1}Uses";

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("bagSlot", "Inventory slot holding the bag", 4, 0, 32, 0, 7),
        new("bagCount", "Items in the bag", 8, 0, 32, 0, MaxSlots),
    }.Concat(Enumerable.Range(0, MaxSlots).SelectMany(slot => new[]
    {
        new TableField(ItemKey(slot), $"Bag item {slot + 1}", HeaderLength + slot * EntryLength, 0, 16, 0, 0xFFFF) { Group = "Bag", ItemReference = true },
        new TableField(UsesKey(slot), $"Bag item {slot + 1} uses", HeaderLength + slot * EntryLength + 2, 4, 8, 0, 255) { Group = "Bag" },
    })).ToArray());

    public static int Count(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadInt32LittleEndian(record[8..]);

    /// <summary>The editable fields for a bag holding this many items.</summary>
    public static IReadOnlyList<TableField> EditableFor(int count) => Fields.Skip(2).Take(Math.Clamp(count, 0, MaxSlots) * 2).ToList();

    /// <summary>Reads a bag block that starts right after a character record, padded to <see cref="RecordLength"/>. Null when there is none.</summary>
    public static byte[]? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < ReadLength || !data[..4].SequenceEqual(Tag)) return null;
        int slot = BinaryPrimitives.ReadInt32LittleEndian(data[4..]), count = Count(data);
        if (slot is < 0 or > 7 || count is < 1 or > MaxSlots) return null;
        int end = HeaderLength + count * EntryLength;
        // The next block must start with its own tag; otherwise this is not the layout we know.
        for (int i = end; i < end + 4; i++) if (data[i] is < (byte)'A' or > (byte)'Z' && data[i] is < (byte)'0' or > (byte)'9') return null;
        var record = new byte[RecordLength];
        data[..end].CopyTo(record);
        return record;
    }

    public static string Validate(IReadOnlyDictionary<string, int> values)
    {
        int count = values.TryGetValue("bagCount", out int c) ? c : 0;
        for (int slot = count; slot < MaxSlots; slot++)
            if ((values.TryGetValue(ItemKey(slot), out int item) && item != 0) || (values.TryGetValue(UsesKey(slot), out int uses) && uses != 0))
                return $"This bag holds {count} items on the disc, and more cannot be added.";
        return "";
    }
}
