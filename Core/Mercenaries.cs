using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>
/// Mercenary fees, a table of 18 rows of 24 bytes in the game program at 0x0042D450 (identical in both editions).
/// Each row: character ID, flags, base fee, fee per level (signed 16 bits), then values the editor leaves alone.
/// The game charges base + level × per-level amount (0x00170480); when both are 0 it uses a random fee instead (Saphira).
/// </summary>
public static class MercenaryData
{
    public const long Address = 0x0042D450;
    public const int Rows = 18, RowLength = 24, MaxBase = 99999, MaxPerLevel = 9999;
    public static long FileOffset => Address - GameRules.ProgramToFile;

    // The disc's values, used to recognise an exported disc's program.
    private static readonly (int Id, int Base, int PerLevel)[] Original =
    [
        (0x6D, 500, 110), (0x86, 400, 80), (0x60, 500, 100), (0x93, 400, 60), (0x90, 200, 150), (0x95, 400, 150),
        (0x81, 600, 100), (0x98, 800, 100), (0x6F, 1200, 50), (0x96, 400, 30), (0x87, 0, 0), (0x5A, 500, 80),
        (0x6E, 800, 100), (0x74, 0, 100), (0x85, 1000, 70), (0x7E, 700, 150), (0x71, 800, 20), (0x99, 500, 150),
    ];

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("mercenaryBase", "Base fee", 8, 0, 32, 0, MaxBase) { Group = "Mercenary fee" },
        new("mercenaryPerLevel", "Fee per level", 12, 0, 16, 0, MaxPerLevel) { Group = "Mercenary fee" },
    });

    public static int Fee(int baseFee, int perLevel, int level) => baseFee + level * perLevel;

    private static bool LayoutMatches(ReadOnlySpan<byte> executable)
    {
        if (FileOffset < 0 || FileOffset + Rows * RowLength > executable.Length) return false;
        for (int row = 0; row < Rows; row++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(executable[(int)(FileOffset + row * RowLength)..]) != Original[row].Id) return false;
        return true;
    }

    /// <summary>Each mercenary's row and its position, or nothing when the program does not have the known table.</summary>
    public static IReadOnlyList<(int Id, int Row, byte[] Bytes)> Read(ReadOnlySpan<byte> executable)
    {
        if (!LayoutMatches(executable)) return [];
        var rows = new List<(int, int, byte[])>();
        for (int row = 0; row < Rows; row++)
            rows.Add((Original[row].Id, row, executable.Slice((int)(FileOffset + row * RowLength), RowLength).ToArray()));
        return rows;
    }

    /// <summary>Puts the disc's fees back, so a program with edited fees can be matched to its edition.</summary>
    public static void Restore(Span<byte> executable)
    {
        if (!LayoutMatches(executable)) return;
        for (int row = 0; row < Rows; row++)
        {
            var at = executable[(int)(FileOffset + row * RowLength)..];
            BinaryPrimitives.WriteInt32LittleEndian(at[8..], Original[row].Base);
            BinaryPrimitives.WriteInt16LittleEndian(at[12..], (short)Original[row].PerLevel);
        }
    }
}
