using System.Buffers.Binary;

namespace Berwick.Core;

public static partial class GameRules
{
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

}
