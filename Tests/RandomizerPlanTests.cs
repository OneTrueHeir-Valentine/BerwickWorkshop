using Berwick.Core;

/// <summary>Randomizer plans on a small generated project: composition, order, locks, tampering, one undo step, weak vs unusable.</summary>
public static class RandomizerPlanTests
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Refused(Action action, string message)
    {
        try { action(); } catch (EditException) { return; }
        throw new Exception(message);
    }

    public static void Run(Action<string, Action> test)
    {
        test("Invalid nested plans are refused before they reach the window", () =>
        {
            var plan = Plan(77);
            Refused(() => RandomizerPlans.FromJson(RandomizerPlans.ToJson(plan with
                { GrowthRates = plan.GrowthRates! with { Fields = null! } })), "Null growth fields were accepted.");
            Refused(() => RandomizerPlans.FromJson(RandomizerPlans.ToJson(plan with
                { GrowthRates = plan.GrowthRates! with { Target = R0StatTarget.PersonalBonuses } })), "Wrong growth target was accepted.");
            Refused(() => RandomizerPlans.Check(new EditSession(Fixture()), plan with
                { GrowthRates = plan.GrowthRates! with { UnitIds = [65535] } }), "Unknown character was accepted.");
        });
        test("Visible settings retain legacy growth and proficiency replay settings", () =>
        {
            var session = new EditSession(Fixture());
            var growth = R0StatProfile.BudgetPreset(79) with { SchemaVersion = R0StatProfile.StoredPointSchema,
                TransferAttempts = 13, Fields = ["mindGrowth", "hpGrowth", "strengthGrowth"] };
            var loaded = RandomizerPlans.FromJson(RandomizerPlans.ToJson(Plan(77) with { GrowthRates = growth })).GrowthRates!;
            var shown = RandomizerPlans.UpdateStatSettings(loaded, loaded.Seed, loaded.Mode, loaded.MaximumChange,
                loaded.UnitIds, loaded.Fields.Reverse().ToArray());
            Check(R0StatProfileFiles.ToJson(shown) == R0StatProfileFiles.ToJson(growth), "Visible settings changed the legacy growth profile.");
            Check(R0StatRandomizer.Preview(session, shown).FieldEdits.SequenceEqual(R0StatRandomizer.Preview(session, growth).FieldEdits),
                "The legacy growth replay changed.");
            var proficiency = R0ProficiencyProfile.StartingBudget(80) with { Compatibility = R0ProficiencyCompatibility.AllowClassPermittedChannels,
                TransferAttempts = 17, Fields = ["spearLevel", "knifeLevel"] };
            var updated = RandomizerPlans.UpdateProficiencySettings(proficiency, proficiency.Seed, proficiency.Mode, proficiency.MaximumChange,
                0, proficiency.UnitIds, proficiency.Fields.Reverse().ToArray());
            Check(R0ProficiencyProfileFiles.ToJson(updated) == R0ProficiencyProfileFiles.ToJson(proficiency), "Visible settings changed the proficiency profile.");
            Check(R0ProficiencyRandomizer.Preview(session, updated).FieldEdits.SequenceEqual(R0ProficiencyRandomizer.Preview(session, proficiency).FieldEdits),
                "The proficiency replay changed.");
        });
        test("A plan gives each part's own edits, minus locked characters", () =>
        {
            var session = new EditSession(Fixture());
            var plan = Plan(77) with { Locked = [3] };
            var preview = RandomizerPlans.Preview(session, plan);
            var alone = R0StatRandomizer.Preview(session, plan.StatBonuses!).FieldEdits
                .Concat(R0StatRandomizer.Preview(session, plan.GrowthRates!).FieldEdits)
                .Concat(R0ProficiencyRandomizer.Preview(session, plan.StartingProficiency!).FieldEdits)
                .Concat(R0ProficiencyRandomizer.Preview(session, plan.ProficiencyGrowth!).FieldEdits)
                .Where(e => e.Id != 3).ToList();
            Check(alone.Count > 0, "The fixture plan changed nothing, so the test proves nothing.");
            Check(alone.SequenceEqual(preview.Edits), "A part gave different edits inside the plan.");
            Check(preview.Edits.All(e => e.Id != 3) && preview.Rows.All(r => r.CharacterId != 3), "A locked character was changed.");
            Check(preview.Rows.Select(r => r.Module).Distinct().Count() >= 3, "Rows are not named by part.");
        });
        test("A plan gives the same edits whatever order its fields and locks are listed in", () =>
        {
            var session = new EditSession(Fixture());
            var plan = Plan(78) with { Locked = [4, 2] };
            var reordered = plan with
            {
                Locked = [2, 4],
                StatBonuses = plan.StatBonuses! with { Fields = plan.StatBonuses.Fields.Reverse().ToArray() },
                StartingProficiency = plan.StartingProficiency! with { Fields = plan.StartingProficiency.Fields.Reverse().ToArray() },
                ProficiencyGrowth = plan.ProficiencyGrowth! with { Fields = plan.ProficiencyGrowth.Fields.Reverse().ToArray() },
            };
            // Same values for every field; the list order follows the order fields are listed in. Stat budget mode is the one
            // exception that depends on field order, and it is kept that way so saved stat profiles replay exactly.
            static IEnumerable<FieldEdit> Sorted(RandomizerPlanPreview p) => p.Edits.OrderBy(e => e.Table).ThenBy(e => e.Id).ThenBy(e => e.Field, StringComparer.Ordinal);
            Check(Sorted(RandomizerPlans.Preview(session, plan)).SequenceEqual(Sorted(RandomizerPlans.Preview(session, reordered))),
                "Listing fields or locks in another order changed a value.");
            var saved = RandomizerPlans.FromJson(RandomizerPlans.ToJson(plan));
            Check(RandomizerPlans.Preview(session, saved).Edits.SequenceEqual(RandomizerPlans.Preview(session, plan).Edits),
                "A saved and reloaded plan gave other edits.");
        });
        test("A plan applies as one undo step and refuses changed or rebuilt previews", () =>
        {
            var session = new EditSession(Fixture());
            var preview = RandomizerPlans.Preview(session, Plan(79));
            Refused(() => RandomizerPlans.Apply(session, preview with { Edits = [.. preview.Edits.Skip(1)] }), "A shortened preview was applied.");
            Refused(() => RandomizerPlans.Apply(session, preview with { Edits = [.. preview.Edits.Select(e => e with { Value = e.Value + 10 })] }), "Changed values were applied.");
            Refused(() => RandomizerPlans.Apply(session, preview with { Plan = preview.Plan with { StatBonuses = preview.Plan.StatBonuses! with { Seed = 1 } } }), "A changed seed was applied.");
            Refused(() => RandomizerPlans.Apply(session, preview with { Problems = [] , Rows = [] }), "A preview with its rows removed was applied.");
            Refused(() => RandomizerPlans.Apply(session, preview with { SourceSha256 = "other" }), "A preview for another disc was applied.");
            Check(!session.CanUndo && session.ChangeCount == 0, "A refused preview changed the project.");
            var before = preview.Edits.Select(e => session.Value(e.Table, e.Id, e.Field)).ToList();
            Check(RandomizerPlans.Apply(session, preview), "The plan did not apply.");
            Check(preview.Edits.All(e => session.Value(e.Table, e.Id, e.Field) == e.Value), "The project does not hold the previewed values.");
            Refused(() => RandomizerPlans.Apply(session, preview), "A used preview was applied twice.");
            session.Undo();
            Check(!session.CanUndo, "The plan took more than one undo step.");
            Check(preview.Edits.Select(e => session.Value(e.Table, e.Id, e.Field)).SequenceEqual(before), "Undo did not restore every value.");
        });
        test("A level of zero does not block a plan; an older profile that makes one needs an explicit choice", () =>
        {
            var session = new EditSession(Fixture());
            RandomizerPlan? older = null;
            RandomizerPlanPreview? preview = null;
            for (int seed = 0; seed < 400 && older is null; seed++)
            {
                var candidate = new RandomizerPlan(RandomizerPlan.CurrentVersion, "Older weapon levels", null, null, null,
                    R0ProficiencyProfile.StartingVariance(seed) with { MaximumChange = 50 }, null, []);
                if (RandomizerPlans.ZeroedTrainedCharacters(session, candidate).Contains(2)) { older = candidate; preview = RandomizerPlans.Preview(session, candidate); }
            }
            Check(older is not null && preview is not null, "No seed lowered a trained level to zero, so the test proves nothing.");
            Check(preview!.Problems.Count == 0 && preview.CanApply, "A zero level was treated as a character who cannot fight.");
            Check(preview.Warnings.Any(w => w.Contains("would become 0", StringComparison.Ordinal)), "The zero level was not named in the warnings.");
            Refused(() => RandomizerPlans.Apply(session, preview), "An older profile's zero levels were applied without being confirmed.");
            Check(!session.CanUndo, "A refused plan changed the project.");
            Check(RandomizerPlans.Apply(session, preview, allowZeroedTrainedLevels: true) && session.CanUndo, "Confirmed zero levels were not applied.");
            Check(preview.Edits.All(e => session.Value(e.Table, e.Id, e.Field) == e.Value), "The project does not hold the previewed values.");
            session.Undo();
            var locked = older! with { Locked = [.. RandomizerPlans.ZeroedTrainedCharacters(session, older)] };
            Check(RandomizerPlans.ZeroedTrainedCharacters(session, locked).Count == 0 && RandomizerPlans.ZeroedTrainedLevels(session, locked).Count == 0,
                "Locking every named character did not clear the zero levels.");
            var kept = older with { StartingProficiency = older.StartingProficiency!.Migrated() };
            Check(RandomizerPlans.ZeroedTrainedLevels(session, kept).Count == 0, "Keeping trained levels still lowered a level to zero.");
            var single = R0ProficiencyRandomizer.Preview(session, older.StartingProficiency!);
            Refused(() => R0ProficiencyRandomizer.Apply(session, single), "The weapon level window applied zero levels without confirmation.");
            Check(R0ProficiencyRandomizer.Apply(session, single, allowZeroedTrainedLevels: true), "Confirmed zero levels were refused in the weapon level window.");
        });
        test("Weak is a warning, never a block, and the level is the user's choice", () =>
        {
            var session = new EditSession(Fixture());
            var plan = new RandomizerPlan(RandomizerPlan.CurrentVersion, "Weak check", null, null, null,
                R0ProficiencyProfile.StartingVariance(5).Migrated() with { MaximumChange = 50, TrainedMinimum = 1 }, null, []);
            var off = RandomizerPlans.Preview(session, plan);
            Check(!off.Warnings.Any(w => w.Contains("Weak, but still usable", StringComparison.Ordinal)), "Weak warnings appeared while turned off.");
            var on = RandomizerPlans.Preview(session, plan with { WarnBelowLevel = 30 });
            Check(on.Warnings.Any(w => w.Contains("Weak, but still usable", StringComparison.Ordinal)), "No weak warning at a high chosen level.");
            Check(on.Problems.Count == 0 && on.Edits.SequenceEqual(off.Edits), "The weak warning changed the edits or blocked the plan.");
            Check(!RandomizerPlans.ToJson(plan).Contains("WarnBelowLevel", StringComparison.Ordinal), "An unset warning level was written to the file.");
            Refused(() => RandomizerPlans.Preview(session, plan with { WarnBelowLevel = 31 }), "A warning level above 30 was accepted.");
        });
        test("Weapon families follow the game: a lance uses the spear level and a staff the holy level", () =>
        {
            Check(WeaponFamilies.LevelFor("Lance") == "spearLevel" && WeaponFamilies.LevelFor("Fork") == "spearLevel", "Lance or Fork mapping is wrong.");
            Check(WeaponFamilies.LevelFor("Blade") == "swordLevel" && WeaponFamilies.LevelFor("Mace") == "swordLevel", "Blade or Mace mapping is wrong.");
            Check(WeaponFamilies.LevelFor("Ballista") == "crossbowLevel", "Ballista mapping is wrong.");
            Check(WeaponFamilies.LevelFor("Healing") == "holyLevel" && WeaponFamilies.LevelFor("Dark healing") == "darkLevel", "Staff mapping is wrong.");
            Check(WeaponFamilies.LevelFor("Small shield") is null && WeaponFamilies.LevelFor("Arrow") is null, "A non-weapon has a weapon level.");
            Check(WeaponFamilies.AmmunitionFor("Bow") == "Arrow" && WeaponFamilies.AmmunitionFor("Crossbow") == "Bolt" &&
                  WeaponFamilies.AmmunitionFor("Ballista") is null, "Ammunition mapping is wrong.");
            var session = new EditSession(Fixture());
            LoadoutUse First(int unit, Func<string, string, int>? value = null) => Loadouts.Read(session, unit, value)[0].Use;
            Check(First(1) == LoadoutUse.Attack, "A lance user with spear training cannot attack.");
            Check(First(1, (t, f) => f == "spearLevel" ? 0 : session.Value(t, 1, f)) == LoadoutUse.Attack,
                "A lance user with spear level 0 was judged unable to attack; the game allows it, with very low accuracy.");
            Check(First(4) == LoadoutUse.Heal, "A staff user was not counted as a healer.");
            Check(First(3) == LoadoutUse.NoAmmunition, "A bow carried without arrows was counted as an attack.");
            Check(First(3, (t, f) => f == "item2" ? 6 : session.Value(t, 3, f)) == LoadoutUse.Attack, "A bow with arrows was not counted as an attack.");
            Check(First(2, (t, f) => f == "item1" ? 5 : session.Value(t, 2, f)) == LoadoutUse.ClassCannotUse,
                "A tome the class has no fire cap for was counted as usable.");
            var mixed = Loadouts.Read(session, 3, (t, f) => f == "item2" ? 2 : session.Value(t, 3, f));
            Check(mixed.Single(s => s.Slot == 1).Use == LoadoutUse.NoAmmunition && mixed.Single(s => s.Slot == 2).Use == LoadoutUse.Attack,
                "A loadout with one unusable slot and one usable one was not told apart.");
        });
        test("Each part keeps its own characters, saves them exactly and changes nobody else", () =>
        {
            var session = new EditSession(Fixture());
            var plan = Plan(81) with
            {
                StartingItems = new RandomizeOptions(81) { UnitIds = [4, 2] },
                StatBonuses = Plan(81).StatBonuses! with { UnitIds = [1] },
                GrowthRates = Plan(81).GrowthRates! with { UnitIds = [3, 4] },
                StartingProficiency = Plan(81).StartingProficiency! with { UnitIds = [] },
                ProficiencyGrowth = Plan(81).ProficiencyGrowth! with { UnitIds = [2] },
            };
            string json = RandomizerPlans.ToJson(plan);
            var loaded = RandomizerPlans.FromJson(json);
            Check(RandomizerPlans.ToJson(loaded) == json, "A plan with a different roster per part did not save exactly.");
            Check(loaded.StartingItems!.UnitIds!.SequenceEqual(new[] { 4, 2 }) && loaded.GrowthRates!.UnitIds.SequenceEqual(new[] { 3, 4 }),
                "A part's characters were reordered or merged on loading.");
            var preview = RandomizerPlans.Preview(session, plan);
            Check(preview.Rows.Where(r => r.Module == "Stat bonuses").All(r => r.CharacterId == 1), "Stat bonuses changed a character outside its list.");
            Check(preview.Rows.Where(r => r.Module == "Growth rates").All(r => r.CharacterId is 3 or 4), "Growth rates changed a character outside its list.");
            Check(preview.Rows.Where(r => r.Module == "Weapon growth").All(r => r.CharacterId == 2), "Weapon growth changed a character outside its list.");
            var everyone = R0StatRandomizer.Preview(session, plan.GrowthRates! with { UnitIds = [] }).FieldEdits;
            Check(R0StatRandomizer.Preview(session, plan.GrowthRates!).FieldEdits.SequenceEqual(everyone.Where(e => e.Id is 3 or 4)),
                "Choosing characters changed the result for the characters that were chosen.");
            var scopes = RandomizerPlans.Scopes(session, plan with { Locked = [4] });
            Check(scopes.Single(s => s.Part == "Starting items") == ("Starting items", 2, 4, 1), "Item scope is wrong.");
            Check(scopes.Single(s => s.Part == "Starting weapon levels").Count == 4, "An empty list did not mean every character.");
            Refused(() => RandomizerPlans.Preview(session, plan with { StartingItems = new RandomizeOptions(81) { UnitIds = [99] } }),
                "An item list naming a character not on the disc was accepted.");
            Refused(() => RandomizerPlans.Preview(session, plan with { StartingItems = new RandomizeOptions(81) { UnitIds = [2, 2] } }),
                "An item list naming a character twice was accepted.");
            Check(!RandomizerPlans.ToJson(Plan(81) with { StartingItems = new RandomizeOptions(81) }).Contains("UnitIds\": null", StringComparison.Ordinal) &&
                  !System.Text.Json.JsonSerializer.Serialize(new RandomizeOptions(81)).Contains("UnitIds", StringComparison.Ordinal),
                "Item options without a character list gained a new field.");
            Check(new RandomizeOptions(5) { UnitIds = [1, 2] } == new RandomizeOptions(5) { UnitIds = [1, 2] }, "Equal item options compare unequal.");
        });

        test("Saved item plans preserve explicit extended inventory without changing old defaults", () =>
        {
            var plan = Plan(81) with { StartingItems = new RandomizeOptions(81) { UnitIds = [4, 2] } };
            string oldJson = RandomizerPlans.ToJson(plan);
            Check(!oldJson.Contains("ExtendedInventory", StringComparison.Ordinal), "Legacy plan gained a new default field.");
            Check(!RandomizerPlans.FromJson(oldJson).StartingItems!.ExtendedInventory, "Legacy plan opted in unexpectedly.");
            var wide = plan with { StartingItems = plan.StartingItems! with { ExtendedInventory = true } };
            string wideJson = RandomizerPlans.ToJson(wide);
            var loaded = RandomizerPlans.FromJson(wideJson);
            Check(loaded.StartingItems == wide.StartingItems && loaded.StartingItems != plan.StartingItems,
                "Scope was lost or ignored by option equality.");
            Check(RandomizerPlans.ToJson(loaded) == wideJson, "Expanded plan did not round-trip exactly.");
            var session = new EditSession(Fixture());
            Check(RandomizerPlans.Preview(session, loaded).Edits.SequenceEqual(RandomizerPlans.Preview(session, wide).Edits),
                "Reloaded extended scope changed the preview.");
        });
        test("Every preset is valid, keeps trained levels, uses its own seed per part and saves exactly", () =>
        {
            var session = new EditSession(Fixture());
            foreach (string name in RandomizerPlans.PresetNames)
            {
                var plan = RandomizerPlans.Preset(name, 1000);
                Check(plan.StartingProficiency is { SchemaVersion: 2 } && plan.ProficiencyGrowth is { SchemaVersion: 2 }, $"{name}: weapon levels do not keep training.");
                var seeds = new[] { plan.StatBonuses?.Seed, plan.GrowthRates?.Seed, plan.StartingProficiency?.Seed, plan.ProficiencyGrowth?.Seed }.OfType<int>().ToList();
                Check(seeds.Distinct().Count() == seeds.Count, $"{name}: two parts share a seed.");
                Check(RandomizerPlans.ToJson(RandomizerPlans.FromJson(RandomizerPlans.ToJson(plan))) == RandomizerPlans.ToJson(plan), $"{name}: did not save exactly.");
                var preview = RandomizerPlans.Preview(session, plan);
                Check(preview.Problems.Count == 0, $"{name}: a preset left a character unarmed.");
            }
            Refused(() => RandomizerPlans.Preset("Unknown", 1), "An unknown preset was accepted.");
        });
    }

    private static RandomizerPlan Plan(int seed) => new(RandomizerPlan.CurrentVersion, "Test plan", null,
        R0StatProfile.VariancePreset(seed) with { Target = R0StatTarget.PersonalBonuses, Fields = (string[])R0StatProfile.BonusFieldKeys.Clone(), SchemaVersion = R0StatProfile.StoredPointSchema, MaximumChange = 3 },
        R0StatProfile.VariancePreset(seed + 1),
        R0ProficiencyProfile.StartingVariance(seed + 2).Migrated(),
        R0ProficiencyProfile.GrowthVariance(seed + 3).Migrated(), []);

    /// <summary>Four characters of one class: a lance user, a spear user, an archer and a healer.</summary>
    internal static InspectionResult Fixture()
    {
        int Type(string name) => Array.IndexOf(ItemDecoder.Types, name);
        byte[] Item(string type, int might)
        {
            var bytes = new byte[56];
            FieldCodec.Write(bytes, ItemEncoder.Field("type"), Type(type));
            FieldCodec.Write(bytes, ItemEncoder.Field("might"), might);
            FieldCodec.Write(bytes, ItemEncoder.Field("uses"), 30);
            return bytes;
        }
        var items = new Dictionary<int, byte[]> { [1] = Item("Lance", 9), [2] = Item("Spear", 8), [3] = Item("Bow", 7), [4] = Item("Healing", 0),
            [5] = Item("Fire", 6), [6] = Item("Arrow", 0) };
        string[] itemNames = ["", "Lance", "Spear", "Bow", "Heal", "Fire", "Arrows"];
        void W(byte[] bytes, IReadOnlyList<TableField> fields, string key, int value) => FieldCodec.Write(bytes, fields.Single(f => f.Key == key), value);
        byte[] Unit(int item, params (string Key, int Value)[] levels)
        {
            var bytes = new byte[256];
            W(bytes, CharacterFields.Record, "class", 1); W(bytes, CharacterFields.Record, "level", 5);
            W(bytes, CharacterFields.Record, "strengthBonus", 2); W(bytes, CharacterFields.Record, "agilityBonus", -1);
            W(bytes, CharacterFields.Record, "hpBonus", 4); W(bytes, CharacterFields.Record, "defenseBonus", 1); W(bytes, CharacterFields.Record, "mindBonus", 0);
            W(bytes, CharacterFields.Record, "item1", item); W(bytes, CharacterFields.Record, "item1Uses", 30);
            foreach (var (key, value) in levels) W(bytes, CharacterFields.Record, key, value);
            return bytes;
        }
        byte[] Growth(params (string Key, int Value)[] weapon)
        {
            var bytes = new byte[32]; bytes[2] = 1 << 5; Array.Fill(bytes, (byte)255, 26, 6);
            foreach (var (key, value) in new[] { ("hpGrowth", 60), ("strengthGrowth", 40), ("defenseGrowth", 30), ("agilityGrowth", 45), (MindGrowth.Field, 40) }.Concat(weapon))
                W(bytes, CharacterFields.Growth, key, value);
            return bytes;
        }
        var units = new Dictionary<int, byte[]>
        {
            [1] = Unit(1, ("spearLevel", 40), ("swordLevel", 25)),
            [2] = Unit(2, ("spearLevel", 10), ("axeLevel", 30)),
            [3] = Unit(3, ("bowLevel", 25), ("knifeLevel", 15)),
            [4] = Unit(4, ("holyLevel", 30), ("knifeLevel", 5)),
        };
        var growths = new Dictionary<int, byte[]>
        {
            [1] = Growth(("spearGrowth", 6), ("swordGrowth", 3)), [2] = Growth(("spearGrowth", 4), ("axeGrowth", 5)),
            [3] = Growth(("bowGrowth", 7), ("knifeGrowth", 2)), [4] = Growth(("holyGrowth", 6), ("knifeGrowth", 1)),
        };
        var cls = new byte[100];
        foreach (string cap in new[] { "knifeCap", "swordCap", "spearCap", "axeCap", "bowCap", "holyCap" })
            FieldCodec.Write(cls, ClassFields.Fields.Single(f => f.Key == cap), 40);
        string[] names = ["", "Adel", "Ruby", "Owen", "Cleric"];
        var profile = new GameProfile("Test edition", "hash", 0, 1024)
        { UnitTable = 2048, SecondUnitTable = 4096, ClassTable = 8192, SecondClassTable = 16384 };
        var unitTemplate = units[1];
        return new("source.iso", 8 * 1024 * 1024, "", "", "", "hash", "", profile,
            [new("DATA3.DAT", 0, 4 * 1024 * 1024), new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024)],
            items.Select(i => ItemDecoder.Decode(i.Key, itemNames[i.Key], i.Value)).ToList(), 0)
        {
            ItemBytes = items.ToDictionary(i => i.Key, i => (ReadOnlyMemory<byte>)i.Value),
            UnitBytes = units.ToDictionary(u => u.Key, u => (ReadOnlyMemory<byte>)u.Value),
            GrowthBytes = growths.ToDictionary(g => g.Key, g => (ReadOnlyMemory<byte>)g.Value),
            ClassBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = cls },
            Characters = units.Select(u => CharacterDecoder.Decode(new(u.Key, names[u.Key], 0), u.Value, growths[u.Key],
                itemNames.Select((n, i) => (n, i)).ToDictionary(x => x.i, x => x.n))).ToList(),
            Classes = [ClassDecoder.Decode(1, "Soldier", cls)],
            CharacterOffsets = units.Keys.ToDictionary(id => id, id => (id - 1) * 256),
            ArmyUnitBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [1000] = unitTemplate.ToArray() },
            Armies = [new(1, "Test army", [100000], 256, [new(1000, 1, 0, 1, "Enemy", 0, 180)])],
            RuleWords = new Dictionary<int, ReadOnlyMemory<byte>> { [1] = GameRules.ById(1).EncodeBytes(GameRules.ById(1).Default) },
            ExecutableFile = new("SLPS_254.97", 4 * 1024 * 1024, 4 * 1024 * 1024)
        };
    }
}
