using System.Reflection;

namespace Berwick.Core;

/// <summary>Names as the English release shows them in game (items, classes, terrain, movement types).
/// Names the game repeats get a number from the second one on, so lists can tell them apart.</summary>
public static class GameNames
{
    private static readonly Dictionary<(string Kind, int Id), string> names = Load();

    public static string? Item(int id) => names.TryGetValue(("item", id), out var n) ? n : null;
    public static string? Class(int id) => names.TryGetValue(("class", id), out var n) ? n : null;
    public static string? Terrain(int kind) => names.TryGetValue(("terrain", kind), out var n) ? n : null;
    public static string? Food(int dish) => names.TryGetValue(("food", dish), out var n) ? n : null;
    public static string? FoodEffect(int code) => names.TryGetValue(("foodeffect", code), out var n) ? n : null;
    public static string? MovementType(int type) => names.TryGetValue(("movetype", type + 1), out var n) ? n : null;

    /// <summary>Adds " (2)", " (3)" and so on to names that appear more than once, in the order given.</summary>
    public static IReadOnlyList<string> Distinguish(IEnumerable<string> list)
    {
        var count = new Dictionary<string, int>();
        var result = new List<string>();
        foreach (var name in list)
        {
            int n = count.TryGetValue(name, out var c) ? c + 1 : 1;
            count[name] = n;
            result.Add(n == 1 ? name : $"{name} ({n})");
        }
        return result;
    }

    private static Dictionary<(string, int), string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("game_names.csv")!;
        using var reader = new StreamReader(stream);
        var raw = new List<(string Kind, int Id, string Name)>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], out int id)) raw.Add((parts[0], id, parts[2]));
        }
        var result = new Dictionary<(string, int), string>();
        foreach (var kind in raw.Select(r => r.Kind).Distinct())
        {
            var rows = raw.Where(r => r.Kind == kind).OrderBy(r => r.Id).ToList();
            var shown = kind == "item" ? Distinguish(rows.Select(r => r.Name)) : rows.Select(r => r.Name).ToList();
            for (int i = 0; i < rows.Count; i++) result[(kind, rows[i].Id)] = shown[i];
        }
        return result;
    }
}
