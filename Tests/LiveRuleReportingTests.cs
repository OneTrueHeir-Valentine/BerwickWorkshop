using Berwick.Core;

internal static class LiveRuleReportingTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Promotion campaign windows are export-only and never claimed active", () =>
        {
            var window = PromotionQuestWindows.RuleFor(88);
            Check(LiveRuleReporting.Support(window) == LiveRuleSupport.ExportOnly,
                "A campaign-window rule was treated as live-writable.");
            string message = LiveRuleReporting.Summary(0, 0, 1, 0);
            Check(message.Contains("export-only") && message.Contains("not applied")
                && message.Contains("boot"), "Live feedback failed to disclose export-only behavior.");
        });
        test("Live rule status distinguishes fully live and partly writable changes", () =>
        {
            RuleSite Site(bool live) => new(0x00120000, i => (uint)i, w => (int)w) { Live = live };
            GameRule Rule(params RuleSite[] sites) => new(1999, "fixture", "Fixture", "Test-only rule",
                RuleKind.Limit, 0, 3, 0, null, sites, confirmed: true);
            Check(LiveRuleReporting.Support(Rule(Site(true))) == LiveRuleSupport.Full,
                "A fully live rule was misclassified.");
            Check(LiveRuleReporting.Support(Rule(Site(true), Site(false))) == LiveRuleSupport.Partial,
                "A partly writable rule was misclassified.");
            Check(LiveRuleReporting.Support(Rule(Site(false))) == LiveRuleSupport.ExportOnly,
                "An export-only rule was misclassified.");
            string feedback = LiveRuleReporting.Summary(1, 1, 2, 1);
            Check(feedback.Contains("only in part") && feedback.Contains("were not applied")
                && feedback.Contains("code did not match") && feedback.Contains("export-only"),
                "Combined Live Apply feedback exaggerated activation.");
            Check(LiveRuleReporting.Summary(0, 0, 0, 0).Contains("No changed"),
                "Empty live apply was misreported.");
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
