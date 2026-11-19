using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>
/// The Kingfisher Pavilion's dishes: 54 rows of 27 words in the game program at 0x00429F50, the same in both editions.
/// Word 0 is the dish's kind (0 to 4), word 1 its menu tier (the random menu takes 1, 2, 3, 2 and 1 dishes from tiers 0 to 4),
/// word 2 its price, then three sets of four (effect code, amount) pairs: for a unit that likes the dish, finds it neutral
/// and dislikes it (read by 0x00170870). A liked dish also raises the unit's condition and a disliked one lowers it.
/// Effect codes 1 to 46 are named by the game's own table at 0x004461F0 (food_eff texts). Dishes 48 to 54 are drinks
/// with no price or effects. Dish names are the game's foods001 to foods054.
/// </summary>
public static class DishData
{
    public const long Address = 0x00429F50;
    public const int Rows = 54, RowLength = 108, Sets = 3, Pairs = 4, Codes = 46;
    public static long FileOffset => Address - GameRules.ProgramToFile;
    public static readonly string[] SetNames = ["Liked", "Neutral", "Disliked"];

    private static readonly int[] Original =
    [
        0, 0, 1000, 1, 5, 24, 3, 21, 2, 0, 0, 1, 5, 24, 2, 21, 1, 0, 0, 1, 5, 24, 1, 0, 0, 0, 0,
        0, 0, 850, 1, 5, 21, 3, 29, 1, 0, 0, 1, 5, 21, 3, 0, 0, 0, 0, 1, 5, 21, 1, 0, 0, 0, 0,
        0, 0, 760, 1, 10, 16, 30, 27, 1, 0, 0, 1, 10, 16, 30, 0, 0, 0, 0, 1, 10, 0, 0, 0, 0, 0, 0,
        0, 1, 690, 1, 1, 8, 5, 0, 0, 0, 0, 1, 1, 8, 3, 0, 0, 0, 0, 1, 1, 8, 1, 0, 0, 0, 0,
        0, 1, 600, 1, 3, 44, 20, 0, 0, 0, 0, 1, 3, 44, 15, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0,
        0, 1, 580, 1, 2, 22, 3, 30, 1, 0, 0, 1, 2, 22, 2, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0,
        0, 1, 520, 1, 2, 3, 5, 24, 1, 0, 0, 1, 2, 3, 3, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0,
        0, 2, 500, 1, 5, 17, 50, 0, 0, 0, 0, 1, 5, 17, 25, 0, 0, 0, 0, 1, 5, 0, 0, 0, 0, 0, 0,
        0, 2, 480, 1, 1, 24, 1, 21, 1, 0, 0, 1, 1, 21, 1, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
        0, 2, 450, 1, 1, 37, 50, 0, 0, 0, 0, 1, 1, 37, 30, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0,
        0, 2, 400, 1, 8, 16, 30, 0, 0, 0, 0, 1, 4, 16, 10, 0, 0, 0, 0, 1, 2, 46, 10, 0, 0, 0, 0,
        0, 2, 380, 1, 2, 8, 3, 0, 0, 0, 0, 1, 2, 8, 2, 0, 0, 0, 0, 1, 2, 46, 10, 0, 0, 0, 0,
        0, 2, 350, 1, 2, 23, 2, 0, 0, 0, 0, 1, 2, 23, 1, 0, 0, 0, 0, 1, 2, 46, 10, 0, 0, 0, 0,
        0, 3, 290, 1, 5, 28, 20, 5, 20, 6, 20, 1, 5, 0, 0, 0, 0, 0, 0, 1, 5, 22, -1, 0, 0, 0, 0,
        0, 3, 220, 1, 4, 33, 1, 0, 0, 0, 0, 1, 4, 0, 0, 0, 0, 0, 0, 1, 4, 21, -1, 0, 0, 0, 0,
        0, 3, 180, 1, 5, 5, 50, 0, 0, 0, 0, 1, 3, 0, 0, 0, 0, 0, 0, 1, 3, 23, -1, 0, 0, 0, 0,
        0, 4, 150, 1, 5, 16, 20, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0, 1, 2, 24, -1, 0, 0, 0, 0,
        1, 0, 820, 9, 20, 0, 0, 0, 0, 0, 0, 9, 15, 0, 0, 0, 0, 0, 0, 9, 10, 0, 0, 0, 0, 0, 0,
        1, 0, 770, 3, 12, 0, 0, 0, 0, 0, 0, 3, 9, 0, 0, 0, 0, 0, 0, 3, 6, 0, 0, 0, 0, 0, 0,
        1, 1, 650, 9, 7, 8, 5, 0, 0, 0, 0, 9, 7, 8, 2, 0, 0, 0, 0, 9, 7, 0, 0, 0, 0, 0, 0,
        1, 1, 590, 9, 10, 41, 1, 0, 0, 0, 0, 9, 10, 0, 0, 0, 0, 0, 0, 9, 10, 3, -3, 0, 0, 0, 0,
        1, 1, 550, 1, 3, 9, 8, 0, 0, 0, 0, 1, 3, 9, 5, 0, 0, 0, 0, 9, 5, 0, 0, 0, 0, 0, 0,
        1, 1, 520, 3, 5, 23, 3, 0, 0, 0, 0, 3, 4, 23, 2, 0, 0, 0, 0, 3, 3, 23, 1, 0, 0, 0, 0,
        1, 2, 490, 9, 5, 45, 10, 0, 0, 0, 0, 9, 5, 0, 0, 0, 0, 0, 0, 9, 5, 21, -1, 0, 0, 0, 0,
        1, 2, 470, 9, 5, 8, 2, 0, 0, 0, 0, 9, 3, 8, 2, 0, 0, 0, 0, 8, 2, 46, 5, 0, 0, 0, 0,
        1, 2, 450, 3, 8, 31, 1, 0, 0, 0, 0, 3, 8, 24, -1, 0, 0, 0, 0, 3, 8, 24, -1, 0, 0, 0, 0,
        1, 2, 420, 1, 5, 9, 6, 0, 0, 0, 0, 1, 3, 9, 3, 0, 0, 0, 0, 9, 3, 46, 5, 0, 0, 0, 0,
        1, 2, 390, 3, 5, 8, 3, 0, 0, 0, 0, 3, 5, 0, 0, 0, 0, 0, 0, 3, 5, 9, -3, 0, 0, 0, 0,
        1, 2, 360, 9, 5, 5, 50, 0, 0, 0, 0, 9, 5, 0, 0, 0, 0, 0, 0, 9, 5, 23, -2, 0, 0, 0, 0,
        1, 3, 270, 9, 3, 3, 3, 0, 0, 0, 0, 9, 2, 3, 2, 0, 0, 0, 0, 9, 1, 3, 1, 0, 0, 0, 0,
        1, 4, 120, 1, 3, 3, 3, 0, 0, 0, 0, 1, 3, 3, 3, 24, -1, 0, 0, 1, 3, 3, 3, 24, -2, 0, 0,
        2, 2, 500, 23, 3, 0, 0, 0, 0, 0, 0, 23, 2, 0, 0, 0, 0, 0, 0, 23, 2, 21, -1, 0, 0, 0, 0,
        2, 2, 500, 3, 3, 32, 1, 0, 0, 0, 0, 3, 3, 23, 1, 24, -1, 0, 0, 3, 3, 24, -1, 0, 0, 0, 0,
        2, 2, 460, 1, 5, 43, 20, 0, 0, 0, 0, 1, 5, 43, 10, 0, 0, 0, 0, 1, 5, 0, 0, 0, 0, 0, 0,
        2, 2, 430, 7, 7, 0, 0, 0, 0, 0, 0, 7, 3, 0, 0, 0, 0, 0, 0, 7, 3, 3, -5, 0, 0, 0, 0,
        2, 2, 390, 1, 4, 42, 1, 0, 0, 0, 0, 1, 4, 0, 0, 0, 0, 0, 0, 1, 4, 23, -1, 0, 0, 0, 0,
        2, 2, 360, 34, 50, 0, 0, 0, 0, 0, 0, 34, 30, 0, 0, 0, 0, 0, 0, 34, 10, 0, 0, 0, 0, 0, 0,
        2, 3, 210, 35, 30, 0, 0, 0, 0, 0, 0, 35, 20, 0, 0, 0, 0, 0, 0, 35, 10, 0, 0, 0, 0, 0, 0,
        2, 3, 180, 23, 1, 43, 10, 0, 0, 0, 0, 23, 1, 0, 0, 0, 0, 0, 0, 23, 1, 24, -1, 0, 0, 0, 0,
        2, 3, 160, 7, 3, 36, 1, 0, 0, 0, 0, 7, 2, 36, 1, 0, 0, 0, 0, 7, 1, 0, 0, 0, 0, 0, 0,
        2, 4, 100, 35, 20, 0, 0, 0, 0, 0, 0, 35, 10, 0, 0, 0, 0, 0, 0, 35, 10, 1, -3, 0, 0, 0, 0,
        2, 4, 50, 5, 30, 6, 30, 0, 0, 0, 0, 5, 10, 6, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 0, 800, 38, 12, 39, 12, 40, 12, 0, 0, 38, 6, 39, 6, 40, 6, 0, 0, 38, 3, 39, 3, 40, 3, 0, 0,
        3, 2, 480, 1, 5, 38, 10, 0, 0, 0, 0, 1, 4, 38, 5, 0, 0, 0, 0, 1, 3, 38, 2, 0, 0, 0, 0,
        3, 3, 250, 1, 3, 39, 10, 0, 0, 0, 0, 1, 2, 39, 5, 0, 0, 0, 0, 1, 1, 39, 2, 0, 0, 0, 0,
        3, 4, 80, 1, 1, 40, 5, 0, 0, 0, 0, 1, 1, 40, 3, 0, 0, 0, 0, 1, 1, 40, 1, 0, 0, 0, 0,
        3, 4, 50, 38, 3, 39, 3, 40, 3, 0, 0, 38, 1, 39, 1, 40, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        4, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    public static string Name(int id) => GameNames.Food(id) ?? $"Dish {id}";

    /// <summary>The game's name for an effect code, without the trailing "became" of stat changes; repeated names get their code.</summary>
    public static string EffectName(int code)
    {
        if (code == 0) return "None";
        string name = GameNames.FoodEffect(code) ?? $"Effect {code}";
        if (name.EndsWith(" became", StringComparison.Ordinal)) name = name[..^7];
        return code is 10 or 29 or 36 or 42 ? $"{name} ({code})" : name;
    }

    public static int EffectOffset(int set, int pair) => 12 + set * 32 + pair * 8;

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(BuildFields());
    private static TableField[] BuildFields()
    {
        var codes = Enumerable.Range(0, Codes + 1).Select(EffectName).ToArray();
        var list = new List<TableField>
        {
            new("price", "Price", 8, 0, 32, 0, 99999) { Group = "Dish" },
            new("tier", "Menu tier", 4, 0, 32, 0, 4) { Group = "Dish" },
            new("kind", "Kind", 0, 0, 32, 0, 4) { Group = "Dish" },
        };
        for (int s = 0; s < Sets; s++)
            for (int k = 0; k < Pairs; k++)
            {
                list.Add(new($"effect{s}{k}", $"Effect {k + 1}", EffectOffset(s, k), 0, 32, 0, Codes) { Group = SetNames[s], Choices = codes });
                list.Add(new($"amount{s}{k}", $"Amount {k + 1}", EffectOffset(s, k) + 4, 0, 32, -999, 999) { Group = SetNames[s], Signed = true });
            }
        return list.ToArray();
    }

    /// <summary>The effects of one set as (code, amount), leaving out empty pairs.</summary>
    public static IReadOnlyList<(int Code, int Amount)> Effects(ReadOnlySpan<byte> row, int set)
    {
        var list = new List<(int, int)>();
        for (int k = 0; k < Pairs; k++)
        {
            int code = BinaryPrimitives.ReadInt32LittleEndian(row[EffectOffset(set, k)..]);
            if (code != 0) list.Add((code, BinaryPrimitives.ReadInt32LittleEndian(row[(EffectOffset(set, k) + 4)..])));
        }
        return list;
    }

    public static string Describe(ReadOnlySpan<byte> row, int set)
    {
        var effects = Effects(row, set);
        return effects.Count == 0 ? "No effect" : string.Join(", ", effects.Select(e => $"{EffectName(e.Code)} {(e.Amount >= 0 ? "+" : "")}{e.Amount}"));
    }

    // The game's effect name table: rows of six words starting with codes 1 to 46.
    private const long EffectTable = 0x004461F0;
    internal static bool LayoutMatches(ReadOnlySpan<byte> executable)
    {
        long table = EffectTable - GameRules.ProgramToFile;
        if (FileOffset < 0 || FileOffset + Rows * RowLength > executable.Length || table + Codes * 24 > executable.Length) return false;
        for (int c = 1; c <= Codes; c++)
            if (BinaryPrimitives.ReadInt32LittleEndian(executable[(int)(table + (c - 1) * 24)..]) != c) return false;
        return TasteData.LayoutMatches(executable);
    }

    /// <summary>Each dish by number (1 to 54), its row and bytes, or nothing when the program does not have the known table.</summary>
    public static IReadOnlyList<(int Id, int Row, byte[] Bytes)> Read(ReadOnlySpan<byte> executable)
    {
        if (!LayoutMatches(executable)) return [];
        var rows = new List<(int, int, byte[])>();
        for (int row = 0; row < Rows; row++)
            rows.Add((row + 1, row, executable.Slice((int)(FileOffset + row * RowLength), RowLength).ToArray()));
        return rows;
    }

    /// <summary>Puts the disc's dishes back, so a program with edited dishes can be matched to its edition.</summary>
    public static void Restore(Span<byte> executable)
    {
        if (!LayoutMatches(executable)) return;
        for (int i = 0; i < Original.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(executable[(int)(FileOffset + i * 4)..], Original[i]);
    }
}

/// <summary>
/// Which dishes each character likes: 35 rows of 55 words in the game program at 0x0042B620, the same in both editions.
/// Word 0 is the character, then one word per dish: 0 liked, 1 neutral, 2 disliked. A character without a row gets no
/// effects from any dish.
/// </summary>
public static class TasteData
{
    public const long Address = 0x0042B620;
    public const int Rows = 35, RowLength = 220;
    public static long FileOffset => Address - GameRules.ProgramToFile;
    public static readonly string[] Choices = ["Liked", "Neutral", "Disliked"];

    private static readonly int[] Original =
    [
        83, 1, 1, 0, 1, 1, 2, 2, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 2, 0, 0, 1, 0, 0, 1, 1, 2, 0, 1, 2, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 2, 0, 1, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        84, 1, 0, 1, 0, 0, 1, 2, 0, 1, 1, 1, 0, 1, 1, 2, 1, 2, 0, 1, 1, 1, 1, 2, 1, 1, 0, 1, 1, 1, 2, 1, 0, 1, 1, 1, 1, 2, 1, 1, 2, 2, 2, 1, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        85, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 2, 1, 2, 0, 1, 0, 2, 1, 1, 1, 2, 2, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 2, 1, 1, 1, 2, 1, 1, 0, 1, 1, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2,
        124, 1, 1, 0, 1, 2, 1, 0, 1, 1, 0, 1, 1, 1, 2, 2, 2, 2, 1, 0, 1, 1, 1, 0, 1, 1, 2, 0, 1, 2, 1, 2, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 2, 1, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        87, 1, 0, 2, 0, 1, 1, 1, 1, 2, 2, 1, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 0, 1, 1, 0, 1, 1, 2, 2, 2, 1, 1, 1, 2, 2, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        88, 0, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 2, 0, 1, 1, 1, 0, 0, 1, 1, 2, 2, 2, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        89, 0, 0, 0, 2, 1, 1, 1, 0, 2, 1, 2, 2, 1, 1, 1, 1, 1, 2, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 2, 1, 0, 0, 2, 1, 1, 1, 0, 1, 1, 1, 1, 2, 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        131, 2, 1, 1, 1, 2, 2, 2, 2, 1, 0, 1, 1, 1, 2, 2, 1, 2, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 1, 0, 1, 2, 1, 1, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        130, 1, 1, 0, 1, 1, 0, 0, 1, 0, 0, 1, 1, 2, 1, 2, 1, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1, 1, 1, 1, 2, 1, 0, 2, 1, 1, 1, 1, 1, 2, 0, 1, 1, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        97, 0, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 1, 0, 2, 2, 1, 2, 2, 1, 2, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 2, 2, 1, 1, 0, 1, 1, 0, 2, 2, 2, 2, 2, 2, 2,
        96, 1, 0, 1, 1, 2, 1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 0, 1, 1, 1, 1, 2, 1, 1, 1, 1, 2, 0, 1, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        132, 0, 1, 1, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 2, 1, 2, 1, 1, 0, 1, 1, 1, 1, 0, 0, 1, 1, 2, 2, 1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 2, 1, 1, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        86, 1, 0, 1, 1, 0, 2, 1, 0, 1, 1, 2, 1, 1, 1, 2, 1, 2, 0, 1, 1, 1, 0, 1, 2, 1, 1, 0, 1, 1, 2, 1, 0, 1, 1, 1, 0, 2, 1, 1, 1, 2, 1, 0, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        99, 1, 1, 0, 1, 1, 1, 2, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 2, 1, 2, 0, 1, 2, 1, 1, 1, 1, 1, 2, 0, 2, 1, 2, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2,
        127, 1, 1, 1, 2, 2, 1, 1, 1, 2, 1, 2, 2, 2, 2, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 1, 2, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        128, 0, 1, 1, 1, 0, 1, 0, 0, 1, 1, 1, 2, 1, 2, 1, 2, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 2, 1, 2, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 2, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        152, 1, 0, 1, 0, 1, 2, 1, 0, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1, 0, 0, 2, 1, 1, 1, 2, 2, 0, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        126, 0, 1, 1, 1, 2, 0, 0, 1, 1, 0, 1, 1, 1, 1, 2, 1, 2, 1, 0, 0, 1, 0, 2, 1, 1, 2, 0, 1, 1, 2, 1, 1, 0, 1, 1, 1, 1, 1, 2, 0, 1, 2, 1, 0, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        149, 1, 0, 1, 0, 1, 2, 2, 1, 1, 1, 1, 0, 1, 1, 2, 1, 1, 0, 2, 1, 0, 1, 2, 2, 1, 0, 1, 1, 0, 2, 1, 0, 1, 1, 2, 0, 1, 1, 2, 1, 1, 1, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        98, 2, 1, 0, 1, 2, 1, 1, 1, 1, 1, 0, 1, 1, 2, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 2, 0, 1, 1, 0, 1, 1, 2, 1, 0, 1, 2, 1, 2, 1, 1, 1, 2, 0, 2, 2, 2, 2, 2, 2, 2,
        109, 1, 1, 0, 0, 1, 1, 1, 2, 0, 1, 0, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 2, 2, 1, 1, 0, 0, 2, 2, 1, 0, 1, 0, 2, 1, 1, 1, 1, 2, 0, 2, 1, 1, 1, 2, 0, 2, 2, 2, 2, 2, 2, 2,
        129, 1, 1, 0, 1, 2, 1, 1, 2, 0, 0, 1, 1, 1, 1, 0, 2, 1, 2, 0, 1, 1, 1, 1, 0, 1, 0, 1, 1, 2, 1, 2, 0, 1, 1, 1, 1, 1, 1, 0, 1, 1, 2, 0, 1, 0, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        113, 1, 1, 1, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2, 1, 2, 1, 2, 0, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 0, 2, 2, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        111, 0, 1, 2, 0, 1, 1, 1, 1, 1, 2, 1, 0, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 0, 1, 0, 2, 0, 0, 1, 1, 2, 1, 0, 1, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        134, 2, 2, 0, 2, 1, 0, 1, 1, 0, 0, 1, 2, 1, 2, 1, 1, 1, 2, 1, 0, 1, 0, 1, 1, 1, 1, 0, 1, 2, 1, 2, 1, 1, 2, 1, 0, 0, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        135, 2, 1, 1, 1, 2, 1, 1, 2, 1, 0, 1, 1, 1, 1, 2, 2, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 2, 1, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 2, 1, 0, 1, 2, 1, 2, 2, 2, 2, 2, 2, 2,
        125, 2, 1, 0, 1, 2, 0, 1, 1, 0, 0, 1, 1, 1, 2, 1, 1, 1, 2, 1, 0, 0, 0, 1, 1, 1, 1, 0, 1, 1, 2, 1, 0, 1, 1, 2, 2, 1, 1, 1, 0, 2, 1, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        90, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 1, 1, 2, 1, 2, 2, 1, 0, 1, 1, 1, 0, 1, 1, 2, 1, 1, 2, 0, 2, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        144, 1, 0, 1, 0, 1, 1, 1, 1, 2, 2, 1, 0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 2, 2, 1, 1, 2, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        133, 1, 1, 0, 2, 1, 0, 1, 2, 0, 0, 1, 1, 1, 2, 2, 2, 1, 0, 2, 1, 1, 1, 2, 1, 1, 1, 1, 1, 0, 1, 2, 1, 1, 1, 1, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        147, 0, 0, 1, 1, 1, 1, 1, 1, 2, 2, 1, 0, 1, 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 0, 2, 1, 1, 1, 1, 2, 2, 1, 1, 2, 2, 1, 0, 1, 1, 1, 0, 1, 2, 2, 2, 2, 2, 2, 2,
        153, 1, 1, 0, 1, 2, 0, 0, 1, 1, 0, 1, 1, 1, 1, 2, 1, 1, 1, 0, 1, 1, 1, 2, 1, 2, 1, 1, 0, 1, 2, 1, 1, 1, 0, 2, 1, 1, 1, 1, 0, 2, 2, 1, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        150, 2, 1, 1, 2, 2, 1, 1, 2, 1, 1, 1, 1, 1, 2, 1, 2, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 1, 1, 1, 0, 1, 2, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 2, 1, 0, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        110, 1, 0, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 1, 0, 1, 0, 1, 2, 1, 2, 1, 1, 1, 1, 1, 1, 0, 2, 1, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 1, 0, 1, 2, 1, 1, 2, 2, 2, 2, 2, 2, 2,
        116, 0, 1, 1, 1, 0, 1, 1, 0, 2, 2, 1, 1, 1, 1, 0, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    ];

    public static string Key(int dish) => $"dish{dish}";
    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(Enumerable.Range(1, DishData.Rows)
        .Select(d => new TableField(Key(d), DishData.Name(d), d * 4, 0, 32, 0, 2) { Group = "Taste", Choices = Choices }).ToArray());

    internal static bool LayoutMatches(ReadOnlySpan<byte> executable)
    {
        if (FileOffset < 0 || FileOffset + Rows * RowLength > executable.Length) return false;
        for (int row = 0; row < Rows; row++)
            if (BinaryPrimitives.ReadInt32LittleEndian(executable[(int)(FileOffset + row * RowLength)..]) != Original[row * 55]) return false;
        return true;
    }

    /// <summary>Each character's taste row, keyed by character, or nothing when the program does not have the known table.</summary>
    public static IReadOnlyList<(int Id, int Row, byte[] Bytes)> Read(ReadOnlySpan<byte> executable)
    {
        if (!DishData.LayoutMatches(executable)) return [];
        var rows = new List<(int, int, byte[])>();
        for (int row = 0; row < Rows; row++)
            rows.Add((Original[row * 55], row, executable.Slice((int)(FileOffset + row * RowLength), RowLength).ToArray()));
        return rows;
    }

    public static void Restore(Span<byte> executable)
    {
        if (!LayoutMatches(executable)) return;
        for (int i = 0; i < Original.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(executable[(int)(FileOffset + i * 4)..], Original[i]);
    }
}
