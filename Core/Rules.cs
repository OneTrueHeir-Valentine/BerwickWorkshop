using System.Buffers.Binary;

namespace Berwick.Core;

public enum RuleKind { Choice, FixedAmount, Limit, Range, Pair }

/// <summary>
/// One instruction a rule changes: how a setting becomes the instruction word, and back. File gives the place in the
/// program file when it is not a loaded address (the program header). Live is false for a word a running game cannot
/// take: the reserved page (heap memory until a disc with the new load segment is booted), the heap start and the header.
/// </summary>
public sealed record RuleSite(uint Address, Func<int, uint> Encode, Func<uint, int?> Decode)
{
    public long? File { get; init; }
    public bool Live { get; init; } = true;
    public long FileOffset => File ?? GameRules.FileOf(Address);
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
    /// <summary>For a Pair rule: what each of the two numbers sets.</summary>
    public IReadOnlyList<string> PairLabels { get; init; } = [];
    /// <summary>A colour shown beside the rule, as #RRGGBB (the durability band marks).</summary>
    public string Swatch { get; init; } = "";
    public int Length => Sites.Count * 4;
    public uint[] Original => Encode(Default);

    /// <summary>The words a running game gets for a setting: sites it cannot take keep their original words.</summary>
    public uint[] LiveEncode(int setting)
    {
        var words = Encode(setting);
        var original = Original;
        for (int i = 0; i < words.Length; i++) if (!Sites[i].Live) words[i] = original[i];
        return words;
    }

    /// <summary>The setting of words read from a running game, where sites it cannot take hold their original words.</summary>
    public int? LiveDecode(IReadOnlyList<uint> words)
    {
        if (Decode(words) is int found) return found;
        if (Sites.All(s => s.Live) || Max - Min > 4096) return null;
        for (int s = Min; s <= Max; s++)
        {
            uint[] candidate;
            try { candidate = LiveEncode(s); } catch (EditException) { continue; }
            if (candidate.SequenceEqual(words)) return s;
        }
        return null;
    }

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
        : Kind == RuleKind.Pair ? $"{PairLabels[0]} {GameRules.RangeLow(setting)}, {PairLabels[1]} {GameRules.RangeHigh(setting)}{(setting == Default ? " (normal)" : "")}"
        : setting == Default ? $"Normal ({setting})" : setting.ToString();

    public TableField Field => new(Key, Name, 0, 0, 1, Min, Max) { Choices = Choices };
}

public static class GameRules
{
    /// <summary>Program address minus file offset inside the executable (one load segment at file offset 0x100, address 0x100000).</summary>
    public const long ProgramToFile = 0xFFF00;

    // A page of new code: the program's empty third load segment (header at file 0x74) is pointed at unused debug data
    // (file 0x3B5000) and loads it to 0x00571000, and the heap start (addiu v0,v0,0x800 at 0x00126114) moves up one page so
    // the heap begins at 0x00572000. 0x00570380 to 0x00571000 stays as it was: the library heap (SetupHeap at startup)
    // grows into it. Both editions share these bytes.
    public const uint ReservedAddress = 0x00571000;
    public const long ReservedFile = 0x3B5000;
    public const int ReservedLength = 0x1000;
    public static long FileOf(uint address) =>
        address >= ReservedAddress && address < ReservedAddress + ReservedLength ? ReservedFile + (address - ReservedAddress) : address - ProgramToFile;
    internal const uint HeapStartSite = 0x00126114, HeapStartOriginal = 0x24420800u, HeapStartReserved = 0x24421800u;
    internal const long SegmentHeader = 0x74;
    internal static readonly uint[] SegmentOriginal = [1, 0x3B4780, 0x00570380, 0x00570380, 0, 0, 6, 0x10];
    internal static readonly uint[] SegmentReserved = [1, (uint)ReservedFile, ReservedAddress, ReservedAddress, ReservedLength, ReservedLength, 7, 0x80];
    /// <summary>The sites that load the reserved page, set whenever a rule places code there.</summary>
    internal static IEnumerable<RuleSite> ReservedPageSites(Func<int, bool> uses) =>
        new[] { new RuleSite(HeapStartSite, s => uses(s) ? HeapStartReserved : HeapStartOriginal, w => null) { Live = false } }
        .Concat(Enumerable.Range(0, 8).Select(i => new RuleSite((uint)(SegmentHeader + i * 4), s => uses(s) ? SegmentReserved[i] : SegmentOriginal[i], w => null)
            { File = SegmentHeader + i * 4, Live = false }));

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
    // Focus Chant (battle +0x4C bit 2) raises attack by a random 10 to 30 percent (0x002271B0).
    private static GameRule FocusChantAttack() => new(168, "focusChantAttack", "Focus Chant attack bonus",
        "When a unit attacks using Focus Chant, its attack rises by a random percentage between these two values, both included. The game uses 10% to 30%. The low value cannot be higher than the high value.",
        RuleKind.Range, 0, RangeSetting(100, 100), RangeSetting(10, 30), null,
        [new RuleSite(0x002271B4, s => 0x24000000u | A1 << 16 | (uint)RangeLow(s), w => null),
         new RuleSite(0x002271BC, s => 0x24000000u | A2 << 16 | (uint)RangeHigh(s), w => null)], confirmed: true,
        decode: words =>
        {
            uint lowPrefix = 0x24000000u | A1 << 16, highPrefix = 0x24000000u | A2 << 16;
            if ((words[0] & 0xFFFF0000u) != lowPrefix || (words[1] & 0xFFFF0000u) != highPrefix) return null;
            int low = (int)(words[0] & 0xFFFF), high = (int)(words[1] & 0xFFFF);
            return low <= high && high <= 100 ? RangeSetting(low, high) : null;
        },
        check: s => RangeHigh(s) > 100 ? "The Focus Chant attack bonus can be at most 100%." : RangeLow(s) > RangeHigh(s) ? "The low value of the Focus Chant attack bonus cannot be higher than the high value." : null);

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
    // Settings 4 and 5 (quarter or half, at least 1) use a tighter layout: the delay slot after the block branch
    // (0x0022A63C) loads the shield's Might into at, the s5 == 0 case becomes a likely branch that copies it,
    // and the two freed words raise the failed-block guard to at least 1 before knives clear it.
    private static readonly uint[] GuardMinimum =
    [
        0x8E570038u, 0x52A00007u, 0x0020882Du, 0x8FA601A0u, 0x02E0202Du, 0x0C08B2E0u, 0x0000282Du, 0x8E4300ACu, 0x00628821u, 0x8FA601B0u,
        0x02E0202Du, 0x0C08B2E0u, 0x0000282Du, 0x8E4300ACu, 0x00621021u, 0x1000000Au, 0xAFA20170u, 0xAFA20130u, 0x00011803u, 0x28610001u,
        0x0021180Bu, 0x0015180Bu, 0x10000003u, 0x02C3B021u, 0x1000FFF8u, 0x24020001u,
    ];
    private const int GuardMinimumShiftIndex = 18;
    private const uint GuardSlot = 0x0022A63C, GuardSlotLoad = 0x8E4100A8u;
    private static uint GuardShift(int setting) => GuardPatched[GuardShiftIndex] | (uint)(3 - setting) << 6;

    /// <summary>The damage routine words from 0x0022A63C in the earlier layouts (read from discs exported before the guard moved to setup).</summary>
    private static uint[] GuardWords(int setting)
    {
        var words = new uint[GuardOriginal.Length + 1];
        if (setting >= 4)
        {
            words[0] = GuardSlotLoad;
            GuardMinimum.CopyTo(words, 1);
            words[1 + GuardMinimumShiftIndex] |= (uint)(setting == 4 ? 2 : 1) << 6;
            return words;
        }
        (setting == 0 ? GuardOriginal : GuardPatched).CopyTo(words, 1);
        if (setting > 0) words[1 + GuardShiftIndex] = GuardShift(setting);
        return words;
    }

    // Shield guard, current layout: battle setup jumps at 0x0022C2C0 (after Desperation, sleep and the other cases have set
    // defense and shield Might) to padding at 0x001481C8, which moves part of the Might (+0xA8) into physical defense (+0x6C,
    // used for damage) and the defense against this opponent (+0x68, shown in the forecast). A successful block adds the rest.
    // The status screen (0x0019C1C4, 0x0019C214) and the equipment window (0x00324EDC, 0x00389B3C) call small routines placed
    // in the reserved page at 0x00571000 that do the same move for what they show. The damage routine stays original.
    // Versions 0.93 to 0.108 placed those routines at 0x0022E660, which is not free: the town calls it when leaving town.
    // That routine is now always kept original, and discs made with the old layout are still recognised.
    private const uint GuardHook = 0x0022C2C0, GuardHookOriginal = 0x8E550008u, GuardCave = 0x001481C8, GuardShow = GameRules.ReservedAddress, GuardShowOld = 0x0022E660;
    // The unused debug bytes at the start of the reserved page in the program file (the same on both discs).
    private static readonly uint[] ReservedOriginal =
    [
        0x08590012u, 0x00550000u, 0x00230008u, 0x02010005u, 0x38000000u, 0x6E654C00u, 0x00001F00u, 0x12000500u, 0x00087800u, 0x08005500u,
        0x05002300u, 0x00020100u, 0x00380000u, 0x006D754Eu, 0x00000018u, 0x0012000Bu, 0x000008D1u, 0x1B100111u, 0x01210010u, 0x00101F30u,
        0x00000020u, 0x0012000Cu, 0x000008B0u, 0x00090055u, 0x00050023u, 0x00000301u, 0x54003800u, 0x00455059u,
    ];
    private const int GuardCaveLength = 14;
    private static readonly (uint Address, uint Original, int Routine)[] GuardShowHooks =
        [(0x00324EDC, 0x0260282Du, 0), (0x00389B3C, 0x02C0282Du, 1), (0x0019C1C4, 0x0200202Du, 2), (0x0019C214, 0xAFA20148u, 2)];
    private static readonly uint[] GuardShowOriginal =
    [
        0x27BDFFD0u, 0xFFBF0020u, 0x7FB10010u, 0x7FB00000u, 0x0000882Du, 0x27908640u, 0x06200003u, 0x2A230002u, 0x14600003u, 0x00000000u,
        0x10000003u, 0x0000202Du, 0x8E040000u, 0x00000000u, 0x10800009u, 0x00000000u, 0x2402FFFFu, 0x24050002u, 0xAC82003Cu, 0x0000302Du,
        0xAC800040u, 0x0C08C05Cu, 0x0000382Du, 0x00000000u, 0x26310001u, 0x2A230002u, 0x1460FFEBu, 0x26100004u,
    ];
    private static readonly int[] GuardShowStart = [0, 10, 20];
    /// <summary>v1 = the guard part of the Might in v0 (at least 1 for settings 4 and 5 when there is a shield), v0 = the rest.</summary>
    private static uint[] GuardSplit(int setting)
    {
        int shift = setting switch { 1 or 4 => 2, 2 or 5 => 1, _ => 0 };
        bool least = setting >= 4;
        return [0x00021803u | (uint)shift << 6, least ? 0x0002082Bu : 0u, least ? 0x0023180Au : 0u, 0x00431023u];
    }
    private static uint[] GuardCaveWords(int setting) => setting == 0 ? new uint[GuardCaveLength] :
    [
        0x8E550008u, 0x8E6200A8u,                   // lw s5,8(s2) (the hooked instruction); lw v0,168(s3) shield Might
        .. GuardSplit(setting),
        0xAE6200A8u,                                // sw v0,168(s3)  what a successful block still adds
        0x8E61006Cu, 0x00230821u, 0xAE61006Cu,      // defense +0x6C += guard
        0x8E610068u, 0x00230821u,                   // defense against this opponent +0x68 += guard
        0x08000000u | (GuardHook + 8) >> 2,         // j 0x0022C2C8
        0xAE610068u,
    ];
    private static uint[] GuardShowWords(int setting)
    {
        if (setting == 0) return GuardShowOriginal;
        var split = GuardSplit(setting);
        return
        [
            .. split, 0xAEA2001Cu, 0x8EA10010u, 0x00230821u, 0xAEA10010u, 0x03E00008u, 0x0260282Du,   // equipment window: Might +28, Def +16 of s5
            .. split, 0xAFA200CCu, 0x8FA100C0u, 0x00230821u, 0xAFA100C0u, 0x03E00008u, 0x02C0282Du,   // command window: Might sp+204, Def sp+192
            .. split, 0xAFA20148u, 0x02A3A821u, 0x03E00008u, 0x0200202Du,                             // status screen: Might sp+328, Def s5
        ];
    }
    private static uint[] GuardAll(int setting) => GuardLayout(setting, GuardShow);

    // The words of every site for a setting, with the display routines at the given place (the reserved page, or the
    // old place 0x0022E660 for recognising discs made by earlier versions).
    private static uint[] GuardLayout(int setting, uint show)
    {
        bool reserved = show == GuardShow && setting != 0;
        return
        [
            .. GuardWords(0), setting == 0 ? GuardHookOriginal : 0x08000000u | GuardCave >> 2, .. GuardCaveWords(setting),
            .. GuardShowHooks.Select(h => setting == 0 ? h.Original : 0x0C000000u | (show + (uint)GuardShowStart[h.Routine] * 4) >> 2),
            .. show == GuardShowOld ? GuardShowWords(setting) : GuardShowOriginal,
            .. reserved ? GuardShowWords(setting) : ReservedOriginal,
            reserved ? GameRules.HeapStartReserved : GameRules.HeapStartOriginal,
            .. reserved ? GameRules.SegmentReserved : GameRules.SegmentOriginal,
        ];
    }

    private static IReadOnlyList<RuleSite> GuardSites()
    {
        var addresses = Enumerable.Range(0, GuardOriginal.Length + 1).Select(i => GuardSlot + (uint)i * 4)
            .Append(GuardHook).Concat(Enumerable.Range(0, GuardCaveLength).Select(i => GuardCave + (uint)i * 4)).ToList();
        var sites = addresses.Select((address, i) => new RuleSite(address, s => GuardAll(s)[i], w => null)).ToList();
        int at = sites.Count;
        foreach (var h in GuardShowHooks) { int i = at++; sites.Add(new RuleSite(h.Address, s => GuardAll(s)[i], w => null) { Live = false }); }
        for (int k = 0; k < GuardShowOriginal.Length; k++) { int i = at++; sites.Add(new RuleSite(GuardShowOld + (uint)k * 4, s => GuardAll(s)[i], w => null)); }
        for (int k = 0; k < ReservedOriginal.Length; k++) { int i = at++; sites.Add(new RuleSite(GuardShow + (uint)k * 4, s => GuardAll(s)[i], w => null) { Live = false }); }
        foreach (var site in GameRules.ReservedPageSites(s => s != 0)) sites.Add(site);
        return sites;
    }

