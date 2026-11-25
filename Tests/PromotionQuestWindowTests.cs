using Berwick.Core;

internal static class PromotionQuestWindowTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Promotion campaign windows preserve packed neighboring fields", () =>
        {
            var rule = PromotionQuestWindows.RuleFor(88);
            int setting = GameRules.RangeSetting(0, 12);
            uint original = rule.Original.Single();
            uint changed = rule.Encode(setting).Single();
            Check((original & 0xF000FFFFu) == (changed & 0xF000FFFFu), "A neighboring field changed.");
            Check(rule.Decode([changed]) == setting, "Window did not round-trip.");
            Check(rule.Describe(setting).Contains("0") && !rule.Sites.Single().Live, "Window is mislabeled or live-writable.");
        });
        test("Promotion windows enforce inclusive six-bit bounds and ordering", () =>
        {
            var rule = PromotionQuestWindows.RuleFor(88);
            foreach (var pair in new[] { (0, 0), (0, 63), (63, 63) })
            {
                int setting = GameRules.RangeSetting(pair.Item1, pair.Item2);
                Check(rule.Decode(rule.Encode(setting)) == setting, "Valid inclusive range rejected.");
            }
            Reject(() => rule.Encode(GameRules.RangeSetting(12, 1)));
            Reject(() => rule.Encode(GameRules.RangeSetting(0, 64)));
            Reject(() => rule.Encode(-1));
            Check(rule.Decode([rule.Original.Single() ^ 1u]) is null, "Unknown neighboring bits were accepted.");
        });
        test("Promotion windows have one registered rule per verified caller", () =>
        {
            var profile = Profiles.All[0];
            var paths = PromotionCatalogue.Read(profile, profile.Sha256, profile.ExecutableSha256, false).Paths;
            var rules = paths.Select(p => PromotionQuestWindows.RuleFor(p.UnitId)).ToList();
            Check(rules.Count == 16 && rules.Select(r => r.Id).Distinct().Count() == 16, "Quest rule duplicated.");
            Check(rules.All(r => ReferenceEquals(GameRules.ById(r.Id), r)), "Window not registered in session rules.");
            Check(GameRules.All.Count(r => r.Section == PromotionQuestWindows.Section) == 16
                && rules.All(r => !r.Confirmed), "Promotion windows were omitted or marked gameplay-confirmed.");
            Check(GameRules.All.Select(r => r.Id).Distinct().Count() == GameRules.All.Count, "Rule IDs collide.");
            Reject(() => PromotionQuestWindows.RuleFor(999));
        });
        test("Promotion campaign windows use existing undo and project persistence", () =>
        {
            var profile = Profiles.All[0];
            var rule = PromotionQuestWindows.RuleFor(88);
            var disc = ChangeReviewTests.Fixture() with
            {
                Profile = profile, Sha256 = profile.Sha256, ExecutableSha256 = profile.ExecutableSha256,
                RuleWords = new Dictionary<int, ReadOnlyMemory<byte>> { [rule.Id] = rule.EncodeBytes(rule.Default) }
            };
            var session = new EditSession(disc);
            Check(PromotionQuestWindows.Read(session, 88) == (1, 12), "Wrong original window.");
            Check(PromotionQuestWindows.SetWindow(session, 88, 0, 9), "Window edit not applied.");
            Check(!PromotionQuestWindows.SetWindow(session, 88, 0, 9), "No-op reported a change.");
            var loaded = EditSession.FromJson(disc, session.ToJson());
            Check(PromotionQuestWindows.Read(loaded, 88) == (0, 9), "Project lost window.");
            session.Undo();
            Check(PromotionQuestWindows.Read(session, 88) == (1, 12), "Undo failed.");
            session.Redo();
            Check(PromotionQuestWindows.Read(session, 88) == (0, 9), "Redo failed.");
            session.Revert(EditSession.Rules, rule.Id);
            Check(PromotionQuestWindows.Read(session, 88) == (1, 12), "Revert failed.");
            Reject(() => PromotionQuestWindows.SetWindow(session, 88, 12, 1));
            Check(session.ChangeCount == 0, "Refused window changed project.");
            Check(rule.LiveEncode(GameRules.RangeSetting(0, 9)).SequenceEqual(rule.Original), "Window became live-writable.");
        });
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (EditException) { return; }
        throw new Exception("Invalid campaign window accepted.");
    }
}
