using System.Globalization;

namespace Berwick.Core;

/// <summary>One movement type: how many movement points each of the 64 terrain kinds costs. Stored in tenths; 255 means the terrain cannot be entered.</summary>
public sealed record MovementTypeRecord(int Id, string Name, int Mount, IReadOnlyList<int> Costs)
{
    public string MountText => Mount switch { 0 => "On foot", 1 => "Mounted", 2 => "Flying", _ => $"Mount {Mount}" };
    public string Cost(int terrain) => TerrainData.Format(Costs[terrain]);
}

public static class TerrainData
{
    public const int Terrains = 64, Types = 14, HeaderLength = 8, RowLength = 68, CostOffset = 4, Blocked = 255;
    public const int TableLength = HeaderLength + Types * RowLength;

    public static readonly string[] TypeNames =
        ["Cavalry", "Horseman", "Flying", "Knight", "Armor", "Infantry", "Thief", "Civilian", "Special", "Snow", "War priest", "Fighter", "Priest", "Unused type"];

    /// <summary>Terrain kinds by the published terrain list they match. Several kinds share the same costs, so some names cover a group.</summary>
    public static readonly string[] TerrainNames =
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
        { Display = 0.1, SpecialValue = Blocked, SpecialText = "×", Group = TerrainNames[t] == "Blocked" ? "Blocked" : "" }).ToArray());

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
