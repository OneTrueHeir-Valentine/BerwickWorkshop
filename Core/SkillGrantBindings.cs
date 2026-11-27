namespace Berwick.Core;

/// <summary>Known native delivery bindings, distinct from personal skill IDs. A binding is not proof of all lifecycle behavior.</summary>
public sealed record SkillGrantBinding(string Source, int Code, string Skill, string Scope);

public static class SkillGrantBindings
{
    public static IReadOnlyList<SkillGrantBinding> All { get; } = Array.AsReadOnly(new SkillGrantBinding[]
    {
        new("Food", 10, "Counter", "Named native food effect"),
        new("Food", 29, "Counter", "Named native food effect; separate code"),
        new("Food", 30, "Vantage", "Named native food effect"),
        new("Food", 31, "Arrowbane", "Named native food effect"),
        new("Food", 32, "Magicbane", "Named native food effect"),
        new("Food", 41, "Adept", "Named native food effect"),
        new("Equipment flag", 45, "Hide", "Existing item flag"),
        new("Equipment flag", 46, "Overwatch", "Existing item flag"),
        new("Equipment flag", 47, "Parry", "Existing item flag"),
        new("Equipment flag", 81, "Horse-Lover", "Existing item flag"),
        new("Furniture rule", 250, "Miracle", "Reese-specific battle behavior, not a general skill grant"),
    });
    /// <summary>Query encoding observed in the native three-bank skill dispatcher. This is not a food or item effect code.</summary>
    public static uint QueryMaskForPersonalBit(int bit)
    {
        if (bit < 0 || bit >= CharacterDecoder.SkillBits.Length || CharacterDecoder.SkillBits[bit].Length == 0)
            throw new EditException("Choose a named skill, not a reserved skill bit.");
        uint bank = (bit / 32) switch { 0 => 0u, 1 => 0x80000000u, 2 => 0x40000000u, _ => throw new EditException("Unknown skill bank.") };
        return bank | (1u << (bit % 32));
    }

    /// <summary>Native enumeration at 0x0017D100 visits three banks of 30 positions, skipping the reserved two bits in each stored word.</summary>
    public static uint QueryMaskForEnumerationIndex(int index)
    {
        if (index < 0 || index >= 90) throw new EditException("Choose a native skill position from 0 to 89.");
        return QueryMaskForPersonalBit(index / 30 * 32 + index % 30);
    }

    public static IReadOnlyList<SkillGrantBinding> ForSkill(string skill) => All.Where(x => x.Skill == skill).ToArray();

    public static SkillGrantBinding? Food(int code) => All.FirstOrDefault(x => x.Source == "Food" && x.Code == code);
}
