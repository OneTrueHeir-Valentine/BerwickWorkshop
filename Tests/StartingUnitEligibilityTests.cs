using Berwick.Core;
using static UnitPlacementTests;

internal static class StartingUnitEligibilityTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Vacant army coordinates do not establish a starting unit", () =>
        {
            var candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord()).Single();
            Check(!candidate.CanPlace, "An unverified vacant source slot was offered as a starting unit");
            candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord(), EmptyRecord(), verifiedOriginalDisc: true).Single();
            Check(!candidate.CanPlace && candidate.PlacementProblem is not null, "Vacant source coordinates were treated as a starting-unit guarantee");
        });
        test("Modified source discs never prove a starting position", () =>
        {
            var source = SourceRecord();
            var removed = EmptyRecord();
            var candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], removed, source,
                verifiedOriginalDisc: false).Single();
            Check(!candidate.CanPlace && candidate.PlacementProblem!.Contains("original disc"),
                "A modified source disc authorized restoration");
            var occupied = StartingUnitPlacement.Candidates(Map(), References(), [Army()], source, source,
                verifiedOriginalDisc: false).Single();
            Check(occupied.CanRemove && !occupied.CanPlace,
                "Source provenance incorrectly disabled removal");
            var retail = StartingUnitPlacement.Candidates(Map(), References(), [Army()], removed, source,
                verifiedOriginalDisc: true).Single();
            Check(retail.CanPlace, "A supported original source could not restore its own position");
        });
        test("Vacant source slots stay unverified after coordinates are written", () =>
        {
            var current = Placement.Move(EmptyRecord(), 2, 1, 4, 5);
            var candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], current, EmptyRecord(), verifiedOriginalDisc: true).Single();
            Check(!candidate.CanPlace && candidate.CanRemove, "An unsupported addition could not be removed");
            Check(candidate.PlacementProblem is not null, "The source-slot limitation was lost");
        });
        test("Starting records after gaps remain distinct from unused duplicate names", () =>
        {
            var units = Enumerable.Range(0, 3).Select(i => new ArmyUnit(5000 + i, 5, i, 375, "Deserter", i * 256, i * 256 + 180)).ToArray();
            var army = new Army(5, "Army", [], 768, units);
            var source = Placement.Move(Placement.Move(EmptyRecord(), 2, 1, 2, 3), 2, 3, 4, 5);
            var candidates = StartingUnitPlacement.Candidates(Map(), References(), [army], EmptyRecord(), source, verifiedOriginalDisc: true);
            Check(candidates[0].CanPlace && !candidates[1].CanPlace && candidates[2].CanPlace,
                "An empty slot truncated the group or conferred eligibility on a different record");
            Check(candidates[1].Unit.Index == 1 && candidates[2].Slot == 3, "Duplicate names lost record identity");
        });
        test("Incomplete source positions cannot authorize restoration", () =>
        {
            foreach (var source in new[] { new byte[3], EmptyRecord(), Placement.Move(EmptyRecord(), 2, 1, 11, 5) })
            {
                var candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord(), source, verifiedOriginalDisc: true).Single();
                Check(!candidate.CanPlace && candidate.PlacementProblem is not null, "Unsupported source position accepted");
            }
            var partial = EmptyRecord();
            partial[65] = 4;
            Check(!StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord(), partial, verifiedOriginalDisc: true).Single().CanPlace,
                "A partial source coordinate authorized placement");
        });
        test("Unsupported opening parameters refuse placement without disabling removal", () =>
        {
            var valid = Appear(5, 2);
            foreach (var command in new[]
            {
                new ScriptNode { Command = 0x32C, Args = valid.Args.Take(3).ToList() },
                new ScriptNode { Command = 0x32C, Args = [.. valid.Args, new List<ScriptNode> { Number(0) }] },
                new ScriptNode { Command = 0x32C, Args = [.. valid.Args.Take(3), [new() { Command = ScriptParser.Variable, Variable = 1 }], [Number(0)]] },
                new ScriptNode { Command = 0x32C, Args = [.. valid.Args.Take(4), [Number(2)]] }
            })
            {
                var references = References(command);
                var candidate = StartingUnitPlacement.Candidates(Map(), references, [Army()], EmptyRecord(), SourceRecord(), verifiedOriginalDisc: true).Single();
                Check(!candidate.CanPlace && candidate.PlacementProblem is not null, "Unknown loading parameters were ignored");
                candidate = StartingUnitPlacement.Candidates(Map(), references, [Army()], SourceRecord(), SourceRecord(), verifiedOriginalDisc: true).Single();
                Check(candidate.CanRemove, "The loading restriction disabled placement removal");
            }
        });
        test("Shared maps retain loading restrictions from every consumer", () =>
        {
            var unsupported = new ScriptNode { Command = 0x32C, Args = Appear(5, 2).Args.Take(3).ToList() };
            var references = BattlePlacementReferences.Analyze(
                [new(12, 0, [Launch(9, "first.ar"), Appear(5, 2)]), new(13, 1, [Launch(10, "first.ar"), unsupported])],
                [new(12, 9, 13, []), new(13, 10, 13, [])], _ => 0);
            var candidate = StartingUnitPlacement.Candidates(Map(), references, [Army()], EmptyRecord(), SourceRecord(), verifiedOriginalDisc: true).Single();
            Check(!candidate.CanPlace && candidate.PlacementProblem is not null, "A supported consumer masked an unsupported shared-map launch");
        });
        test("A removed starting position can only return to its original hex", () =>
        {
            var candidate = StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord(), SourceRecord(), verifiedOriginalDisc: true).Single();
            Check(candidate.CanPlace && candidate.OriginalPosition == (1, 1), "The original hex was not identified");
            bool refused = false;
            try { StartingUnitPlacement.PlaceValues(Map(), candidate, EmptyRecord(), [], 4, 5, false); }
            catch (EditException error) { refused = error.Message.Contains("original hex"); }
            Check(refused, "Restoration accepted a hex other than the original");
            var values = StartingUnitPlacement.PlaceValues(Map(), candidate, EmptyRecord(), [], 1, 1, false);
            Check(values["g2x1"] == 1 && values["g2y1"] == 1, "Restoration did not use the original hex");
            var unverified = StartingUnitPlacement.Candidates(Map(), References(), [Army()], EmptyRecord(), SourceRecord(), verifiedOriginalDisc: false).Single();
            Check(unverified.OriginalPosition is null, "An unverified disc supplied an original hex");
        });
        test("Only an exact original image counts as a verified original disc", () =>
        {
            const string hash = "aa";
            var profile = new GameProfile("Synthetic", hash, 0, 0);
            var exact = new InspectionResult("x.iso", 1, "", "", "", hash, "", profile, [], [], 0);
            Check(exact.IsVerifiedOriginal, "An exact original image was not accepted");
            Check(!(exact with { IsModified = true }).IsVerifiedOriginal, "A modified image was accepted");
            Check(!(exact with { Profile = null }).IsVerifiedOriginal, "An unrecognized image was accepted");
            Check(!(exact with { Sha256 = "bb" }).IsVerifiedOriginal, "An image with a different hash was accepted");
        });
    }

    private static Army Army() => new(5, "Army", [], 256, [new(5000, 5, 0, 375, "Deserter", 0, 180)]);
    private static BattlePlacementReferences References(ScriptNode? appear = null) => BattlePlacementReferences.Analyze(
        [new(12, 0, [Launch(9, "first.ar"), appear ?? Appear(5, 2)])], [new(12, 9, 13, [])], _ => 0);
}
