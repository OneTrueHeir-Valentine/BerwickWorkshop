using Berwick.Core;

public static class R0ProficiencyModesTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action)
    {
        try { action(); } catch (EditException) { return; }
        throw new Exception("Invalid proficiency profile was accepted.");
    }
    public static void Run(Action<string, Action> test)
    {
        test("R0 starting proficiency variance preserves tenths and field independence", () =>
        {
            string[] fields = ["spearLevel", "smallshieldLevel", "bowLevel"];
            int[] original = [233, 180, 0], min = [0, 0, 0], max = [400, 250, 300];
            int[] a = R0ProficiencyMath.Bounded(189, "characters", 88, fields, original, min, max, 5, 10);
            int[] b = R0ProficiencyMath.Bounded(189, "characters", 88, fields, original, min, max, 5, 10);
            Check(a.SequenceEqual(b), "Same seed produced different proficiency changes.");
            for (int i = 0; i < a.Length; i++)
            {
                Check(Math.Abs(a[i] - original[i]) <= 50, "Proficiency change exceeds five points.");
                Check((a[i] - original[i]) % 10 == 0, "Fractional tenths were overwritten.");
            }
            int[] reverse = R0ProficiencyMath.Bounded(189, "characters", 88,
                ["bowLevel", "spearLevel", "smallshieldLevel"], [0, 233, 180],
                [0, 0, 0], [300, 400, 250], 5, 10);
            Check(reverse[1] == a[0] && reverse[2] == a[1] && reverse[0] == a[2],
                "Selecting fields in another order changed independent values.");
        });
        test("R0 proficiency budget conserves selected stored points per character", () =>
        {
            var keys = new[] { "spearLevel", "swordLevel", "smallshieldLevel" };
            int[] original = [300, 150, 220], min = [0, 0, 0], max = [400, 400, 400];
            int[] a = R0ProficiencyMath.Redistribute(454, "characters", 87, keys,
                original, min, max, 5, 120, 10);
            int[] b = R0ProficiencyMath.Redistribute(454, "characters", 87, keys,
                original, min, max, 5, 120, 10);
            Check(a.SequenceEqual(b), "Budget changes are not deterministic.");
            Check(a.Sum() == original.Sum(), "Starting proficiency budget changed.");
            Check(a.Select((x, i) => Math.Abs(x - original[i])).All(x => x <= 50),
                "Starting proficiency budget exceeded field maximum change.");
            int[] growths = R0ProficiencyMath.Redistribute(454, "growths", 88,
                ["spearGrowth", "bowGrowth", "shieldGrowth"], [8, 3, 5],
                [0, 0, 0], [15, 15, 15], 3, 100, 1);
            Check(growths.Sum() == 16, "Growth channel budget changed.");
            Check(growths.All(x => x is >= 0 and <= 15), "4-bit growth bounds failed.");
            Check(growths.Select((x, i) => Math.Abs(x - new[] { 8, 3, 5 }[i])).All(x => x <= 3),
                "Growth field exceeded configured delta.");
        });
        test("R0 proficiency profiles validate target-specific fields and independent policies", () =>
        {
            var start = R0ProficiencyProfile.StartingVariance(987) with { UnitIds = [87, 88] };
            var growth = R0ProficiencyProfile.GrowthBudget(987) with { UnitIds = [87, 88] };
            var a = R0ProficiencyProfileFiles.FromJson(R0ProficiencyProfileFiles.ToJson(start));
            var b = R0ProficiencyProfileFiles.FromJson(R0ProficiencyProfileFiles.ToJson(growth));
            Check(a.Target == R0ProficiencyTarget.StartingProficiencies
                && b.Target == R0ProficiencyTarget.ProficiencyGrowths, "Profile target was lost.");
            Check(a.Mode == R0StatAlgorithm.BoundedVariance && b.Mode == R0StatAlgorithm.BudgetRedistribution,
                "Stat algorithms were merged.");
            Check(a.Compatibility == R0ProficiencyCompatibility.PreserveTrainedChannels,
                "Safe policy did not round trip.");
            Check(R0ProficiencyProfile.GrowthKeys.Contains("shieldGrowth") &&
                !R0ProficiencyProfile.GrowthKeys.Contains("smallshieldGrowth"),
                "Combined shield growth was split into nonexistent channels.");
            Reject(() => (start with { Fields = ["shieldGrowth"] }).Validate());
            Reject(() => (growth with { Fields = ["smallshieldLevel"] }).Validate());
            Reject(() => (growth with { Fields = ["spearGrowth", "spearGrowth"] }).Validate());
            Reject(() => (growth with { Fields = ["spearGrowth"] }).Validate());
            Reject(() => (start with { Compatibility = (R0ProficiencyCompatibility)99 }).Validate());
            Reject(() => (start with { UnitIds = [87, 87] }).Validate());
            Reject(() => R0ProficiencyProfileFiles.FromJson(R0ProficiencyProfileFiles.ToJson(start)
                .Replace("fnv1a64-splitmix64-v1", "invalid-algorithm", StringComparison.Ordinal)));
        });
        test("R0 proficiency profiles never overwrite existing profiles", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "bw-" + Guid.NewGuid().ToString("N") + ".bwr0proficiency.json");
            try
            {
                R0ProficiencyProfileFiles.SaveNew(path, R0ProficiencyProfile.StartingVariance());
                Check(R0ProficiencyProfileFiles.Load(path).Target == R0ProficiencyTarget.StartingProficiencies,
                    "Saved file could not be read.");
                bool refused = false;
                try { R0ProficiencyProfileFiles.SaveNew(path, R0ProficiencyProfile.GrowthVariance()); }
                catch (IOException) { refused = true; }
                Check(refused, "Existing profile was overwritten.");
                Check(R0ProficiencyProfileFiles.Load(path).Target == R0ProficiencyTarget.StartingProficiencies,
                    "An existing profile was changed during refusal.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        test("R0 proficiency field keys and cap mappings exist in original source schemas", () =>
        {
            var starting = CharacterFields.Record.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var growths = CharacterFields.Growth.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            var caps = ClassFields.Fields.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
            Check(R0ProficiencyProfile.StartingKeys.All(starting.Contains), "Invalid starting field binding.");
            Check(R0ProficiencyProfile.GrowthKeys.All(growths.Contains), "Invalid growth field binding.");
            Check(R0ProficiencyProfile.StartingKeys.All(k => caps.Contains(k[..^5] + "Cap")),
                "Starting proficiency lacks a matching cap field.");
            Check(R0ProficiencyProfile.GrowthKeys.Where(k => k != "shieldGrowth")
                .All(k => caps.Contains(k[..^6] + "Cap")), "Growth proficiency lacks a matching cap.");
            Check(new[] { "smallshieldCap", "mediumshieldCap", "largeshieldCap" }.All(caps.Contains),
                "Combined growth has no shield cap mapping.");
        });
    }
}
