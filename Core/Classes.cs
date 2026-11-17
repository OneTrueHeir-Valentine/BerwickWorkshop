using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Berwick.Core;

public sealed record ClassRecord(
    int Id, string Name, int Hp, int Strength, int Defense, int Agility, int Mind, int Move, int Experience,
    IReadOnlyList<string> Skills, IReadOnlyList<WeaponValue> WeaponCaps)
{
    public string IdText => $"0x{Id:X2}";
    /// <summary>Class record byte 6 bit 1 through byte 7 bit 0: the row of the terrain cost table this class uses.</summary>
    public int MovementType { get; init; }
    /// <summary>Class record byte 3 bits 4 to 5: 0 on foot, 1 mounted, 2 flying.</summary>
    public int Mount { get; init; }
    /// <summary>Class record byte 0x1B bits 4 to 6: small, medium and large shields the class can use.</summary>
    public int ShieldSizes { get; init; }
    public string ShieldSizeText => ShieldSizes == 0 ? "None" : string.Join(", ", new[] { "Small", "Medium", "Large" }.Where((_, i) => (ShieldSizes >> i & 1) != 0));
    public string MovementTypeText => MovementType < TerrainData.TypeNames.Length ? TerrainData.TypeNames[MovementType] : $"Type {MovementType}";
    public string MountText => Mount switch { 0 => "On foot", 1 => "Mounted", 2 => "Flying", _ => $"Mount {Mount}" };
    public string SkillText => Skills.Count == 0 ? "None" : string.Join(", ", Skills);

    public IReadOnlyDictionary<string, string> Fields()
    {
        var fields = new Dictionary<string, string>
        {
            ["HP"] = Hp.ToString(), ["Strength"] = Strength.ToString(), ["Defense"] = Defense.ToString(),
            ["Agility"] = Agility.ToString(), ["Mind"] = Mind.ToString(), ["Move"] = Move.ToString(),
            ["Experience"] = Experience.ToString(), ["Skills"] = SkillText, ["Movement type"] = MovementTypeText, ["Mount"] = MountText, ["Shield sizes"] = ShieldSizeText,
        };
        foreach (var cap in WeaponCaps) fields[$"{cap.Weapon} cap"] = cap.Value.ToString();
        return fields;
    }
}

public static class ClassDecoder
{
    public const int RecordLength = 100;

    private static readonly (string Name, int Offset, int Bit)[] CapFields =
    [
        ("Knife", 28, 7), ("Sword", 29, 5), ("Spear", 30, 3), ("Axe", 31, 1), ("Bow", 32, 0), ("Crossbow", 32, 6),
        ("Fire", 33, 4), ("Thunder", 34, 2), ("Wind", 35, 0), ("Holy", 36, 0), ("Dark", 36, 6),
        ("Small shield", 37, 4), ("Medium shield", 38, 2), ("Large shield", 39, 0),
    ];

    public static ClassRecord Decode(int id, string name, ReadOnlySpan<byte> record)
    {
        if (record.Length < RecordLength) throw new InvalidDataException("A class record is incomplete.");
        int B(ReadOnlySpan<byte> data, int offset, int bit, int width) => ItemDecoder.Bits(data, offset, bit, width);
        var skills = new List<string>();
        for (int i = 0; i < CharacterDecoder.SkillBits.Length; i++)
            if ((record[12 + i / 8] >> (i % 8) & 1) != 0)
                skills.Add(CharacterDecoder.SkillBits[i].Length > 0 ? CharacterDecoder.SkillBits[i] : $"Skill bit {i}");
        var caps = new List<WeaponValue>();
        foreach (var (weapon, offset, bit) in CapFields)
        {
            int value = B(record, offset, bit, 6);
            if (value != 0) caps.Add(new(weapon, value));
        }
        // Base stats are five packed 5-bit fields: Strength, Agility, Defense, Mind, HP.
        return new(id, name,
            Hp: B(record, 2, 5, 5), Strength: B(record, 0, 0, 5), Defense: B(record, 1, 2, 5), Agility: B(record, 0, 5, 5), Mind: B(record, 2, 0, 5),
            Move: B(record, 8, 0, 4), Experience: B(record, 4, 0, 7), Skills: skills.AsReadOnly(), WeaponCaps: caps.AsReadOnly())
        { MovementType = B(record, 6, 1, 8), Mount = B(record, 3, 4, 2), ShieldSizes = B(record, 0x1B, 4, 3) };
    }

    public static string DisplayName(string raw, int id = -1)
    {
        string name = raw.Trim(), suffix = "";
        if (name.EndsWith("_R")) { name = name[..^2]; suffix = " (mounted)"; }
        if (name.EndsWith("_M")) { name = name[..^2]; suffix = " (male)" + suffix; }
        else if (name.EndsWith("_F")) { name = name[..^2]; suffix = " (female)" + suffix; }
        foreach (var who in new[] { "Larentia", "Paramythis", "Owen" })
            if (name.EndsWith(who) && name.Length > who.Length) { name = name[..^who.Length]; suffix = $" ({who})" + suffix; }
        if (GameNames.Class(id) is { } shown) return shown + suffix;
        name = name.Replace("Chevalire", "Chevalier");
        name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z0-9])", " ");
        return name + suffix;
    }

    public static IReadOnlyList<(int Id, string Name)> LoadList()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("classes.csv")!;
        using var reader = new StreamReader(stream);
        var seen = new HashSet<int>();
        var result = new List<(int, string)>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length != 2) continue;
            int id = int.Parse(parts[1].Trim()[2..], NumberStyles.HexNumber);
            if (seen.Add(id)) result.Add((id, DisplayName(parts[0], id)));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        var shown = GameNames.Distinguish(result.Select(c => c.Item2));
        return result.Select((c, i) => (c.Item1, shown[i])).ToList().AsReadOnly();
    }
}

public static class ClassComparison
{
    public static IReadOnlyList<ItemDifference> Compare(IEnumerable<ClassRecord> first, IEnumerable<ClassRecord> second)
    {
        var a = first.ToDictionary(c => c.Id); var b = second.ToDictionary(c => c.Id);
        var differences = new List<ItemDifference>();
        foreach (int id in a.Keys.Union(b.Keys).Order())
        {
            if (!a.TryGetValue(id, out var x)) { differences.Add(new(id, "Class", "Absent", "Present")); continue; }
            if (!b.TryGetValue(id, out var y)) { differences.Add(new(id, "Class", "Present", "Absent")); continue; }
            var left = x.Fields(); var right = y.Fields();
            foreach (var key in left.Keys.Union(right.Keys))
            {
                string l = left.TryGetValue(key, out var lv) ? lv : "None", r = right.TryGetValue(key, out var rv) ? rv : "None";
                if (l != r) differences.Add(new(id, key, l, r));
            }
        }
        return differences.AsReadOnly();
    }
}
