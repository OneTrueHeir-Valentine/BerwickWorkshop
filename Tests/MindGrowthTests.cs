using System.Security.Cryptography;
using System.Text;
using Berwick.Core;

/// <summary>
/// Personal Mind growth is stored at twice the rate the game uses. These tests cover the conversion,
/// the editor field, records using the unsupported top bit, stat profile versions and exports.
/// </summary>
public static class MindGrowthTests
{
    private static readonly string[] GrowthKeys = ["hpGrowth", "strengthGrowth", "defenseGrowth", "agilityGrowth", "mindGrowth"];
    private static readonly string[] BonusKeys = ["hpBonus", "strengthBonus", "defenseBonus", "agilityBonus", "mindBonus"];

    // Published Mind growth rates for the starting roster, by character ID. Both editions agree.
    private static readonly Dictionary<int, int> PublishedRates = new()
    {
        [0x53] = 0, [0x54] = 0, [0x55] = 0, [0x7C] = 0, [0x57] = 0, [0x58] = 0, [0x59] = 0, [0x83] = 15, [0x82] = 0,
        [0x7F] = 15, [0x61] = 0, [0x84] = 20, [0x56] = 0, [0x80] = 3, [0x86] = 3, [0x60] = 0, [0x95] = 0, [0x6D] = 0,
        [0x81] = 3, [0x7E] = 0, [0x71] = 12, [0x99] = 0, [0x5A] = 0, [0x6F] = 0, [0x90] = 0, [0x6E] = 0, [0x98] = 0,
        [0x93] = 0, [0x85] = 0, [0x96] = 9, [0x62] = 0, [0x74] = 0, [0x87] = 20, [0x7D] = 20, [0x63] = 6,
    };

