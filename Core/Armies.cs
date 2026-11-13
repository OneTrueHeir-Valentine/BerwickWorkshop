using System.Buffers.Binary;
using System.Text;

namespace Berwick.Core;

/// <summary>One unit in an army file: where its record starts and where its item list starts inside team.dat.</summary>
public sealed record ArmyUnit(int Key, int Army, int Index, int NameId, string Name, int RecordOffset, int ItemOffset)
{
    public bool Named { get; init; }
}

/// <summary>An army file (team.dat in a unit data archive). Identical copies are edited together.</summary>
public sealed record Army(int Id, string Label, IReadOnlyList<long> Locations, int Length, IReadOnlyList<ArmyUnit> Units);

/// <summary>
/// Enemy and guest armies. Each map's units are in team.dat inside an archive named udNNN.ar, with a loose copy of the same file
/// beside it. A unit block starts with the tag UNIT and has the same layout as a starting character record up to 0xB4; its item
/// list follows the tag ITEM (a count of 8, then 8-byte slots), whose position varies because the game skips empty words before it.
/// For editing, each unit is presented as a 0x100-byte record in the starting record layout, with the item list moved to 0xB4.
/// </summary>
public static class ArmyData
{
    public const int RecordLength = 0x100, ItemListAt = 0xB4, ItemListLength = 0x48, StatsLength = 0x40, ItemSlots = 7;
    private const int MaxTeamLength = 1 << 20, NoArchive = 10000;

    public static int Key(int army, int index) => army * 1000 + index;

    private static readonly Lazy<IReadOnlyList<TableField>> fields = new(() =>
    {
        var list = CharacterFields.Record.Where(f => !f.Key.StartsWith("item", StringComparison.Ordinal)).ToList();
        for (int slot = 0; slot < ItemSlots; slot++)
        {
            int at = 0xBC + slot * 8;
            list.Add(new($"item{slot + 1}", $"Slot {slot + 1}", at, 0, 16, 0, 0xFFFF) { Group = "Items", ItemReference = true });
            list.Add(new($"item{slot + 1}Uses", $"Slot {slot + 1} durability or uses", at + 2, 4, 8, 0, 255) { Group = "Items" });
            list.Add(new($"item{slot + 1}Drop", $"Slot {slot + 1} dropped when defeated", at + 3, 7, 1, 0, 1) { Group = "Drops" });
        }
        return list.AsReadOnly();
    });
    public static IReadOnlyList<TableField> Fields => fields.Value;

    /// <summary>Where a byte of the presented record is in the unit block.</summary>
    public static int RealOffset(ArmyUnit unit, int offset) =>
        offset < ItemListAt ? unit.RecordOffset + offset : unit.ItemOffset + (offset - ItemListAt);

    /// <summary>The unit in the presented layout: stats and skills in place, the item list moved to 0xB4.</summary>
    public static byte[] Present(ReadOnlySpan<byte> team, ArmyUnit unit)
    {
        var record = new byte[RecordLength];
        team.Slice(unit.RecordOffset, StatsLength).CopyTo(record);
        team.Slice(unit.ItemOffset, ItemListLength).CopyTo(record.AsSpan(ItemListAt));
        return record;
    }

    /// <summary>Unit blocks in a team file, or nothing when the file does not have the expected layout.</summary>
    public static IReadOnlyList<(int Record, int Items, int NameId)> Units(ReadOnlySpan<byte> team)
    {
        var units = new List<(int, int, int)>();
        if (team.Length < 8 || !team[..4].SequenceEqual("UNIT"u8)) return units;
        int at = 0;
        while (at >= 0 && at + 4 <= team.Length)
        {
            int next = team[(at + 4)..].IndexOf("UNIT"u8);
            int end = next < 0 ? team.Length : at + 4 + next;
            int items = team[at..end].IndexOf("ITEM"u8);
            if (items < StatsLength || at + items + ItemListLength > team.Length || BinaryPrimitives.ReadInt32LittleEndian(team[(at + items + 4)..]) != 8) return [];
            units.Add((at, at + items, BinaryPrimitives.ReadUInt16LittleEndian(team[(at + 14)..])));
            at = next < 0 ? -1 : end;
        }
        return units;
    }

