namespace Berwick.Core;

public static class PromotionQuestWindows
{
    public const string Section = "Promotions";
    private const uint TableAddress = 0x0046D9E0, WindowMask = 0x0FFF0000;
    private static readonly (int Unit, int Quest, int Start, int End)[] Bindings =
    [
        (132,158,7,12), (83,162,1,9), (85,163,1,12), (87,164,1,12),
        (88,165,1,12), (89,166,1,12), (124,167,9,12), (97,168,1,12),
        (134,169,1,12), (96,170,1,12), (131,171,1,12), (149,172,1,12),
        (109,173,1,12), (90,174,1,12), (127,198,1,12), (113,199,1,12)
    ];
    private static readonly Lazy<IReadOnlyList<GameRule>> Registered = new(() =>
        Array.AsReadOnly(Bindings.Select(CreateRule).ToArray()));
    internal static IReadOnlyList<GameRule> Rules => Registered.Value;

    public static int QuestFor(int unitId) => Binding(unitId).Quest;

    public static GameRule RuleFor(int unitId) => Rules.Single(r => r.Id == 1000 + Binding(unitId).Quest);

    public static (int Start, int End) Read(EditSession session, int unitId)
    {
        PromotionRequirements.RequireSource(session);
        var rule = RuleFor(unitId);
        if (!session.Disc.RuleWords.TryGetValue(rule.Id, out var original) || rule.Decode(original.Span) != rule.Default)
            throw new EditException("The original promotion campaign window is not verified on this source.");
        int current = session.Value(EditSession.Rules, rule.Id, rule.Key);
        rule.Encode(current);
        return (GameRules.RangeLow(current), GameRules.RangeHigh(current));
    }

    public static bool SetWindow(EditSession session, int unitId, int start, int end)
    {
        Read(session, unitId);
        if (start is < 0 or > 63 || end is < 0 or > 63 || start > end)
            throw new EditException("Campaign counters must be from 0 to 63, with the start no later than the end.");
        var rule = RuleFor(unitId);
        int setting = GameRules.RangeSetting(start, end);
        return session.Apply(EditSession.Rules, rule.Id, new Dictionary<string, int> { [rule.Key] = setting });
    }

    private static (int Unit, int Quest, int Start, int End) Binding(int unitId)
    {
        var binding = Bindings.SingleOrDefault(b => b.Unit == unitId);
        if (binding.Unit == 0) throw new EditException("This unit has no verified promotion campaign window.");
        return binding;
    }

    private static GameRule CreateRule((int Unit, int Quest, int Start, int End) binding)
    {
        uint address = TableAddress + (uint)(binding.Quest - 1) * 52 + 40;
        uint Encode(int setting) => 0x53u | (uint)(GameRules.RangeLow(setting) | GameRules.RangeHigh(setting) << 6) << 16;
        int? Decode(uint word)
        {
            if ((word & ~WindowMask) != 0x53u) return null;
            int start = (int)(word >> 16 & 63), end = (int)(word >> 22 & 63);
            return start <= end ? GameRules.RangeSetting(start, end) : null;
        }
        return new GameRule(1000 + binding.Quest, $"promotionQuest{binding.Quest}Window",
            $"Promotion quest {binding.Quest} campaign window",
            "Inclusive campaign counters for considering this promotion quest. Story, phase and quest-history conditions still apply. Existing saves can retain adopted candidates.",
            RuleKind.Range, 0, GameRules.RangeSetting(63, 63), GameRules.RangeSetting(binding.Start, binding.End), null,
            [new RuleSite(address, Encode, Decode) { Live = false }], confirmed: false,
            check: setting => GameRules.RangeLow(setting) > 63 || GameRules.RangeHigh(setting) > 63
                || GameRules.RangeLow(setting) > GameRules.RangeHigh(setting)
                ? "Campaign counters must be from 0 to 63, with the start no later than the end." : null)
            { Section = Section, Group = "Campaign availability", Short = $"Quest {binding.Quest}" };
    }
}