    // Version 1 profiles must keep producing exactly what they produced before the Mind correction.
    // Each value is the number of field edits and a digest of the edits, expected values and changes.
    private static readonly Dictionary<string, (int Edits, string Digest)> FixtureReplay = new()
    {
        ["variance-growth-1729"] = (3, "29531941957c54bf470828ca56bd855c0635c3171dd4baf2f58781610dbfffe7"),
        ["budget-growth-1729"] = (0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        ["variance-hp-mind-7"] = (1, "8936a34a68f2388e36c6e5a07b856bd42795115a26d12ec62dd7f11e8c26eed9"),
        ["budget-hp-mind-12345"] = (0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        ["variance-bonus-1729"] = (3, "8d01ae2232c4d78e56cc9aaae8a18b1fe8d79c7901c9b2aefdb23145d477b73c"),
        ["budget-bonus-99"] = (2, "fd8c5a0728e33227920c2e3cd636d7aeb9348613ae4bbaa47850b2b14e141b40"),
    };

    // The two editions have the same starting records, so their version 1 results are the same.
    private static readonly Dictionary<string, (int Edits, string Digest)> DiscReplay = new()
    {
        ["variance-growth-1729"] = (144, "73729784a432f70c0ec7ef63d259edc0b171c53f4206e118c4a5ffafee1f687d"),
        ["budget-growth-1729"] = (163, "21a6c07f4d21dcddda91d15eeff987de9a6567518fdf43ea1fa71959223abc89"),
        ["variance-hp-mind-7"] = (57, "05504bc3e7ac45e231b4e6e6e8e3ab8ac5a5cbe94b22c6d4ab6b4d0e2e44a973"),
        ["budget-hp-mind-12345"] = (64, "a3193c703f29a59ec8fe694c6e0f0687b4ddf2be49466484c6a67d8f3d113afd"),
        ["variance-bonus-1729"] = (150, "f167b9e49da5dc83de56b8fd57a5f959f640a37c33fc32c2cbbe97cda4476bf1"),
        ["budget-bonus-99"] = (153, "484ea9d2207ee807aaef58947b34760518f0b1c0a6b2fab36022bf0489ee02aa"),
    };

    public static void Run(Action<string, Action> test)
    {
        test("Mind growth: stored values convert to the rate the game uses", () =>
        {
            int[] stored = [0, 1, 6, 29, 30, 31, 40, 126, 127];
            int[] rate = [0, 0, 3, 14, 15, 15, 20, 63, 63];
            Check(stored.Select(MindGrowth.Percent).SequenceEqual(rate), "Stored Mind growth converted incorrectly.");
        });

        test("Mind growth: new rates store twice the value and keep an unchanged odd value", () =>
        {
            Check(MindGrowth.Stored(0, 30) == 0 && MindGrowth.Stored(63, 30) == 126, "Rates 0 and 63 were stored incorrectly.");
            Check(MindGrowth.Stored(20, 30) == 40, "A 5-point increase from 15% should store 40.");
            Check(MindGrowth.Stored(15, 31) == 31 && MindGrowth.Stored(0, 1) == 1, "An unchanged rate replaced an odd stored value.");
            Reject(() => MindGrowth.Stored(-1, 30));
            Reject(() => MindGrowth.Stored(64, 30));
        });

        test("Mind growth field shows and accepts rates; class Mind growth is not halved", () =>
        {
            var field = CharacterFields.Growth.Single(f => f.Key == MindGrowth.Field);
            Check(field.StoredDoubled && field.Label == "Mind growth", "The personal Mind growth field is not marked as stored doubled.");
            Check(FieldCodec.Format(field, 31) == "15" && FieldCodec.Format(field, 40) == "20", "The field did not show the rate.");
            Check(FieldCodec.RangeText(field) == "0 to 63", "The field range is not shown as a rate.");
            Check(FieldCodec.TryParse(field, "20", out int value, out _) && value == 40, "A rate of 20 did not parse to a stored 40.");
            Check(FieldCodec.TryParse(field, "0", out value, out _) && value == 0, "A rate of 0 did not parse.");
            Check(FieldCodec.TryParse(field, "63", out value, out _) && value == 126, "A rate of 63 did not parse to a stored 126.");
            foreach (string text in new[] { "64", "-1", "15.5", "many" })
                Check(!FieldCodec.TryParse(field, text, out _, out string error) && error.Length > 0, $"The rate \"{text}\" was accepted.");
            var classField = ClassFields.Fields.Single(f => f.Key == "mindGrowth");
            Check(!classField.StoredDoubled && FieldCodec.Format(classField, 31) == "31", "Class Mind growth was halved.");
        });

        test("Mind growth edits change only the low seven bits, undo in one step and save in projects", () =>
        {
            var disc = WithMindByte(ChangeReviewTests.Fixture(), 30);
            var before = disc.GrowthBytes[1].ToArray();
            var session = new EditSession(disc);
            Check(session.EditedCharacters().Single().MindGrowthPercent == 15, "The fixture did not start at 15%.");
            Check(session.Apply(EditSession.Growths, 1, new Dictionary<string, int> { [MindGrowth.Field] = MindGrowth.Stored(20, 30) }),
                "The Mind growth edit was not applied.");
            var after = session.EditedBytes(EditSession.Growths, 1);
            Check(after[5] == 40 && after.Select((b, i) => i == 5 || b == before[i]).All(x => x), "Bytes other than Mind growth changed.");
            Check(session.EditedCharacters().Single().MindGrowthPercent == 20, "The edited rate was not 20%.");
            Check(EditSession.FromJson(disc, session.ToJson()).Value(EditSession.Growths, 1, MindGrowth.Field) == 40,
                "The project did not keep the stored value.");
            session.Undo();
            Check(session.EditedBytes(EditSession.Growths, 1).SequenceEqual(before), "Undo did not restore the record.");
            session.Redo();
            Check(session.EditedBytes(EditSession.Growths, 1)[5] == 40, "Redo did not reapply the edit.");

            var odd = new EditSession(WithMindByte(ChangeReviewTests.Fixture(), 31));
            int kept = MindGrowth.Stored(MindGrowth.Percent(odd.Value(EditSession.Growths, 1, MindGrowth.Field)), 31);
            Check(kept == 31 && !odd.Apply(EditSession.Growths, 1, new Dictionary<string, int> { [MindGrowth.Field] = kept }),
                "Re-entering an unchanged rate rewrote an odd stored value.");
        });

        test("Mind growth with the unknown top bit is refused, other growth fields stay editable", () =>
        {
            var disc = WithMindByte(ChangeReviewTests.Fixture(), 0x80 | 20);
            var character = disc.Characters.Single();
            Check(character.MindGrowthUnsupported && character.MindGrowthText == "Unsupported stored form",
                "The unsupported stored form was not reported.");
            var session = new EditSession(disc);
            Reject(() => session.Apply(EditSession.Growths, 1, new Dictionary<string, int> { [MindGrowth.Field] = 30 }));
            Check(session.Apply(EditSession.Growths, 1, new Dictionary<string, int> { ["hpGrowth"] = 30 }), "An HP growth edit was refused.");
            Check(session.EditedBytes(EditSession.Growths, 1)[5] == (0x80 | 20), "The Mind growth byte was changed by an HP edit.");
            Reject(() => R0StatRandomizer.Preview(session, R0StatProfile.VariancePreset(3)));
            Reject(() => R0StatRandomizer.Preview(session, new R0StatProfile("Old", R0StatAlgorithm.BoundedVariance,
                R0StatTarget.GrowthRates, 3, 10, 24, [], (string[])GrowthKeys.Clone())));
            R0StatRandomizer.Preview(session, R0StatProfile.VariancePreset(3) with { Fields = ["hpGrowth", "strengthGrowth"] });
        });

        test("Version 1 stat profiles replay exactly as before the Mind correction", () =>
        {
            foreach (var (name, profile) in LegacyProfiles())
            {
                var preview = R0StatRandomizer.Preview(new EditSession(ChangeReviewTests.Fixture()), profile);
                Check(preview.SchemaVersion == 1 && (preview.FieldEdits.Count, Digest(preview)) == FixtureReplay[name],
                    $"Version 1 profile {name} no longer replays its earlier result.");
            }
        });

        test("Percentage profiles change Mind in rates, store it doubled and keep budgets", () =>
        {
            var disc = WithGrowths(ChangeReviewTests.Fixture(), hp: 40, mind: 31);
            int checkedChanges = 0;
            for (int seed = 1; seed <= 60; seed++)
            {
                var session = new EditSession(disc);
                var budget = R0StatRandomizer.Preview(session, R0StatProfile.BudgetPreset(seed) with { Fields = ["hpGrowth", MindGrowth.Field] });
                Check(budget.SchemaVersion == 2, "A new growth profile did not use version 2.");
                Check(budget.Changes.Sum(c => c.After - c.Before) == 0, "The rate budget was not conserved.");
                foreach (var change in budget.Changes.Where(c => c.Field == MindGrowth.Field))
                {
                    var edit = budget.FieldEdits.Single(e => e.Field == MindGrowth.Field);
                    var expected = budget.ExpectedValues.Single(e => e.Field == MindGrowth.Field);
                    Check(change.Before == 15 && edit.Value == change.After * 2 && expected.Value == 31,
                        "A Mind change was not converted between rate and stored value.");
                    var hp = budget.Changes.Single(c => c.Field == "hpGrowth");
                    Check(hp.After - hp.Before == change.Before - change.After, "Moving HP points did not move whole Mind rate points.");
                    checkedChanges++;
                }
                var variance = R0StatRandomizer.Preview(session, R0StatProfile.VariancePreset(seed) with { MaximumChange = 63 });
                Check(variance.Changes.Where(c => c.Field == MindGrowth.Field).All(c => c.After is >= 0 and <= MindGrowth.MaxPercent),
                    "A Mind rate left the 0% to 63% range.");
            }
            Check(checkedChanges > 0, "No seed moved points between HP and Mind.");
        });

        test("Percentage profile previews are read-only and refuse stale or altered previews", () =>
        {
            var disc = WithGrowths(ChangeReviewTests.Fixture(), hp: 40, mind: 30);
            var session = new EditSession(disc);
            var preview = R0StatRandomizer.Preview(session, R0StatProfile.VariancePreset(11) with { MaximumChange = 20 });
            Check(preview.Changes.Count > 0, "The test preview produced no changes.");
            Check(session.ChangeCount == 0 && !session.IsEdited(EditSession.Growths, 1), "Previewing changed the project.");
            Reject(() => R0StatRandomizer.Apply(session, preview with { Profile = preview.Profile with { Seed = 12 } }));
            Reject(() => R0StatRandomizer.Apply(session, preview with { SchemaVersion = 1 }));
            Reject(() => R0StatRandomizer.Apply(session, preview with { FieldEdits = [.. preview.FieldEdits.Select(e => e with { Value = e.Value + 1 })] }));
            var stale = new EditSession(disc);
            var stalePreview = R0StatRandomizer.Preview(stale, R0StatProfile.VariancePreset(11) with { MaximumChange = 20 });
            stale.Apply(EditSession.Growths, 1, new Dictionary<string, int> { ["strengthGrowth"] = 7 });
            Reject(() => R0StatRandomizer.Apply(stale, stalePreview));
            Check(R0StatRandomizer.Apply(session, preview), "The valid preview was not applied.");
            foreach (var edit in preview.FieldEdits)
                Check(session.Value(edit.Table, edit.Id, edit.Field) == edit.Value, "An applied value differs from the preview.");
            session.Undo();
            Check(session.EditedBytes(EditSession.Growths, 1).SequenceEqual(disc.GrowthBytes[1].ToArray()), "One undo did not remove the whole randomization.");
        });

        test("Stat profiles: new growth profiles save as version 2 and version 1 files load unchanged", () =>
        {
            var current = R0StatProfileFiles.FromJson(R0StatProfileFiles.ToJson(R0StatProfile.BudgetPreset(5)));
            Check(current.SchemaVersion == 2 && !current.UsesStoredMindPoints, "A new growth profile did not save as version 2.");
            var legacy = new R0StatProfile("Old", R0StatAlgorithm.BudgetRedistribution, R0StatTarget.GrowthRates, 5, 10, 24, [], (string[])GrowthKeys.Clone());
            var loaded = R0StatProfileFiles.FromJson(R0StatProfileFiles.ToJson(legacy));
            Check(loaded.SchemaVersion == 1 && loaded.UsesStoredMindPoints, "A version 1 profile was not kept as version 1.");
            Check(R0StatProfileFiles.ToJson(legacy).Contains("\"SchemaVersion\": 1", StringComparison.Ordinal), "Version 1 was not written.");
            Reject(() => (legacy with { SchemaVersion = 2, Target = R0StatTarget.PersonalBonuses, Fields = (string[])BonusKeys.Clone() }).Validate());
            Reject(() => (legacy with { SchemaVersion = 3 }).Validate());
        });
    }

    public static void RunDiscs(Action<string, Action> test, InspectionResult disc)
    {
        test($"Mind growth matches the published rates on {disc.Edition}", () =>
        {
            Check(disc.Characters.Count == PublishedRates.Count, "The roster size changed.");
            foreach (var character in disc.Characters)
            {
                Check(!character.MindGrowthUnsupported, $"{character.Name} uses the unsupported stored form.");
                Check(character.MindGrowthPercent == PublishedRates[character.Id], $"{character.Name} has Mind growth {character.MindGrowthPercent}%.");
            }
        });

        test($"Version 1 stat profiles replay exactly as before on {disc.Edition}", () =>
        {
            foreach (var (name, profile) in LegacyProfiles())
            {
                var preview = R0StatRandomizer.Preview(new EditSession(disc), profile);
                Check((preview.FieldEdits.Count, Digest(preview)) == DiscReplay[name], $"Version 1 profile {name} changed on {disc.Edition}.");
            }
        });

        test($"Percentage budgets move whole Mind rate points on {disc.Edition}", () =>
        {
            var preview = R0StatRandomizer.Preview(new EditSession(disc), R0StatProfile.BudgetPreset(12345) with { Fields = ["hpGrowth", MindGrowth.Field], MaximumChange = 15, TransferAttempts = 80 });
            Check(preview.Changes.Count > 0, "The budget produced no changes.");
            foreach (var unit in preview.Changes.GroupBy(c => c.UnitId))
            {
                Check(unit.Sum(c => c.After - c.Before) == 0, "A character's rate budget was not conserved.");
                foreach (var change in unit.Where(c => c.Field == MindGrowth.Field))
                {
                    int stored = disc.Characters.Single(c => c.Id == unit.Key).MindGrowth;
                    var edit = preview.FieldEdits.Single(e => e.Id == unit.Key && e.Field == MindGrowth.Field);
                    Check(edit.Value - stored == 2 * (change.After - change.Before), "One rate point did not move two stored Mind points.");
                }
            }
        });

        test($"Mind growth change exports to both table copies and reads back on {disc.Edition}", () =>
        {
            var session = new EditSession(disc);
            const int id = 0x83;
            int stored = session.Value(EditSession.Growths, id, MindGrowth.Field);
            Check(stored == 30, "The test character does not start at 15%.");
            session.Apply(EditSession.Growths, id, new Dictionary<string, int> { [MindGrowth.Field] = MindGrowth.Stored(20, stored) });
            var patches = Exporter.PatchesFor(session, disc);
            Check(patches.Count == 2 && patches.All(p => p.Bytes.Length == 1 && p.Bytes[0] == 40 && p.Expected![0] == 30),
                "The Mind change was not written as one byte in each table copy.");
            string output = Path.Combine(Path.GetTempPath(), $"bw-mind-export-{Guid.NewGuid():N}.iso");
            try
            {
                var result = Exporter.Export(disc.Path, disc.Sha256, output, patches);
                var back = Inspector.Inspect(output);
                Check(result.ChangedBytes == 2 && back.CharacterCopiesMatch, "The export changed more than the two Mind bytes.");
                var character = back.Characters.Single(c => c.Id == id);
                Check(character.MindGrowth == 40 && character.MindGrowthPercent == 20, "The exported Mind growth did not read back as 20%.");
                Check(back.GrowthBytes.Where(g => g.Key != id).All(g => g.Value.Span.SequenceEqual(disc.GrowthBytes[g.Key].Span)),
                    "Another character's growth record changed.");
                Check(Inspector.Inspect(disc.Path).Sha256 == disc.Sha256, "The original disc changed.");
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        });
    }

    private static IReadOnlyList<(string Name, R0StatProfile Profile)> LegacyProfiles() =>
    [
        ("variance-growth-1729", new("Bounded growth variance", R0StatAlgorithm.BoundedVariance, R0StatTarget.GrowthRates, 1729, 10, 24, [], (string[])GrowthKeys.Clone())),
        ("budget-growth-1729", new("Conserved growth budget", R0StatAlgorithm.BudgetRedistribution, R0StatTarget.GrowthRates, 1729, 10, 24, [], (string[])GrowthKeys.Clone())),
        ("variance-hp-mind-7", new("HP and Mind", R0StatAlgorithm.BoundedVariance, R0StatTarget.GrowthRates, 7, 20, 24, [], ["hpGrowth", "mindGrowth"])),
        ("budget-hp-mind-12345", new("HP to Mind", R0StatAlgorithm.BudgetRedistribution, R0StatTarget.GrowthRates, 12345, 15, 80, [], ["hpGrowth", "mindGrowth"])),
        ("variance-bonus-1729", new("Bonus variance", R0StatAlgorithm.BoundedVariance, R0StatTarget.PersonalBonuses, 1729, 3, 24, [], (string[])BonusKeys.Clone())),
        ("budget-bonus-99", new("Bonus budget", R0StatAlgorithm.BudgetRedistribution, R0StatTarget.PersonalBonuses, 99, 4, 40, [], (string[])BonusKeys.Clone())),
    ];

    private static string Digest(R0StatPreview preview)
    {
        var text = new StringBuilder();
        foreach (var e in preview.FieldEdits) text.Append($"E|{e.Table}|{e.Id}|{e.Field}|{e.Value}\n");
        foreach (var v in preview.ExpectedValues) text.Append($"X|{v.Table}|{v.RecordId}|{v.Field}|{v.Value}\n");
        foreach (var c in preview.Changes) text.Append($"C|{c.UnitId}|{c.Field}|{c.Before}|{c.After}\n");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static InspectionResult WithMindByte(InspectionResult disc, int value)
    {
        var growth = disc.GrowthBytes[1].ToArray();
        growth[5] = (byte)value;
        return Replace(disc, growth);
    }

    private static InspectionResult WithGrowths(InspectionResult disc, int hp, int mind)
    {
        var growth = disc.GrowthBytes[1].ToArray();
        FieldCodec.Write(growth, CharacterFields.Growth.Single(f => f.Key == "hpGrowth"), hp);
        FieldCodec.Write(growth, CharacterFields.Growth.Single(f => f.Key == MindGrowth.Field), mind);
        return Replace(disc, growth);
    }

    private static InspectionResult Replace(InspectionResult disc, byte[] growth)
    {
        var unit = disc.UnitBytes[1].ToArray();
        return disc with
        {
            GrowthBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = growth },
            Characters = [CharacterDecoder.Decode(new(1, "Unit", 0), unit, growth, new Dictionary<int, string>())],
        };
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static void Reject(Action action)
    {
        try { action(); } catch (EditException) { return; }
        throw new Exception("Expected the operation to be refused.");
    }
}
