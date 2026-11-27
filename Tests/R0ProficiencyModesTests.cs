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
        test("R0 proficiency version 1 files stay the same and version 2 is an explicit conversion", () =>
        {
            var legacy = R0ProficiencyProfile.StartingBudget(303);
            string json = R0ProficiencyProfileFiles.ToJson(legacy);
            Check(!json.Contains("rainedMinimum", StringComparison.Ordinal), "A version 1 file gained a new field.");
            Check(R0ProficiencyProfileFiles.ToJson(R0ProficiencyProfileFiles.FromJson(json)) == json,
                "A version 1 file did not round trip byte for byte.");
            var kept = legacy.Migrated();
            Check(kept.SchemaVersion == 2 && kept.TrainedMinimum == 1 && kept.Seed == 303 && kept.Mode == legacy.Mode,
                "Conversion changed more than the version and the trained minimum.");
            kept.Validate();
            var back = R0ProficiencyProfileFiles.FromJson(R0ProficiencyProfileFiles.ToJson(kept with { TrainedMinimum = 3 }));
            Check(back.SchemaVersion == 2 && back.TrainedMinimum == 3, "Version 2 settings did not round trip.");
            Check(kept.Migrated() == kept, "Converting twice changed the profile.");
            Reject(() => (legacy with { TrainedMinimum = 1 }).Validate());
            Reject(() => (kept with { TrainedMinimum = 0 }).Validate());
            Reject(() => (kept with { TrainedMinimum = 16 }).Validate());
            Reject(() => (legacy with { SchemaVersion = 3 }).Validate());
        });
        test("R0 proficiency version 2 floor keeps trained levels within class limits and leaves version 1 alone", () =>
        {
            Check(R0ProficiencyMath.Floor(1, 0, 40, 0, 400, 10) == 0, "Version 1 floor changed.");
            Check(R0ProficiencyMath.Floor(2, 1, 40, 0, 400, 10) == 10, "Trained level floor is not one point.");
            Check(R0ProficiencyMath.Floor(2, 3, 40, 0, 400, 10) == 30, "Higher minimum was ignored.");
            Check(R0ProficiencyMath.Floor(2, 3, 15, 0, 400, 10) == 15, "A level below the minimum was raised or lowered.");
            Check(R0ProficiencyMath.Floor(2, 1, 5, 0, 400, 10) == 5, "Half a point of training was lowered to zero.");
            Check(R0ProficiencyMath.Floor(2, 3, 40, 0, 20, 10) == 20, "Floor went above the class limit.");
            Check(R0ProficiencyMath.Floor(2, 1, 0, 0, 400, 10) == 0, "An untrained channel was given a floor.");
            Check(R0ProficiencyMath.Floor(2, 2, 6, 0, 15, 1) == 2, "Growth floor does not use growth steps.");
        });
        test("R0 proficiency version 2 variance never zeroes training and keeps every unaffected draw", () =>
        {
            string[] keys = ["axeLevel", "spearLevel", "swordLevel", "bowLevel"];
            int[] original = [40, 10, 5, 25], max = [400, 400, 400, 30];
            int[] legacyMin = [0, 0, 0, 0];
            int[] floor = original.Select((o, i) => R0ProficiencyMath.Floor(2, 1, o, 0, max[i], 10)).ToArray();
            int legacyZeroes = 0;
            for (int seed = 0; seed < 1000; seed++)
                for (int unit = 80; unit < 84; unit++)
                {
                    int[] v1 = R0ProficiencyMath.Bounded(seed, "characters", unit, keys, original, legacyMin, max, 5, 10);
                    int[] v2 = R0ProficiencyMath.Bounded(seed, "characters", unit, keys, original, floor, max, 5, 10);
                    legacyZeroes += v1.Count(x => x == 0);
                    for (int i = 0; i < keys.Length; i++)
                    {
                        Check(v2[i] >= floor[i] && v2[i] <= max[i], $"Seed {seed}: version 2 left its bounds.");
                        Check((v2[i] - original[i]) % 10 == 0, $"Seed {seed}: fractional tenths changed.");
                        if (v1[i] >= floor[i]) Check(v1[i] == v2[i], $"Seed {seed}: an unaffected draw changed.");
                    }
                }
            Check(legacyZeroes > 0, "The sweep never reached the version 1 problem, so it proves nothing.");
        });
        test("R0 proficiency version 2 budget conserves the exact total inside trained floors and caps", () =>
        {
            string[] keys = ["axeLevel", "knifeLevel", "spearLevel", "swordLevel", "bowLevel"];
            int[] original = [40, 10, 5, 0, 25], max = [400, 400, 400, 400, 30];
            foreach (int minimum in new[] { 1, 2, 4 })
            {
                int[] floor = original.Select((o, i) => R0ProficiencyMath.Floor(2, minimum, o, 0, max[i], 10)).ToArray();
                for (int seed = 0; seed < 1000; seed++)
                {
                    int[] next = R0ProficiencyMath.Redistribute(seed, "characters", 87, keys, original, floor, max, 5, 120, 10);
                    Check(next.Sum() == original.Sum(), $"Seed {seed}: the budget total changed.");
                    for (int i = 0; i < keys.Length; i++)
                    {
                        Check(next[i] >= floor[i] && next[i] <= max[i], $"Seed {seed}: a channel left its bounds.");
                        Check((next[i] - original[i]) % 10 == 0, $"Seed {seed}: fractional tenths changed.");
                        if (original[i] > 0) Check(next[i] > 0, $"Seed {seed}: a trained level became zero.");
                    }
                }
            }
            int[] locked = [10, 10];
            int[] same = R0ProficiencyMath.Redistribute(5, "characters", 87, ["spearLevel", "swordLevel"], locked,
                locked, [400, 400], 5, 120, 10);
            Check(same.SequenceEqual(locked), "Channels with no room to move were changed.");
        });
        test("R0 proficiency apply names every trained level a version 1 preview would zero", () =>
        {
            var profile = R0ProficiencyProfile.StartingBudget(303);
            var changes = new List<R0ProficiencyChange>
            {
                new(12, "Adel", "characters", "spearLevel", 40, 0),
                new(12, "Adel", "characters", "swordLevel", 100, 140),
                new(20, "Reese", "characters", "bowLevel", 0, 10),
            };
            var preview = new R0ProficiencyPreview(1, R0SeedStreams.Algorithm, "", 0, profile, [], [], changes, []);
            var zeroed = R0ProficiencyRandomizer.ZeroedTrainedChannels(preview);
            Check(zeroed.Count == 1 && zeroed[0].Field == "spearLevel", "Zeroed trained channels were not identified.");
        });
    }
}
