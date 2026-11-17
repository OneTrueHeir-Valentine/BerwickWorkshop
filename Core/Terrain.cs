using System.Globalization;

namespace Berwick.Core;

/// <summary>One movement type: how many movement points each of the 64 terrain kinds costs. Stored in tenths; 255 means the terrain cannot be entered.</summary>
public sealed record MovementTypeRecord(int Id, string Name, int Mount, IReadOnlyList<int> Costs)
{
    public string MountText => Mount switch { 0 => "On foot", 1 => "Mounted", 2 => "Flying", _ => $"Mount {Mount}" };
    public string Cost(int terrain) => TerrainData.Format(Costs[terrain]);
}

/// <summary>One terrain kind's effects: avoid bonus, recovery while resting and flags. Shares its number with the cost table's terrain kinds.</summary>
public sealed record TerrainKindRecord(int Id, int Avoid, int Recovery, int Flags)
{
    public string Name => TerrainData.TerrainLabel(Id);
    public bool FlyersCannotStop => (Flags & TerrainKindData.FlyersCannotStopFlag) != 0;
    public string FlyersText => FlyersCannotStop ? "Pass over only" : "Yes";
    public string RecoveryText => Recovery == 0 ? "None" : Recovery > 0 ? $"+{Recovery}%" : $"{Recovery}%";
}

/// <summary>The terrain effect table: right after the cost table in DATA3.DAT, in both copies. A count (64), then 24 bytes per terrain kind.</summary>
public static class TerrainKindData
{
    public const int OffsetFromCosts = 0x420, HeaderLength = 4, RowLength = 24, FlyersCannotStopFlag = 0x40;
    public const int TableLength = HeaderLength + TerrainData.Terrains * RowLength;

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("avoid", "Avoid bonus", 0, 0, 32, -99, 99) { Signed = true },
        new("recovery", "Recovery while resting (%)", 0x10, 0, 32, -100, 100) { Signed = true },
        new("flyersCannotStop", "Flyers cannot stop here", 0x14, 6, 1, 0, 1),
    });

    public static TerrainKindRecord Decode(int id, ReadOnlySpan<byte> row)
    {
        if (row.Length < RowLength) throw new InvalidDataException("A terrain record is incomplete.");
        return new(id, BitConverter.ToInt32(row[..4]), BitConverter.ToInt32(row[0x10..0x14]), BitConverter.ToInt32(row[0x14..0x18]));
    }

    public static bool HeaderValid(ReadOnlySpan<byte> table) => table.Length >= TableLength && BitConverter.ToInt32(table[..4]) == TerrainData.Terrains;
}

public static class TerrainData
{
    public const int Terrains = 64, Types = 14, HeaderLength = 8, RowLength = 68, CostOffset = 4, Blocked = 255;
    public const int TableLength = HeaderLength + Types * RowLength;

    /// <summary>Movement type names as the game shows them (Cav, LtCav, Fly and so on).</summary>
    public static readonly string[] TypeNames = Enumerable.Range(0, Types).Select(t => GameNames.MovementType(t) ?? $"Type {t}").ToArray();

    /// <summary>Terrain kind names as the game shows them.</summary>
    public static readonly string[] TerrainNames = Enumerable.Range(0, Terrains).Select(t => GameNames.Terrain(t) ?? $"Terrain {t}").ToArray();

    /// <summary>Terrain kinds grouped by the published terrain list they match (several kinds share the same costs); used for map colours and walls.</summary>
    public static readonly string[] TerrainGroups =
    [
        "Plain or grassland", "Plain or grassland", "Wasteland", "Sand", "Forest", "Flying only", "Mountain foot", "Flying only",
        "Flying only", "Shallows", "Flying only", "Unidentified", "Cave (open)", "Cave (narrow)", "Plain or grassland", "Blocked",
        "Blocked", "Blocked", "Snow, temple or castle", "Blocked", "Gate", "Snow, temple or castle", "Snow, temple or castle", "Snow, temple or castle",
        "Road or bridge", "Flying only", "Blocked", "Blocked", "Cave (narrow)", "Snow, temple or castle", "Floor", "Unidentified",
        "Throne", "Blocked", "Unidentified", "Cliff", "Building", "Road or bridge", "Building", "Building",
        "Snow, temple or castle", "Road or bridge", "Gate", "Blocked", "Building", "Building", "Building", "Building",
        "Snow, temple or castle", "Snow, temple or castle", "Snow forest", "Poison swamp", "Unidentified", "Building", "Building", "Building",
        "Plain or grassland", "Blocked", "Road or bridge", "Flying only", "Blocked", "Road or bridge", "Road or bridge", "Blocked",
    ];

    public static string TerrainLabel(int terrain) => $"{terrain}. {TerrainNames[terrain]}";

    public static string Format(int raw) => raw == Blocked ? "×" : (raw / 10.0).ToString("0.#", CultureInfo.InvariantCulture);

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(Enumerable.Range(0, Terrains).Select(t =>
        new TableField($"cost{t}", TerrainLabel(t), CostOffset + t, 0, 8, 1, Blocked)
        { Display = 0.1, SpecialValue = Blocked, SpecialText = "×", Group = TerrainGroups[t] == "Blocked" ? "Blocked" : "" }).ToArray());

    public static MovementTypeRecord Decode(int id, ReadOnlySpan<byte> row)
    {
        if (row.Length < RowLength) throw new InvalidDataException("A movement type record is incomplete.");
        var costs = new int[Terrains];
        for (int t = 0; t < Terrains; t++) costs[t] = row[CostOffset + t];
        return new(id, id < TypeNames.Length ? TypeNames[id] : $"Type {id}", row[0], Array.AsReadOnly(costs));
    }

    /// <summary>Checks the table header (terrain count, type count) before the table is trusted.</summary>
    public static bool HeaderValid(ReadOnlySpan<byte> table) =>
        table.Length >= TableLength && BitConverter.ToInt32(table[..4]) == Terrains && BitConverter.ToInt32(table[4..8]) == Types;
}