    private static int? GuardDecode(IReadOnlyList<uint> words)
    {
        for (int s = 0; s <= 5; s++) if (GuardAll(s).SequenceEqual(words) || s > 0 && GuardLayout(s, GuardShowOld).SequenceEqual(words)) return s;
        int damage = GuardOriginal.Length + 1;
        var rest = words.Skip(damage).ToArray();
        if (!GuardAll(0).Skip(damage).SequenceEqual(rest)) return null;
        for (int s = 1; s <= 5; s++) if (GuardWords(s).SequenceEqual(words.Take(damage))) return s;
        return null;
    }

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
        [FurnitureGroup] = "Furniture bought from the furniture shop goes into Reese's room, where each piece can be shown or put away. While a piece is shown, Reese gets its bonus in every battle; the bonuses go to Reese only. Sword Mount adds to his weapon level growth for every weapon type, not only swords. Big Rug adds to the chance of his Adept extra attack when he has Adept. Deer Mount lowers enemy critical chance against him. Coat of Arms adds to his shield block chance. Tapestry adds to the range of his Commander and Leader auras. Bird Statue adds to his hit in fights on forest terrain. Art \"Hope\" lowers enemy hit against him. Maiden Statue gives him Miracle, a second roll against a hit that would defeat him, at 10 less hit. Art \"Revival\" adds to his chance that a weapon use costs no durability. Porcelain Vase raises the favor female mercenaries gain from 5 to 10 (events 136 and 152 on the Events page). Coffers and Treasure Chest can be opened in the room for items and gold. Luxury Bed is meant to add 10 to his HP growth, but the game checks it and ignores the result, so it does nothing.",
        [ExperienceGroup] = "Experience comes only from defeating an enemy. The game adds the enemy class's experience value (edit it on the Classes page) and a bonus for the level difference below, then adds 20% for a unit with the experience skill and any item bonus, and keeps the result between 0 and the highest amount. 100 experience is one level. The level difference is the enemy's level minus the unit's level; past 7 above or 8 below, the end values apply.",
        ["Iaido"] = "When the unit attacks with a sword, Iaido activates at a chance of half the unit's weapon level plus the base chance. The game's base is 40. The roll is 0 to 99, so a total of 100 or more always activates. After the unit evades a strike, its counter gets extra hit and critical; the game gives 33 hit and 3 critical. Counter bonus after can also count a hit that does 0 damage, a parry, or a hit cancelled by a bane skill such as Swordbane. Weapon chooses which weapons it works with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Knock Away"] = "When the unit attacks, Knock Away activates at a chance of half the unit's weapon level plus the base chance. The game's base is 30. The roll is 0 to 99, so a total of 100 or more always activates.",
        ["Spearbane"] = BaneText("a spear"),
        ["Swordbane"] = BaneText("a sword"),
        ["Axebane"] = BaneText("an axe"),
        ["Arrowbane"] = BaneText("a bow"),
        ["Magicbane"] = BaneText("magic"),
        ["Pursuit"] = "A unit with Pursuit attacks again when its attack speed beats the enemy's by at least the speed lead. The game's lead is 5. At 0, an equal attack speed is enough.",
        ["Parry"] = "A unit with the Parry skill parries at a chance of the unit's weapon level plus this bonus, halved: (weapon level + bonus) ÷ 2. The game's bonus is 10. Whether parrying happens at all is set by the Parry rule on the Rules page.",
        ["Sunder"] = "When the unit attacks using Sunder, its critical chance rises by this bonus for that attack. The game's bonus is 25. " + "In the game, Sunder can only be chosen before the unit moves. Allowed also offers it after a move.",
        ["Desperation"] = "When the unit attacks using Desperation, its hit chance rises by this bonus for that attack, and its own defenses drop to 0. The game's bonus is 22. " + "In the game, Desperation can be chosen after a move unless the unit's weapon rules it out. Allowed offers it after any move; Not allowed offers it only before a move. In the game the enemy always hits a unit using Desperation. Own defense set to Kept leaves the unit its defenses. The weapon it needs is set on the Flourish card. Lance attacks cannot be countered, so Desperation is never offered with a lance.",
        ["Aim"] = "When the unit attacks using Aim, its hit chance rises by this bonus for that attack. The game's bonus is 33. In the game, Aim can only be chosen before the unit moves. Allowed also offers it after a move. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Deadeye"] = "When the unit attacks using Deadeye, its hit chance changes by this amount for that attack. The game's amount is -15. In the game, Deadeye can only be chosen before the unit moves. Allowed also offers it after a move. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation. The same weapon setting covers Double-Shot and Triple-Shot.",
        ["Steal"] = "In the game, Steal, Robbery and Mug+ share one count per map: 3 steals, or 2 for a unit with Mug+, and each steal uses one. No limit keeps the count from going down. In the game, Steal can only be chosen before the unit moves. Allowed also offers it after a move.",
        ["Swimmer"] = "With Swimmer, water costs the unit the same movement as plain ground. In the game this works mounted or on foot. On foot only gives a mounted unit no help from Swimmer until it dismounts.",
        ["Pulverize"] = "In the game, Pulverize can only be chosen before the unit moves. Allowed also offers it after a move. The first hit has double attack and the unit's own defenses drop to 0. Attack set to Not doubled keeps the normal attack; Own defense set to Kept leaves the unit its defenses. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Overwatch"] = "In the game, Overwatch can only be chosen before the unit moves. Allowed also offers it after a move.",
        ["Huntsman"] = "With a bow, crossbow or ballista, an enemy standing in forest gets no avoid from the terrain. Weapon chooses which weapons it works with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Axe Guard"] = "In the game, Axe Guard can only be chosen before the unit moves. Allowed also offers it after a move. Until its next turn, attacks on the unit can be blocked: the chance is the axe level plus the block bonus, and a block takes the axe's Might off the hit. The game's block bonus is 25. Weapon chooses which weapons it works with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation. With another weapon the block takes that weapon's Might off the hit.",
        ["Windsweep"] = "In the game, Windsweep can only be chosen before the unit moves. Allowed also offers it after a move.",
        ["Battle Cry"] = "In the game, Battle Cry can only be chosen before the unit moves. Allowed also offers it after a move. Each use adds the hit and attack for each use to the unit's next fight; the game gives 10 hit and 3 attack. It can be used up to the most uses before a fight; the game allows 6.",
        ["Blessing"] = "At the start of each turn, each ally within range recovers a random amount of HP from 0 up to the most healing. The game's range is 3 hexes and the most healing 3.",
        ["Horse-Lover"] = "After each chapter, the unit's horse recovers this much HP, up to its maximum. The game's value is 20.",
        ["Scotopic"] = "Adds to the unit's vision range, which matters on maps with fog. The game adds 1.",
        ["Our Bond"] = "The game raises Reese's critical chance to this value while Lynette stands next to him; it checks Lynette herself, not the skill. The game's value is 100.",
        ["Hateful"] = "When a unit with Hateful is attacked, it becomes Provoked at its attacker for this many of its own turns. The game uses 10.",
        ["Provoke"] = "A provoked enemy stays provoked for Turns provoked of its own turns; the game uses 10. In the game, Provoke can only be chosen before the unit moves. Allowed also offers it after a move. It reaches enemies within range; the game's range is 5 hexes.",
        ["Evasion"] = "While Evasion is active the unit gets the avoid bonus; the game gives 50. In the game, Evasion can only be chosen before the unit moves. Allowed also offers it after a move. After use, Evasion can be chosen again this many of the unit's turns later. The game's wait is 4; at 1 it can be used every turn.",
        ["Silence"] = "In the game, Silence can only be chosen before the unit moves. Allowed also offers it after a move. After use, Silence can be chosen again this many of the unit's turns later; the game's wait is 9.",
        ["One-Two"] = "After use, One-Two can be chosen again this many of the unit's turns later. The game's wait is 6.",
        ["Camouflage"] = "A unit with Camouflage multiplies the avoid it gets from terrain. The game uses one and a half times.",
        ["Armsthrift"] = "Every use of a weapon has a chance to cost no durability. Armsthrift adds this much to that chance; the game adds 20.",
        ["Watchful"] = "A hidden unit is uncovered when a unit of the other side comes within 1 hex, or within this many hexes of a unit with Watchful. The game uses 3. The range for units without Watchful is also the view they refresh in fog after acting; the game uses 1.",
        ["Counter"] = "In the game, a unit that takes damage in a fight stops counterattacking unless it has Counter. Keep countering lets every unit, on both sides, go on counterattacking after taking damage.",
        ["Vantage"] = "When attacked, a unit with Vantage strikes first if its attack speed beats the attacker's and its weapon can reach. Attack speed set to Any speed drops the speed check.",
        ["Despoil"] = "After the unit defeats an enemy, its side gains the enemy's level times the gold per level, plus a random amount from 0 up to the random extra. The game gives 4 per level and up to 50 extra.",
        ["Imbue"] = "In the game, Imbue can only be chosen before the unit moves. Allowed also offers it after a move. After use, Imbue can be chosen again this many of the unit's turns later. The game's wait is 3; at 1 it can be used every turn.",
        ["Double-Shot"] = "In the game, Double-Shot can be chosen before or after the unit moves. Not allowed offers it only before a move. After use, Double-Shot can be chosen again this many of the unit's turns later. The game's wait is 3; at 1 it can be used every turn. Double-Shot strikes two times in the game; it can be set from 1 to 7. The weapon it needs is set on the Deadeye card.",
        ["Triple-Shot"] = "In the game, Triple-Shot can be chosen before or after the unit moves. Not allowed offers it only before a move. After use, Triple-Shot can be chosen again this many of the unit's turns later. The game's wait is 4; at 1 it can be used every turn. Triple-Shot strikes three times in the game; it can be set from 1 to 7. The weapon it needs is set on the Deadeye card.",
        ["Astra"] = "In the game, Astra can be chosen before or after the unit moves. Not allowed offers it only before a move. After use, Astra can be chosen again this many of the unit's turns later. The game's wait is 6; at 1 it can be used every turn. Astra strikes five times in the game; it can be set from 1 to 7. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Focus Chant"] = "In the game, Focus Chant can be chosen before or after the unit moves. Not allowed offers it only before a move. After use, Focus Chant can be chosen again this many of the unit's turns later. The game's wait is 6; at 1 it can be used every turn. Focus Chant adds 30 hit in the game and raises attack by a random 10% to 30% (set on the Game rules page). Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Canto"] = "In the game, Canto is not offered after a fight in which the unit took damage; this can be changed so it is offered anyway.",
        ["Debility"] = "A unit with Debility has its HP, Strength, Defense, Agility and Mind divided by this number. The game uses 2 (halved); 1 turns the penalty off.",
        ["Pitchfork"] = "Weapon type allowed sets which weapons the skill lets a unit use; the game adds pitchforks.",
        ["Lance"] = "Weapon type allowed sets which weapons the skill lets a unit use; the game adds lances. Lances themselves still need a mounted class.",
        ["Blade"] = "Weapon type allowed sets which weapons the skill lets a unit use; the game adds blades.",
        ["Locktouch"] = "Who can open sets who may open doors, gates and chests. In the game a unit needs Locktouch, or a Door Key (used up) for doors and gates; Every unit lets anyone open them as a Locktouch holder does.",
        ["Guard"] = "While a unit guards, the game halves its own defense and avoid; this can be changed so they are kept. In the game, Guard can be chosen before or after the unit moves. Not allowed offers it only before a move.",
        ["Search"] = "In the game, Search can be chosen before or after the unit moves. Not allowed offers it only before a move.",
        ["Return"] = "In the game, Return can be chosen before or after the unit moves. Not allowed offers it only before a move.",
        ["Convert"] = "In the game, Convert can be chosen before or after the unit moves. Not allowed offers it only before a move.",
        ["Throw"] = "In the game, Throw can be chosen after a move unless the unit's weapon rules it out. Allowed offers it after any move; Not allowed offers it only before a move. A throw gets 10 more hit in the game. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation.",
        ["Mercy"] = "In the game, Mercy can be chosen after a move unless the unit's weapon rules it out. Allowed offers it after any move; Not allowed offers it only before a move.",
        ["Flourish"] = "In the game, Flourish can be chosen after a move unless the unit's weapon rules it out. Allowed offers it after any move; Not allowed offers it only before a move. After use, Flourish can be chosen again this many of the unit's turns later. The game's wait is 3; at 1 it can be used every turn. Flourish halves the unit's attack for that attack and adds 33 to the chance that the hit cripples. Attack set to Not halved keeps the full attack. Weapon chooses which weapons it can attack with: the game's own choice, one weapon type, or any weapon. The unit must still be able to use the weapon, and a class with no artwork for that weapon type shows an empty hand in the attack animation. The same weapon setting covers Desperation.",
        ["Hurry"] = "A unit that chooses Hurry moves this many hexes farther, after which its only command is Wait. The game gives 1.",
        ["Deathmatch"] = "Rounds sets how many rounds of fighting Deathmatch starts; the game uses 5 (a normal attack has 1). In the game, Deathmatch can be chosen after a move unless the unit's weapon rules it out. Allowed offers it after any move; Not allowed offers it only before a move.",
        ["Shieldfaire"] = "Shieldfaire adds a bonus to the unit's shield block chance. The block chance is the shield's accuracy plus the shield level plus this bonus. The game's bonus is 18.",
        [BattleStatsGroup] = "Which stat each part of a fight uses. Physical attack is the stat plus weapon Might (crossbows and ballistae use Might alone). Physical defense and magic defense are the stat plus their bonuses. Mag on the status screen is the Mind stat itself, not magic defense. Attack speed is the stat minus the weight of the weapon and shield beyond what the carrying stat offsets, and avoid is twice attack speed, so the attack speed stat also sets avoid. Healing is the stat plus the staff's power. Each choice counts the stat with its bonuses from equipment and skills. The game uses Strength for attack, Defense for defense, Mind for magic defense and healing, Agility for attack speed, and half of Strength to offset weight.",
        [HitCriticalGroup] = "Hit is the weapon's accuracy plus the unit's weapon level plus bonuses from items, food and skills; the target's avoid is then taken off. Critical is the weapon's critical plus the weapon level above a threshold (the game uses 50, so most units get nothing from it) plus bonuses, and a unit with the Critical skill also adds its level; the target's critical evade is taken off. The weapon level used is the unit's level in the weapon's type, at most the class's limit. Hit adds and Critical adds can use a stat instead (with its equipment and skill bonuses) or nothing.",
        [DamageGroup] = "Damage is attack minus defense minus any block reduction (a shield's Might on a successful block, or a guard skill). If that is 0 or less the hit does no damage in the game; it can be set to deal 1 instead. A critical hit then adds its bonus damage on top. Knives leave the attack stat out and add a random amount from 0 up to Strength in battle instead, so their damage varies from hit to hit. Which stat knives add, and whether they add a random part of it or all of it, can be changed; knives also ignore the target's Defense.",
        ["Commander"] = "Each ally within range gets this much hit for every unit with Commander near it. The game gives 10, and the range is 3 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        ["Safe Zone"] = "Each ally within range gets this much hit and avoid for every unit with Safe Zone near it. The game gives 15 of each, and the range is 3 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        ["Charisma"] = "Each ally within range gets this much hit for every unit with Charisma near it. The game gives 3, and the range is 5 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        ["Supporter"] = "A unit with Supporter gets these bonuses while an ally with Supporter is within range (3 hexes in the game). The game gives 20 hit and 4 critical; the critical bonus can be 0, 1, 2, 4, 8 or 16. Partner set to Not needed gives a unit with Supporter the bonuses on its own.",
        ["Leader"] = "Each ally within range gets this much avoid for every unit with Leader near it. The game gives 5, and the range is 3 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        ["Protector"] = "Each ally within range gets this much defense for every unit with Protector near it. The game gives 5, and the range is 3 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        ["Resistor"] = "Each ally within range gets this much magic defense for every unit with Resistor near it. The game gives 5, and the range is 3 hexes. Who counts: in the game only other units with the skill count, and each one adds its bonus. The holder can be included, so it helps itself, and several holders can count as one.",
        [InjuryGroup] = "When a hit lands, the target may be crippled. The chance is the base, plus the knife bonus when the attacker uses a knife, plus Flourish and item bonuses, rolled against 0 to 99. Units with Robust or Robust+ are never crippled. The game's base is 3 and the knife bonus 6.",
        ["Miracle"] ="When a hit would kill a unit with Miracle, the hit is rolled again with the attacker's hit lowered by this much. The game takes off 10.",
        ["Mug"] = "From an adjacent hex, a hit can take an item the enemy is not using. The chance is the unit's level divided by 10 plus this base, plus any steal bonus on the unit's items, in percent. The game's base is 3.",
        ["Mug+"] = "Like Mug, but it can also take equipped items while the unit has steals left. The chance is the unit's level divided by 10 plus this base, plus item bonuses; the game's base is 3. A unit with Mug+ starts each map with 2 steals in the game, shared with Steal and Robbery.",
        ["Robbery"] = "On defeating an enemy at close range, the unit may take its items. In the game the chance is 25% and the enemy must be at range 0 or 1; both can be changed. A unit with Robbery starts each map with 3 steals in the game, shared with Steal and Mug+.",
        ["Maim"] = "Maim activates at a chance of half the weapon level plus this base and doubles the chance that the hit cripples. The game's base is 5, and the skill can be used 5 times per map.",
        ["Celerity"] ="A unit with Celerity adds this many hexes to its move while on foot; mounted units, wyvern riders included, do not get it. The game adds 1. Mounted units set to Bonus too gives it to mounted units as well.",
        ["Slow Start"] = "A unit with Slow Start takes this many hexes off its move, mounted or on foot. The game takes 1.",
        ["Critical"] = "A unit with the Critical skill adds its level to its critical chance. The bonus can be changed to twice or four times the level, half of it, or nothing.",
        ["Paragon"] = "Paragon adds a share of the experience the unit earns, on top of the normal amount, while the unit is at or below the highest level. The game adds 20% up to level 10.",
        ["Vengeful"] = "After taking damage while holding an axe, the unit rolls a chance for each point of damage taken. On success it counters with double hit and its attack raised by the damage. The game uses 3% for each point and needs an axe; Any weapon lets it work with every weapon.",
        ["Adept"] = "Adept needs at least the attack speed set here. Its chance is attack speed minus that value, plus half of the weapon level plus the bonus. On success the unit strikes more than once. The game needs speed 5, adds 10 before halving and strikes twice.",
        ["Expert"] = "When the game checks whether a unit's level is high enough to use an item safely and get its bonuses, Expert counts the item's rank this much lower, never below 1. The game uses 7.",
        ["Climber"] = "On a cliff a unit's avoid drops to 0, unless it has Climber. Kept lets every unit keep its avoid on cliffs, so Climber no longer matters.",
        ["Lethargy"] = "When a unit with Lethargy begins a move, it has this many hexes less to spend. The Move on its status screen does not change. The game takes off 1.",
        ["Limited"] = "A unit with Limited uses items up to the highest rank normally. Items above it count as needing the level set here, so with the game's values (rank 3, level 50) the unit can never use them safely.",
        [StaffGroup] = "Using a staff gives the staff's level plus the base amount plus a random amount from 0 up to the random bonus, minus a fifth of the unit's level. It never goes below 0, and the experience skill and item bonuses then apply.",
        [LuckGroup] = "Luck protects a unit in two ways. When a hit would defeat it and its Luck is at least the amount needed, the hit is cancelled and the unit loses Luck. After a fight, a unit hit for more damage than it had HP left faces a roll based on the excess; with 1 or more Luck it is spared and loses Luck instead. A level up that raises nothing else adds Luck. The game needs 5, takes 5 and 1, and adds 1. The highest Luck is set by the Luck limit.",
        [DurabilityGroup] = "Items with a durability grade lose durability as they are used. Each use costs the amount for the item's grade; the game uses 1 for S up to 7 for F, and shields cost 4 times that. Before each use the unit has a chance to save it: 20 with Armsthrift (Skills page), plus item bonuses, plus 20 for Reese from a point in the story. Wear never takes an item below 0. After each use the item breaks at the break chance of its durability band; the game uses 0% at 61 or more, 1% at 41 to 60, 2% at 21 to 40, 4% at 1 to 20 and 100% at 0. The bands also set the colour and text of an item's condition, and that text keeps the game's numbers, so keep the lowest values in order from the top band down. Items that count uses instead lose exactly 1 per use, and unbreakable items lose nothing. A weapon is used when it hits, when its strike fails, and when it parries; bows and their arrows are also used on a miss. A shield, or an axe with Axe Guard, is used when its block takes something off a hit and the unit survives.",
        [RankFailureGroup] = "When an item's rank is above the unit's level, each strike with it can fail: the chance is the rank minus the unit's level, times the amount per level, kept between 0 and 100, less item bonuses. The game uses 5% per level. A failed strike misses and still uses the weapon; the battle forecast does not show this chance. Personal weapons count as rank 1, and Expert and Limited (Skills page) change the rank that is compared.",
    };

    public const string FurnitureGroup = "Furniture in Reese's room", DurabilityGroup = "Durability", RankFailureGroup = "Items above the unit's level", LuckGroup = "Luck", GrowthBandGroup = "Growth bands";

    private static int? Negative(uint word, uint upper, int max) =>
        (word & 0xFFFF0000u) == upper && -(short)(word & 0xFFFF) is var n && n >= 0 && n <= max ? n : null;

    // Luck (unit +0x14 bits 5 to 9, read through 0x00177000). A hit that would defeat the unit is cancelled when its Luck is at least 5
    // (slti at 0x00228540); the unit then loses 5 Luck (0x00225F28). In the fight results (0x0022CCC0), a roll from the damage beyond the
    // unit's HP sets state bit 0x80000000 unless Luck is 1 or more, which costs 1 Luck (0x0022CFA4, shown amount 0x0022CFB8).
    // A level up with no other gain adds 1 Luck (0x00223D60).
    // Growth bands (0x002237D0): on a level up, each stat's gains so far are compared with levels gained x growth / 100. Over the band
    // the stat cannot rise; under it the stat is forced to rise. Growth record byte 2 bits 5 to 7: 2 Loose (HP, Strength, Mind 3; Defense,
    // Agility 2, stored at 0x00223870 from registers shared with the band compares), 3 Tight (1 for all, 0x00223880), anything else none.
    // The Loose words are rewritten to load their own values; the Tight words drop a needless reload to hold a second value.
    private const int BandMost = 30;
    private static string? PairCheck(int s) => GameRules.RangeLow(s) > BandMost || GameRules.RangeHigh(s) > BandMost ? $"Each band value can be at most {BandMost}." : null;
    private static readonly uint[] LooseOriginal = [0xAFA300B0u, 0x10000006u, 0xAFA200A0u, 0u], TightOriginal = [0x24020001u, 0xAFA200B0u, 0x8FA200B0u, 0xAFA200A0u];
    private static uint[] LooseWords(int s) => s == GameRules.RangeSetting(3, 2) ? LooseOriginal
        : [0x24030000u | (uint)GameRules.RangeLow(s), 0xAFA300B0u, 0x10000004u, 0x24020000u | (uint)GameRules.RangeHigh(s)];
    private static uint[] TightWords(int s) => s == GameRules.RangeSetting(1, 1) ? TightOriginal
        : [0x24020000u | (uint)GameRules.RangeLow(s), 0xAFA200B0u, 0x24020000u | (uint)GameRules.RangeHigh(s), 0xAFA200A0u];
    private static int? PairDecode(IReadOnlyList<uint> w, uint[] original, int normal, Func<int, uint[]> words, int second)
    {
        if (w.SequenceEqual(original)) return normal;
        int a = (int)(w[0] & 0xFFFF), b = (int)(w[second] & 0xFFFF);
        if (a > BandMost || b > BandMost) return null;
        int s = GameRules.RangeSetting(a, b);
        return words(s).SequenceEqual(w) ? s : null;
    }
    private static IEnumerable<GameRule> GrowthBandRules()
    {
        string[] labels = ["HP, Strength and Mind", "Defense and Agility"];
        yield return new GameRule(236, "growthBandLoose", "Loose growth band", "How far a stat may drift from its expected growth for characters with the Loose band before a level up holds it back or forces it up. The game uses 3 for HP, Strength and Mind and 2 for Defense and Agility.", RuleKind.Pair, 0, GameRules.RangeSetting(BandMost, BandMost), GameRules.RangeSetting(3, 2), null,
            Enumerable.Range(0, 4).Select(i => new RuleSite(0x00223870u + (uint)i * 4, s => LooseWords(s)[i], w => null)).ToList(), confirmed: false,
            decode: w => PairDecode(w, LooseOriginal, GameRules.RangeSetting(3, 2), LooseWords, 3), check: PairCheck)
            { PairLabels = labels };
        yield return new GameRule(237, "growthBandTight", "Tight growth band", "How far a stat may drift from its expected growth for characters with the Tight band before a level up holds it back or forces it up. The game uses 1 for every stat.", RuleKind.Pair, 0, GameRules.RangeSetting(BandMost, BandMost), GameRules.RangeSetting(1, 1), null,
            Enumerable.Range(0, 4).Select(i => new RuleSite(0x00223880u + (uint)i * 4, s => TightWords(s)[i], w => null)).ToList(), confirmed: false,
            decode: w => PairDecode(w, TightOriginal, GameRules.RangeSetting(1, 1), TightWords, 2), check: PairCheck)
            { PairLabels = labels };
    }

    // Furniture shown in Reese's room (event flags 3750 to 3763 clear) clears effect flags 3700 to 3709 (event 1013). Each bonus is
    // checked for unit 83 (Reese) while effect flag 37xx is clear and story flag 99 is set, and set as addiu <reg>, zero, n.
    private static IEnumerable<GameRule> FurnitureRules()
    {
        GameRule Bonus(int id, string key, string piece, string what, int normal, int max, params (uint At, uint Upper)[] sites) =>
            new(id, key, $"{piece}: {what}", "", RuleKind.Limit, 0, max, normal, null,
                sites.Select(x => new RuleSite(x.At, n => x.Upper | (uint)n, w => Imm(w, x.Upper, max))).ToList(), confirmed: false)
            { Group = FurnitureGroup, Short = $"{piece}: {what}" };
        yield return Bonus(243, "furnitureWeaponGrowth", "Sword Mount", "weapon level growth, every weapon type", 1, 100, (0x00171468, 0x24020000u));
        yield return Bonus(244, "furnitureAdept", "Big Rug", "Adept chance", 10, 100, (0x00226C28, 0x24110000u));
        yield return Bonus(245, "furnitureCritAvoid", "Deer Mount", "critical avoid", 3, 100, (0x002273D0, 0x24020000u));
        yield return Bonus(246, "furnitureBlock", "Coat of Arms", "shield block chance", 10, 100, (0x0022A600, 0x24020000u));
        yield return Bonus(247, "furnitureCommandRange", "Tapestry", "Commander and Leader range", 1, 10, (0x001B8EE8, 0x24020000u), (0x001B90C8, 0x24020000u));
        yield return Bonus(248, "furnitureForestHit", "Bird Statue", "hit in forest", 13, 100, (0x00227300, 0x24020000u));
        yield return Bonus(249, "furnitureAvoid", "Art \"Hope\"", "avoid", 3, 100, (0x00227368, 0x24020000u));
        yield return new GameRule(250, "furnitureMiracle", "Maiden Statue: Miracle", "", RuleKind.Choice, 0, 1, 1, ["Off", "On"],
            [new RuleSite(0x00227FC0, s => s == 1 ? 0x24020001u : 0x0000102Du, w => w switch { 0x24020001u => 1, 0x0000102Du => 0, _ => null })], confirmed: false)
            { Group = FurnitureGroup, Short = "Maiden Statue: Reese has Miracle" };
    }

    private static IEnumerable<GameRule> LuckRules()
    {
        yield return new GameRule(232, "luckLethalNeed", "Luck needed to cancel a defeating hit", "", RuleKind.Limit, 1, 32, 5, null,
            [new RuleSite(0x00228540, n => 0x28420000u | (uint)n, w => (w & 0xFFFF0000u) == 0x28420000u && (w & 0xFFFF) is >= 1 and <= 32 ? (int)(w & 0xFFFF) : null)],
            confirmed: false) { Group = LuckGroup, Short = "Luck needed to cancel a defeating hit (32 never)" };
        yield return new GameRule(233, "luckLethalCost", "Luck spent to cancel a defeating hit", "", RuleKind.Limit, 0, 31, 5, null,
            [new RuleSite(0x00225F28, n => 0x24060000u | (ushort)(-n), w => Negative(w, 0x24060000u, 31))], confirmed: false)
            { Group = LuckGroup, Short = "Luck spent to cancel a defeating hit" };
        yield return new GameRule(234, "luckOverkillCost", "Luck spent on the overkill save", "", RuleKind.Limit, 0, 31, 1, null,
            [new RuleSite(0x0022CFA4, n => 0x24060000u | (ushort)(-n), w => Negative(w, 0x24060000u, 31)),
             new RuleSite(0x0022CFB8, n => 0x24070000u | (ushort)(-n), w => Negative(w, 0x24070000u, 31))], confirmed: false)
            { Group = LuckGroup, Short = "Luck spent on the overkill save" };
        yield return new GameRule(235, "luckLevelUp", "Luck from an empty level up", "", RuleKind.Limit, 0, 31, 1, null,
            [new RuleSite(0x00223D60, n => 0x24060000u | (uint)n, w => Imm(w, 0x24060000u, 31))], confirmed: false)
            { Group = LuckGroup, Short = "Luck gained when a level up raises nothing else" };
    }
    // Checked live: grade D wear 20, band edge 90 with band 1 breaking at 100 (menu text and a break), failure 16 per level.
    // Durability use 0x0016CFE0: wear per use by grade is data (7 words at 0x00429ED0, S to F); shields multiply it (sll s6,s6,2 at
    // 0x0016D2AC); Reese (unit 83) gets 20 more chance to save a use while story flag 99 is set and 3709 is not (0x0016D308).
    // Break chance 0x0016E8D0 returns a percent for the band from 0x0016EB00, which compares the stored durability (shown + 1).
    private const uint WearTable = 0x00429ED0;
    private static readonly string[] Grades = ["S", "A", "B", "C", "D", "E", "F"];
    private static readonly uint[] BandSites = [0x0016EB48u, 0x0016EB60u, 0x0016EB78u, 0x0016EB90u];
    private static readonly int[] BandLowest = [61, 41, 21, 1];
    private static readonly string[] BandNames = ["61 or more", "41 to 60", "21 to 40", "1 to 20", "0"];
    // The condition mark colours in the item lists (sprite sheet 8, read from the game screen).
    public static readonly string[] BandColors = ["#2F4F8F", "#3E9E4E", "#D8C23A", "#E0902E", "#9C3A2C"];
    public static readonly string[] BandColorNames = ["blue", "green", "yellow", "orange", "red"];
    // Each band's percent is set in a branch delay slot; a changed setting becomes addiu v0, zero, n.
    private static readonly uint[] BreakSites = [0x0016E984u, 0x0016E98Cu, 0x0016E994u, 0x0016E99Cu, 0x0016E9A4u];
    private static readonly uint[] BreakOriginal = [0x0000102Du, 0x0060102Du, 0x0060102Du, 0x0080102Du, 0x24020064u];
    private static readonly int[] BreakDefaults = [0, 1, 2, 4, 100];
    // Failure chance 0x0017C880: (item rank - unit level) x 5 as sll v0,v1,2 (0x0017C8D8) and addu v0,v0,v1 in a delay slot (0x0017C8E0).
    private static readonly string[] FailureChoices = ["Never", "1%", "2%", "3%", "4%", "5%", "7%", "8%", "9%", "15%", "16%"];
    private static readonly uint[][] FailureWords =
    [
        [0x0000102Du, 0u], [0x00031000u, 0u], [0x00031040u, 0u], [0x00031040u, 0x00431021u], [0x00031080u, 0u], [0x00031080u, 0x00431021u],
        [0x000310C0u, 0x00431023u], [0x000310C0u, 0u], [0x000310C0u, 0x00431021u], [0x00031100u, 0x00431023u], [0x00031100u, 0u],
    ];

    private static IEnumerable<GameRule> DurabilityRules()
    {
        for (int g = 0; g < Grades.Length; g++)
            yield return new GameRule(213 + g, $"wearGrade{Grades[g]}", $"Durability used per use, grade {Grades[g]}", "", RuleKind.Limit, 0, 127, g + 1, null,
                [new RuleSite(WearTable + (uint)g * 4, s => (uint)s, w => null)], confirmed: true,
                decode: words => words[0] <= 127 ? (int)words[0] : null) { Group = DurabilityGroup, Short = $"Used per use, grade {Grades[g]}" };
        yield return new GameRule(220, "shieldWear", "Shield durability multiplier", "", RuleKind.Choice, 0, 3, 2, ["Times 1", "Times 2", "Times 4", "Times 8"],
            [new RuleSite(0x0016D2AC, s => 0x0016B000u | (uint)s << 6, w => w switch { 0x0016B000u => 0, 0x0016B040u => 1, 0x0016B080u => 2, 0x0016B0C0u => 3, _ => null })], confirmed: false)
            { Group = DurabilityGroup, Short = "Shields use" };
        for (int b = 0; b < BandSites.Length; b++)
            yield return new GameRule(221 + b, $"durabilityBand{b + 1}", $"Lowest durability of the {BandNames[b]} band", "", RuleKind.Limit, 0, 126, BandLowest[b], null,
                [new RuleSite(BandSites[b], s => 0x28410000u | (uint)(s + 1), w => (w & 0xFFFF0000u) == 0x28410000u && (w & 0xFFFF) is >= 1 and <= 127 ? (int)(w & 0xFFFF) - 1 : null)], confirmed: true)
                { Group = DurabilityGroup, Short = $"Lowest durability, {BandColorNames[b]} band ({BandNames[b]})", Swatch = BandColors[b] };
        for (int b = 0; b < BreakSites.Length; b++)
        {
            int band = b;
            yield return new GameRule(225 + b, $"breakChance{b + 1}", $"Break chance, band {BandNames[b]}", "", RuleKind.Limit, 0, 100, BreakDefaults[b], null,
                [new RuleSite(BreakSites[b], s => s == BreakDefaults[band] ? BreakOriginal[band] : 0x24020000u | (uint)s,
                    w => w == BreakOriginal[band] ? BreakDefaults[band] : Imm(w, 0x24020000u, 100))], confirmed: true)
                { Group = DurabilityGroup, Short = $"Break chance (%), {BandColorNames[b]} band ({BandNames[b]})", Swatch = BandColors[b] };
        }
        yield return new GameRule(230, "reeseThrift", "Reese's chance to save a use", "", RuleKind.Limit, 0, 100, 20, null,
            [new RuleSite(0x0016D308, s => 0x26100000u | (uint)s, w => Imm(w, 0x26100000u, 100))], confirmed: false)
            { Group = FurnitureGroup, Short = "Art \"Revival\": chance to save a weapon use (%)" };
        yield return new GameRule(231, "rankFailure", "Failure chance per level", "", RuleKind.Choice, 0, FailureChoices.Length - 1, 5, FailureChoices,
            [new RuleSite(0x0017C8D8, s => FailureWords[s][0], w => null), new RuleSite(0x0017C8E0, s => FailureWords[s][1], w => null)], confirmed: true,
            decode: found => Array.FindIndex(FailureWords, set => set.SequenceEqual(found)) is var k and >= 0 ? k : null)
            { Group = RankFailureGroup, Short = "Failure chance per level of rank above" };
    }

    private static string BaneText(string weapon) =>
        $"When an enemy attacks the unit with {weapon}, the skill activates at a chance equal to the attacker's hit rate, but never above the highest chance. The game's highest chance is 33.";

    public const string SkillsSection = "Skills";

    /// <summary>An addiu v0,v1,N site: the equipment window's own copy of a skill bonus. Discs exported before this copy was known
    /// keep the game's word here, so that word fits any setting.</summary>
    private static RuleSite WindowCopy(uint address, int game, int min, int max) =>
        new(address, s => 0x24620000u | (uint)(s & 0xFFFF),
            w => (w & 0xFFFF0000u) != 0x24620000u ? null : (short)(w & 0xFFFF) is var v && v == game ? AnySetting : v >= min && v <= max ? v : null);

    private static readonly string[] MoveChoices = ["Allowed", "Not allowed"];
    private static readonly string[] WeaponMoveChoices = ["Allowed", "Not allowed", "Weapon decides"];

    /// <summary>A rule whose settings are whole instruction words: words[setting][site].</summary>
    // An immediate of 0 to max in an instruction with the given upper half, or null.
    private static int? Imm(uint word, uint upper, int max = 10) => (word & 0xFFFF0000u) == upper && (word & 0xFFFF) <= max ? (int)(word & 0xFFFF) : null;

    public static readonly string[] WeaponTypeChoices = ["Knife", "Sword", "Blade", "Mace", "Spear", "Lance", "Fork", "Axe", "Bow", "Crossbow", "Ballista", "Fire", "Thunder", "Wind", "Holy", "Dark"];

    private static uint Branch(uint op, uint at, uint target) => op | ((uint)(((int)target - (int)(at + 4)) >> 2) & 0xFFFFu);

    // A command's weapon check in the weapon filter 0x001BE800: the block at start passes to ok or fails to the shared "cannot use" exit.
    // A weapon type rewrites the block as: item type (0x0016E820) equal to the type passes; Any weapon always passes.
    private static GameRule WeaponNeed(int id, string key, string name, string group, string game, uint start, uint ok, uint[] original)
    {
        int n = original.Length;
        var sets = new List<uint[]> { original };
        for (uint type = 0; type < WeaponTypeChoices.Length; type++)
        {
            var words = new uint[n];
            words[0] = 0x0C05BA08u;                                  // jal item type
            words[1] = 0x0280202Du;                                  // daddu a0, s4, zero
            words[2] = 0x24030000u | type;                           // addiu v1, zero, type
            words[3] = Branch(0x10430000u, start + 12, ok);          // beq v0, v1, ok
            if (n >= 7) { words[5] = Branch(0x10000000u, start + 20, 0x001BEE58u); words[6] = 0x0000102Du; }
            else words[5] = Branch(0x10000000u, start + 20, 0x001BE944u);   // to an exit that clears v0
            sets.Add(words);
        }
        var any = new uint[n];
        any[0] = Branch(0x10000000u, start, ok);
        sets.Add(any);
        return WordSets(id, key, name, group, "Weapon", 0, [game, .. WeaponTypeChoices, "Any weapon"],
            Enumerable.Range(0, n).Select(i => start + (uint)(4 * i)).ToArray(), sets.ToArray());
    }

    // A skill's own weapon check: one or more blocks, each rewritten for a weapon type (built by typed) or made to always pass (any).
    private static GameRule SkillWeapon(int id, string key, string name, string group, string game, (uint Start, uint[] Original, Func<uint, uint[]> Typed, uint[] Any)[] blocks, bool confirmed = false)
    {
        var addresses = blocks.SelectMany(b => b.Original.Select((_, i) => b.Start + (uint)(4 * i))).ToArray();
        var sets = new List<uint[]> { blocks.SelectMany(b => b.Original).ToArray() };
        for (uint type = 0; type < WeaponTypeChoices.Length; type++) sets.Add(blocks.SelectMany(b => b.Typed(type)).ToArray());
        sets.Add(blocks.SelectMany(b => b.Any).ToArray());
        return WordSets(id, key, name, group, "Weapon", 0, [game, .. WeaponTypeChoices, "Any weapon"], addresses, sets.ToArray(), confirmed);
    }

    private static GameRule AuraCount(int id, string key, string skill, uint selfSkip, uint selfWord, uint add) =>
        WordSets(id, key, $"{skill} holders", skill, "Who counts", 0,
            ["Others, each adds", "Others, counted once", "Itself too, each adds", "Itself too, counted once"],
            [selfSkip, add], [[selfWord, 0x26100001u], [selfWord, 0x24100001u], [0u, 0x26100001u], [0u, 0x24100001u]]);

    // Lethargy: after the skill check, "addiu rN, zero, 10" is the move taken off at the start of a move, in tenths of a hex,
    // in the player's moves and the enemy's move planning (26 places, each with its own register).
    private static readonly (uint Address, uint Upper)[] SlowStartSites =
    [
        (0x002F37F0, 0x24100000u), (0x002F4A04, 0x24100000u), (0x002F5A78, 0x24120000u), (0x002F5AEC, 0x24120000u), (0x002FEC0C, 0x24140000u),
        (0x002FF3D0, 0x24150000u), (0x002FF84C, 0x24120000u), (0x002FFE80, 0x24130000u), (0x00300DD4, 0x24110000u), (0x00300E2C, 0x24110000u),
        (0x00300E6C, 0x24100000u), (0x00301220, 0x24110000u), (0x003013B4, 0x24120000u), (0x00301A50, 0x24160000u), (0x00302218, 0x24130000u),
        (0x00303294, 0x24110000u), (0x003032D4, 0x24100000u), (0x003036FC, 0x24110000u), (0x0030373C, 0x24100000u), (0x00303FC8, 0x24110000u),
        (0x003043D8, 0x24120000u), (0x00304788, 0x24120000u), (0x003064FC, 0x24150000u), (0x00307F8C, 0x24130000u), (0x00308FC0, 0x24110000u),
        (0x00309008, 0x24110000u),
    ];

    private static GameRule SlowStartPenalty() => new(198, "slowStartPenalty", "Lethargy move penalty", "", RuleKind.Limit, 0, 5, 1, null,
        SlowStartSites.Select(s => new RuleSite(s.Address, n => s.Upper | (uint)(n * 10),
            w => (w & 0xFFFF0000u) == s.Upper && (w & 0xFFFF) % 10 == 0 && (w & 0xFFFF) <= 50 ? (int)(w & 0xFFFF) / 10 : null)).ToArray(), confirmed: false)
        { Group = "Lethargy", Short = "Hexes taken off", Section = SkillsSection };

    private const uint JalItemType = 0x0C05BA08u;          // jal 0x0016E820
    private static uint XoriV0(uint type) => 0x38420000u | type;
    private static uint BneV0(uint at, uint target) => Branch(0x14400000u, at, target);

    // "jal check; lw a0, 8(s2); beq v0, zero, fail; nop" becomes "jal item type; lw a0, 8(s2); xori v0, v0, type; bne v0, zero, fail".
    private static (uint, uint[], Func<uint, uint[]>, uint[]) FightCheck(uint start, uint check, uint failBranch, uint fail) =>
        (start, [check, 0x8E440008u, failBranch, 0u], t => [JalItemType, 0x8E440008u, XoriV0(t), BneV0(start + 12, fail)], [check, 0x8E440008u, 0u, 0u]);

    private static GameRule IaidoWeapon() => SkillWeapon(186, "iaidoWeapon", "Iaido weapon", "Iaido", "Sword or blade",
        [FightCheck(0x00229E50, 0x0C05B89Cu, 0x10400019u, 0x00229EC0)], confirmed: true);

    private static GameRule HunterWeapon() => SkillWeapon(187, "hunterWeapon", "Huntsman weapon", "Huntsman", "Bow, crossbow or ballista",
        [FightCheck(0x0022BFA0, 0x0C05B928u, 0x1040000Du, 0x0022BFE0)], confirmed: true);

    // Axe Guard checks for an axe in the fight (0x0022A3F8), the command menu (0x0038B94C) and enemy decisions (0x003077D4).
    private static GameRule AxeGuardWeapon() => SkillWeapon(188, "axeGuardWeapon", "Axe Guard weapon", "Axe Guard", "Axe",
    [
        (0x0022A3F4, [0u, 0x0C05B85Cu, 0x8E440008u, 0x10400025u, 0u, 0x0C05B38Cu, 0x8E440008u, 0x14400021u, 0u],
            t => [0x8E440008u, JalItemType, 0u, XoriV0(t), BneV0(0x0022A404, 0x0022A498), 0u, 0x0C05B38Cu, 0x8E440008u, BneV0(0x0022A414, 0x0022A498)],
            [0u, 0x0C05B85Cu, 0x8E440008u, 0u, 0u, 0x0C05B38Cu, 0x8E440008u, 0x14400021u, 0u]),
        (0x0038B94C, [0x0C05B85Cu, 0x0040202Du, 0x10400006u, 0x3C060039u, 0x0280202Du, 0x24050275u, 0x0C08C6F0u, 0x24C6EBC0u, 0u],
            t => [JalItemType, 0x0040202Du, XoriV0(t), BneV0(0x0038B958, 0x0038B970), 0x3C060039u, 0x0280202Du, 0x24050275u, 0x0C08C6F0u, 0x24C6EBC0u],
            [0x0C05B85Cu, 0x0040202Du, 0u, 0x3C060039u, 0x0280202Du, 0x24050275u, 0x0C08C6F0u, 0x24C6EBC0u, 0u]),
        (0x003077C8, [0x0200202Du, 0x0C05E14Cu, 0u, 0x0C05B85Cu, 0x0040202Du, 0x10400008u, 0x0220202Du],
            t => [0x0C05E14Cu, 0x0200202Du, JalItemType, 0x0040202Du, XoriV0(t), BneV0(0x003077DC, 0x00307800), 0x0220202Du],
            [0x0200202Du, 0x0C05E14Cu, 0u, 0x0C05B85Cu, 0x0040202Du, 0u, 0x0220202Du]),
    ]);

    // A negated immediate of 0 to max (an addiu that subtracts), or null.
    private static int? Minus(uint word, uint upper, int max) => (word & 0xFFFF0000u) == upper && -(short)(word & 0xFFFF) is var n && n >= 0 && n <= max ? n : null;

    // An aura's range checked as "slti at, s3, range + 1".
    private static RuleSite Below(uint address) =>
        new(address, n => 0x2A610000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x2A610000u && (w & 0xFFFF) is >= 1 and <= 11 ? (int)(w & 0xFFFF) - 1 : null);

    // An aura's range passed to the map drawing as "addiu t0, zero, range".
    private static RuleSite Drawn(uint address) => new(address, n => 0x24080000u | (uint)n, w => Imm(w, 0x24080000u));

    // Steals per map, stored as "daddiu v1, zero, count << 2".
    private static GameRule StealCount(int id, string key, string skill, int count, uint address) =>
        new(id, key, $"{skill} steals per map", "", RuleKind.Limit, 0, 3, count, null,
            [new RuleSite(address, n => 0x64030000u | (uint)(n << 2), w => (w & 0xFFFF0000u) == 0x64030000u && (w & 0xFFF3u) == 0 ? (int)((w & 0xC) >> 2) : null)], confirmed: false)
        { Group = skill, Short = "Steals per map", Section = SkillsSection };

    private static GameRule AuraRange(int id, string key, string skill, int range, params RuleSite[] sites) => AuraRange(id, key, skill, range, true, sites);

    private static GameRule AuraRange(int id, string key, string skill, int range, bool confirmed, params RuleSite[] sites) =>
        new(id, key, $"{skill} range", "", RuleKind.Limit, 0, 10, range, null, sites, confirmed: confirmed) { Group = skill, Short = "Range (hexes, 0 turns it off)", Section = SkillsSection };

    private static GameRule Strikes(int id, string key, string skill, int count, uint address, bool confirmed = false) =>
        new(id, key, $"{skill} strikes", "", RuleKind.Limit, 1, 7, count, null,
            [new RuleSite(address, n => 0x24030000u | (uint)n, w => Imm(w, 0x24030000u, 7))], confirmed: confirmed)
        { Group = skill, Short = "Strikes", Section = SkillsSection };

    // "ori v0, v0, bit" in the class set and "ori s0, s0, bit" in the unit set, one bit per weapon type.
    private static GameRule GrantedWeapon(int id, string key, string skill, int type, uint classSite, uint unitSite) =>
        WordSets(id, key, $"{skill} weapon", skill, "Weapon type allowed", type, WeaponTypeChoices.Take(16).ToArray(),
            [classSite, unitSite], Enumerable.Range(0, 16).Select(t => new[] { 0x34420000u | (1u << t), 0x36100000u | (1u << t) }).ToArray());

    private static GameRule WordSets(int id, string key, string name, string group, string shortName, int normal, string[] choices, uint[] addresses, uint[][] words, bool confirmed = true) =>
        new(id, key, name, "", RuleKind.Choice, 0, choices.Length - 1, normal, choices,
            addresses.Select((a, i) => new RuleSite(a, s => words[s][i], w => null)).ToArray(), confirmed: confirmed,
            decode: found => Array.FindIndex(words, set => set.SequenceEqual(found)) is var k and >= 0 ? k : null)
        { Group = group, Short = shortName, Section = SkillsSection };

    public const string BattleStatsGroup = "Battle stats";
    public static readonly string[] StatNames = ["Strength", "Agility", "Defense", "Mind", "Max HP"];
    // The game's stat functions that add equipment and skill bonuses; all take the unit (and a flag the call already sets).
    private static readonly uint[] StatFunctions = [0x001800B0u, 0x0017E8B0u, 0x0017F0C0u, 0x0017F8B0u, 0x001808B0u];
    private static uint Jal(uint target) => 0x0C000000u | (target >> 2);

    // A formula reads its stat by calling one of these functions; the choice is which one it calls.
    private static GameRule StatChoice(int id, string key, string part, int normal, params uint[] sites) => StatChoice(id, key, part, normal, true, sites);
    private static GameRule StatChoice(int id, string key, string part, int normal, bool confirmed, params uint[] sites) =>
        new(id, key, $"{part} stat", "", RuleKind.Choice, 0, StatNames.Length - 1, normal, StatNames,
            sites.Select(a => new RuleSite(a, s => Jal(StatFunctions[s]), w => Array.FindIndex(StatFunctions, f => Jal(f) == w) is var k and >= 0 ? k : null)).ToArray(), confirmed,
            decode: found => found.Distinct().Count() == 1 && Array.FindIndex(StatFunctions, f => Jal(f) == found[0]) is var k and >= 0 ? k : null)
        { Group = BattleStatsGroup, Short = part };

    public const string HitCriticalGroup = "Hit and critical", DamageGroup = "Damage", InjuryGroup = "Injury";
    // The same stats without the flag argument, as the knife code calls them.
    private static readonly uint[] PlainStatFunctions = [0x001808A0u, 0x0017F0A0u, 0x0017F0B0u, 0x001800A0u, 0x001808B0u];
    private const uint WeaponLevel = 0x0017A230u, WeaponLevelAbove = 0x0017A1F0u;

    // A call that adds a value to hit or critical: the game's own helpers, any stat function, or a zero (daddu v0,zero,zero).
    private static GameRule AddedValue(int id, string key, string part, uint site, (string Name, uint Word)[] own, int normal, bool confirmed)
    {
        var options = own.Concat(StatNames.Select((n, i) => (n, Jal(StatFunctions[i])))).Append(("Nothing", 0x0000102Du)).ToArray();
        return new GameRule(id, key, part, "", RuleKind.Choice, 0, options.Length - 1, normal, options.Select(o => o.Item1).ToArray(),
            [new RuleSite(site, s => options[s].Item2, w => Array.FindIndex(options, o => o.Item2 == w) is var k and >= 0 ? k : null)], confirmed)
        { Group = HitCriticalGroup, Short = part };
    }

    // A multiplication by a constant: the game's own instructions for its value, or "addiu temp,zero,N" then a mult into the result,
    // with any remaining sites emptied.
    private static GameRule Multiplier(int id, string key, string skill, string part, int normal, uint[] sites, uint[] original, int temp, uint mult, bool confirmed = false)
    {
        uint[] Words(int n) => n == normal ? original : [0x24000000u | (uint)temp << 16 | (uint)n, mult, .. Enumerable.Repeat(0u, sites.Length - 2)];
        return new GameRule(id, key, $"{skill} {part.ToLowerInvariant()}", "", RuleKind.Limit, 0, 200, normal, null,
            sites.Select((a, i) => new RuleSite(a, n => Words(n)[i], w => null)).ToArray(), confirmed,
            decode: found => found.SequenceEqual(original) ? normal
                : found[1] == mult && (found[0] & 0xFFFF0000u) == (0x24000000u | (uint)temp << 16) && (found[0] & 0xFFFF) <= 200 && found.Skip(2).All(w => w == 0) ? (int)(found[0] & 0xFFFF) : null)
        { Group = skill, Short = part, Section = SkillsSection };
    }

    private static GameRule MoveRule(int id, string key, string skill, int normal, string[] choices, uint[] addresses, uint[][] words) =>
        WordSets(id, key, $"{skill} after moving", skill, "After moving", normal, choices, addresses, words);

    // After a command is used, the game stores a wait in the unit's state (addiu a0,zero,N) and counts it down once per turn.
    // A command's wait is stored when it is chosen (address) and, for commands used in a fight, stored again one higher by the
    // battle code afterwards (afterFight, "addiu a2, zero, wait + 1"), which is the one that counts.
    private static GameRule Wait(int id, string key, string skill, uint address, int normal, bool confirmed = true, uint afterFight = 0) =>
        new(id, key, $"{skill} wait", "", RuleKind.Limit, 1, 30, normal, null,
            afterFight == 0
                ? [new RuleSite(address, s => 0x24040000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24040000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null)]
                : [new RuleSite(address, s => 0x24040000u | (uint)s, w => (w & 0xFFFF0000u) == 0x24040000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null),
                   new RuleSite(afterFight, s => 0x24060000u | (uint)(s + 1), w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 2 and <= 31 ? (int)(w & 0xFFFF) - 1 : null)],
            confirmed: confirmed)
        { Group = skill, Short = "Wait (turns)", Section = SkillsSection };

    // Swimmer in the movement cost builder: the game copies plain-ground costs onto water for any unit with the skill.
    // The on-foot form merges two stores that write the same byte and uses the room to skip mounted units (mount state from 0x0017DFB0).
    private static readonly uint[] SwimmerGame = [0x10400019u, 0x0000802Du, 0x0C0603D4u, 0x0200202Du, 0x8C430014u, 0x30630100u, 0x10600002u, 0x0200182Du, 0x0000182Du, 0x02232021u, 0x90840004u, 0x240300FFu, 0x14830005u, 0x00000000u, 0x2404FFFFu, 0x02501821u, 0x10000003u, 0xA0640000u, 0x02501821u, 0xA0640000u, 0x26100001u, 0x2A030040u, 0x1460FFEBu, 0x00000000u, 0x10000011u, 0x00000000u];
    private static readonly uint[] SwimmerOnFoot = [0x10400019u, 0x00000000u, 0x0C05F7ECu, 0x0200202Du, 0x24030001u, 0x10430014u, 0x0000802Du, 0x0C0603D4u, 0x0200202Du, 0x8C430014u, 0x30630100u, 0x10600002u, 0x0200182Du, 0x0000182Du, 0x02232021u, 0x90840004u, 0x02501821u, 0xA0640000u, 0x26100001u, 0x2A030040u, 0x1460FFF2u, 0x00000000u, 0x10000013u, 0x00000000u, 0x00000000u, 0x00000000u];

    // The command menu tests action state bit 1 (the unit has moved) with an andi. Clearing the bit in the mask allows the command
    // after a move; setting it blocks it.
    private static GameRule MoveBlocked(int id, string key, string skill, uint address, uint word) =>
        MoveRule(id, key, skill, 1, MoveChoices, [address], [[word & ~1u], [word]]);

    private static GameRule MoveOpen(int id, string key, string skill, uint address, uint word) =>
        MoveRule(id, key, skill, 0, MoveChoices, [address], [[word], [word | 1u]]);

    // These commands check the action state mask first, then after a move ask whether the weapon allows it.
    private static GameRule MoveWeapon(int id, string key, string skill, uint mask, params uint[] moved)
    {
        uint[] addresses = [mask, .. moved];
        uint[] allowed = [0x30424282u, .. moved.Select(_ => 0x30420000u)];
        uint[] blocked = [0x30424283u, .. moved.Select(_ => 0x30420001u)];
        uint[] weapon = [0x30424282u, .. moved.Select(_ => 0x30420001u)];
        return MoveRule(id, key, skill, 2, WeaponMoveChoices, addresses, [allowed, blocked, weapon]);
    }

    /// <summary>An addiu v0,v0,N site holding a bonus.</summary>
    private static RuleSite AddV0(uint address, int min, int max) =>
        new(address, s => 0x24420000u | (uint)(s & 0xFFFF), w => (w & 0xFFFF0000u) == 0x24420000u && (short)(w & 0xFFFF) is var v && v >= min && v <= max ? v : null);

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
            "Part of the shield's Might always counts as Defense against physical attacks, rounded down, and a successful block takes off the rest, so a block still takes off the full Might in total. The at least 1 choices never give less than 1, so a small shield still helps. The battle forecast shows the guard in Def and the rest of the Might as the shield bonus: with a quarter of Might 5, Def 7 +5 becomes Def 8 +4. Guard's halving and Desperation's drop to 0 do not reduce this bonus, because it is added after them. A sleeping unit has no shield bonus. Attacks that ignore Defense, including knives, also ignore this guard. With Full Might a block adds nothing more. Off is the game's own behaviour. The Live page applies the battle part; the forecast, status screen and equipment windows show the guard on an exported disc.",
            RuleKind.Choice, 0, 5, 0, ["Off", "Quarter of Might", "Half of Might", "Full Might", "Quarter of Might, at least 1", "Half of Might, at least 1"],
            GuardSites(), confirmed: true, decode: GuardDecode),
        Step(11, "shieldTraining", "Shield training speed",
            "How much a shield level rises on each successful training roll, in tenths of a level (the game's 4 is 0.4 of a level). Shield training happens when a unit's block roll succeeds, or fails on a hit while the level is below 20. 0 stops shield levels from rising.",
            4, 0x001716A4, confirmed: true),
        Step(12, "weaponTraining", "Weapon training speed",
            "How much a weapon level rises on each successful training roll after an attack, in tenths of a level (the game's 1 is 0.1 of a level). 0 stops weapon levels from rising in battle.",
            1, 0x001716C4, confirmed: true),
        new GameRule(52, "avoidSpeed", "Attack speed in avoid",
            "Avoid is the unit's attack speed times this amount, plus item and terrain bonuses. The game uses times 2. Times 1 makes every unit easier to hit; times 4 makes fast units very hard to hit.",
            RuleKind.Choice, 0, 2, 1, ["Times 1", "Times 2", "Times 4"],
            [new RuleSite(0x00179D68, s => 0x0002B000u | (uint)s << 6, w => w switch { 0x0002B000u => 0, 0x0002B040u => 1, 0x0002B080u => 2, _ => null })], confirmed: true),
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
        ..DurabilityRules(),
        ..LuckRules(),
        ..GrowthBandRules(),
        ..FurnitureRules(),
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
        new GameRule(53, "sunderCritical", "Sunder critical bonus", "", RuleKind.Limit, 0, 100, 25, null,
            [AddV0(0x0022C0F4, 0, 100), AddV0(0x0022C100, 0, 100), WindowCopy(0x00389CB0, 25, 0, 100)], confirmed: true) { Group = "Sunder", Short = "Critical bonus", Section = SkillsSection },
        new GameRule(54, "desperationHit", "Desperation hit bonus", "", RuleKind.Limit, 0, 100, 22, null,
            [AddV0(0x0022C1A4, 0, 100), WindowCopy(0x00389E70, 22, 0, 100)], confirmed: true) { Group = "Desperation", Short = "Hit bonus", Section = SkillsSection },
        new GameRule(55, "aimHit", "Aim hit bonus", "", RuleKind.Limit, 0, 100, 33, null,
            [AddV0(0x0022C0AC, 0, 100), AddV0(0x0022C0B8, 0, 100), WindowCopy(0x00389C88, 33, 0, 100)], confirmed: true) { Group = "Aim", Short = "Hit bonus", Section = SkillsSection },
        new GameRule(56, "deadeyeHit", "Deadeye hit change", "", RuleKind.Limit, -100, 100, -15, null,
            [AddV0(0x0022C0D4, -100, 100), WindowCopy(0x00389CD8, -15, -100, 100)], confirmed: true,
            // -1 and -2 are real settings here, so the words are read directly.
            decode: w => (w[0] & 0xFFFF0000u) == 0x24420000u && (w[1] & 0xFFFF0000u) == 0x24620000u && (short)(w[0] & 0xFFFF) is var v && v >= -100 && v <= 100
                && ((short)(w[1] & 0xFFFF) == v || (short)(w[1] & 0xFFFF) == -15) ? v : null) { Group = "Deadeye", Short = "Hit change", Section = SkillsSection },
        MoveBlocked(60, "aimAfterMoving", "Aim", 0x0038B740, 0x30420001u),
        MoveBlocked(61, "deadeyeAfterMoving", "Deadeye", 0x0038BA18, 0x30420001u),
        MoveBlocked(62, "sunderAfterMoving", "Sunder", 0x0038B9B4, 0x30420001u),
        MoveWeapon(63, "desperationAfterMoving", "Desperation", 0x0038BDD0, 0x0038BDFC),
        MoveBlocked(64, "pulverizeAfterMoving", "Pulverize", 0x0038B7F4, 0x30420001u),
        MoveBlocked(65, "overwatchAfterMoving", "Overwatch", 0x0038B8C4, 0x30420001u),
        MoveBlocked(66, "axeGuardAfterMoving", "Axe Guard", 0x0038B938, 0x30420001u),
        MoveBlocked(67, "windsweepAfterMoving", "Windsweep", 0x0038BD44, 0x30420001u),
        MoveBlocked(68, "battleCryAfterMoving", "Battle Cry", 0x0038C400, 0x30620001u),
        MoveBlocked(69, "stealAfterMoving", "Steal", 0x0038C65C, 0x30420001u),
        MoveBlocked(70, "provokeAfterMoving", "Provoke", 0x0038C1A8, 0x30624283u),
        MoveBlocked(71, "evasionAfterMoving", "Evasion", 0x0038C280, 0x30624283u),
        MoveBlocked(72, "silenceAfterMoving", "Silence", 0x0038C2E0, 0x30624283u),
        MoveBlocked(73, "imbueAfterMoving", "Imbue", 0x0038C340, 0x30624283u),
        MoveOpen(74, "doubleShotAfterMoving", "Double-Shot", 0x0038BB98, 0x30424282u),
        MoveOpen(75, "tripleShotAfterMoving", "Triple-Shot", 0x0038BC58, 0x30424282u),
        MoveOpen(76, "astraAfterMoving", "Astra", 0x0038BEB8, 0x30424282u),
        MoveOpen(77, "focusChantAfterMoving", "Focus Chant", 0x0038BF78, 0x30424282u),
        MoveOpen(78, "guardAfterMoving", "Guard", 0x0038C0C8, 0x30420002u),
        MoveOpen(79, "searchAfterMoving", "Search", 0x0038C5B8, 0x30420282u),
        MoveOpen(80, "returnAfterMoving", "Return", 0x0038C6A0, 0x30420282u),
        MoveOpen(81, "convertAfterMoving", "Convert", 0x0038C6E0, 0x30420282u),
        MoveWeapon(82, "throwAfterMoving", "Throw", 0x0038B630, 0x0038B660),
        MoveWeapon(83, "mercyAfterMoving", "Mercy", 0x0038BAB0, 0x0038BADC),
        MoveWeapon(84, "flourishAfterMoving", "Flourish", 0x0038C82C, 0x0038C870),
        MoveWeapon(85, "deathmatchAfterMoving", "Deathmatch", 0x0038C97C, 0x0038C9C0, 0x0038CA00),
        Wait(86, "astraWait", "Astra", 0x001C355C, 6, true, 0x0022D4B8),
        Wait(87, "focusChantWait", "Focus Chant", 0x001C357C, 6, true, 0x0022D4DC),
        Wait(88, "tripleShotWait", "Triple-Shot", 0x001C359C, 4, true, 0x0022D500),
        Wait(89, "doubleShotWait", "Double-Shot", 0x001C35BC, 3, true, 0x0022D494),
        Wait(90, "evasionWait", "Evasion", 0x001C35FC, 4),
        Wait(91, "imbueWait", "Imbue", 0x001C361C, 3),
        Wait(92, "flourishWait", "Flourish", 0x001C363C, 3, true, 0x0022D544),
        WordSets(93, "swimmerMounted", "Swimmer while mounted", "Swimmer", "Works when", 0, ["Mounted or on foot", "On foot only"],
            Enumerable.Range(0, SwimmerGame.Length).Select(i => 0x001BA950u + (uint)i * 4).ToArray(), [SwimmerGame, SwimmerOnFoot]),
        // Each steal lowers the shared count with addiu v1,v1,-1; adding 0 keeps it.
        WordSets(94, "stealLimit", "Steal limit", "Steal", "Steals per map", 0, ["Limited", "No limit"], [0x001DF1BCu], [[0x246300FFu], [0x24630000u]]),
        StatChoice(95, "physicalAttackStat", "Physical attack", 0, 0x0017C00C),
        StatChoice(96, "magicAttackStat", "Magic attack", 3, 0x0017BFA4),
        StatChoice(97, "physicalDefenseStat", "Physical defense", 2, 0x0017B9F8, 0x0017BB48),
        StatChoice(98, "magicDefenseStat", "Magic defense", 3, 0x0017B6C0),
        StatChoice(99, "attackSpeedStat", "Attack speed", 1, 0x0017C514),
        StatChoice(100, "weightStat", "Carries weight", 0, 0x0017C4E0),
        // Attack speed loses weapon and shield weight minus a share of the carrying stat: sra v1,v0,N (both rounding paths).
        new GameRule(101, "weightShare", "Share of the carrying stat that offsets weight", "", RuleKind.Choice, 0, 3, 1, ["All of it", "Half", "A quarter", "An eighth"],
            new[] { 0x0017C4ECu, 0x0017C4F4u }.Select(a => new RuleSite(a, n => 0x00021803u | (uint)n << 6, w => (w & ~0x7C0u) == 0x00021803u && (w >> 6 & 31) <= 3 ? (int)(w >> 6 & 31) : null)).ToArray(), confirmed: true,
            decode: found => found.Distinct().Count() == 1 && (found[0] & ~0x7C0u) == 0x00021803u && (found[0] >> 6 & 31) <= 3 ? (int)(found[0] >> 6 & 31) : null)
        { Group = BattleStatsGroup, Short = "Share that offsets weight" },
        StatChoice(102, "healingStat", "Healing", 3, 0x0017B530, 0x0017B4FC),
        // Hit adds the weapon level through a call at 0x00179A08; critical adds the weapon level above a threshold through 0x00179420.
        // Either call can be pointed at a stat function instead, or replaced by a zero.
        AddedValue(103, "hitAdds", "Hit adds", 0x00179A08, [("Weapon level", Jal(WeaponLevel))], 0, true),
        AddedValue(104, "criticalAdds", "Critical adds", 0x00179420, [("Weapon level above the threshold", Jal(WeaponLevelAbove)), ("Weapon level", Jal(WeaponLevel))], 0, true),
        new GameRule(105, "criticalThreshold", "Weapon level that critical counts above", "", RuleKind.Limit, 0, 100, 50, null,
            [new RuleSite(0x0017A200, n => 0x28410000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x28410000u && (w & 0xFFFF) is >= 1 and <= 101 ? (int)(w & 0xFFFF) - 1 : null),
             new RuleSite(0x0017A210, n => 0x24420000u | (uint)(-n & 0xFFFF), w => (w & 0xFFFF0000u) == 0x24420000u && (-(short)(w & 0xFFFF)) is >= 0 and <= 100 ? -(short)(w & 0xFFFF) : null)], confirmed: true)
        { Group = HitCriticalGroup, Short = "Critical counts weapon level above" },
        // The Critical skill adds the unit's level (addu at 0x00179488, then an empty slot); a shift first scales it.
        // Damage below 1 becomes 0 (daddu s5,zero,zero at 0x0022AEC4); 1 makes every hit that lands deal at least 1.
        new GameRule(107, "minimumDamage", "Damage when attack does not get past defense", "", RuleKind.Choice, 0, 1, 0, ["No damage", "1 damage"],
            [new RuleSite(0x0022AEC4, s => s == 0 ? 0x0000A82Du : 0x24150001u, w => w == 0x0000A82Du ? 0 : w == 0x24150001u ? 1 : null)], confirmed: true)
        { Group = DamageGroup, Short = "When attack does not beat defense" },
        // Knives leave the stat out of attack and add a random amount from 0 up to it in battle (stat at 0x00227100, roll at 0x00227110).
        new GameRule(108, "knifeStat", "Stat knives add", "", RuleKind.Choice, 0, StatNames.Length - 1, 0, StatNames,
            [new RuleSite(0x00227100, s => Jal(PlainStatFunctions[s]), w => Array.FindIndex(PlainStatFunctions, f => Jal(f) == w) is var k and >= 0 ? k : null)], confirmed: true)
        { Group = DamageGroup, Short = "Knives add" },
        new GameRule(109, "knifeRandom", "How knives add it", "", RuleKind.Choice, 0, 1, 0, ["A random part of it", "All of it"],
            [new RuleSite(0x00227110, s => s == 0 ? 0x0C04E9D0u : 0u, w => w == 0x0C04E9D0u ? 0 : w == 0 ? 1 : null)], confirmed: true)
        { Group = DamageGroup, Short = "How much of it" },
        // Bonuses from allies' skills: the battle hooks multiply the number of allies in range by a constant (shifts and adds),
        // rewritten as addiu + the R5900 three-operand mult so any amount can be used.
        Multiplier(110, "commanderHit", "Commander", "Hit for each ally with it", 10, [0x001B7FEC, 0x001B7FF4, 0x001B7FFC], [0x00021880u, 0x00621021u, 0x00021040u], 3, 0x00431018u, true),
        Multiplier(111, "safeZoneHit", "Safe Zone", "Hit for each ally with it", 15, [0x001B8008, 0x001B8010], [0x00021900u, 0x00621023u], 3, 0x00431018u, true),
        Multiplier(112, "safeZoneAvoid", "Safe Zone", "Avoid for each ally with it", 15, [0x001B7F18, 0x001B7F1C], [0x00022100u, 0x00822023u], 4, 0x00442018u, true),
        Multiplier(113, "charismaHit", "Charisma", "Hit for each ally with it", 3, [0x001B8020, 0x001B8028], [0x00021840u, 0x00621021u], 3, 0x00431018u, true),
        Multiplier(114, "supporterHit", "Supporter", "Hit", 20, [0x001B8038, 0x001B8040, 0x001B8048], [0x00021880u, 0x00621021u, 0x00021080u], 3, 0x00431018u, true),
        // Supporter's critical bonus is a single shift, so it takes powers of two.
        new GameRule(115, "supporterCritical", "Supporter critical bonus", "", RuleKind.Choice, 0, 5, 3, ["0", "1", "2", "4", "8", "16"],
            [new RuleSite(0x001B7A20, s => s == 0 ? 0x0000102Du : 0x00021000u | (uint)(s - 1) << 6,
                w => w == 0x0000102Du ? 0 : (w & ~0x7C0u) == 0x00021000u && (w >> 6 & 31) <= 4 ? (int)(w >> 6 & 31) + 1 : null)], confirmed: true)
        { Group = "Supporter", Short = "Critical", Section = SkillsSection },
        Multiplier(116, "commanderIIAvoid", "Leader", "Avoid for each ally with it", 5, [0x001B7EFC, 0x001B7F04], [0x00021880u, 0x00621021u], 3, 0x00431018u, true),
        Multiplier(117, "protectorDefense", "Protector", "Defense for each ally with it", 5, [0x001B8188, 0x001B8190], [0x00021880u, 0x00621021u], 3, 0x00431018u, true),
        Multiplier(118, "resistorResistance", "Resistor", "Resistance for each ally with it", 5, [0x001B80E0, 0x001B80E8], [0x00021880u, 0x00621021u], 3, 0x00431018u, true),
        Multiplier(119, "battleCryHit", "Battle Cry", "Hit for each use", 10, [0x001B8068, 0x001B806C, 0x001B8070], [0x00031080u, 0x00431021u, 0x00021040u], 2, 0x00621018u, true),
        new GameRule(120, "evasionAvoid", "Evasion avoid bonus", "", RuleKind.Limit, 0, 200, 50, null,
            [new RuleSite(0x001B7F44, n => 0x26310000u | (uint)n, w => (w & 0xFFFF0000u) == 0x26310000u && (w & 0xFFFF) <= 200 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Evasion", Short = "Avoid", Section = SkillsSection },
        // Move is counted in tenths: Slow Start takes 10 off (addiu s0,s0,-10 at 0x0017E6A0), Celerity adds 10 (0x0017E768).
        new GameRule(121, "celerityMove", "Celerity move bonus", "", RuleKind.Limit, 0, 3, 1, null,
            [new RuleSite(0x0017E768, n => 0x26100000u | (uint)(n * 10), w => (w & 0xFFFF0000u) == 0x26100000u && (w & 0xFFFF) % 10 == 0 && (w & 0xFFFF) <= 30 ? (int)(w & 0xFFFF) / 10 : null)], confirmed: true)
        { Group = "Celerity", Short = "Move bonus", Section = SkillsSection },
        new GameRule(122, "slowStartMove", "Slow Start move penalty", "", RuleKind.Limit, 0, 3, 1, null,
            [new RuleSite(0x0017E6A0, n => 0x26100000u | (uint)(-n * 10 & 0xFFFF), w => (w & 0xFFFF0000u) == 0x26100000u && -(short)(w & 0xFFFF) is var v && v % 10 == 0 && v is >= 0 and <= 30 ? v / 10 : null)], confirmed: true)
        { Group = "Slow Start", Short = "Move penalty", Section = SkillsSection },
        // Camouflage multiplies terrain avoid by 3 and halves it (rounded toward zero) in the forecast hooks (0x001B7B74, 0x001B7DAC,
        // one per difficulty) and in battle (0x0022C040); terrain avoid is always even, so every choice is exact.
        WordSets(123, "camouflageAvoid", "Camouflage terrain avoid", "Camouflage", "Terrain avoid", 1,
            ["No bonus", "One and a half times", "Twice", "Two and a half times"],
            [0x001B7B74u, 0x001B7B78u, 0x001B7DACu, 0x001B7DB0u, 0x0022C040u, 0x0022C044u],
            [[0x00101040u, 0u, 0x00101040u, 0u, 0x00031840u, 0u],
             [0x00101040u, 0x00501021u, 0x00101040u, 0x00501021u, 0x00031040u, 0x00431821u],
             [0x00101080u, 0u, 0x00101080u, 0u, 0x00031880u, 0u],
             [0x00101080u, 0x00501021u, 0x00101080u, 0x00501021u, 0x00031080u, 0x00431821u]]),
        // Armsthrift adds 20 to the chance that a weapon use costs no durability (addiu s0,s0,20 at 0x0016D2D4).
        new GameRule(124, "armsthriftChance", "Armsthrift chance", "", RuleKind.Limit, 0, 100, 20, null,
            [new RuleSite(0x0016D2D4, n => 0x26100000u | (uint)n, w => (w & 0xFFFF0000u) == 0x26100000u && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Armsthrift", Short = "Chance to save a use (%)", Section = SkillsSection },
        // Watchful: hidden units are uncovered within 3 hexes instead of 1 (0x001BDF3C), and the map view is refreshed over 3 hexes (0x001ACF48).
        new GameRule(125, "watchfulRange", "Watchful range", "", RuleKind.Limit, 1, 10, 3, null,
            [new RuleSite(0x001BDF3C, n => 0x24100000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24100000u && (w & 0xFFFF) is >= 1 and <= 10 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x001ACF48, n => 0x241E0000u | (uint)n, w => (w & 0xFFFF0000u) == 0x241E0000u && (w & 0xFFFF) is >= 1 and <= 10 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Watchful", Short = "Range (hexes)", Section = SkillsSection },
        // One-Two sets status 4 (unit state +0x5C bit 4); choosing it stores a wait of 5 (0x001C35DC), and after the fight the
        // battle code sets the same counter to 6 (0x0022D524), which is the one that counts.
        new GameRule(126, "oneTwoWait", "One-Two wait", "", RuleKind.Limit, 1, 30, 6, null,
            [new RuleSite(0x0022D524, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x001C35DC, n => 0x24040000u | (uint)(n - 1), w => (w & 0xFFFF0000u) == 0x24040000u && (w & 0xFFFF) <= 29 ? (int)(w & 0xFFFF) + 1 : null)], confirmed: true)
        { Group = "One-Two", Short = "Wait (turns)", Section = SkillsSection },
        // Silence: choosing it stores a wait of 9 in unit state +0x85 (0x001C365C), and after the command runs the game sets the
        // same counter to 10 (0x001DF674, status 1 of the second set at +0x80), which is the one that counts, as with One-Two.
        new GameRule(127, "silenceWait", "Silence wait", "", RuleKind.Limit, 1, 30, 10, null,
            [new RuleSite(0x001DF674, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x001C365C, n => 0x24040000u | (uint)(n - 1), w => (w & 0xFFFF0000u) == 0x24040000u && (w & 0xFFFF) <= 29 ? (int)(w & 0xFFFF) + 1 : null)], confirmed: true)
        { Group = "Silence", Short = "Wait (turns)", Section = SkillsSection },
        // Despoil: the defeated enemy's level times 4 (sll v1,v1,2 at 0x003F98BC) plus a random 0 to 50 (0x003F98AC).
        new GameRule(128, "despoilPerLevel", "Despoil gold per enemy level", "", RuleKind.Choice, 0, 5, 2, ["1", "2", "4", "8", "16", "32"],
            [new RuleSite(0x003F98BC, n => 0x00031800u | (uint)n << 6, w => (w & ~0x7C0u) == 0x00031800u && (w >> 6 & 31) <= 5 ? (int)(w >> 6 & 31) : null)], confirmed: true)
        { Group = "Despoil", Short = "Gold per enemy level", Section = SkillsSection },
        new GameRule(129, "despoilRandom", "Despoil random gold", "", RuleKind.Limit, 0, 999, 50, null,
            [new RuleSite(0x003F98AC, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) <= 999 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Despoil", Short = "Random extra gold, up to", Section = SkillsSection },
        // Aura ranges. Each aura skill has a helper that counts the allies in range who have the skill (distance from 0x0015BA70).
        // The range is either compared directly (slti at, s3, range + 1) or added to a register first (addiu v0, v0, range).
        // The map shows the area with 0x001B2780, which takes the range in t0, while game option 0x0052C000 is 0.
        AuraRange(130, "commanderRange", "Commander", 3, new RuleSite(0x001B90D0, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u)),
            new RuleSite(0x001B2640, n => 0x24480000u | (uint)n, w => Imm(w, 0x24480000u))),
        AuraRange(131, "commander2Range", "Leader", 3, new RuleSite(0x001B8EF0, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u))),
        AuraRange(132, "safeZoneRange", "Safe Zone", 3, Below(0x001B8D10), Drawn(0x001B26E4)),
        AuraRange(133, "charismaRange", "Charisma", 5, Below(0x001B8B90), Drawn(0x001B274C)),
        AuraRange(134, "supporterRange", "Supporter", 3, Below(0x001B8710), Drawn(0x001B2714)),
        AuraRange(135, "resistorRange", "Resistor", 3, Below(0x001B8A10), Drawn(0x001B2684)),
        // The map's Protector area copies its range from a register that happens to hold 3 (daddu t0, a3, zero).
        AuraRange(136, "protectorRange", "Protector", 3, Below(0x001B8890),
            new RuleSite(0x001B26BC, n => n == 3 ? 0x00E0402Du : 0x24080000u | (uint)n, w => w == 0x00E0402Du ? 3 : Imm(w, 0x24080000u))),
        // Focus Chant (fight flag 0x2) and Throw (unit state 0x80) add hit in the fight (two fields) and in the forecast.
        new GameRule(137, "focusChantHit", "Focus Chant hit bonus", "", RuleKind.Limit, 0, 100, 30, null,
            [new RuleSite(0x0022C174, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u, 100)), new RuleSite(0x0022C180, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u, 100)),
             new RuleSite(0x00389E28, n => 0x24620000u | (uint)n, w => Imm(w, 0x24620000u, 100))], confirmed: true)
        { Group = "Focus Chant", Short = "Hit bonus", Section = SkillsSection },
        new GameRule(138, "throwHit", "Throw hit bonus", "", RuleKind.Limit, 0, 100, 10, null,
            [new RuleSite(0x0022C08C, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u, 100)), new RuleSite(0x00389DA0, n => 0x24620000u | (uint)n, w => Imm(w, 0x24620000u, 100))], confirmed: true)
        { Group = "Throw", Short = "Hit bonus", Section = SkillsSection },
        // Miracle: when a hit would kill, 0x0022B7C0 rolls it again with the attacker's hit lowered (addiu a1, s0, -10).
        new GameRule(139, "miracleReroll", "Miracle hit reduction", "", RuleKind.Limit, 0, 100, 10, null,
            [new RuleSite(0x00227FD4, n => 0x26050000u | (uint)(-n & 0xFFFF), w => (w & 0xFFFF0000u) == 0x26050000u && -(short)(w & 0xFFFF) is >= 0 and <= 100 ? -(short)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Miracle", Short = "Hit taken off the second roll", Section = SkillsSection },
        // Mug and Mug+: the chance is level / 10 plus 3, each skill with its own copy.
        new GameRule(140, "mugChance", "Mug base chance", "", RuleKind.Limit, 0, 100, 3, null,
            [new RuleSite(0x00227B48, n => 0x24420000u | (uint)n, w => Imm(w, 0x24420000u, 100))], confirmed: true)
        { Group = "Mug", Short = "Chance added to level / 10", Section = SkillsSection },
        new GameRule(141, "mug2Chance", "Mug+ base chance", "", RuleKind.Limit, 0, 100, 3, null,
            [new RuleSite(0x0022DE44, n => 0x24500000u | (uint)n, w => Imm(w, 0x24500000u, 100))], confirmed: true)
        { Group = "Mug+", Short = "Chance added to level / 10", Section = SkillsSection },
        // Steals per map: 0x001C3740 sets unit state +0x105 bits 2 and 3 at the start of a map, so 3 is the most.
        StealCount(142, "stealCount", "Steal", 3, 0x001C386C),
        StealCount(143, "robberyCount", "Robbery", 3, 0x001C38DC),
        StealCount(144, "mug2Count", "Mug+", 2, 0x001C38A4),
        // Maim: uses per map in unit state +0x10C bits 1 to 4; the chance is (weapon level + 10) / 2.
        new GameRule(145, "maimUses", "Maim uses per map", "", RuleKind.Limit, 0, 15, 5, null,
            [new RuleSite(0x001C3914, n => 0x64030000u | (uint)(n << 1), w => (w & 0xFFFF0000u) == 0x64030000u && (w & 0xFFE1u) == 0 ? (int)((w & 0x1E) >> 1) : null)], confirmed: false)
        { Group = "Maim", Short = "Uses per map", Section = SkillsSection },
        new GameRule(146, "maimChance", "Maim base chance", "", RuleKind.Limit, 0, 50, 5, null,
            [new RuleSite(0x00226194, n => 0x24430000u | (uint)(n * 2), w => (w & 0xFFFF0000u) == 0x24430000u && (w & 1) == 0 && (w & 0xFFFF) <= 100 ? (int)(w & 0xFFFF) / 2 : null)], confirmed: false)
        { Group = "Maim", Short = "Chance added to half the weapon level", Section = SkillsSection },
        // Iaido: after the unit evades, battle flag 0x4000 gives its counter more hit (s0) and critical (s5).
        new GameRule(147, "iaidoCounterHit", "Iaido counter hit bonus", "", RuleKind.Limit, 0, 100, 33, null,
            [new RuleSite(0x0022726C, n => 0x26100000u | (uint)n, w => Imm(w, 0x26100000u, 100))], confirmed: false)
        { Group = "Iaido", Short = "Counter hit bonus", Section = SkillsSection },
        new GameRule(148, "iaidoCounterCrit", "Iaido counter critical bonus", "", RuleKind.Limit, 0, 100, 3, null,
            [new RuleSite(0x00227278, n => 0x26B50000u | (uint)n, w => Imm(w, 0x26B50000u, 100))], confirmed: true)
        { Group = "Iaido", Short = "Counter critical bonus", Section = SkillsSection },
        // Battle Cry: the count of uses is unit state +0x104 bits 2 to 5. Attack adds 3 per use (0x001B8270); the command menu
        // and the enemy decisions offer it below 6 uses, and each use keeps the count at 6 or less.
        Multiplier(149, "battleCryAttack", "Battle Cry", "Attack for each use", 3, [0x001B827C, 0x001B8280], [0x00031040u, 0x00431021u], 2, 0x00621018u, true),
        new GameRule(150, "battleCryUses", "Battle Cry uses", "", RuleKind.Limit, 1, 15, 6, null,
            [new RuleSite(0x0038C42C, n => 0x28410000u | (uint)n, w => Imm(w, 0x28410000u, 15)),
             .. new uint[] { 0x002F76E4, 0x002FB434, 0x002FB77C, 0x002FB944, 0x002FBACC, 0x002FC054 }.Select(a => new RuleSite(a, n => 0x28420000u | (uint)n, w => Imm(w, 0x28420000u, 15))),
             .. new uint[] { 0x001E2E14, 0x002F2480 }.Select(a => new RuleSite(a, n => 0x28410000u | (uint)(n + 1), w => Imm(w, 0x28410000u, 16) is int k and > 0 ? k - 1 : null)),
             .. new uint[] { 0x001E2E2C, 0x002F2498 }.Select(a => new RuleSite(a, n => 0x24020000u | (uint)n, w => Imm(w, 0x24020000u, 15)))], confirmed: true)
        { Group = "Battle Cry", Short = "Most uses", Section = SkillsSection },
        // Provoke: the target check 0x001BFE10 (menu, targeting and enemy decisions) takes enemies within 5 hexes.
        AuraRange(151, "provokeRange", "Provoke", 5, Below(0x001BFED0)),
        // Flourish sets unit state bit 0x1000: the injury roll adds 33 (0x00228A48) and the fight halves attack (0x0022C354).
        new GameRule(152, "flourishInjury", "Flourish injury bonus", "", RuleKind.Limit, 0, 100, 33, null,
            [new RuleSite(0x00228A48, n => 0x26100000u | (uint)n, w => Imm(w, 0x26100000u, 100))], confirmed: true)
        { Group = "Flourish", Short = "Injury chance bonus", Section = SkillsSection },
        WordSets(153, "flourishAttack", "Flourish attack", "Flourish", "Attack", 0, ["Halved", "Not halved"],
            [0x0022C358], [[0x1040000Du], [0x1000000Du]]),
        // Desperation (unit state bit 0x20): the enemy's hit becomes 900 in the strike loop, and the user's defenses become 0.
        WordSets(154, "desperationEnemyHit", "Desperation enemy hit", "Desperation", "Enemy hit", 0, ["Always hits", "Normal"],
            [0x00227238, 0x0022C618], [[0x10400003u, 0x10400003u], [0x10000003u, 0x10000003u]]),
        WordSets(155, "desperationDefense", "Desperation defense", "Desperation", "Own defense", 0, ["Drops to 0", "Kept"],
            [0x0022C1AC, 0x0022C1B0, 0x0022C1B4], [[0xAE600068u, 0xAE60006Cu, 0xAE600070u], [0u, 0u, 0u]]),
        // Injury roll 0x002289C0: base 3, plus 6 with a knife (weapon type 0), plus Flourish and item effects, against 0 to 99.
        new GameRule(156, "injuryBase", "Injury base chance", "", RuleKind.Limit, 0, 100, 3, null,
            [new RuleSite(0x00228A1C, n => 0x24100000u | (uint)n, w => Imm(w, 0x24100000u, 100))], confirmed: true)
        { Group = InjuryGroup, Short = "Base chance" },
        new GameRule(157, "injuryKnife", "Injury knife bonus", "", RuleKind.Limit, 0, 100, 6, null,
            [new RuleSite(0x00228A28, n => 0x26100000u | (uint)n, w => Imm(w, 0x26100000u, 100))], confirmed: true)
        { Group = InjuryGroup, Short = "Knife bonus" },
        // Blessing at each turn start (0x001CEEE0): allies passing the check 0x001BF830 (within 3 hexes) recover random 0 to 3 HP.
        AuraRange(158, "blessingRange", "Blessing", 3, true, Below(0x001BF8F0)),
        new GameRule(159, "blessingHeal", "Blessing most healing", "", RuleKind.Limit, 0, 30, 3, null,
            [new RuleSite(0x001CEF34, n => 0x24060000u | (uint)n, w => Imm(w, 0x24060000u, 30))], confirmed: true)
        { Group = "Blessing", Short = "Most HP healed", Section = SkillsSection },
        // Horse-Lover (0x001729E0): the horse's HP rises by 20 after each chapter, up to its maximum.
        new GameRule(160, "horseLoverHeal", "Horse-Lover recovery", "", RuleKind.Limit, 0, 100, 20, null,
            [new RuleSite(0x00172A98, n => 0x24A40000u | (uint)n, w => Imm(w, 0x24A40000u, 100))], confirmed: false)
        { Group = "Horse-Lover", Short = "Horse HP recovered", Section = SkillsSection },
        // Scotopic (0x00176B50): vision range plus 1.
        new GameRule(161, "scotopicVision", "Scotopic vision bonus", "", RuleKind.Limit, 0, 5, 1, null,
            [new RuleSite(0x00176C58, n => 0x26100000u | (uint)n, w => Imm(w, 0x26100000u, 5))], confirmed: false)
        { Group = "Scotopic", Short = "Vision bonus", Section = SkillsSection },
        // Strike counts set in the fight from the command bits: Astra 5 (0x00226AB4), Triple-Shot 3 (0x00226B00), Double-Shot 2 (0x00226B50).
        // Weapon effects already give up to 7 strikes, so 7 is the top.
        Strikes(162, "astraStrikes", "Astra", 5, 0x00226AB4, true),
        Strikes(163, "tripleShotStrikes", "Triple-Shot", 3, 0x00226B00, true),
        Strikes(164, "doubleShotStrikes", "Double-Shot", 2, 0x00226B50, true),
        // Axe Guard block chance: the block bonus plus the axe level plus 25 (0x0022A434); a block takes the axe's Might off the hit.
        new GameRule(165, "axeGuardChance", "Axe Guard block bonus", "", RuleKind.Limit, 0, 100, 25, null,
            [new RuleSite(0x0022A434, n => 0x24440000u | (uint)n, w => Imm(w, 0x24440000u, 100))], confirmed: true)
        { Group = "Axe Guard", Short = "Block chance bonus", Section = SkillsSection },
        // Pulverize (battle +0x4C bit 0x2000): battle setup doubles attack (0x0022C110 to 0x0022C130) and zeroes the user's defenses (0x0022C150 to 0x0022C158).
        WordSets(166, "pulverizeAttack", "Pulverize attack", "Pulverize", "Attack", 0, ["Doubled", "Not doubled"],
            [0x0022C110], [[0x10400009u], [0x10000009u]]),
        WordSets(167, "pulverizeDefense", "Pulverize defense", "Pulverize", "Own defense", 0, ["Drops to 0", "Kept"],
            [0x0022C150, 0x0022C154, 0x0022C158], [[0xAE600068u, 0xAE60006Cu, 0xAE600070u], [0u, 0u, 0u]]),
        FocusChantAttack(),
        // Vengeful (skill bit 0x400) in the fight: needs an axe (type 7, 0x0022688C); chance is damage taken times 3 (0x002268A0, 0x002268A8).
        WordSets(169, "vengefulChance", "Vengeful chance", "Vengeful", "Chance for each point of damage taken", 2, ["1%", "2%", "3%", "4%", "5%"],
            [0x002268A0, 0x002268A8], [[0x00052000u, 0x00808821u], [0x00052040u, 0x00808821u], [0x00052040u, 0x00858821u], [0x00052080u, 0x00808821u], [0x00052080u, 0x00858821u]], confirmed: true),
        WordSets(170, "vengefulWeapon", "Vengeful weapon", "Vengeful", "Weapon", 0, ["Axe only", "Any weapon"],
            [0x0022688C], [[0x14430014u], [0u]], confirmed: true),
        // Adept (skill bit 0x100): needs attack speed 5 (0x00226BA8), chance adds (weapon level + 10) / 2 (0x00226C3C), strikes 2 (0x00226C8C).
        new GameRule(171, "adeptSpeed", "Adept speed needed", "", RuleKind.Limit, 0, 20, 5, null,
            [new RuleSite(0x00226BA8, n => 0x24550000u | (uint)(-n & 0xFFFF), w => Minus(w, 0x24550000u, 20))], confirmed: true)
        { Group = "Adept", Short = "Attack speed needed", Section = SkillsSection },
        new GameRule(172, "adeptBonus", "Adept chance bonus", "", RuleKind.Limit, 0, 200, 10, null,
            [new RuleSite(0x00226C3C, n => 0x24430000u | (uint)n, w => Imm(w, 0x24430000u, 200))], confirmed: true)
        { Group = "Adept", Short = "Added to the weapon level before halving", Section = SkillsSection },
        new GameRule(173, "adeptStrikes", "Adept strikes", "", RuleKind.Limit, 1, 7, 2, null,
            [new RuleSite(0x00226C8C, n => 0x24020000u | (uint)n, w => Imm(w, 0x24020000u, 7))], confirmed: true)
        { Group = "Adept", Short = "Strikes", Section = SkillsSection },
        // Paragon only counts at level 10 or lower: slti at, v0, 11 in the skill check (0x0017D484).
        new GameRule(174, "paragonLevel", "Paragon highest level", "", RuleKind.Limit, 0, 63, 10, null,
            [new RuleSite(0x0017D484, n => 0x28410000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x28410000u && (w & 0xFFFF) is >= 1 and <= 64 ? (int)(w & 0xFFFF) - 1 : null)], confirmed: true)
        { Group = "Paragon", Short = "Highest level", Section = SkillsSection },
        // Item level check 0x0017A240: Limited makes ranks above 3 (0x0017A5C8) need level 50 (0x0017A5D0); Expert takes 7 off the rank (0x0017A604).
        new GameRule(175, "expertRank", "Expert rank reduction", "", RuleKind.Limit, 0, 20, 7, null,
            [new RuleSite(0x0017A604, n => 0x26310000u | (uint)(-n & 0xFFFF), w => Minus(w, 0x26310000u, 20))], confirmed: true)
        { Group = "Expert", Short = "Rank reduction", Section = SkillsSection },
        new GameRule(176, "limitedRank", "Limited highest rank", "", RuleKind.Limit, 0, 20, 3, null,
            [new RuleSite(0x0017A5C8, n => 0x2A210000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x2A210000u && (w & 0xFFFF) is >= 1 and <= 21 ? (int)(w & 0xFFFF) - 1 : null)], confirmed: true)
        { Group = "Limited", Short = "Highest rank used normally", Section = SkillsSection },
        new GameRule(177, "limitedLevel", "Limited level", "", RuleKind.Limit, 1, 99, 50, null,
            [new RuleSite(0x0017A5D0, n => 0x24020000u | (uint)n, w => Imm(w, 0x24020000u, 99))], confirmed: true)
        { Group = "Limited", Short = "Level needed above that rank", Section = SkillsSection },
        // On terrain with flag 0x400 (cliffs) avoid becomes 0 unless the unit has Climber: forecast 0x001B7E74, fight 0x0022C3A8.
        WordSets(178, "cliffAvoid", "Avoid on cliffs", "Climber", "Avoid on cliffs without Climber", 0, ["Drops to 0", "Kept"],
            [0x001B7E74, 0x0022C3A8], [[0x10400010u, 0x10400007u], [0x10000010u, 0x10000007u]], confirmed: false),
        // Weapons a command can attack with, checked by command state bit in 0x001BE800 (menu, targeting and forecast).
        WeaponNeed(179, "aimWeapon", "Aim weapon", "Aim", "Bow, crossbow or ballista", 0x001BE9BC, 0x001BE9D8,
            [0x0C05B928u, 0u, 0x14400004u, 0u, 0x10000122u, 0x0000102Du, 0u]),
        WeaponNeed(180, "bowShotsWeapon", "Deadeye, Double-Shot and Triple-Shot weapon", "Deadeye", "Bow", 0x001BE9F4, 0x001BEA10,
            [0x0C05BA08u, 0u, 0x24030008u, 0x10430003u, 0u, 0x10000113u, 0x0000102Du]),
        WeaponNeed(181, "pulverizeWeapon", "Pulverize weapon", "Pulverize", "Axe or blade", 0x001BEA24, 0x001BEA50,
            [0x0C05B85Cu, 0u, 0x14400008u, 0x0280202Du, 0x0C05B87Cu, 0u, 0x14400004u, 0u, 0x10000104u, 0x0000102Du]),
        WeaponNeed(182, "throwWeapon", "Throw weapon", "Throw", "Thrown weapon", 0x001BEA74, 0x001BEA90,
            [0x0280282Du, 0x0C05BB7Cu, 0x2406003Cu, 0x14400003u, 0u, 0x100000F3u, 0x0000102Du]),
        WeaponNeed(183, "astraWeapon", "Astra weapon", "Astra", "Sword or blade", 0x001BEAA8, 0x001BEAC0,
            [0x0C05B89Cu, 0u, 0x14400003u, 0u, 0x100000E7u, 0x0000102Du]),
        WeaponNeed(184, "focusChantWeapon", "Focus Chant weapon", "Focus Chant", "Attack magic", 0x001BEAD4, 0x001BEAF0,
            [0x0C05B94Cu, 0u, 0x24030001u, 0x10430003u, 0u, 0x100000DBu, 0x0000102Du]),
        WeaponNeed(185, "flourishWeapon", "Flourish and Desperation weapon", "Flourish", "Physical weapon", 0x001BEB04, 0x001BEB20,
            [0x0C05B94Cu, 0u, 0x24030001u, 0x14430003u, 0u, 0x100000CFu, 0x0000102Du]),
        // Aura counts: each holder in range adds one (addiu s0, s0, 1), and distance 0, the unit itself, is skipped (beq s3, zero).
        AuraCount(190, "commanderCount", "Commander", 0x001B90E0, 0x12600003u, 0x001B90E8),
        AuraCount(191, "commander2Count", "Leader", 0x001B8F00, 0x12600003u, 0x001B8F08),
        AuraCount(192, "safeZoneCount", "Safe Zone", 0x001B8D1C, 0x12600002u, 0x001B8D24),
        AuraCount(193, "charismaCount", "Charisma", 0x001B8B9C, 0x12600002u, 0x001B8BA4),
        AuraCount(194, "resistorCount", "Resistor", 0x001B8A1C, 0x12600002u, 0x001B8A24),
        AuraCount(195, "protectorCount", "Protector", 0x001B889C, 0x12600002u, 0x001B88A4),
        WordSets(196, "supporterSelf", "Supporter partner", "Supporter", "Partner", 0, ["Another unit with Supporter", "Not needed"],
            [0x001B871C], [[0x12600002u], [0u]]),
        // Celerity skips every class in the mounted class list (branch at 0x0017E760).
        WordSets(197, "celerityMounted", "Celerity while mounted", "Celerity", "Mounted units", 0, ["No bonus", "Bonus too"],
            [0x0017E760], [[0x14400003u], [0u]]),
        SlowStartPenalty(),
        // A strike for 0 damage jumps past the Iaido roll (0x00229E24 to 0x00229F68); Also 0 damage sends it to the roll (0x00229E3C).
        // Failed strikes (a miss, a parry, a hit cancelled by a bane skill) jump from 0x00228048 to 0x00229E30, where the hit roll
        // (sp+512) skips Iaido unless the strike really missed (0x00229E34); removing that check lets parries and cancelled hits count.
        WordSets(203, "iaidoTrigger", "Iaido counter trigger", "Iaido", "Counter bonus after", 0,
            ["A miss", "A miss or a hit for 0 damage", "A miss, a parry or a cancelled hit", "Any of these"],
            [0x00229E24, 0x00229E34], [[0x10000050u, 0x14400022u], [0x10000005u, 0x14400022u], [0x10000050u, 0u], [0x10000005u, 0u]]),
        // Partner bonuses count partners closer than 3 hexes (slti at, s1, 3 at 0x001B8530).
        new GameRule(202, "partnerRange", "Partner range",
            "A unit gets the hit and critical bonuses of its partners (set on the Characters page) while a partner is within this many hexes. The game uses 2.",
            RuleKind.Limit, 1, 10, 2, null,
            [new RuleSite(0x001B8530, n => 0x2A210000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x2A210000u && (w & 0xFFFF) is >= 2 and <= 11 ? (int)(w & 0xFFFF) - 1 : null)], confirmed: true),
        // Hurry: the move hook 0x001B7950 adds 10 tenths (addiu s0, s0, 10 at 0x001B798C) while the unit has the Hurry status (+0x5C bit 0x04000000).
        new GameRule(206, "hurryMove", "Hurry extra move", "", RuleKind.Limit, 0, 5, 1, null,
            [new RuleSite(0x001B798C, n => 0x26100000u | (uint)(n * 10), w => (w & 0xFFFF0000u) == 0x26100000u && (w & 0xFFFF) <= 50 && (w & 0xFFFF) % 10 == 0 ? (int)(w & 0xFFFF) / 10 : null)], confirmed: true)
        { Group = "Hurry", Short = "Extra hexes", Section = SkillsSection },
        // Deathmatch: the map fight start (0x003F7300) asks for 5 rounds (0x003F730C) when the attacker has the Deathmatch status (+0x5C bit 0x80000), otherwise 1.
        new GameRule(207, "deathmatchRounds", "Deathmatch rounds", "", RuleKind.Limit, 1, 20, 5, null,
            [new RuleSite(0x003F730C, n => 0x24100000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24100000u && (w & 0xFFFF) is >= 1 and <= 20 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Deathmatch", Short = "Rounds", Section = SkillsSection },
        // Canto after a fight: the menu (0x00388450) skips Canto when state +0x58 bit 0x02000000 is set (0x00388480); the fight results
        // (0x0022CCC0) set that bit when a strike did damage to the unit (battle flag 0x10000).
        WordSets(208, "cantoAfterDamage", "Canto after taking damage", "Canto", "After taking damage", 0, ["Not offered", "Offered"],
            [0x00388480], [[0x14400065u], [0u]]),
        // Guard: the avoid hooks (0x001B7A90, 0x001B7BE0, 0x001B7CD0) and the defense hook (0x001B8140) halve the value while the unit guards (state +0x58 bit 0x4000).
        WordSets(209, "guardHalving", "Guard's own defense and avoid", "Guard", "Own defense and avoid", 0, ["Halved", "Kept"],
            [0x001B7B9C, 0x001B7C8C, 0x001B7DD4, 0x001B81E4], [[0x10400006u, 0x10400006u, 0x10400006u, 0x10400006u], [0x10000006u, 0x10000006u, 0x10000006u, 0x10000006u]]),
        // Our Bond: the critical hook (0x001B79D0) sets Reese's critical to 100 (0x001B7A6C) while Lynette (unit 0x7D) stands next to him (0x001B82A0).
        new GameRule(210, "ourBondCritical", "Our Bond critical", "", RuleKind.Limit, 0, 100, 100, null,
            [new RuleSite(0x001B7A6C, n => 0x24100000u | (uint)n, w => Imm(w, 0x24100000u, 100))], confirmed: false)
        { Group = "Our Bond", Short = "Reese's critical", Section = SkillsSection },
        // Status counters (+0x60 + status) drop by 1 on each of the unit's turns (0x001B6F10). The Provoke effect (0x002F2390) gives status 6
        // for 10 (0x002F2410), or status 15 for 10 in its other mode (0x002F23E0); Hateful gives status 15 for 10 (0x002F2FB4).
        new GameRule(211, "provokeTurns", "Provoke turns", "", RuleKind.Limit, 1, 30, 10, null,
            [new RuleSite(0x002F2410, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x002F23E0, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Provoke", Short = "Turns provoked", Section = SkillsSection },
        new GameRule(212, "hatefulTurns", "Hateful turns", "", RuleKind.Limit, 1, 30, 10, null,
            [new RuleSite(0x002F2FB4, n => 0x24060000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24060000u && (w & 0xFFFF) is >= 1 and <= 30 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Hateful", Short = "Turns provoked", Section = SkillsSection },
        // Debility: the stat getter (0x00180BF0) divides max HP, Strength, Defense, Agility, Mind and stat 9 by 2 (0x00180C54) for a unit with the skill.
        new GameRule(238, "debilityDivisor", "Debility divisor", "", RuleKind.Limit, 1, 4, 2, null,
            [new RuleSite(0x00180C54, n => 0x24110000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24110000u && (w & 0xFFFF) is >= 1 and <= 4 ? (int)(w & 0xFFFF) : null)], confirmed: true)
        { Group = "Debility", Short = "Stats divided by", Section = SkillsSection },
        // Weapon skills: the class (0x0017C960) and unit (0x0017CA30) weapon permission sets add 1 << weapon type for Pitchfork (fork),
        // Lance (lance) and Blade (blade); an item is usable when its type bit is set.
        GrantedWeapon(239, "pitchforkWeapon", "Pitchfork", 6, 0x0017C9B4, 0x0017CA90),
        GrantedWeapon(240, "lanceWeapon", "Lance", 5, 0x0017C9D0, 0x0017CAB8),
        GrantedWeapon(241, "bladeWeapon", "Blade", 2, 0x0017C9EC, 0x0017CAE0),
        // Locktouch: the Open command (menus 0x001B9D20 and 0x0038CF5C), the door action (0x001DEA14, spends a Door Key without the skill)
        // and the chest action (0x001E4984) ask the skill check; answering yes for everyone lets any unit open without a key.
        WordSets(242, "locktouchEveryone", "Opening without Locktouch", "Locktouch", "Who can open", 0, ["Locktouch, or a Door Key for doors", "Every unit, no key used"],
            [0x001B9D34, 0x001DEA14, 0x001E4984, 0x0038CF5C], [[0x0C05F428u, 0x0C05F428u, 0x0C05F428u, 0x0C05F428u], [0x24020001u, 0x24020001u, 0x24020001u, 0x24020001u]], confirmed: false),
        // Robbery after a kill (0x003F9960): a percent roll below 25 (0x003F99B8), then the range below 2 (0x003F99C0).
        new GameRule(204, "robberyChance", "Robbery chance", "", RuleKind.Limit, 0, 100, 25, null,
            [new RuleSite(0x003F99B8, n => 0x28410000u | (uint)n, w => Imm(w, 0x28410000u, 100))], confirmed: true)
        { Group = "Robbery", Short = "Chance", Section = SkillsSection },
        new GameRule(205, "robberyRange", "Robbery range", "", RuleKind.Limit, 0, 10, 1, null,
            [new RuleSite(0x003F99C0, n => 0x2A010000u | (uint)(n + 1), w => (w & 0xFFFF0000u) == 0x2A010000u && (w & 0xFFFF) is >= 1 and <= 11 ? (int)(w & 0xFFFF) - 1 : null)], confirmed: true)
        { Group = "Robbery", Short = "Highest range", Section = SkillsSection },
        // A unit that took damage (battle +0x48 bit 0x800) stops counterattacking unless it has Counter (0x002268EC);
        // Always skips to the next step without the Counter message.
        WordSets(201, "counterAfterDamage", "Counterattacks after taking damage", "Counter", "Without Counter", 0, ["Stop", "Keep countering"],
            [0x002268EC], [[0x10400E60u], [0x10400008u]]),
        // Vantage only strikes first when the attack speed comparison passes (branch at 0x00226518).
        WordSets(199, "vantageSpeed", "Vantage speed check", "Vantage", "Attack speed", 0, ["Must beat the attacker", "Any speed"],
            [0x00226518], [[0x14200003u], [0x10000003u]]),
        // Units without Watchful uncover hidden enemies (0x001BDF40) and refresh the map view (0x001ACF44) from 1 hex.
        new GameRule(200, "uncoverRange", "Uncover range without Watchful", "", RuleKind.Limit, 1, 10, 1, null,
            [new RuleSite(0x001BDF40, n => 0x24030000u | (uint)n, w => (w & 0xFFFF0000u) == 0x24030000u && (w & 0xFFFF) is >= 1 and <= 10 ? (int)(w & 0xFFFF) : null),
             new RuleSite(0x001ACF44, n => 0x241E0000u | (uint)n, w => (w & 0xFFFF0000u) == 0x241E0000u && (w & 0xFFFF) is >= 1 and <= 10 ? (int)(w & 0xFFFF) : null)], confirmed: false)
        { Group = "Watchful", Short = "Range for units without it", Section = SkillsSection },
        IaidoWeapon(),
        HunterWeapon(),
        AxeGuardWeapon(),
        WordSets(106, "criticalSkillLevel", "Critical skill bonus", "Critical", "Adds to critical", 0, ["Level", "Twice the level", "Four times the level", "Half the level", "Nothing"],
            [0x00179488u, 0x0017948Cu], [[0x02028021u, 0u], [0x00021040u, 0x02028021u], [0x00021080u, 0x02028021u], [0x00021042u, 0x02028021u], [0u, 0u]]),
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
        MercenaryData.Restore(copy);
        RecipeData.Restore(copy);
        DishData.Restore(copy);
        TasteData.Restore(copy);
        MapSpriteData.Restore(copy);
        return copy;
    }
}
