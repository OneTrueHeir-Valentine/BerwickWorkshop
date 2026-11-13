using System.Buffers.Binary;

namespace Berwick.Core;

public enum RuleKind { Choice, FixedAmount, Limit, Range }

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
    IReadOnlyList<string>? choices, IReadOnlyList<RuleSite> sites, bool confirmed, Func<IReadOnlyList<uint>, int?>? decode = null, Func<int, string?>? check = null)
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
    /// <summary>Rules that share a group are shown together as one formula; Short is the name inside the group.</summary>
    public string Group { get; init; } = "";
    public string Short { get; init; } = "";
    /// <summary>Skills is shown on its own page; everything else on the Rules page.</summary>
    public string Section { get; init; } = "";
    public int Length => Sites.Count * 4;
    public uint[] Original => Encode(Default);

    public uint[] Encode(int setting)
    {
        if (setting < Min || setting > Max) throw new EditException($"{Name} must be between {Min} and {Max}.");
        if (check?.Invoke(setting) is { } problem) throw new EditException(problem);
        return Sites.Select(s => s.Encode(setting)).ToArray();
    }

    /// <summary>The setting the words represent, or null when any word is unknown or the sites disagree.</summary>
    public int? Decode(IReadOnlyList<uint> words)
    {
        if (words.Count != Sites.Count) return null;
        if (decode is not null) return decode(words);
        int? setting = null;
        bool changed = false;
        for (int i = 0; i < Sites.Count; i++)
        {
            int? s = Sites[i].Decode(words[i]);
            if (s is null) return null;
            if (s == GameRules.AnySetting) continue;
            if (s == GameRules.AnyChanged) { changed = true; continue; }
            if (setting is not null && s != setting) return null;
            setting = s;
        }
        if (setting is null || (changed && setting == Default)) return null;
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
        : Kind == RuleKind.Range ? $"{GameRules.RangeLow(setting)} to {GameRules.RangeHigh(setting)}{(setting == Default ? " (normal)" : "")}"
        : setting == Default ? $"Normal ({setting})" : setting.ToString();

    public TableField Field => new(Key, Name, 0, 0, 1, Min, Max) { Choices = Choices };
}

public static class GameRules
{
    /// <summary>Program address minus file offset inside the executable (one load segment at file offset 0x100, address 0x100000).</summary>
    public const long ProgramToFile = 0xFFF00;

    /// <summary>A site's answer when its word is shared by every setting except the default; another site names the setting.</summary>
    public const int AnyChanged = -1;
    /// <summary>A site's answer when its word is the same for every setting.</summary>
    public const int AnySetting = -2;

    /// <summary>An <c>addiu reg, zero, value</c> instruction holding a limit; decodes any value in range.</summary>
    private static RuleSite Immediate(uint address, uint register, int min, int max)
    {
        uint prefix = 0x24000000u | (register << 16);
        return new(address, v => prefix | (uint)v,
            w => (w & 0xFFFF0000u) == prefix && (w & 0xFFFF) is var v && v >= min && v <= max ? (int)v : null);
    }

    private const uint S1 = 17, S2 = 18, T1 = 9;

    /// <summary>Range rules keep a low and a high value in one setting: low × 256 + high.</summary>
    public static int RangeSetting(int low, int high) => low * 256 + high;
    public static int RangeLow(int setting) => setting / 256;
    public static int RangeHigh(int setting) => setting % 256;

    private const uint A1 = 5, A2 = 6;
    private static GameRule CriticalDamage() => new(13, "criticalBonus", "Critical hit bonus damage",
        "A critical hit adds a random amount between these two values to the damage, both included. The game uses 10 to 20. The low value cannot be higher than the high value.",
        RuleKind.Range, 0, RangeSetting(99, 99), RangeSetting(10, 20), null,
        [new RuleSite(0x0022AEE8, s => 0x24000000u | A1 << 16 | (uint)RangeLow(s), w => null),
         new RuleSite(0x0022AEF0, s => 0x24000000u | A2 << 16 | (uint)RangeHigh(s), w => null)], confirmed: true,
        decode: words =>
        {
            uint lowPrefix = 0x24000000u | A1 << 16, highPrefix = 0x24000000u | A2 << 16;
            if ((words[0] & 0xFFFF0000u) != lowPrefix || (words[1] & 0xFFFF0000u) != highPrefix) return null;
            int low = (int)(words[0] & 0xFFFF), high = (int)(words[1] & 0xFFFF);
            return low <= high && high <= 99 ? RangeSetting(low, high) : null;
        },
        check: s => RangeHigh(s) > 99 ? "Critical hit bonus damage can be at most 99." : RangeLow(s) > RangeHigh(s) ? "The low value of the critical hit bonus cannot be higher than the high value." : null);

