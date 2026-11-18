using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>
/// Atelier synthesis recipes: 30 rows of six words in the game program at 0x00467C20, the same in both editions.
/// Word 0 is the item made, word 1 the craftsman (0 Orlando, weapons and shields; 5 Lumiere, magic and potions),
/// words 2 to 5 the ingredients (0 for none). Recipes are keyed by the item they make.
/// </summary>
public static class RecipeData
{
    public const long Address = 0x00467C20;
    public const int Rows = 30, RowLength = 24, Ingredients = 4;
    public static long FileOffset => Address - GameRules.ProgramToFile;

    private static readonly uint[][] Original =
    [
        [0x059, 0x000, 0x023, 0x215, 0x217, 0x216],
        [0x05A, 0x000, 0x02A, 0x218, 0x248, 0x235],
        [0x05B, 0x000, 0x025, 0x21A, 0x21B, 0x24B],
        [0x05C, 0x000, 0x02F, 0x216, 0x21C, 0x24A],
        [0x05D, 0x000, 0x032, 0x21F, 0x229, 0x228],
        [0x08B, 0x000, 0x06A, 0x221, 0x222, 0x232],
        [0x08D, 0x000, 0x064, 0x22A, 0x222, 0x218],
        [0x08E, 0x000, 0x066, 0x221, 0x226, 0x22D],
        [0x072, 0x000, 0x067, 0x228, 0x229, 0x22D],
        [0x0BC, 0x000, 0x099, 0x21E, 0x22A, 0x24B],
        [0x0BD, 0x000, 0x096, 0x22C, 0x22F, 0x224],
        [0x0BF, 0x000, 0x098, 0x248, 0x21B, 0x21F],
        [0x006, 0x000, 0x001, 0x22E, 0x231, 0x249],
        [0x0D6, 0x000, 0x0C4, 0x23C, 0x23F, 0x21A],
        [0x0D7, 0x000, 0x0C3, 0x23C, 0x23F, 0x230],
        [0x0EF, 0x000, 0x0D9, 0x240, 0x243, 0x24C],
        [0x0DF, 0x000, 0x0DE, 0x240, 0x243, 0x21C],
        [0x160, 0x000, 0x163, 0x223, 0x232, 0x233],
        [0x178, 0x000, 0x164, 0x217, 0x21E, 0x22F],
        [0x158, 0x000, 0x14C, 0x227, 0x235, 0x223],
        [0x0FF, 0x005, 0x215, 0x245, 0x24A, 0x000],
        [0x10E, 0x005, 0x22E, 0x245, 0x249, 0x000],
        [0x107, 0x005, 0x22C, 0x245, 0x237, 0x000],
        [0x12B, 0x005, 0x238, 0x24D, 0x24C, 0x000],
        [0x130, 0x005, 0x238, 0x234, 0x233, 0x000],
        [0x12D, 0x005, 0x238, 0x24D, 0x237, 0x000],
        [0x1DD, 0x005, 0x227, 0x231, 0x226, 0x000],
        [0x1D2, 0x005, 0x239, 0x246, 0x234, 0x000],
        [0x1D6, 0x005, 0x239, 0x23A, 0x230, 0x000],
        [0x1D3, 0x005, 0x246, 0x23A, 0x224, 0x000],
    ];

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(Enumerable.Range(0, Ingredients)
        .Select(i => new TableField($"ingredient{i + 1}", $"Ingredient {i + 1}", 8 + i * 4, 0, 32, 0, 1023) { Group = "Atelier recipe", ItemReference = true }).ToArray());

    public static string Craftsman(int type) => type switch { 0 => "Orlando", 5 => "Lumiere", _ => $"Craftsman {type}" };

    /// <summary>The craftsman number of a recipe row (word 1).</summary>
    public static int Type(ReadOnlySpan<byte> row) => (int)(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) & 7);

    private static bool LayoutMatches(ReadOnlySpan<byte> executable)
    {
        if (FileOffset < 0 || FileOffset + Rows * RowLength > executable.Length) return false;
        for (int row = 0; row < Rows; row++)
        {
            var at = executable[(int)(FileOffset + row * RowLength)..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(at) != Original[row][0] || BinaryPrimitives.ReadUInt32LittleEndian(at[4..]) != Original[row][1]) return false;
        }
        return true;
    }

    /// <summary>Each recipe's result item, its row and bytes, or nothing when the program does not have the known table.</summary>
    public static IReadOnlyList<(int Id, int Row, byte[] Bytes)> Read(ReadOnlySpan<byte> executable)
    {
        if (!LayoutMatches(executable)) return [];
        var rows = new List<(int, int, byte[])>();
        for (int row = 0; row < Rows; row++)
            rows.Add(((int)Original[row][0], row, executable.Slice((int)(FileOffset + row * RowLength), RowLength).ToArray()));
        return rows;
    }

    /// <summary>Puts the disc's ingredients back, so a program with edited recipes can be matched to its edition.</summary>
    public static void Restore(Span<byte> executable)
    {
        if (!LayoutMatches(executable)) return;
        for (int row = 0; row < Rows; row++)
            for (int i = 2; i < 6; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(executable[(int)(FileOffset + row * RowLength + i * 4)..], Original[row][i]);
    }

    public static string Validate(IReadOnlyDictionary<string, int> values, Func<int, bool> itemExists)
    {
        if (values.TryGetValue("ingredient1", out int first) && first == 0) return "A recipe needs a first ingredient.";
        foreach (var (key, id) in values)
            if (key.StartsWith("ingredient", StringComparison.Ordinal) && id != 0 && !itemExists(id)) return $"There is no item 0x{id:X3}.";
        return "";
    }
}
