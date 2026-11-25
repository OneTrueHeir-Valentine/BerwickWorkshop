using Berwick.Core;

internal static class PromotionDestinationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Adel and Leon destination binding uses exact unit and class arguments", () =>
        {
            foreach (var (unit,caller,app,town) in new[] { (87,370,167,1353), (88,371,169,1367) })
            {
                var source = PromotionEditingTests.Caller(unit: unit);
                var found = PromotionDestinations.CallerTarget(unit, caller, source);
                Check(found.Destination == 28, "Incorrect original helper destination.");
                var application = Application(unit, 28, town);
                var selected = PromotionDestinations.ApplicationTarget(unit, town, app, application);
                Check(selected.Destination == 28, "Incorrect original application destination.");
                var newCaller = ScriptValues.Apply(source, new Dictionary<int, int> { [found.LiteralIndex] = 24 });
                var newApplication = ScriptValues.Apply(application,
                    new Dictionary<int, int> { [selected.LiteralIndex] = 24 });
                Check(PromotionDestinations.CallerTarget(unit, caller, newCaller).Destination == 24
                    && PromotionDestinations.ApplicationTarget(unit, town, app, newApplication).Destination == 24,
                    "Destination was not replaced at both verified literals.");
                Check(PromotionRequirements.ReadLevel(unit, newCaller, expectedTarget: 24).Level == 10,
                    "Level editing became invalid after an edited destination.");
                Check(ScriptValues.Numbers(app, ScriptParser.Parse(application))
                        .Zip(ScriptValues.Numbers(app, ScriptParser.Parse(newApplication)))
                        .Count(x => x.First.Value != x.Second.Value) == 1,
                    "Destination editing altered unrelated application values.");
            }
        });
        test("Destination editors fail closed on mistaken person, helper, or scene", () =>
        {
            Reject(() => PromotionDestinations.ApplicationTarget(87, 1353, 167, Application(88, 28, 1353)));
            Reject(() => PromotionDestinations.ApplicationTarget(87, 1353, 167, Application(87, 28, 1367)));
            Reject(() => PromotionDestinations.CallerTarget(87, 370, PromotionEditingTests.Caller(unit: 88)));
            Reject(() => PromotionDestinations.CallerTarget(87, 370, PromotionEditingTests.Caller(unit: 87, call: "237")));
            Reject(() => PromotionDestinations.ApplicationTarget(87, 1353, 167, new byte[7]));
        });
        test("Unsupported characters and modified discs cannot obtain destination write authority", () =>
        {
            Check(PromotionDestinations.Supports(87) && PromotionDestinations.Supports(88)
                && !PromotionDestinations.Supports(134), "Unverified character gained a destination writer.");
            var fake = new EditSession(ChangeReviewTests.Fixture() with { IsModified = true });
            Reject(() => PromotionDestinations.SetDestination(fake, 87, 24));
        });
    }

    public static void RunDiscs(Action<string, Action> test, params InspectionResult[] discs)
    {
        foreach (var disc in discs)
        {
            test($"Coordinated promotion destination undo, redo and project persistence ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var first = PromotionDestinations.Read(session, 87);
                var reward = session.EventScript(168)!;
                Check(first.Destination == 28 && first.ApplicationEvent == 167, "Original Adel route not verified.");
                Check(PromotionDestinations.SetDestination(session, 87, 24), "Target change was not applied.");
                Check(PromotionDestinations.Read(session, 87).Destination == 24, "Eligibility and application targets mismatch.");
                Check(PromotionRequirements.ReadLevel(session, 87).Level == 10, "Minimum level reader broke.");
                Check(reward.SequenceEqual(session.EventScript(168)!), "Original Iron Lance reward was modified.");
                Check(!PromotionDestinations.SetDestination(session, 87, 24), "Identical Apply changed undo history.");
                var restored = EditSession.FromJson(disc, session.ToJson());
                Check(PromotionDestinations.Read(restored, 87).Destination == 24, "Project reload discarded class edit.");
                session.Undo();
                Check(PromotionDestinations.Read(session, 87).Destination == 28, "One Undo did not restore both events.");
                session.Redo();
                Check(PromotionDestinations.Read(session, 87).Destination == 24, "One Redo did not restore both events.");
                Check(reward.SequenceEqual(session.EventScript(168)!), "Reward changed during Undo/Redo.");
            });
        }
    }

    private static byte[] Application(int unit, int target, int town) => ScriptWriter.Write(
        ScriptCompiler.Compile($"event (0 local values)\n    v-1 = command30({unit})\n    v-3 = command31({target})\n    run event 188\n    run event {town}\n", null));

    private static void Check(bool okay, string reason)
    {
        if (!okay) throw new Exception(reason);
    }
    private static void Reject(Action check)
    {
        try { check(); }
        catch (Exception ex) when (ex is EditException or InvalidDataException) { return; }
        throw new Exception("Unverified promotion structure was accepted.");
    }
}