    private static GameRule Step(int id, string key, string name, string description, int normal, uint address, bool confirmed) =>
        new(id, key, name, description, RuleKind.Limit, 0, 50, normal, null, [Immediate(address, T1, 0, 50)], confirmed);

    // Shield guard: the damage routine's block-success code (0x0022A640 to 0x0022A6A4) rewritten in fewer
    // instructions with the same behaviour, so that a failed block can add part of the shield's Might to
    // the defender's physical defense. Index 20 holds the shift that picks the part (2 quarter, 1 half, 0 full).
    private const uint GuardStart = 0x0022A640;
    private const int GuardShiftIndex = 20;
    private static readonly uint[] GuardOriginal =
    [
        0x24020001u, 0x12A20008u, 0x8E570038u, 0x12A00004u, 0x00000000u, 0x1000000Au, 0x00000000u, 0x00000000u, 0x10000007u, 0x8E5100A8u,
        0x8FA601A0u, 0x02E0202Du, 0x0C08B2E0u, 0x0000282Du, 0x8E4300ACu, 0x00628821u, 0x8FA601B0u, 0x02E0202Du, 0x0C08B2E0u, 0x0000282Du,
        0x8E4300ACu, 0x00621021u, 0x10000003u, 0xAFA20170u, 0x24020001u, 0xAFA20130u,
    ];
    private static readonly uint[] GuardPatched =
    [
        0x8E570038u, 0x12A00007u, 0x8FA601A0u, 0x02E0202Du, 0x0C08B2E0u, 0x0000282Du, 0x8E4300ACu, 0x10000002u, 0x00628821u, 0x8E5100A8u,
        0x8FA601B0u, 0x02E0202Du, 0x0C08B2E0u, 0x0000282Du, 0x8E4300ACu, 0x00621021u, 0x10000009u, 0xAFA20170u, 0xAFA20130u, 0x8E4300A8u,
        0x00031803u, 0x0015180Bu, 0x10000003u, 0x02C3B021u, 0x1000FFF9u, 0x24020001u,
    ];
    private static uint GuardShift(int setting) => GuardPatched[GuardShiftIndex] | (uint)(3 - setting) << 6;

    private static IReadOnlyList<RuleSite> GuardSites() => Enumerable.Range(0, GuardOriginal.Length).Select(i =>
    {
        uint original = GuardOriginal[i];
        if (i == GuardShiftIndex)
            return new RuleSite(GuardStart + (uint)i * 4, s => s == 0 ? original : GuardShift(s),
                w => w == original ? 0 : w == GuardShift(1) ? 1 : w == GuardShift(2) ? 2 : w == GuardShift(3) ? 3 : null);
        uint patched = GuardPatched[i];
        return new RuleSite(GuardStart + (uint)i * 4, s => s == 0 ? original : patched,
            w => w == original && w == patched ? AnySetting : w == original ? 0 : w == patched ? AnyChanged : null);
    }).ToList();

    // Vantage shield training: where the battle decides whether the defender strikes first (0x00224E68 to
    // 0x00224EF4), an inlined battle-event push is replaced by a call to the routine that does the same push,
    // and the space left grants the defender one shield training event when it has a usable shield.
    // Index 12 holds the success value (0 failed block, 1 successful block).
    private const uint VantageStart = 0x00224E68;
    private const int VantageSuccessIndex = 12;
    private static readonly uint[] VantageOriginal =
    [
        0x12600005u, 0x8E7411E0u, 0x8E6211D8u, 0x14400020u, 0x00000000u, 0x00000000u, 0x8E820000u, 0x00021080u, 0x00541021u, 0xAC400004u,
        0x8E820000u, 0x00021080u, 0x02821021u, 0xAC400064u, 0x8E820000u, 0x24420001u, 0xAE820000u, 0x8E820000u, 0x28420018u, 0x14400010u,
        0x00000000u, 0x8E8200C4u, 0x1440000Bu, 0x00000000u, 0x8F8483E4u, 0x24050004u, 0x240600C8u, 0x0C047C4Cu, 0x24070001u, 0xAE8200C4u,
        0x8E8200C4u, 0xAC4000C4u, 0x8E8200C4u, 0xAC400000u, 0x8E8200C4u, 0xAE6211E0u,
    ];
    private static readonly uint[] VantagePatched =
    [
        0x0260202Du, 0x0000282Du, 0x0C08B878u, 0x0000302Du, 0x8E0200A8u, 0x18400009u, 0x8E07010Cu, 0x18E00007u, 0x0260202Du, 0x8E050000u,
        0x8E060108u, 0x24080001u, 0x24090000u, 0x0C08ACB0u, 0x240A0001u, 0x10000014u, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0,
    ];

