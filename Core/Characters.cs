using System.Globalization;
using System.Reflection;

namespace Berwick.Core;

public sealed record WeaponValue(string Weapon, int Value);
public sealed record LearnedSkill(int Level, string Skill);
public sealed record CarriedItem(int ItemId, string Name, int Durability);

public sealed record CharacterRecord(
    int Id, string Name, int Level,
    int HpBonus, int StrengthBonus, int DefenseBonus, int AgilityBonus, int MindBonus,
    int HpGrowth, int StrengthGrowth, int DefenseGrowth, int AgilityGrowth, int MindGrowth,
    IReadOnlyList<WeaponValue> WeaponGrowths, IReadOnlyList<WeaponValue> WeaponLevels,
    IReadOnlyList<string> Skills, IReadOnlyList<LearnedSkill> Learned, IReadOnlyList<CarriedItem> Items)
{
    public string IdText => $"0x{Id:X3}";
    public int ClassId { get; init; }
    public string ClassName { get; init; } = "";
    public static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);
    public static string WeaponLevel(int raw) => (raw / 10.0).ToString(raw % 10 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);

    public IReadOnlyDictionary<string, string> Fields()
    {
        var fields = new Dictionary<string, string>
        {
            ["Class"] = $"0x{ClassId:X2}", ["Level"] = Level.ToString(), ["HP bonus"] = Signed(HpBonus), ["Strength bonus"] = Signed(StrengthBonus),
            ["Defense bonus"] = Signed(DefenseBonus), ["Agility bonus"] = Signed(AgilityBonus), ["Mind bonus"] = Signed(MindBonus),
            ["HP growth"] = HpGrowth.ToString(), ["Strength growth"] = StrengthGrowth.ToString(), ["Defense growth"] = DefenseGrowth.ToString(),
            ["Agility growth"] = AgilityGrowth.ToString(), ["Mind growth"] = MindGrowth.ToString(),
            ["Skills"] = string.Join(", ", Skills),
            ["Learned skills"] = string.Join(", ", Learned.Select(l => $"{l.Skill} {l.Level}")),
            ["Starting items"] = string.Join(", ", Items.Select(i => $"{i.ItemId:X3}:{i.Durability}")),
        };
        foreach (var w in WeaponGrowths) fields[$"{w.Weapon} growth"] = w.Value.ToString();
        foreach (var w in WeaponLevels) fields[$"{w.Weapon} level"] = WeaponLevel(w.Value);
        return fields;
    }
}

public sealed record CharacterEntry(int Id, string Name, int RecordOffset);

public static class CharacterDecoder
{
    public const int RecordLength = 0x100;
    public const int GrowthLength = 32;

    public static readonly string[] GrowthWeapons = ["Knife", "Sword", "Spear", "Axe", "Bow", "Crossbow", "Fire", "Thunder", "Wind", "Holy", "Dark", "Shield"];
    private static readonly (string Name, int Offset, int Bit)[] LevelFields =
    [
        ("Knife", 36, 0), ("Sword", 37, 2), ("Spear", 38, 4), ("Axe", 40, 0), ("Bow", 41, 2), ("Crossbow", 42, 4),
        ("Fire", 44, 0), ("Thunder", 45, 2), ("Wind", 46, 4), ("Holy", 48, 0), ("Dark", 49, 2),
        ("Small shield", 50, 4), ("Medium shield", 52, 0), ("Large shield", 53, 2),
    ];

    // Order of the personal skill bitfield. Empty strings are unused bits.
    public static readonly string[] SkillBits =
    [
        "Locktouch", "Canto", "Overwatch", "Watchful", "Triple Shot", "Counter", "Shieldfaire", "Mercy",
        "Adept", "Vantage", "Vengeful", "Spearbane", "Provoke", "Safe Zone", "Guard", "Astra",
        "Arrowbane", "Commander", "Paragon", "Fortune", "Obfuscate", "Steal", "Silence", "Hateful", "Battle Cry",
        "Blessing", "Sunder", "Charisma", "Acrobat", "Evasion", "", "", "Axe Guard",
        "Deadeye", "Hunter", "Aim", "Swimmer", "Resistor", "Search", "One-Two", "Deathmatch",
        "Swap Horse", "Throw", "Parry", "Prepared", "Robust", "Vulnerable", "Focus Chant", "Our Bond",
        "Horse Lover", "Robbery", "Despoil", "Knock Away", "Iaido", "Pulverize", "Mug II", "Miracle",
        "Armsthrift", "Hide", "Hurry", "Lance", "Blade", "", "", "Cavalry", "Celerity",
        "Swordbane", "Axebane", "Limited", "Convert", "Expert", "Magicbane", "Mug", "Versatile",
        "Slow Start", "Fork", "Pursuit", "Double Shot", "Commander II", "Return", "Camouflage", "Scotopic",
        "Supporter", "Climber", "Windsweep", "Desperation", "Flourish", "Slow Start II", "Robust+",
        "Imbue", "Critical", "Protector", "Debility", "Maim", "", "",
    ];

