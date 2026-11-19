using System.Collections;

namespace Berwick.Core;

/// <summary>
/// Where you place your units before a battle, and how many you may place. The hexes are placement group 0 of the battle's
/// map file ("map" in its archive in folder bmap): slot n has its x at 0x22144 + n and its y 0x400 later, 0xFF for none, and
/// slot 0 is never used. The limit is set by SetMapUnitMax in the battle's opening events, or else by event 70 (8 for every
/// battle that does not set its own; every battle with a map sets its own). The preparation screen marks only the first
/// limit hexes in slot order and ignores the rest (checked live: battle 6 with 5 hexes and limit 3 showed 3 hexes).
/// </summary>
public static class Deployment
{
    public const int Slots = 64, RecordLength = Slots * 2, NoSlot = 0xFF;
    private const int PlacementX = 0x22144, PlacementY = PlacementX + 0x400;
    public const int DefaultEvent = 70;
    public const string LimitCommand = "SetMapUnitMax";

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(Enumerable.Range(1, Slots - 1)
        .SelectMany(n => new[]
        {
            new TableField($"x{n}", $"Hex {n} column", n, 0, 8, 0, 255) { Group = "Deployment" },
            new TableField($"y{n}", $"Hex {n} row", Slots + n, 0, 8, 0, 255) { Group = "Deployment" },
        }).ToArray());

    /// <summary>The deployment slots of a map file as a record: 64 x bytes, then 64 y bytes.</summary>
    public static byte[]? Read(ReadOnlySpan<byte> map)
    {
        if (map.Length < PlacementY + Slots) return null;
        var record = new byte[RecordLength];
        map.Slice(PlacementX, Slots).CopyTo(record);
        map.Slice(PlacementY, Slots).CopyTo(record.AsSpan(Slots));
        return record;
    }

    /// <summary>The map file with its deployment slots replaced by the record's.</summary>
    public static byte[] Write(ReadOnlySpan<byte> map, ReadOnlySpan<byte> record)
    {
        if (map.Length < PlacementY + Slots || record.Length != RecordLength) throw new EditException("The map file is not in the expected layout.");
        var copy = map.ToArray();
        record[..Slots].CopyTo(copy.AsSpan(PlacementX));
        record[Slots..].CopyTo(copy.AsSpan(PlacementY));
        return copy;
    }

    /// <summary>The hexes of a record, in slot order.</summary>
    public static IReadOnlyList<(int X, int Y)> Hexes(ReadOnlySpan<byte> record)
    {
        var list = new List<(int, int)>();
        for (int n = 1; n < Slots; n++)
            if (record[n] != NoSlot && record[Slots + n] != NoSlot) list.Add((record[n], record[Slots + n]));
        return list;
    }

    /// <summary>A record holding these hexes in slots 1 onward, with slot 0 and the rest as in the original.</summary>
    public static byte[] WithHexes(ReadOnlySpan<byte> original, IReadOnlyList<(int X, int Y)> hexes)
    {
        if (hexes.Count > Slots - 1) throw new EditException($"A map can have at most {Slots - 1} deployment hexes.");
        if (hexes.Distinct().Count() != hexes.Count) throw new EditException("Each deployment hex can be listed only once.");
        foreach (var (x, y) in hexes)
            if (x <= 0 || y <= 0 || x >= BattleMaps.Side - 1 || y >= BattleMaps.Side - 1) throw new EditException("A deployment hex must be inside the map.");
        var record = original.ToArray();
        for (int n = 1; n < Slots; n++) { record[n] = NoSlot; record[Slots + n] = NoSlot; }
        for (int i = 0; i < hexes.Count; i++) { record[1 + i] = (byte)hexes[i].X; record[Slots + 1 + i] = (byte)hexes[i].Y; }
        return record;
    }

