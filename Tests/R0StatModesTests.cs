using Berwick.Core;

public static class R0StatModesTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action test)
    {
        try { test(); } catch (EditException) { return; }
        throw new Exception("Expected invalid R0 profile/operation to fail closed.");
    }
    public static void Run(Action<string, Action> test)
    {
        test("R0 bounded mode is deterministic, bounded, and field-isolated", () =>
        {
            string[] fields = ["hpGrowth", "strengthGrowth", "defenseGrowth", "agilityGrowth", "mindGrowth"];
            int[] before = [30, 40, 50, 20, 10], mins = [0, 0, 0, 0, 0], maxs = [127, 127, 127, 127, 127];
            var a = R0StatMath.Bounded(177, "growths", 88, fields, before, mins, maxs, 7);
            var b = R0StatMath.Bounded(177, "growths", 88, fields, before, mins, maxs, 7);
            Check(a.SequenceEqual(b), "Bounded randomization was not repeatable.");
            Check(a.Select((v, i) => Math.Abs(v - before[i])).All(d => d <= 7), "A field exceeded maximum change.");
            var reversed = new[] { fields[4], fields[0], fields[2], fields[1], fields[3] };
            var reordered = R0StatMath.Bounded(177, "growths", 88, reversed,
                [before[4], before[0], before[2], before[1], before[3]], mins, maxs, 7);
            Check(a[0] == reordered[1] && a[1] == reordered[3] && a[4] == reordered[0], "Field order changed an independent draw.");
            var atLimit = R0StatMath.Bounded(177, "characters", 88, ["strengthBonus"], [15], [-16], [15], 7);
            Check(atLimit[0] is >= -16 and <= 15, "Signed personal bonus exceeded packed limits.");
        });
        test("R0 budget mode conserves exact per-unit sum without exceeding field bounds", () =>
        {
            string[] fields = ["hpGrowth", "strengthGrowth", "defenseGrowth", "agilityGrowth", "mindGrowth"];
            int[] original = [23, 45, 72, 18, 9], min = [0, 0, 0, 0, 0], max = [127, 127, 127, 127, 127];
            var a = R0StatMath.Redistribute(567, "growths", 87, fields, original, min, max, 12, 80);
            var b = R0StatMath.Redistribute(567, "growths", 87, fields, original, min, max, 12, 80);
            Check(a.SequenceEqual(b), "Budget redistribution is not repeatable.");
            Check(a.Sum() == original.Sum(), "The stat budget was not conserved.");
            Check(a.Select((v, i) => Math.Abs(v - original[i])).All(d => d <= 12), "Budget field exceeded maximum deviation.");
            Check(a.All(v => v is >= 0 and <= 127), "Budget field exceeded its legal bounds.");
            var saturated = R0StatMath.Redistribute(1, "growths", 87, fields,
                [0, 0, 0, 0, 0], min, max, 12, 80);
            Check(saturated.All(v => v == 0), "Budget routine created points that did not exist.");
        });
        test("R0 v1 profiles have separate JSON round trips and cannot silently change algorithms", () =>
        {
            var variance = R0StatProfile.VariancePreset(345) with { UnitIds = [87, 88], MaximumChange = 5 };
            var budget = R0StatProfile.BudgetPreset(345) with { UnitIds = [87, 88], MaximumChange = 15 };
            var v = R0StatProfileFiles.FromJson(R0StatProfileFiles.ToJson(variance));
            var b = R0StatProfileFiles.FromJson(R0StatProfileFiles.ToJson(budget));
            Check(v.Mode == R0StatAlgorithm.BoundedVariance && b.Mode == R0StatAlgorithm.BudgetRedistribution,
                "Independent saved modes were collapsed.");
            Check(v.UnitIds.SequenceEqual([87, 88]) && b.UnitIds.SequenceEqual([87, 88]), "Profile unit selection was lost.");
            Reject(() => R0StatProfileFiles.FromJson(R0StatProfileFiles.ToJson(variance)
                .Replace("fnv1a64-splitmix64-v1", "unrecognized-rng-v2", StringComparison.Ordinal)));
            Reject(() => (budget with { Fields = ["hpGrowth", "nonexistent"] }).Validate());
            Reject(() => (variance with { UnitIds = [87, 87] }).Validate());
            Reject(() => (budget with { Fields = ["hpGrowth"] }).Validate());
        });
        test("R0 saved profiles use new files and preserve unrelated existing files", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), "r0-" + Guid.NewGuid().ToString("N") + ".bwr0stats.json");
            try
            {
                R0StatProfileFiles.SaveNew(file, R0StatProfile.VariancePreset());
                var loaded = R0StatProfileFiles.Load(file);
                Check(loaded.Mode == R0StatAlgorithm.BoundedVariance, "Saved profile readback failed.");
                try { R0StatProfileFiles.SaveNew(file, R0StatProfile.BudgetPreset()); }
                catch (IOException)
                {
                    Check(R0StatProfileFiles.Load(file).Mode == R0StatAlgorithm.BoundedVariance,
                        "Existing profile was changed by a different mode.");
                    return;
                }
                throw new Exception("Existing profile was overwritten unexpectedly.");
            }
            finally { if (File.Exists(file)) File.Delete(file); }
        });
    }
}
