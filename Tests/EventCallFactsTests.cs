using Berwick.Core;

internal static class EventCallFactsTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Verified computed calls propagate every finite map and army destination", () =>
        {
            var sources = Fixture();
            var facts = Facts(sources);
            Check(Analyze(sources).Problems.Count > 0, "An unproved variable call was accepted");
            var references = Analyze(sources, facts);
            Check(references.Problems.Count == 0, "Verified finite call remained unresolved");
            Check(references.MapArchives.Count == 2 && references.ArmyBattles[6].SetEquals([26, 27]), "A relevant destination was excluded from the graph");
        });
        test("Computed-call proof covers the complete active graph and archive identities", () =>
        {
            var original = Fixture();
            var facts = Facts(original);
            Check(EventCallFacts.Fingerprint(original.Reverse().ToArray()) == facts.GraphSha256, "Source enumeration order changed the proof");
            var edited = original.ToArray();
            edited[2] = edited[2] with { Nodes = [Preload(7)] };
            Check(Analyze(edited, facts).Problems.Any(problem => problem.Contains("active scripts differ")), "Edited scripts reused the proof or lacked an explanation");
            edited = original.ToArray();
            edited[1] = edited[1] with { Archive = 99 };
            Check(Analyze(edited, facts).Problems.Count > 0, "Changed archive relationships reused the proof");
            Check(Analyze([.. original, new(40, 4, [Call(20)])], facts).Problems.Count > 0, "An added caller reused the original proof");
        });
        test("Verified computed calls still refuse missing and ambiguous destinations", () =>
        {
            var sources = Fixture();
            string hash = EventCallFacts.Fingerprint(sources);
            Check(Analyze(sources, new(hash, [new(20, 0x20, [999])])).Problems.Count > 0, "Missing destination was ignored");
            Check(Analyze(sources, new(hash, [new(20, 0x20, [])])).Problems.Count > 0, "Empty destination set was accepted");
            Check(Analyze(sources, new(hash, [new(20, 0x20, [30]), new(20, 0x20, [31])])).Problems.Count > 0, "Duplicate proof records were accepted");
            var unproved = sources.ToArray();
            unproved[1] = unproved[1] with { Nodes = [VariableCall(0x20), VariableCall(0x40)] };
            Check(Analyze(unproved, Facts(unproved)).Problems.Count > 0, "A second ambiguous call inherited the first call's destinations");
        });
        test("Computed-call proof cannot cover a different offset or expression", () =>
        {
            var sources = Fixture();
            sources[1] = sources[1] with { Nodes = [VariableCall(0x24)] };
            Check(Analyze(sources, Facts(sources)).Problems.Count > 0, "A different call offset reused the proof");
            sources[1] = sources[1] with { Nodes = [new() { Command = ScriptParser.RunEvent, Offset = 0x20, Args = [[Preload(6)]] }] };
            Check(Analyze(sources, Facts(sources)).Problems.Count > 0, "An arbitrary expression reused a variable-call proof");
        });
        test("Finite computed opening paths retain conditional placement restrictions", () =>
        {
            var sources = Fixture();
            sources[2] = sources[2] with { Nodes = [UnitPlacementTests.Appear(5, 1)] };
            var references = Analyze(sources, Facts(sources));
            var assignment = references.Assignments.Single();
            Check(references.Problems.Count == 0 && assignment.Conditional, "A computed opening destination became unconditional");
            var map = UnitPlacementTests.Map() with { Battle = 26 };
            var army = new Army(5, "Army", [], 256, [new(5000, 5, 0, 1, "Unit", 0, 180)]);
            var candidate = StartingUnitPlacement.Candidates(map, references, [army], UnitPlacementTests.EmptyRecord()).Single();
            Check(!candidate.CanPlace && candidate.Problem!.Contains("Conditional"), "Conditional army placement became editable");
        });
        test("Original computed-call facts require the recognized unchanged image and executable", () =>
        {
            foreach (var profile in Profiles.All)
            {
                var disc = new InspectionResult("synthetic", 0, "", "", "", profile.Sha256, profile.ExecutableSha256, profile, [], [], 0);
                Check(EventCallFacts.ForOriginal(disc)?.Calls.Count == 3, "A supported edition lacks its verified finite calls");
                Check(EventCallFacts.ForOriginal(disc with { IsModified = true }) is null, "Modified input used original facts");
                Check(EventCallFacts.ForOriginal(disc with { Sha256 = "unknown" }) is null, "Unknown image used original facts");
                Check(EventCallFacts.ForOriginal(disc with { ExecutableSha256 = "unknown" }) is null, "Unknown native getter used original facts");
            }
        });
    }

    private static PlacementEventSource[] Fixture() =>
    [
        new(10, 0, [UnitPlacementTests.Launch(26, "first.ar"), Call(20)]),
        new(20, 1, [VariableCall(0x20)]),
        new(30, 2, [Preload(6)]),
        new(31, 3, [UnitPlacementTests.Launch(27, "second.ar"), Preload(6)])
    ];

    private static EventCallFacts Facts(IReadOnlyList<PlacementEventSource> sources) =>
        new(EventCallFacts.Fingerprint(sources), [new(20, 0x20, [30, 31])]);

    private static BattlePlacementReferences Analyze(IReadOnlyList<PlacementEventSource> sources, EventCallFacts? facts = null) =>
        BattlePlacementReferences.Analyze(sources, [new(10, 26, 13, [])], file => file == "first.ar" ? 0 : 1, facts);

    private static ScriptNode Call(int target) => new() { Command = ScriptParser.RunEvent, Args = [[UnitPlacementTests.Number(target)]] };
    private static ScriptNode VariableCall(int offset) => new() { Command = ScriptParser.RunEvent, Offset = offset, Args = [[new() { Command = ScriptParser.Variable, Variable = 4 }]] };
    private static ScriptNode Preload(int army) => new() { Command = 0x167, Args = [[UnitPlacementTests.Text($"ud{army:000}")]] };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