    private static IReadOnlyList<RuleSite> VantageSites() => Enumerable.Range(0, VantageOriginal.Length).Select(i =>
    {
        uint original = VantageOriginal[i];
        if (i == VantageSuccessIndex)
            return new RuleSite(VantageStart + (uint)i * 4, s => s == 0 ? original : VantagePatched[i] | (uint)(s - 1),
                w => w == original ? 0 : w == VantagePatched[i] ? 1 : w == (VantagePatched[i] | 1) ? 2 : null);
        uint patched = VantagePatched[i];
        return new RuleSite(VantageStart + (uint)i * 4, s => s == 0 ? original : patched,
            w => w == original && w == patched ? AnySetting : w == original ? 0 : w == patched ? AnyChanged : null);
    }).ToList();

    public const string ExperienceGroup = "Experience formula", StaffGroup = "Staff experience";

    /// <summary>Group explanations: the game's own formula, with the editable numbers named.</summary>
    public static IReadOnlyDictionary<string, string> GroupDescriptions { get; } = new Dictionary<string, string>
    {
        [ExperienceGroup] = "Experience comes only from defeating an enemy. The game adds the enemy class's experience value (edit it on the Classes page) and a bonus for the level difference below, then adds 20% for a unit with the experience skill and any item bonus, and keeps the result between 0 and the highest amount. 100 experience is one level. The level difference is the enemy's level minus the unit's level; past 7 above or 8 below, the end values apply.",
        ["Iaido"] = "When the unit attacks with a sword, Iaido activates at a chance of half the unit's weapon level plus the base chance. The game's base is 40. The roll is 0 to 99, so a total of 100 or more always activates.",
        ["Knock Away"] = "When the unit attacks, Knock Away activates at a chance of half the unit's weapon level plus the base chance. The game's base is 30. The roll is 0 to 99, so a total of 100 or more always activates.",
        ["Spearbane"] = BaneText("a spear"),
        ["Swordbane"] = BaneText("a sword"),
        ["Axebane"] = BaneText("an axe"),
        ["Arrowbane"] = BaneText("a bow"),
        ["Magicbane"] = BaneText("magic"),
        ["Pursuit"] = "A unit with Pursuit attacks again when its attack speed beats the enemy's by at least the speed lead. The game's lead is 5. At 0, an equal attack speed is enough.",
        ["Parry"] = "A unit with the Parry skill parries at a chance of the unit's weapon level plus this bonus, halved: (weapon level + bonus) ÷ 2. The game's bonus is 10. Whether parrying happens at all is set by the Parry rule on the Rules page.",
        ["Shieldfaire"] = "Shieldfaire adds a bonus to the unit's shield block chance. The block chance is the shield's accuracy plus the shield level plus this bonus. The game's bonus is 18.",
        ["Paragon"] = "Paragon adds a share of the experience the unit earns, on top of the normal amount. The game adds 20%.",
        [StaffGroup] = "Using a staff gives the staff's level plus the base amount plus a random amount from 0 up to the random bonus, minus a fifth of the unit's level. It never goes below 0, and the experience skill and item bonuses then apply.",
    };

    private static string BaneText(string weapon) =>
        $"When an enemy attacks the unit with {weapon}, the skill activates at a chance equal to the attacker's hit rate, but never above the highest chance. The game's highest chance is 33.";

    public const string SkillsSection = "Skills";

