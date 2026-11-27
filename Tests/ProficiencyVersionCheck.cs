using Berwick.Core;

/// <summary>
/// Real-disc check of the version 2 weapon-level profiles: version 1 previews are unchanged and refused at apply when they
/// zero a trained level; version 2 never does, keeps budget totals per character, and changes nothing a version 1 draw
/// left above the floor. Run with --proficiency-versions &lt;original iso&gt;. Nothing is written to any disc image.
/// </summary>
public static class ProficiencyVersionCheck
{
    public static int Run(string iso)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        bool ok = true;
        void Check(bool condition, string what) { Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"); ok &= condition; }
        var session = new EditSession(disc);
        var names = disc.Characters.ToDictionary(c => c.Id, c => c.Name);
        int[] seeds = [.. Enumerable.Range(1, 60), 303, 4242];
        var makers = new (string Name, Func<int, R0ProficiencyProfile> Make, int Step)[]
        {
            ("starting variance", R0ProficiencyProfile.StartingVariance, 10),
            ("starting budget", R0ProficiencyProfile.StartingBudget, 10),
            ("growth variance", R0ProficiencyProfile.GrowthVariance, 1),
            ("growth budget", R0ProficiencyProfile.GrowthBudget, 1),
        };
        Console.WriteLine($"{disc.Edition}: {seeds.Length} seeds x {makers.Length} profiles");
        foreach (var (name, make, step) in makers)
        {
            int legacyZeroed = 0, keptZeroed = 0, conserved = 0, sameDraws = 0, differentDraws = 0, budgetUnits = 0;
            foreach (int seed in seeds)
            {
                var legacy = make(seed);
                var kept = legacy.Migrated();
                var a = R0ProficiencyRandomizer.Preview(session, legacy);
                var b = R0ProficiencyRandomizer.Preview(session, kept);
                Check(R0ProficiencyRandomizer.Preview(session, legacy).Changes.SequenceEqual(a.Changes) &&
                      R0ProficiencyRandomizer.Preview(session, kept).Changes.SequenceEqual(b.Changes), $"{name} {seed}: replays") ;
                legacyZeroed += R0ProficiencyRandomizer.ZeroedTrainedChannels(a).Count;
                keptZeroed += R0ProficiencyRandomizer.ZeroedTrainedChannels(b).Count;
                if (legacy.Mode == R0StatAlgorithm.BudgetRedistribution)
                    foreach (var unit in b.Changes.GroupBy(c => c.UnitId))
                    {
                        budgetUnits++;
                        if (unit.Sum(c => c.AfterRaw - c.BeforeRaw) == 0) conserved++;
                    }
                else
                {
                    var byKey = b.Changes.ToDictionary(c => (c.UnitId, c.Field));
                    foreach (var c in a.Changes)
                    {
                        if (c.AfterRaw < R0ProficiencyMath.Floor(2, 1, c.BeforeRaw, 0, int.MaxValue, step)) continue;
                        if (byKey.TryGetValue((c.UnitId, c.Field), out var d) && d == c) sameDraws++; else differentDraws++;
                    }
                }
                if (seed == 303 && name == "starting variance")
                {
                    foreach (var c in R0ProficiencyRandomizer.ZeroedTrainedChannels(a))
                    {
                        var v2 = b.Changes.FirstOrDefault(x => x.UnitId == c.UnitId && x.Field == c.Field);
                        Console.WriteLine($"    seed 303 version 1: {c.UnitName} {c.Field} {c.BeforeRaw} -> 0; version 2: " +
                                          (v2 is null ? $"unchanged at {c.BeforeRaw}" : $"{v2.BeforeRaw} -> {v2.AfterRaw}"));
                    }
                    bool refused;
                    try { R0ProficiencyRandomizer.Apply(session, a); refused = false; }
                    catch (EditException e) { refused = e.Message.Contains("version 2", StringComparison.Ordinal); }
                    Check(refused && !session.CanUndo, "seed 303: the version 1 preview that zeroes a trained level is refused unless confirmed");
                    Check(R0ProficiencyRandomizer.Apply(session, a, allowZeroedTrainedLevels: true), "seed 303: confirmed, the version 1 preview applies exactly as previewed");
                    session.Undo();
                    Check(R0ProficiencyRandomizer.Apply(session, b) && session.CanUndo, "seed 303: the version 2 preview applies");
                    bool sameAsPreview = b.FieldEdits.All(e => session.Value(e.Table, e.Id, e.Field) == e.Value);
                    session.Undo();
                    Check(sameAsPreview && !session.CanUndo && session.ChangeCount == 0, "seed 303: applied as previewed and one undo restores the original");
                }
            }
            Console.WriteLine($"  {name}: version 1 zeroed {legacyZeroed} trained levels, version 2 zeroed {keptZeroed}" +
                (make(1).Mode == R0StatAlgorithm.BudgetRedistribution
                    ? $"; budget kept exactly for {conserved} of {budgetUnits} changed characters"
                    : $"; {sameDraws} unaffected version 1 draws identical in version 2, {differentDraws} different"));
            Check(keptZeroed == 0, $"{name}: version 2 never lowers a trained level to zero");
            if (make(1).Mode == R0StatAlgorithm.BudgetRedistribution) Check(conserved == budgetUnits, $"{name}: every budget total is exact");
            else Check(differentDraws == 0, $"{name}: draws the floor did not touch are unchanged");
        }
        Console.WriteLine(ok ? "All checks passed." : "Some checks failed.");
        return ok ? 0 : 1;
    }
}