    /// <summary>
    /// The number that sets a battle's limit: the first SetMapUnitMax in its opening events, or, when it has none, the one in
    /// event 70 that every battle without its own starts from. Own is false for the shared default.
    /// </summary>
    public static (ScriptNumber Number, bool Own)? Limit(EventLibrary library, int battle, Func<int, byte[]?>? project = null)
    {
        var opening = library.MapEvents().Where(r => r.Battle == battle && r.Type == 13).Select(r => r.Event).Distinct().Order();
        foreach (int number in opening.Append(DefaultEvent))
        {
            if (!library.Numbers.Contains(number)) continue;
            var found = ScriptValues.Numbers(number, ScriptParser.Parse(project?.Invoke(number) ?? library.Bytes(number)), library.CommandNames).FirstOrDefault(n => n.Command == LimitCommand);
            if (found is not null) return (found, number != DefaultEvent);
        }
        return null;
    }

    /// <summary>The opening event that should hold a battle's own limit: the first of its opening events.</summary>
    public static int? OwnLimitEvent(EventLibrary library, int battle) =>
        library.MapEvents().Where(r => r.Battle == battle && r.Type == 13).Select(r => (int?)r.Event).Distinct().Order().FirstOrDefault(e => library.Numbers.Contains(e!.Value));

    /// <summary>The event with SetMapUnitMax(limit) added after its first line, so the battle no longer uses the shared default.</summary>
    public static byte[] AddOwnLimit(ReadOnlySpan<byte> decoded, int limit, IReadOnlyList<string> names)
    {
        int command = -1;
        for (int c = 0; c < names.Count; c++) if (names[c] == LimitCommand) { command = c; break; }
        if (command < 0) throw new EditException("This disc's script commands do not include the deployment limit.");
        var lines = ScriptLines.Lines(ScriptParser.Parse(decoded), names);
        if (lines.Count == 0) throw new EditException("The battle's opening event is empty.");
        return ScriptLines.InsertAfter(decoded, lines[0].Path, ScriptLines.Command(command, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public static string Validate(IReadOnlyDictionary<string, int> values)
    {
        for (int n = 1; n < Slots; n++)
        {
            bool hasX = values.TryGetValue($"x{n}", out int x), hasY = values.TryGetValue($"y{n}", out int y);
            if (!hasX || !hasY) continue;
            if ((x == NoSlot) != (y == NoSlot)) return $"Hex {n} needs both a column and a row, or neither.";
            if (x != NoSlot && (x <= 0 || y <= 0 || x >= BattleMaps.Side - 1 || y >= BattleMaps.Side - 1)) return $"Hex {n} must be inside the map.";
        }
        return "";
    }
}

/// <summary>
/// Each battle map's deployment slots as edit records (keyed by the map's top-level archive), read from the disc when first
/// asked for. Only lookups by key are supported.
/// </summary>
public sealed class DeploymentRecords(string discPath) : IReadOnlyDictionary<int, ReadOnlyMemory<byte>>
{
    private readonly object gate = new();
    private readonly Dictionary<int, byte[]?> records = [];
    private GameArchives? archives;
    private FileStream? stream;

    private byte[]? Load(int top)
    {
        lock (gate)
        {
            if (records.TryGetValue(top, out var known)) return known;
            byte[]? record = null;
            try
            {
                archives ??= new GameArchives(new IsoImage(stream = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.Read)));
                if (top >= 0 && top < archives.Count)
                {
                    var archive = archives.Top(top);
                    if (Tarc.IsArchive(archive) && Tarc.Parse(archive).FirstOrDefault(e => e.Name == "map") is { } entry)
                        record = Deployment.Read(Tarc.Extract(archive, entry));
                }
            }
            catch (InvalidDataException) { }
            return records[top] = record;
        }
    }

    public bool TryGetValue(int key, out ReadOnlyMemory<byte> value)
    {
        var record = Load(key);
        value = record ?? default(ReadOnlyMemory<byte>);
        return record is not null;
    }

    public bool ContainsKey(int key) => TryGetValue(key, out _);
    public ReadOnlyMemory<byte> this[int key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
    public IEnumerable<int> Keys => throw new NotSupportedException();
    public IEnumerable<ReadOnlyMemory<byte>> Values => throw new NotSupportedException();
    public int Count => throw new NotSupportedException();
    public IEnumerator<KeyValuePair<int, ReadOnlyMemory<byte>>> GetEnumerator() => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