    // Paragon adds amount x M >> (32 + shift): lui and ori build M, sra holds the shift. M = 0x66666667 with shift 1 is a fifth.
    private static readonly uint[][] ParagonWords =
    [
        [0x3C020000u, 0x34420000u, 0x00021043u],
        [0x3C026666u, 0x34426667u, 0x00021083u],
        [0x3C026666u, 0x34426667u, 0x00021043u],
        [0x3C024000u, 0x34420000u, 0x00021003u],
        [0x3C025555u, 0x34425556u, 0x00021003u],
        [0x3C026666u, 0x34426667u, 0x00021003u],
    ];

    private static GameRule SkillBase(int id, string key, string skill, uint address, uint prefix, int normal) =>
        new(id, key, $"{skill} base chance", "", RuleKind.Limit, 0, 100, normal, null,
            [new RuleSite(address, s => prefix | (uint)s, w => (w & 0xFFFF0000u) == prefix && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = skill, Short = "Base chance", Section = SkillsSection };

    // Bane skills cap the attacker's hit rate with slti at,s0,N and addiu s0,zero,N.
    private static GameRule Bane(int id, string key, string skill, uint compare, uint cap) =>
        new(id, key, $"{skill} highest chance", "", RuleKind.Limit, 0, 100, 33, null,
            [new RuleSite(compare, s => 0x2A010000u | (uint)s, w => (w & 0xFFFF0000u) == 0x2A010000u && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null),
             new RuleSite(cap, s => 0x24100000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24100000u && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = skill, Short = "Highest chance", Section = SkillsSection };

    // The level difference table is data in the program: 16 words, enemy level minus own level from -8 to +7.
    private const uint LevelGapTable = 0x00429F10;
    private static readonly int[] LevelGapDefaults = [-40, -35, -30, -25, -20, -15, -10, -5, 0, 2, 4, 6, 8, 10, 12, 15];
    private static string GapText(int i) => (i - 8) switch { -8 => "8 or more below", 7 => "7 or more above", 0 => "Same level", < 0 and var g => $"{-g} below", var g => $"{g} above" };
    private static GameRule LevelGap(int i) => new(20 + i, $"expLevelGap{i}", $"Experience bonus, enemy {GapText(i)}", "", RuleKind.Limit, -100, 100, LevelGapDefaults[i], null,
        [new RuleSite(LevelGapTable + (uint)i * 4, s => (uint)s, w => null)], confirmed: true,
        decode: words => (int)words[0] is >= -100 and <= 100 and var v ? v : null) { Group = ExperienceGroup, Short = GapText(i) };

    private static GameRule Limit(int id, string key, string name, string description, int max, int normal, uint addAddress, uint storeAddress) =>
        new(id, key, name, description, RuleKind.Limit, 0, max, normal, null,
            [Immediate(addAddress, S1, 0, max), Immediate(storeAddress, S2, 0, max)], confirmed: true);

    public static IReadOnlyList<GameRule> All { get; } = Array.AsReadOnly<GameRule>(
    [
        new GameRule(1, "shieldParry", "Parry",
            "Parrying with a weapon cancels a hit completely. Normal: only units with the Parry skill parry, at a chance based on their weapon level. Always: every unit holding a usable weapon parries every hit. Never: nobody parries. Applies to every unit in battle, enemies included.",
            RuleKind.Choice, 0, 2, 0, ["Normal", "Always", "Never"],
            [new RuleSite(0x002274EC,
                s => s switch { 0 => 0x1020000Cu, 1 => 0x14000000u, _ => 0x1000000Cu },
                w => w switch { 0x1020000Cu => 0, 0x14000000u => 1, 0x1000000Cu => 2, _ => null })], confirmed: true),
        new GameRule(9, "shieldBlock", "Shield block",
            "A successful shield block takes the shield's Might off a hit. Normal rolls against the shield's block chance. Always makes the block roll succeed; Never makes it fail. Attacks that bypass shields still do so. Applies to every unit with a usable shield, enemies included.",
            RuleKind.Choice, 0, 2, 0, ["Normal", "Always", "Never"],
            [new RuleSite(0x0022A638,
                s => s switch { 0 => 0x10200019u, 1 => 0x14000019u, _ => 0x10000019u },
                w => w switch { 0x10200019u => 0, 0x14000019u => 1, 0x10000019u => 2, _ => null })], confirmed: true),
        new GameRule(10, "shieldGuard", "Shield guard on a failed block",
            "When a shield block roll fails against a physical attack, part of the shield's Might counts as Defense, rounded down. A successful block still takes off the full Might. A successful roll that an attack later cancels does not grant this bonus. Attacks that ignore Defense, including knives, also ignore this guard. Off is the game's own behaviour.",
            RuleKind.Choice, 0, 3, 0, ["Off", "Quarter of Might", "Half of Might", "Full Might"], GuardSites(), confirmed: true),
        Step(11, "shieldTraining", "Shield training speed",
            "How much a shield level rises on each successful training roll, in tenths of a level (the game's 4 is 0.4 of a level). Shield training happens when a unit's block roll succeeds, or fails on a hit while the level is below 20. 0 stops shield levels from rising.",
            4, 0x001716A4, confirmed: true),
        Step(12, "weaponTraining", "Weapon training speed",
            "How much a weapon level rises on each successful training roll after an attack, in tenths of a level (the game's 1 is 0.1 of a level). 0 stops weapon levels from rising in battle.",
            1, 0x001716C4, confirmed: true),
        new GameRule(51, "trainingCeiling", "Training roll ceiling",
            "Each weapon or shield training roll needs two random numbers from 0 to 99 to land low enough: one below the growth rate and one below this ceiling minus 3 times the current level (kept between 0 and 100). The game uses 190, so training slows above level 30 and stops near level 63. A higher ceiling keeps high levels training; a lower one makes all training rarer.",
            RuleKind.Limit, 0, 300, 190, null,
            [new RuleSite(0x001717A4, s => 0x24020000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24020000u && (w & 0xFFFF) <= 300 ? (int)(w & 0xFFFF) : null)], confirmed: true),
        new GameRule(14, "failedTraining", "Training from failed attempts",
            "A missed attack or a failed shield block can still train the weapon or shield. The game allows this only below level 20; At every level lets failed attempts train at any level. Successful attempts train at every level either way.",
            RuleKind.Choice, 0, 1, 0, ["Below level 20", "At every level"],
            [new RuleSite(0x00171760, s => s == 0 ? 0x28A10002u : 0x28A10006u, w => w switch { 0x28A10002u => 0, 0x28A10006u => 1, _ => null })], confirmed: true),
        new GameRule(15, "vantageTraining", "Shield training with Vantage",
            "When a unit with Vantage strikes first, the enemy's attacks never reach its shield, so the shield gets no block rolls and no training. With this rule, a defender that strikes first through Vantage gets one shield training roll per battle, if it holds a shield it can use. Counts as a failed block trains only below level 20 unless Training from failed attempts is set to every level. Off is the game's own behaviour.",
            RuleKind.Choice, 0, 2, 0, ["Off", "Counts as a failed block", "Counts as a successful block"], VantageSites(), confirmed: true),
        CriticalDamage(),
        new GameRule(2, "battleExperience", "Experience after a fight",
            "Normal uses the game's experience formulas below. A fixed amount replaces them and gives every unit exactly that much experience after each fight it takes part in (100 is a full level).",
            RuleKind.FixedAmount, 0, 100, 0, null,
            [new RuleSite(0x00224580,
                s => s == 0 ? 0x0040982Du : 0x24130000u | (uint)s,
                w => w == 0x0040982Du ? 0 : (w & 0xFFFF0000u) == 0x24130000u && (w & 0xFFFF) is >= 1 and <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true),
        ..Enumerable.Range(0, 16).Select(LevelGap),
        new GameRule(36, "expHighest", "Highest experience for defeating an enemy", "", RuleKind.Limit, 1, 100, 100, null,
            [new RuleSite(0x0017410C, s => 0x28410000u | (uint)(s + 1), w => (w & 0xFFFF0000u) == 0x28410000u && (w & 0xFFFF) is >= 2 and <= 101 ? (int)(w & 0xFFFF) - 1 : null),
             new RuleSite(0x00174124, s => 0x24020000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24020000u && (w & 0xFFFF) is >= 1 and <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = ExperienceGroup, Short = "Highest amount" },
        new GameRule(37, "staffBase", "Staff experience base", "", RuleKind.Limit, 0, 100, 6, null,
            [new RuleSite(0x00224394, s => 0x26940000u | (uint)s, w => (w & 0xFFFF0000u) == 0x26940000u && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = StaffGroup, Short = "Base amount" },
        new GameRule(38, "staffRandom", "Staff experience random bonus", "", RuleKind.Limit, 0, 50, 3, null,
            [new RuleSite(0x00224358, s => 0x24060000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) <= 50 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x00224370, s => 0x28410000u | (uint)(s + 1), w => (w & 0xFFFF0000u) == 0x28410000u && (w & 0xFFFF) is >= 1 and <= 51 ? (int)(w & 0xFFFF) - 1 : null),
             new RuleSite(0x00224380, s => 0x24020000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24020000u && (w & 0xFFFF) <= 50 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = StaffGroup, Short = "Random bonus up to" },
        SkillBase(40, "iaidoBase", "Iaido", 0x00229E7C, 0x24700000u, 40),
        SkillBase(41, "knockAwayBase", "Knock Away", 0x002278B8, 0x24750000u, 30),
        Bane(42, "spearbaneCap", "Spearbane", 0x002275F0, 0x00227604),
        Bane(43, "swordbaneCap", "Swordbane", 0x00227694, 0x002276A8),
        Bane(44, "axebaneCap", "Axebane", 0x00227734, 0x00227748),
        Bane(45, "arrowbaneCap", "Arrowbane", 0x00227550, 0x00227564),
        Bane(46, "magicbaneCap", "Magicbane", 0x002277D4, 0x002277E8),
        new GameRule(50, "parryBonus", "Parry chance bonus", "", RuleKind.Limit, 0, 200, 10, null,
            [new RuleSite(0x00177F50, s => 0x24430000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24430000u && (w & 0xFFFF) <= 200 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = "Parry", Short = "Bonus", Section = SkillsSection },
        new GameRule(47, "pursuitLead", "Pursuit speed lead", "", RuleKind.Limit, 0, 50, 5, null,
            [new RuleSite(0x00225488, s => 0x28420000u | (uint)s, w => (w & 0xFFFF0000u) == 0x28420000u && (w & 0xFFFF) <= 50 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x002254E8, s => 0x28630000u | (uint)s, w => (w & 0xFFFF0000u) == 0x28630000u && (w & 0xFFFF) <= 50 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = "Pursuit", Short = "Speed lead", Section = SkillsSection },
        new GameRule(48, "shieldfaireBonus", "Shieldfaire block bonus", "", RuleKind.Limit, 0, 100, 18, null,
            [new RuleSite(0x00177E58, s => 0x26100000u | (uint)s, w => (w & 0xFFFF0000u) == 0x26100000u && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
            { Group = "Shieldfaire", Short = "Block bonus", Section = SkillsSection },
        new GameRule(49, "paragonBonus", "Paragon experience bonus", "", RuleKind.Choice, 0, ParagonWords.Length - 1, 2, ["None", "10%", "20%", "25%", "33%", "40%"],
            [new RuleSite(0x00173DC0, s => ParagonWords[s][0], w => null), new RuleSite(0x00173DC8, s => ParagonWords[s][1], w => null), new RuleSite(0x00173DDC, s => ParagonWords[s][2], w => null)],
            confirmed: true, decode: words => Array.FindIndex(ParagonWords, p => p[0] == words[0] && p[1] == words[1] && p[2] == words[2]) is var i and >= 0 ? i : null)
            { Group = "Paragon", Short = "Experience bonus", Section = SkillsSection },
        Limit(3, "hpLimit", "HP bonus limit", "The most HP a character can gain above the class base, through level ups and other gains.", 127, 50, 0x00174BD8, 0x001746A8),
        Limit(4, "strengthLimit", "Strength bonus limit", "The most Strength a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C34, 0x00174704),
        Limit(5, "agilityLimit", "Agility bonus limit", "The most Agility a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C4C, 0x0017471C),
        Limit(6, "luckLimit", "Luck limit", "The highest Luck a character can reach.", 31, 20, 0x00174C64, 0x00174734),
        Limit(7, "defenseLimit", "Defense bonus limit", "The most Defense a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C7C, 0x0017474C),
        Limit(8, "mindLimit", "Mind bonus limit", "The most Mind a character can gain above the class base. The game stores at most 15.", 15, 15, 0x00174C94, 0x00174764),
    ]);

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