    // Learned skills are numbered without the unused bits.
    public static readonly string[] LearnedSkillIds = SkillBits.Where(s => s.Length > 0).ToArray();

    private static int Signed5(int value) => value >= 16 ? value - 32 : value;

    public static CharacterRecord Decode(CharacterEntry entry, ReadOnlySpan<byte> record, ReadOnlySpan<byte> growth, IReadOnlyDictionary<int, string> itemNames)
    {
        if (record.Length < RecordLength || growth.Length < GrowthLength) throw new InvalidDataException("A character record is incomplete.");
        int B(ReadOnlySpan<byte> data, int offset, int bit, int width) => ItemDecoder.Bits(data, offset, bit, width);

        var weaponGrowths = new List<WeaponValue>();
        for (int i = 0; i < GrowthWeapons.Length; i++)
        {
            int value = (growth[8 + i / 2] >> (4 * (i % 2))) & 0xF;
            if (value != 0) weaponGrowths.Add(new(GrowthWeapons[i], value * 10));
        }
        var weaponLevels = new List<WeaponValue>();
        foreach (var (name, offset, bit) in LevelFields)
        {
            int value = B(record, offset, bit, 10);
            if (value != 0) weaponLevels.Add(new(name, value));
        }
        var skills = new List<string>();
        for (int i = 0; i < SkillBits.Length; i++)
            if ((record[56 + i / 8] >> (i % 8) & 1) != 0) skills.Add(SkillBits[i].Length > 0 ? SkillBits[i] : $"Skill bit {i}");
        var learned = new List<LearnedSkill>();
        for (int i = 0; i < 6; i++)
        {
            int id = growth[26 + i];
            if (id == 0xFF) continue;
            learned.Add(new(growth[20 + i], id < LearnedSkillIds.Length ? LearnedSkillIds[id] : $"Skill {id}"));
        }
        var items = new List<CarriedItem>();
        for (int i = 0; i < 8; i++)
        {
            int at = 0xBC + i * 8;
            int id = B(record, at, 0, 16);
            if (id == 0) continue;
            items.Add(new(id, itemNames.TryGetValue(id, out var name) ? name : $"Item 0x{id:X3}", B(record, at + 2, 4, 8)));
        }

        return new(entry.Id, entry.Name, B(record, 20, 0, 6),
            B(record, 22, 4, 7), Signed5(B(record, 23, 3, 5)), Signed5(B(record, 25, 2, 5)), Signed5(B(record, 24, 0, 5)), Signed5(B(record, 25, 7, 5)),
            B(growth, 0, 0, 7), B(growth, 0, 7, 7), B(growth, 1, 6, 7), B(growth, 4, 2, 7), B(growth, 5, 0, 7),
            weaponGrowths.AsReadOnly(), weaponLevels.AsReadOnly(), skills.AsReadOnly(), learned.AsReadOnly(), items.AsReadOnly()) { ClassId = record[19] };
    }

    public static IReadOnlyList<CharacterEntry> LoadRoster()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("characters.csv")!;
        using var reader = new StreamReader(stream);
        var result = new List<CharacterEntry>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length != 3) continue;
            result.Add(new(int.Parse(parts[1].Trim()[2..], NumberStyles.HexNumber), parts[0].Trim().Replace('_', ' '), int.Parse(parts[2].Trim()[2..], NumberStyles.HexNumber)));
        }
        return result.AsReadOnly();
    }
}

public static class RecordComparison
{
    public static IReadOnlyList<ItemDifference> Compare(IEnumerable<CharacterRecord> first, IEnumerable<CharacterRecord> second)
    {
        var a = first.ToDictionary(c => c.Id); var b = second.ToDictionary(c => c.Id);
        var differences = new List<ItemDifference>();
        foreach (int id in a.Keys.Union(b.Keys).Order())
        {
            if (!a.TryGetValue(id, out var x)) { differences.Add(new(id, "Character", "Absent", "Present")); continue; }
            if (!b.TryGetValue(id, out var y)) { differences.Add(new(id, "Character", "Present", "Absent")); continue; }
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
