using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>
/// A game rule: one instruction in the game program with a small set of known settings.
/// Setting 0 is always the game's own behaviour. Addresses are the same in both supported editions.
/// </summary>
public sealed class GameRule(int id, string key, string name, string description, uint address, int min, int max,
    IReadOnlyList<string>? choices, Func<int, uint> encode, Func<uint, int?> decode)
{
    public int Id { get; } = id;
    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public uint Address { get; } = address;
    public int Min { get; } = min;
    public int Max { get; } = max;
    public IReadOnlyList<string>? Choices { get; } = choices;
    public uint Original => encode(0);
    public long FileOffset => Address - GameRules.ProgramToFile;

    public uint Encode(int setting)
    {
        if (setting < Min || setting > Max) throw new EditException($"{Name} must be between {Min} and {Max}.");
        return encode(setting);
    }

    /// <summary>The setting an instruction word represents, or null when the word is not one this rule knows.</summary>
    public int? Decode(uint word) => decode(word);

    public string Describe(int setting) => Choices is { } c && setting >= 0 && setting < c.Count ? c[setting] : setting == 0 ? "Normal" : setting.ToString();

    public TableField Field => new(Key, Name, 0, 0, 1, Min, Max) { Choices = Choices };
}

public static class GameRules
{
    /// <summary>Program address minus file offset inside the executable (one load segment at file offset 0x100, address 0x100000).</summary>
    public const long ProgramToFile = 0xFFF00;

    public static IReadOnlyList<GameRule> All { get; } = Array.AsReadOnly(new[]
    {
        new GameRule(1, "shieldParry", "Shield parry",
            "Whether a unit with a shield parries. Normal uses the shield rate roll. Always and Never apply to every unit in battle, enemies included.",
            0x002274EC, 0, 2, ["Normal", "Always", "Never"],
            s => s switch { 0 => 0x1020000Cu, 1 => 0x14000000u, _ => 0x1000000Cu },
            w => w switch { 0x1020000Cu => 0, 0x14000000u => 1, 0x1000000Cu => 2, _ => null }),
        new GameRule(2, "battleExperience", "Experience after a fight",
            "Normal uses the game's experience formula. A fixed amount gives every unit exactly that much experience after each fight it takes part in (100 is a full level).",
            0x00224580, 0, 100, null,
            s => s == 0 ? 0x0040982Du : 0x24130000u | (uint)s,
            w => w == 0x0040982Du ? 0 : (w & 0xFFFF0000u) == 0x24130000u && (w & 0xFFFF) is >= 1 and <= 100 ? (int)(w & 0xFFFF) : null),
    });

    public static GameRule ById(int id) => All.FirstOrDefault(r => r.Id == id) ?? throw new EditException("Unknown game rule.");

    public static uint WordAt(ReadOnlySpan<byte> executable, GameRule rule) =>
        rule.FileOffset >= 0 && rule.FileOffset + 4 <= executable.Length ? BinaryPrimitives.ReadUInt32LittleEndian(executable[(int)rule.FileOffset..]) : 0xFFFFFFFF;

    /// <summary>A copy of the executable with every recognised rule site put back to the game's own instruction.</summary>
    public static byte[] Normalize(ReadOnlySpan<byte> executable)
    {
        var copy = executable.ToArray();
        foreach (var rule in All)
            if (rule.Decode(WordAt(copy, rule)) is not null)
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan((int)rule.FileOffset), rule.Original);
        return copy;
    }
}
