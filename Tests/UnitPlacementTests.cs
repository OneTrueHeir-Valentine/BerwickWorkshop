using Berwick.Core;
using System.Diagnostics;

internal static class UnitPlacementTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Starting placement resolves both documented battle launch variants", () =>
        {
            foreach (var launch in new[] { Launch(201, "demo.ar"), LaunchWithMode(7, 201, "demo.ar") })
            {
                var references = BattlePlacementReferences.Analyze([new(12, 0, [launch, Appear(5, 2)])], [new(12, 201, 13, [])], _ => 3);
                Check(references.Problems.Count == 0 && references.MapArchives.Count == 1 && references.MapArchives[201] == 3, "Mode and battle indexes were confused");
                Check(references.ArmyBattles[5].SetEquals([201]), "Launch assigned the army to the wrong battle");
            }
        });
        test("Starting placement refuses incomplete and computed launch variants", () =>
        {
            foreach (var launch in new ScriptNode[]
            {
                new() { Command = 0x2DE, Args = [[Number(201)]] },
                new() { Command = 0x2DF, Args = [[Number(201)], [Text("demo.ar")]] },
                new() { Command = 0x2DF, Args = [[Number(7)], [new() { Command = ScriptParser.Variable, Variable = 1 }], [Text("demo.ar")]] }
            })
            {
                var references = BattlePlacementReferences.Analyze([new(12, 0, [launch])], [new(12, 201, 13, [])], _ => 3);
                Check(references.Problems.Count > 0, "Unsupported launch layout was guessed");
            }
        });
        test("Starting placement uses existing army records and slots 1 through 63", () =>
        {
            var record = EmptyRecord();
            var units = Enumerable.Range(0, 64).Select(i => new ArmyUnit(100 + i, 5, i, i, $"Unit {i}", i * 256, 0)).ToArray();
            var candidates = StartingUnitPlacement.Candidates(Map(), References(), [new(5, "Army", [], 0, units)], record);
            Check(candidates[0].Slot == 1 && candidates[62].Slot == 63, "Unit index was not translated to its reserved-slot offset");
            Check(candidates[0].CanPlace && !candidates[63].CanPlace, "Unsupported final record became placeable");
        });
        test("Starting placement rejects reused groups, repeated armies and conditional assignments", () =>
        {
            foreach (var scripts in new[]
            {
                new[] { Appear(5, 2), Appear(6, 2) },
                new[] { Appear(5, 2), Appear(5, 3) },
                new[] { Appear(5, 0) },
                new[] { Appear(5, 16) },
                new[] { new ScriptNode { Command = ScriptParser.If, Args = [[Number(1)], [Appear(5, 2)]] } }
            })
            {
                var candidate = Candidates(References(scripts)).First(c => c.Unit.Key == 100);
                Check(!candidate.CanPlace && !candidate.CanRemove, "Ambiguous opening assignment accepted");
            }
        });
        test("Starting placement requires a fully unused coordinate pair", () =>
        {
            var record = EmptyRecord();
            record[64 + 1] = 5;
            var candidate = Candidates(References(), record).Single();
            Check(!candidate.CanPlace && !candidate.CanRemove, "Half-empty slot accepted");
            var occupied = Placement.Move(EmptyRecord(), 2, 1, 5, 6);
            candidate = Candidates(References(), occupied).Single();
            Check(!candidate.CanPlace && candidate.CanRemove, "Occupied slot can be added again");
        });
        test("Starting placement rejects border, terrain, deployment and slot collisions", () =>
        {
            var record = EmptyRecord();
            var candidate = Candidates(References(), record).Single();
            Reject(() => StartingUnitPlacement.PlaceValues(Map(), candidate, record, [], 0, 5, false));
            Reject(() => StartingUnitPlacement.PlaceValues(Map(), candidate, record, [(4, 5)], 4, 5, false));
            var terrain = new int[BattleMaps.Side * BattleMaps.Side];
            terrain[5 * BattleMaps.Side + 4] = BattleMaps.Border;
            Reject(() => StartingUnitPlacement.PlaceValues(Map() with { Terrain = terrain }, candidate, record, [], 4, 5, false));
            record = Placement.Move(record, 3, 2, 4, 5);
            Reject(() => StartingUnitPlacement.PlaceValues(Map(), candidate, record, [], 4, 5, false));
        });
        test("Starting placement lists shared army uses and requires shared-map acknowledgement", () =>
        {
            var references = References(sharedMap: true);
            var candidate = Candidates(references).Single();
            Check(candidate.ArmyBattles.SequenceEqual(new[] { 9, 10 }) && candidate.MapBattles.SequenceEqual(new[] { 9, 10 }), "Shared references missing");
            Check(candidate.Events.SequenceEqual(new[] { 12, 13 }), "Known army event references missing");
            Reject(() => StartingUnitPlacement.PlaceValues(Map(), candidate, EmptyRecord(), [], 4, 5, false));
            var values = StartingUnitPlacement.PlaceValues(Map(), candidate, EmptyRecord(), [], 4, 5, true);
            Check(values["g2x1"] == 4 && values.Count == 2, "Placement changes more than its coordinate pair");
            candidate = Candidates(References(sharedMap: false)).Single();
            Check(candidate.ArmyBattles.Count == 2 && candidate.MapBattles.Count == 1, "Shared army was confused with shared coordinates");
        });
        test("Starting placement refuses incomplete references and conflicting shared-map groups", () =>
        {
            var references = References([Appear(5, 2), new() { Command = ScriptParser.RunEvent, Args = [[new() { Command = ScriptParser.Variable, Variable = 1 }]] }]);
            Check(!Candidates(references).Single().CanPlace, "Dynamic event reference accepted");
            references = References(sharedMap: true, otherArmy: 6);
            Check(!Candidates(references).Single().CanPlace, "Shared group interpreted as a different army");
        });
        test("Starting placement follows literal opening event calls and records source identity", () =>
        {
            var events = new[]
            {
                new PlacementEventSource(12, 0, [Launch(9, "first.ar"), new() { Command = ScriptParser.RunEvent, Args = [[Number(14)]] }]),
                new PlacementEventSource(14, 1, [Appear(5, 2)])
            };
            var refs = BattlePlacementReferences.Analyze(events, [new(12, 9, 13, [])], _ => 0);
            var candidate = Candidates(refs).Single();
            Check(candidate.CanPlace && candidate.Assignment.OpeningEvent == 12 && candidate.Assignment.Event == 14, "Opening source identity lost");
        });
        test("Starting placement refuses unresolved shared consumers and global group reuse", () =>
        {
            var sources = new PlacementEventSource[]
            {
                new(12, 0, [Launch(9, "first.ar"), Appear(5, 2)]),
                new(13, 1, [Appear(5, 2)])
            };
            var metadata = new MapEventRecord[] { new(12, 9, 13, []), new(13, 10, 13, []) };
            Check(!Candidates(BattlePlacementReferences.Analyze(sources, metadata, _ => 0)).Single().CanPlace, "Unresolved second battle map accepted");
            sources[1] = new(13, 1, [Launch(10, "first.ar"), Appear(6, 3)]);
            Check(!Candidates(BattlePlacementReferences.Analyze(sources, metadata, _ => 0)).Single().CanPlace, "Missing shared-map group accepted");
            sources[1] = new(15, 1, [Appear(6, 2)]);
            metadata = [new(12, 9, 13, []), new(15, 0, 11, [])];
            Check(!Candidates(BattlePlacementReferences.Analyze(sources, metadata, _ => 0)).Single().CanPlace, "Global group reuse was ignored");
        });
        test("Starting placement refuses a stale candidate after a partial slot write", () =>
        {
            var record = EmptyRecord();
            var candidate = Candidates(References(), record).Single();
            record[65] = 7;
            Reject(() => StartingUnitPlacement.PlaceValues(Map(), candidate, record, [], 4, 5, false));
        });
        test("A proven absent debug map does not disable an unrelated battle", () =>
        {
            var refs = BattlePlacementReferences.Analyze(
            [
                new(12, 0, [Launch(9, "first.ar"), Appear(5, 2)]),
                new(13, 1, [Launch(9999, "maptest.ar"), Appear(6, 2)])
            ], [new(12, 9, 13, []), new(13, 9999, 13, [])], file => file == "first.ar" ? 0 : -1);
            Check(Candidates(refs).Single().CanPlace, "An absent debug archive was treated as an unresolved real map");
        });
        test("Starting placement integration preserves undo, persistence and export", RunIntegration);
    }

    internal static ScriptNode Number(int value) => new() { Command = ScriptParser.Number, Value = value };
    internal static ScriptNode Text(string value) => new() { Command = ScriptParser.TextValue, Text = value };
    internal static ScriptNode Appear(int army, int group) => new() { Command = 0x32C, Args = [[Text($"ud{army:000}")], [Number(1)], [Number(group)]] };
    internal static ScriptNode Launch(int battle, string file) => new() { Command = 0x2DE, Args = [[Number(battle)], [Text(file)]] };
    internal static ScriptNode LaunchWithMode(int mode, int battle, string file) => new() { Command = 0x2DF, Args = [[Number(mode)], [Number(battle)], [Text(file)]] };
    internal static BattleMap Map() => new(9, "first.ar", 12, 12, new int[BattleMaps.Side * BattleMaps.Side], []) { Top = 0 };
    internal static byte[] EmptyRecord() => Enumerable.Repeat((byte)255, Placement.RecordLength).ToArray();
    private static IReadOnlyList<StartingUnitCandidate> Candidates(BattlePlacementReferences references, byte[]? record = null) =>
        StartingUnitPlacement.Candidates(Map(), references, [new(5, "Army", [], 0, [new(100, 5, 0, 1, "Unit", 0, 180)])], record ?? EmptyRecord());
    private static BattlePlacementReferences References(ScriptNode[]? opening = null, bool sharedMap = false, int otherArmy = 5) =>
        BattlePlacementReferences.Analyze(
        [
            new(12, 0, [Launch(9, "first.ar"), .. opening ?? [Appear(5, 2)]]),
            new(13, 1, [Launch(10, sharedMap ? "first.ar" : "second.ar"), Appear(otherArmy, 2)])
        ], [new(12, 9, 13, []), new(13, 10, 13, [])], file => file == "first.ar" ? 0 : 1);

    private static void RunIntegration()
    {
        string folder = Path.Combine(Path.GetTempPath(), "berwick-placement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // Existing session record readers keep source handles for their lifetime; isolate cleanup after process exit.
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(UnitPlacementTests).Assembly.Location);
            start.ArgumentList.Add("--unit-placement-integration");
            start.ArgumentList.Add(folder);
            using var process = Process.Start(start) ?? throw new Exception("Could not start integration fixture");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Placement integration fixture timed out");
            }
            Check(process.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        }
        finally
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(folder).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing cleanup outside the synthetic fixture directory");
            Directory.Delete(folder, recursive: true);
        }
    }
    internal static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
    }
    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (EditException)
        {
            return;
        }
        throw new Exception("Unsafe placement accepted");
    }
}
