using Berwick.Core;

/// <summary>Formula Editor catalogue and calculators, on a generated project holding the game's own rule words.</summary>
public static class FormulaTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run(Action<string, Action> test)
    {
        test("Every formula names its Mechanics entry, and every editable part is an exposed rule", () =>
        {
            foreach (var formula in Formulas.All)
            {
                Check(Mechanics.All.Any(m => m.Name == formula.Mechanic), $"{formula.Name}: no Mechanics entry called {formula.Mechanic}.");
                foreach (var term in formula.Terms.Where(t => t.Kind == FormulaTermKind.Editable))
                    Check(term.RuleId is int id && GameRules.ById(id).EditorVisible, $"{formula.Name}: {term.Name} is not an exposed rule.");
                Check(formula.Terms.Where(t => t.Kind != FormulaTermKind.Editable).All(t => t.RuleId is null), $"{formula.Name}: a fixed part points at a rule.");
                Check(formula.Evidence.Length > 0, $"{formula.Name}: no evidence given.");
            }
        });
        test("Hit rate: whole weapon levels, a level of zero still counts accuracy, and the avoid rule is read from the project", () =>
        {
            var session = new EditSession(Fixture());
            var reese = Formulas.Hit(session, new(70, 80, 0, 0, 0, 0, false));
            Check(reese.Original == 78 && reese.Current == 78, "Rapier 70 with sword level 8 should give 78 against no avoid.");
            Check(Formulas.Hit(session, new(70, 0, 0, 0, 0, 0, false)).Current == 70, "Level 0 should add nothing and still give the weapon's accuracy.");
            Check(Formulas.Hit(session, new(70, 10, 0, 0, 0, 0, false)).Current - Formulas.Hit(session, new(70, 1, 0, 0, 0, 0, false)).Current == 1,
                "Level 1.0 and 0.1 should differ by one point.");
            Check(Formulas.Hit(session, new(80, 50, 5, 9, 10, 0, true)).Current == 80 + 5 + 5 - (18 + 20), "Ranged terrain should count twice.");
            session.Apply(EditSession.Rules, Formulas.AvoidSpeedRule, new Dictionary<string, int> { [GameRules.ById(Formulas.AvoidSpeedRule).Key] = 2 });
            var four = Formulas.Hit(session, new(80, 50, 0, 9, 0, 0, false));
            Check(four.Original == 80 + 5 - 18 && four.Current == 80 + 5 - 36 && four.Changed, "Times 4 avoid was not read from the project.");
            Check(four.CurrentSteps.Contains("× 4", StringComparison.Ordinal), "The steps do not show the multiplier.");
        });
        test("Hit rate refuses to invent a stat contribution and distinguishes Nothing", () =>
        {
            var session = new EditSession(Fixture());
            var rule = GameRules.ById(Formulas.HitAddsRule);
            session.Apply(EditSession.Rules, rule.Id, new Dictionary<string, int> { [rule.Key] = 1 });
            var stat = Formulas.Hit(session, new(70, 80, 0, 0, 0, 0, false));
            Check(stat.Original == 78 && stat.Current is null, "A stat mode was given a false numeric result.");
            session.Apply(EditSession.Rules, rule.Id, new Dictionary<string, int> { [rule.Key] = rule.Max });
            Check(Formulas.Hit(session, new(70, 80, 0, 0, 0, 0, false)).Current == 70, "Nothing should add zero.");
        });
        test("Calculator rejects negative levels, arithmetic overflow and ineligible shield rolls", () =>
        {
            var session = new EditSession(Fixture());
            void Refused<T>(Action action) where T : Exception
            {
                try { action(); } catch (T) { return; }
                throw new Exception("Invalid calculator input was accepted.");
            }
            Refused<EditException>(() => Formulas.Hit(session, new(70, -1, 0, 0, 0, 0, false)));
            Refused<EditException>(() => Formulas.Failure(session, new(64, 0)));
            Refused<OverflowException>(() => Formulas.Hit(session, new(70, 80, int.MaxValue, 0, 0, 0, false)));
            var rule = GameRules.ById(Formulas.ShieldBlockRule);
            session.Apply(EditSession.Rules, rule.Id, new Dictionary<string, int> { [rule.Key] = 1 });
            Check(Formulas.Block(session, new(30, 125, true, false)).Current == 0, "Always incorrectly bypassed eligibility.");
        });
        test("Equipment failure: chance per rank above the level, none at or below it, and the rule's setting", () =>
        {
            var session = new EditSession(Fixture());
            Check(Formulas.Failure(session, new(12, 9)).Current == 15, "Three ranks above should be 15%.");
            Check(Formulas.Failure(session, new(9, 9)).Current == 0 && Formulas.Failure(session, new(4, 9)).Current == 0, "At or below the level should be 0.");
            session.Apply(EditSession.Rules, Formulas.FailureRule, new Dictionary<string, int> { [GameRules.ById(Formulas.FailureRule).Key] = 0 });
            var never = Formulas.Failure(session, new(12, 9));
            Check(never.Original == 15 && never.Current == 0, "Setting the rule to Never did not reach the calculator.");
        });
        test("Shield calculator reads experimental term settings and preserves original comparison", () =>
        {
            var session = new EditSession(Fixture());
            var acc = GameRules.ById(Formulas.ShieldAccuracyTerm);
            var prof = GameRules.ById(Formulas.ShieldProficiencyTerm);
            session.Apply(EditSession.Rules, acc.Id, new Dictionary<string, int> { [acc.Key] = 2 });
            Check(Formulas.Block(session, new(41, 200, false)).Current == 40, "Half accuracy was not included correctly.");
            session.Apply(EditSession.Rules, prof.Id, new Dictionary<string, int> { [prof.Key] = 1 });
            var result = Formulas.Block(session, new(41, 200, false));
            Check(result.Original == 61 && result.Current == 20, "Proficiency omission or original comparison was incorrect.");
        });
        test("Shield block chance: whole shield levels, Shieldfaire, and the Always and Never modes", () =>
        {
            var session = new EditSession(Fixture());
            Check(Formulas.Block(session, new(30, 125, false)).Current == 42, "30 + shield level 12 should be 42.");
            Check(Formulas.Block(session, new(30, 125, true)).Current == 60, "Shieldfaire should add 18.");
            session.Apply(EditSession.Rules, Formulas.ShieldBlockRule, new Dictionary<string, int> { [GameRules.ById(Formulas.ShieldBlockRule).Key] = 2 });
            var never = Formulas.Block(session, new(30, 125, false));
            Check(never.Original == 42 && never.Current == 0 && never.CurrentSteps.Contains("Never", StringComparison.Ordinal), "Never was not shown.");
        });
    }

    private static InspectionResult Fixture()
    {
        var disc = ChangeReviewTests.Fixture();
        var words = new Dictionary<int, ReadOnlyMemory<byte>>(disc.RuleWords);
        foreach (int id in new[] { Formulas.HitAddsRule, Formulas.AvoidSpeedRule, Formulas.FailureRule, Formulas.ShieldBlockRule, Formulas.ShieldfaireRule, Formulas.ShieldProficiencyTerm, Formulas.ShieldAccuracyTerm })
            words[id] = GameRules.ById(id).EncodeBytes(GameRules.ById(id).Default);
        return disc with { RuleWords = words };
    }
}
