using System.Buffers.Binary;
using System.Reflection;

namespace Berwick.Core;

/// <summary>
/// Map sprites: a table of 244 rows of 32 bytes in the game program at 0x004358B0, the same in both editions, one row per class
/// by class number minus one. Word 0 points to the sprite's name (onmap/NAME.ar), word 1 to the name used while the unit holds a
/// shield (0 for none); the other words go with the sprite (frame count, flags, size and type). Rows are keyed by class number;
/// a class takes another class's map sprite by copying that class's whole row.
/// </summary>
public static class MapSpriteData
{
    public const long Address = 0x004358B0;
    public const int Classes = 244, RowLength = 32, Words = 8;
    public static long FileOffset => Address - GameRules.ProgramToFile;

    private static readonly Lazy<uint[][]> original = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("map_sprites.csv")!;
        using var reader = new StreamReader(stream);
        var rows = new uint[Classes][];
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length != Words + 1 || !int.TryParse(parts[0], out int c) || c < 1 || c > Classes) continue;
            rows[c - 1] = parts.Skip(1).Select(p => Convert.ToUInt32(p, 16)).ToArray();
        }
        return rows;
    });

    /// <summary>One field per word, so a project lists exactly what changed. Word 0 and 1 are the names.</summary>
    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(Enumerable.Range(0, Words)
        .Select(i => new TableField(i switch { 0 => "sprite", 1 => "shieldSprite", _ => $"spriteWord{i + 1}" },
            i switch { 0 => "Map sprite", 1 => "Map sprite with a shield", _ => $"Map sprite setting {i - 1}" }, i * 4, 0, 32, int.MinValue, int.MaxValue)
            { Group = "Map sprite", Signed = true }).ToArray());

    private static bool IsName(ReadOnlySpan<byte> executable, uint pointer)
    {
        if (pointer == 0) return true;
        long at = pointer - GameRules.ProgramToFile;
        return at >= 0 && at + 3 < executable.Length && executable[(int)at] == (byte)'o' && executable[(int)at + 1] == (byte)'m';
    }

    /// <summary>The table is there when every row's name words point to sprite names.</summary>
    private static bool LayoutMatches(ReadOnlySpan<byte> executable)
    {
        if (FileOffset < 0 || FileOffset + Classes * RowLength > executable.Length) return false;
        for (int c = 0; c < Classes; c++)
        {
            var row = executable[(int)(FileOffset + c * RowLength)..];
            if (!IsName(executable, BinaryPrimitives.ReadUInt32LittleEndian(row)) || !IsName(executable, BinaryPrimitives.ReadUInt32LittleEndian(row[4..]))) return false;
        }
        return true;
    }

    /// <summary>Each class's row, or nothing when the program does not have the known table.</summary>
    public static IReadOnlyList<(int ClassId, byte[] Bytes)> Read(ReadOnlySpan<byte> executable)
    {
        if (!LayoutMatches(executable)) return [];
        var rows = new List<(int, byte[])>();
        for (int c = 0; c < Classes; c++) rows.Add((c + 1, executable.Slice((int)(FileOffset + c * RowLength), RowLength).ToArray()));
        return rows;
    }

    /// <summary>The sprite name a name word points to, read from the program.</summary>
    public static string? Name(ReadOnlySpan<byte> executable, uint pointer)
    {
        if (pointer == 0) return null;
        long at = pointer - GameRules.ProgramToFile;
        if (at < 0 || at >= executable.Length) return null;
        int end = executable[(int)at..].IndexOf((byte)0);
        return end <= 0 || end > 32 ? null : System.Text.Encoding.ASCII.GetString(executable.Slice((int)at, end));
    }

    /// <summary>Puts the disc's rows back, so a program with changed map sprites can be matched to its edition.</summary>
    public static void Restore(Span<byte> executable)
    {
        if (!LayoutMatches(executable)) return;
        var rows = original.Value;
        for (int c = 0; c < Classes; c++)
            for (int w = 0; w < Words; w++)
                BinaryPrimitives.WriteUInt32LittleEndian(executable[(int)(FileOffset + c * RowLength + w * 4)..], rows[c][w]);
    }

    /// <summary>A row may only hold a row the disc already has, so every name points to a sprite on the disc.</summary>
    public static string Validate(IReadOnlyDictionary<string, int> values)
    {
        var rows = original.Value;
        var row = Fields.Select(f => values.TryGetValue(f.Key, out int v) ? (uint)v : (uint?)null).ToArray();
        if (row.Any(v => v is null)) return "";
        return rows.Any(r => r.Select((w, i) => w == row[i]).All(x => x)) ? "" : "A map sprite can only be copied whole from a class on the disc.";
    }
}