    // Unit names are glyph numbers: capitals, small letters, then digits 1 to 9 and 0, and a few marks.
    public static string DecodeName(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int g = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            char? c = g switch
            {
                < 26 => (char)('A' + g),
                < 52 => (char)('a' + g - 26),
                >= 0x34 and <= 0x3C => (char)('1' + g - 0x34),
                0x3D => '0',
                0x44 => '(',
                0x45 => ')',
                0x46 => '-',
                0x47 => '\'',
                0x50 => ' ',
                _ => null,
            };
            if (c is null) return "";
            text.Append(c.Value);
        }
        return text.ToString();
    }

    /// <summary>The files of a top-level archive, read from its header and name list only.</summary>
    private static IReadOnlyList<ArchiveEntry>? List(GameArchives archives, int top)
    {
        var header = archives.TopRange(top, 0, 16);
        if (!Tarc.IsArchive(header)) return null;
        int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        int names = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (count <= 0 || count > 20000 || names < 16 + count * 12) return null;
        var table = archives.TopRange(top, 16, count * 12);
        var list = archives.TopRange(top, names, count * 40);
        if (table.Length < count * 12) return null;
        var entries = new List<ArchiveEntry>(count);
        for (int k = 0, p = 0; k < count; k++)
        {
            int end = p < list.Length ? Array.IndexOf(list, (byte)0, p) : -1;
            if (end < 0) return null;
            string name = Encoding.ASCII.GetString(list, p, end - p); p = end + 1;
            uint encoded = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(k * 12));
            entries.Add(new(name, (int)(encoded >> 1), BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(k * 12 + 4)), (encoded & 1) != 0));
        }
        return entries;
    }

    private static bool IsUnitArchive(string name, out int number)
    {
        number = 0;
        return name.Length == 8 && name.StartsWith("ud", StringComparison.Ordinal) && name.EndsWith(".ar", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(2, 3), out number);
    }

    private static readonly Lazy<IReadOnlyDictionary<int, string>> catalogue = new(() =>
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("units.csv")!;
        using var reader = new StreamReader(stream);
        var result = new Dictionary<int, string>();
        while (reader.ReadLine() is { } line)
        {
            int comma = line.IndexOf(',');
            if (comma > 0 && int.TryParse(line.AsSpan(0, comma), out int id)) result[id] = line[(comma + 1)..];
        }
        return result;
    });
    /// <summary>Unit names by name ID, in English (the same IDs are used by both editions).</summary>
    public static IReadOnlyDictionary<int, string> Names => catalogue.Value;

    /// <summary>Unit names by name ID, read from the disc's namene.ar (readable on the English disc only).</summary>
    public static IReadOnlyDictionary<int, string> LoadNames(GameArchives archives)
    {
        var result = new Dictionary<int, string>();
        for (int top = 0; top < archives.Count && result.Count == 0; top++)
        {
            if (List(archives, top) is not { } entries) continue;
            var holder = entries.FirstOrDefault(e => e.Name == "namene.ar" && !e.Compressed);
            if (holder is null) continue;
            var archive = archives.TopRange(top, holder.Offset, holder.Size);
            if (!Tarc.IsArchive(archive)) continue;
            foreach (var e in Tarc.Parse(archive))
                if (e.Name.StartsWith("un", StringComparison.Ordinal) && int.TryParse(e.Name.AsSpan(2), out int id) && !e.Compressed)
                    result[id] = DecodeName(Tarc.Extract(archive, e));
        }
        return result;
    }

    /// <summary>Every army on the disc with the bytes of one copy. Copies with identical contents form one army.</summary>
    public static IReadOnlyList<(Army Army, byte[] Team)> Load(GameArchives archives, IReadOnlyDictionary<int, string> names)
    {
        var found = new List<(byte[] Team, long Location, int Number)>();
        for (int top = 0; top < archives.Count; top++)
        {
            if (List(archives, top) is not { } entries) continue;
            long start = archives.TopOffset(top);
            var team = entries.FirstOrDefault(e => e.Name == "team.dat");
            if (team is not null && !team.Compressed && team.Size is > 0 and <= MaxTeamLength)
                found.Add((archives.TopRange(top, team.Offset, team.Size), start + team.Offset, -1));
            foreach (var holder in entries)
            {
                if (holder.Compressed || !IsUnitArchive(holder.Name, out int number) || holder.Size > MaxTeamLength) continue;
                var archive = archives.TopRange(top, holder.Offset, holder.Size);
                if (!Tarc.IsArchive(archive)) continue;
                var inner = Tarc.Parse(archive).FirstOrDefault(e => e.Name == "team.dat");
                if (inner is null || inner.Compressed) continue;
                found.Add((Tarc.Extract(archive, inner), start + holder.Offset + inner.Offset, number));
            }
        }
        var armies = new List<(Army, byte[])>();
        foreach (var group in found.GroupBy(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Team))))
        {
            var team = group.First().Team;
            var blocks = Units(team);
            if (blocks.Count == 0) continue;
            var numbers = group.Where(g => g.Number >= 0).Select(g => g.Number).Order().ToList();
            int id = numbers.Count > 0 ? numbers[0] : NoArchive + armies.Count;
            var nameCounts = blocks.GroupBy(b => b.NameId).ToDictionary(g => g.Key, g => g.Count());
            var units = blocks.Select((b, i) =>
            {
                string name = names.TryGetValue(b.NameId, out var n) && n.Length > 0 ? n : $"Unit {b.NameId}";
                return new ArmyUnit(Key(id, i), id, i, b.NameId, name, b.Record, b.Items) { Named = nameCounts[b.NameId] == 1 && blocks.Count > 1 };
            }).ToList();
            string label = numbers.Count > 0 ? $"Army {numbers[0]:000}" : $"Army {id - NoArchive + 1} (loose file)";
            armies.Add((new Army(id, label, group.Select(g => g.Location).Order().ToList(), team.Length, units), team));
        }
        return armies.OrderBy(a => a.Item1.Id).ToList();
    }
}
