using System.Buffers.Binary;

namespace Berwick.Core;

public enum RuleKind { Choice, FixedAmount, Limit }

/// <summary>One instruction a rule changes: how a setting becomes the instruction word, and back.</summary>
public sealed record RuleSite(uint Address, Func<int, uint> Encode, Func<uint, int?> Decode)
{
    public long FileOffset => Address - GameRules.ProgramToFile;
}

/// <summary>
/// A game rule: one or more instructions in the game program with a range of known settings.
/// <see cref="Default"/> is the game's own behaviour. Addresses are the same in both supported editions.
/// </summary>
public sealed class GameRule(int id, string key, string name, string description, RuleKind kind, int min, int max, int @default,
    IReadOnlyList<string>? choices, IReadOnlyList<RuleSite> sites, bool confirmed)
{
    public int Id { get; } = id;
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleKind Kind { get; } = kind;
    public int Min { get; } = min;
    public int Max { get; } = max;
    public int Default { get; } = @default;
    public IReadOnlyList<string>? Choices { get; } = choices;
    public IReadOnlyList<RuleSite> Sites { get; } = sites;
    /// <summary>Checked in a running game. Only confirmed rules are offered on the Rules page.</summary>
    public bool Confirmed { get; } = confirmed;
    public int Length => Sites.Count * 4;
    public uint[] Original => Encode(Default);

    public uint[] Encode(int setting)
    {
        if (setting < Min || setting > Max) throw new EditException($"{Name} must be between {Min} and {Max}.");
        return Sites.Select(s => s.Encode(setting)).ToArray();
    }

    /// <summary>The setting the words represent, or null when any word is unknown or the sites disagree.</summary>
    public int? Decode(IReadOnlyList<uint> words)
    {
        if (words.Count != Sites.Count) return null;
        int? setting = null;
        for (int i = 0; i < Sites.Count; i++)
        {
            int? s = Sites[i].Decode(words[i]);
            if (s is null || (setting is not null && s != setting)) return null;
            setting = s;
        }
        return setting;
    }

    public int? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length) return null;
        var words = new uint[Sites.Count];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
        return Decode(words);
    }

    public byte[] EncodeBytes(int setting)
    {
        var bytes = new byte[Length];
        var words = Encode(setting);
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    public string Describe(int setting) =>
        Choices is { } c && setting - Min >= 0 && setting - Min < c.Count ? c[setting - Min]
        : Kind == RuleKind.FixedAmount ? (setting == 0 ? "Normal" : setting.ToString())
        : setting == Default ? $"Normal ({setting})" : setting.ToString();

    public TableField Field => new(Key, Name, 0, 0, 1, Min, Max) { Choices = Choices };
}

public static class GameRules
{
    /// <summary>Program address minus file offset inside the executable (one load segment at file offset 0x100, address 0x100000).</summary>
    public const long ProgramToFile = 0xFFF00;

    /// <summary>An <c>addiu reg, zero, value</c> instruction holding a limit; decodes any value in range.</summary>
    private static RuleSite Immediate(uint address, uint register, int min, int max)
    {
        uint prefix = 0x24000000u | (register << 16);
        return new(address, v => prefix | (uint)v,
            w => (w & 0xFFFF0000u) == prefix && (w & 0xFFFF) is var v && v >= min && v <= max ? (int)v : null);
    }

    private const uint S1 = 17, S2 = 18;

    private static GameRule Limit(int id, string key, string name, string description, int max, int normal, uint addAddress, uint storeAddress) =>
        new(id, key, name, description, RuleKind.Limit, 0, max, normal, null,
            [Immediate(addAddress, S1, 0, max), Immediate(storeAddress, S2, 0, max)], confirmed: false);

    public static IReadOnlyList<GameRule> All { get; } = Array.AsReadOnly(new[]
    {
        new GameRule(1, "shieldParry", "Shield block",
            "Whether a unit carrying a shield blocks with it (this is the shield roll, not the Parry skill). Normal rolls against the shield rate. Always and Never apply to every unit in battle, enemies included.",
            RuleKind.Choice, 0, 2, 0, ["Normal", "Always", "Never"],
            [new RuleSite(0x002274EC,
                s => s switch { 0 => 0x1020000Cu, 1 => 0x14000000u, _ => 0x1000000Cu },
                w => w switch { 0x1020000Cu => 0, 0x14000000u => 1, 0x1000000Cu => 2, _ => null })], confirmed: true),
        new GameRule(2, "battleExperience", "Experience after a fight",
            "Normal uses the game's experience formula. A fixed amount gives every unit exactly that much experience after each fight it takes part in (100 is a full level).",
            RuleKind.FixedAmount, 0, 100, 0, null,
            [new RuleSite(0x00224580,
                s => s == 0 ? 0x0040982Du : 0x24130000u | (uint)s,
                w => w == 0x0040982Du ? 0 : (w & 0xFFFF0000u) == 0x24130000u && (w & 0xFFFF) is >= 1 and <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true),
        Limit(3, "hpLimit", "HP bonus limit", "The most HP a character can gain above the class base, through level ups and other gains.", 127, 50, 0x00174BD8, 0x001746A8),
        Limit(4, "strengthLimit", "Strength bonus limit", "The most Strength a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C34, 0x00174704),
        Limit(5, "agilityLimit", "Agility bonus limit", "The most Agility a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C4C, 0x0017471C),
        Limit(6, "luckLimit", "Luck limit", "The highest Luck a character can reach.", 31, 20, 0x00174C64, 0x00174734),
        Limit(7, "defenseLimit", "Defense bonus limit", "The most Defense a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C7C, 0x0017474C),
        Limit(8, "mindLimit", "Mind bonus limit", "The most Mind a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C94, 0x00174764),
    });

    public static GameRule ById(int id) => All.FirstOrDefault(r => r.Id == id) ?? throw new EditException("Unknown game rule.");

    public static uint WordAt(ReadOnlySpan<byte> executable, RuleSite site) =>
        site.FileOffset >= 0 && site.FileOffset + 4 <= executable.Length ? BinaryPrimitives.ReadUInt32LittleEndian(executable[(int)site.FileOffset..]) : 0xFFFFFFFF;

    public static uint[] WordsAt(ReadOnlySpan<byte> executable, GameRule rule)
    {
        var words = new uint[rule.Sites.Count];
        for (int i = 0; i < words.Length; i++) words[i] = WordAt(executable, rule.Sites[i]);
        return words;
    }

    /// <summary>A copy of the executable with every recognised rule put back to the game's own instructions.</summary>
    public static byte[] Normalize(ReadOnlySpan<byte> executable)
    {
        var copy = executable.ToArray();
        foreach (var rule in All)
            if (rule.Decode(WordsAt(copy, rule)) is not null)
            {
                var original = rule.Original;
                for (int i = 0; i < rule.Sites.Count; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan((int)rule.Sites[i].FileOffset), original[i]);
            }
        return copy;
    }
}
