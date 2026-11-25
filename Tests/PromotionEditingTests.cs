using Berwick.Core;

internal static class PromotionEditingTests
{
    public static void RunDiscs(Action<string, Action> test, params InspectionResult[] discs)
    {
        foreach (var disc in discs)
        {
            test($"Promotion level edits stay independent literals ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var binding = PromotionRequirements.ReadLevel(session, 88);
                Check(PromotionRequirements.SetLevel(session, 88, binding.Level + 1), "Level change was not applied.");
                Check(!session.HasScript(binding.CallerEvent), "Level edit unnecessarily replaced the whole event.");
                Check(PromotionRequirements.ReadLevel(session, 88).Level == binding.Level + 1,
                    "Level readback failed.");
                Check(ChangeManifest.Build(session).Any(row => row.Field.Contains("promotion minimum level")),
                    "Change review lost the semantic promotion-level label.");
                var saved = EditSession.FromJson(disc, session.ToJson());
                Check(!saved.HasScript(binding.CallerEvent)
                    && PromotionRequirements.ReadLevel(saved, 88).Level == binding.Level + 1,
                    "Project reload lost the isolated literal edit.");
                session.Undo();
                Check(PromotionRequirements.ReadLevel(session, 88).Level == binding.Level,
                    "Undo did not restore the original level.");
                session.Redo();
                Check(PromotionRequirements.ReadLevel(session, 88).Level == binding.Level + 1,
                    "Redo did not restore the level change.");
            });
            test($"Promotion thresholds stay independent literal edits ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var fields = PromotionPredicates.Read(session, 88);
                var spear = fields.Single(f => f.Selector == 5);
                var shield = fields.Single(f => f.Selector == 14);
                Check(PromotionPredicates.SetThreshold(session, 88, spear.LiteralIndex, spear.Minimum - 1),
                    "Spear threshold was not edited.");
                Check(PromotionPredicates.SetThreshold(session, 88, shield.LiteralIndex, shield.Minimum - 1),
                    "Shield threshold was not edited.");
                Check(!session.HasScript(237), "Shared thresholds unnecessarily became a whole-event edit.");
                Check(ChangeManifest.Build(session).Any(row => row.Field.Contains("Shared Spear")),
                    "Change review lost shared-user semantic information.");
                session.Undo();
                var afterUndo = PromotionPredicates.Read(session, 88);
                Check(afterUndo.Single(f => f.Selector == 5).Minimum == spear.Minimum - 1
                    && afterUndo.Single(f => f.Selector == 14).Minimum == shield.Minimum,
                    "Undo of the second threshold changed the first threshold.");
                session.Redo();
                Check(PromotionPredicates.Read(session, 88).Single(f => f.Selector == 14).Minimum == shield.Minimum - 1,
                    "Threshold redo failed.");
                var reopened = EditSession.FromJson(disc, session.ToJson());
                Check(!reopened.HasScript(237)
                    && PromotionPredicates.Read(reopened, 88).Single(f => f.Selector == 5).Minimum == spear.Minimum - 1,
                    "Project reload lost a literal threshold.");
            });
            test($"Promotion threshold report includes a retargeted recipient ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                Check(PromotionDestinations.SetDestination(session, 87, 37), "Test destination was not changed.");
                var threshold = PromotionPredicates.Read(session, 87).First(f => f.Editable);
                Check(PromotionPredicates.KnownUsers(session, 87, threshold.LiteralIndex).Contains(87),
                    "Known users omitted the retargeted character.");
                int replacement = threshold.Minimum == 63 ? 62 : threshold.Minimum + 1;
                Check(PromotionPredicates.SetThreshold(session, 87, threshold.LiteralIndex, replacement),
                    "Retargeted threshold edit failed.");
                var report = ProjectReview.Build(session);
                string actor = PromotionCatalogue.Read(disc).Paths.Single(p => p.UnitId == 87).CharacterName;
                Check(report.Changes.Any(c => c.Key == $"promotionThreshold{threshold.LiteralIndex}"
                    && c.Field.Contains("current project users:") && c.Field.Contains(actor)),
                    "Threshold report omitted the retargeted character or current-project distinction.");
                session.Undo();
                Check(!ChangeManifest.Build(session).Any(c => c.Key == $"promotionThreshold{threshold.LiteralIndex}"),
                    "Undo left the threshold change in the manifest.");
                session.Redo();
                var restored = EditSession.FromJson(disc, session.ToJson());
                Check(ChangeManifest.Build(restored).Any(c => c.Key == $"promotionThreshold{threshold.LiteralIndex}"),
                    "Reload lost the retargeted threshold description.");
            });
            test($"Malformed promotion path retains unrelated threshold reports ({disc.Edition})", () =>
            {
                foreach (int otherDestination in new[] { 160, 164 })
                {
                    var session = new EditSession(disc);
                    var original = session.EventScript(237)!;
                    var valid = PromotionPredicates.Read(28, original).First(f => f.Selector == 5);
                    var invalid = PromotionPredicates.Read(otherDestination, original).First(f => f.Editable);
                    Check(invalid.LiteralIndex != valid.LiteralIndex, "Test paths share a threshold literal.");
                    var edited = ScriptValues.Apply(original, new Dictionary<int, int>
                    {
                        [valid.LiteralIndex] = valid.Minimum == 0 ? 1 : valid.Minimum - 1,
                        [invalid.LiteralIndex] = 64
                    });
                    Check(session.SetScript(237, edited), "Test script modification failed.");
                    var report = ProjectReview.Build(session);
                    Check(report.Changes.Any(c => c.Key == $"promotionThreshold{valid.LiteralIndex}"),
                        "An unsupported route erased a valid threshold description.");
                    Check(report.Issues.Any(i => i.Code == "promotion-analysis-incomplete"
                        && i.Message.Contains("Event 237")),
                        "Incomplete promotion analysis was not disclosed.");
                    Check(report.Changes.Any(c => c.Kind == "Script" && c.Id == 237),
                        "The underlying script edit disappeared from change review.");
                }
            });
            test($"Promotion level no-op preserves literal edits and redo ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var binding = PromotionRequirements.ReadLevel(session, 88);
                var numbers = ScriptValues.Numbers(binding.CallerEvent, ScriptParser.Parse(session.EventScript(binding.CallerEvent)!));
                var other = numbers.First(n => n.Index != binding.LiteralIndex);
                session.Apply(EditSession.ScriptValues, ScriptValues.Key(binding.CallerEvent, other.Index),
                    new Dictionary<string, int> { ["value"] = other.Value + 1 });
                PromotionRequirements.SetLevel(session, 88, binding.Level + 1);
                session.Undo();
                string before = session.ToJson();
                Check(!PromotionRequirements.SetLevel(session, 88, binding.Level), "A same-level Apply reported a change.");
                Check(!session.HasScript(binding.CallerEvent) && session.CanRedo && session.ToJson() == before,
                    "No-op absorbed literal edits or discarded redo.");
                session.Redo();
                Check(PromotionRequirements.ReadLevel(session, 88).Level == binding.Level + 1, "Redo no longer works.");
            });
            test($"Minimum-level edits preserve unrelated literals without authorizing destination changes ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var binding = PromotionRequirements.ReadLevel(session, 88);
                var numbers = ScriptValues.Numbers(binding.CallerEvent,
                    ScriptParser.Parse(session.EventScript(binding.CallerEvent)!));
                var other = numbers.First(n => n.Index != binding.LiteralIndex);
                session.Apply(EditSession.ScriptValues, ScriptValues.Key(binding.CallerEvent, other.Index),
                    new Dictionary<string, int> { ["value"] = other.Value + 1 });
                Check(PromotionRequirements.ReadLevel(session, 88).Level == binding.Level,
                    "Unrelated numeric edit disabled reading a valid promotion minimum.");
                Check(PromotionRequirements.SetLevel(session, 88, binding.Level + 1),
                    "An unrelated numeric edit disabled minimum-level editing.");
                string before = session.ToJson();
                Reject(() => PromotionDestinations.SetDestination(session, 88, 37));
                Check(session.ToJson() == before,
                    "Rejected destination change altered the project or accepted another literal change.");
            });
            test($"Promotion threshold no-op preserves literal edits and redo ({disc.Edition})", () =>
            {
                var session = new EditSession(disc);
                var field = PromotionPredicates.Read(session, 88).Single(f => f.Selector == 5);
                session.Apply(EditSession.ScriptValues, ScriptValues.Key(237, field.LiteralIndex),
                    new Dictionary<string, int> { ["value"] = 25 });
                PromotionPredicates.SetThreshold(session, 88, field.LiteralIndex, 26);
                session.Undo();
                string before = session.ToJson();
                Check(!PromotionPredicates.SetThreshold(session, 88, field.LiteralIndex, 25), "Same threshold reported a change.");
                Check(!session.HasScript(237) && session.CanRedo && session.ToJson() == before,
                    "No-op absorbed threshold literals or discarded redo.");
            });
        }
    }

    public static void Run(Action<string, Action> test)
    {
        test("Promotion levels bind VM arguments and preserve unrelated numbers", () =>
        {
            var source = Caller();
            var binding = PromotionRequirements.ReadLevel(88, source);
            Check(binding.Level == 10 && binding.CallerEvent == 371, "Wrong caller binding.");
            var edited = PromotionRequirements.ChangeLevel(88, source, 15);
            Check(PromotionRequirements.ReadLevel(88, edited).Level == 15, "Minimum was not changed.");
            var before = ScriptValues.Numbers(371, ScriptParser.Parse(source));
            var after = ScriptValues.Numbers(371, ScriptParser.Parse(edited));
            Check(before.Count == after.Count, "Script structure changed.");
            Check(before.Zip(after).Count(p => p.First.Value != p.Second.Value) == 1,
                "An unrelated number was changed.");
            Check(source.SequenceEqual(Caller()), "Source bytes were mutated.");
            Check(PromotionRequirements.ChangeLevel(88, source, 10).SequenceEqual(source), "No-op changed bytes.");
        });
        test("Promotion minimum levels enforce boundaries", () =>
        {
            foreach (int level in new[] { 1, 63 })
                Check(PromotionRequirements.ReadLevel(88,
                    PromotionRequirements.ChangeLevel(88, Caller(), level)).Level == level, "Boundary rejected.");
            Reject(() => PromotionRequirements.ChangeLevel(88, Caller(), 0));
            Reject(() => PromotionRequirements.ChangeLevel(88, Caller(), 64));
        });
        test("Promotion level binding refuses changed identities and computed arguments", () =>
        {
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(unit: 87)));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(target: 37)));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(requiredClass: 1)));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(level: "v1")));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(call: "v1")));
            Reject(() => PromotionRequirements.ReadLevel(999, Caller()));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller().AsSpan(0, 16)));
        });
        test("Promotion level binding refuses duplicate helpers and interrupted argument setup", () =>
        {
            var two = ScriptWriter.Write(ScriptParser.Parse(Caller()).Concat(ScriptParser.Parse(Caller())).ToArray());
            Reject(() => PromotionRequirements.ReadLevel(88, two));
            Reject(() => PromotionRequirements.ReadLevel(88, Caller(interruption: "        v-4 = 11\n")));
            var missing = ScriptWriter.Write([new ScriptNode { Command = ScriptParser.Event, Args = [[]] }]);
            Reject(() => PromotionRequirements.ReadLevel(88, missing));
        });
        test("Promotion level editing supports another known caller without changing shared requirements", () =>
        {
            var source = Caller(unit: 87);
            var edited = PromotionRequirements.ChangeLevel(87, source, 12);
            Check(PromotionRequirements.ReadLevel(87, edited).CallerEvent == 370, "Adel caller confused with Leon.");
            Check(PromotionRequirements.ReadLevel(87, edited).Level == 12, "Adel minimum not changed.");
        });
        test("Promotion session editing rejects unverified source identities before reading scripts", () =>
        {
            var disc = ChangeReviewTests.Fixture() with { IsModified = true };
            var session = new EditSession(disc);
            Reject(() => PromotionRequirements.ReadLevel(session, 88));
            Reject(() => PromotionRequirements.SetLevel(session, 88, 15));
            Check(session.ChangeCount == 0, "Rejected source was modified.");
        });
    }

    internal static byte[] Caller(int unit = 88, int target = 28, int requiredClass = 0,
        string level = "10", string call = "236", string interruption = "") => ScriptWriter.Write(
        ScriptCompiler.Compile($"event (2 local values)\n    v0 = 77\n    if v1\n        v-1 = command30({unit})\n        v-2 = {requiredClass}\n        v-3 = command31({target})\n        v-4 = {level}\n{interruption}        v0 = run event {call}\n    else\n    v0", null));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is EditException or InvalidDataException) { return; }
        throw new Exception("An unsupported promotion binding was accepted.");
    }
}
