using Berwick.Core;

/// <summary>
/// Real-disc check of a combined Randomizer plan: every module gives the same edits inside the plan as on its own, locked
/// characters are untouched, the plan replays exactly, applies as one undo step, and a changed preview is refused.
/// Run with --randomizer-plan &lt;original iso&gt;. Nothing is written to any disc image.
/// </summary>
public static class RandomizerPlanCheck
{
    public static int Run(string iso)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        using var stream = File.OpenRead(iso);
        using var image = new IsoImage(stream);
        var library = new EventLibrary(new GameArchives(image));
        ScriptDependencyIndex Audit() => ScriptDependencyIndex.Build(library.Numbers, library.Bytes, library.CommandNames);
        bool ok = true;
        void Check(bool condition, string what) { Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"); ok &= condition; }

        var session = new EditSession(disc);
        int[] locked = disc.Characters.OrderBy(c => c.Id).Take(2).Select(c => c.Id).ToArray();
        var plan = new RandomizerPlan(RandomizerPlan.CurrentVersion, "All parts", new RandomizeOptions(4242),
            R0StatProfile.VariancePreset(101) with { Target = R0StatTarget.PersonalBonuses, Fields = (string[])R0StatProfile.BonusFieldKeys.Clone(), SchemaVersion = R0StatProfile.StoredPointSchema },
            R0StatProfile.VariancePreset(202), R0ProficiencyProfile.StartingVariance(303).Migrated(), R0ProficiencyProfile.GrowthVariance(404).Migrated(), locked);
        {
            var older = plan with { StartingProficiency = R0ProficiencyProfile.StartingVariance(303), ProficiencyGrowth = R0ProficiencyProfile.GrowthVariance(404) };
            var olderLocked = older with { Locked = [.. locked, .. RandomizerPlans.Preview(session, older, Audit()).Problems.Select(p => p.CharacterId)] };
            var olderPreview = RandomizerPlans.Preview(session, olderLocked, Audit());
            bool olderRefused;
            try { RandomizerPlans.Apply(session, olderPreview, Audit(), true); olderRefused = false; }
            catch (EditException e) { olderRefused = e.Message.Contains("trained levels to zero", StringComparison.Ordinal); }
            Console.WriteLine($"  older plan: {olderPreview.Problems.Count} problems, {RandomizerPlans.ZeroedTrainedLevels(session, olderLocked).Count} trained levels lowered to zero");
            Check(olderPreview.Problems.Count == 0, "a zero weapon level alone does not make any character unable to fight");
            Check(olderRefused && !session.CanUndo, "an older weapon level profile that zeroes trained levels is refused unless that is confirmed");
            Check(RandomizerPlans.Apply(session, olderPreview, Audit(), true, allowZeroedTrainedLevels: true), "with zero levels confirmed, the older plan applies");
            session.Undo();
            Check(!session.CanUndo && session.ChangeCount == 0, "one undo removes the confirmed older plan");
        }
        var preview = RandomizerPlans.Preview(session, plan, Audit());
        Console.WriteLine($"{disc.Edition}: {preview.Edits.Count} edits in {preview.Rows.Select(r => r.CharacterId).Distinct().Count()} characters, " +
                          $"{preview.Problems.Count} problems, {preview.Warnings.Count} warnings, {preview.ScriptReferences.Count} script-referenced items");
        foreach (var g in preview.Rows.GroupBy(r => r.Module)) Console.WriteLine($"  {g.Key}: {g.Count()} values");

        var alone = RandomizerR0.PreviewStartingItems(session, plan.StartingItems!, Audit()).FieldEdits
            .Concat(R0StatRandomizer.Preview(session, plan.StatBonuses!).FieldEdits)
            .Concat(R0StatRandomizer.Preview(session, plan.GrowthRates!).FieldEdits)
            .Concat(R0ProficiencyRandomizer.Preview(session, plan.StartingProficiency!).FieldEdits)
            .Concat(R0ProficiencyRandomizer.Preview(session, plan.ProficiencyGrowth!).FieldEdits)
            .Where(e => !locked.Contains(e.Id)).ToList();
        Check(alone.SequenceEqual(preview.Edits), "each module gives the same edits inside the plan as on its own");
        Check(preview.Edits.All(e => !locked.Contains(e.Id)), "locked characters have no edits");
        Check(RandomizerPlans.Preview(session, plan, Audit()).Edits.SequenceEqual(preview.Edits), "the same plan replays to the same edits");
        var reloaded = RandomizerPlans.FromJson(RandomizerPlans.ToJson(plan));
        Check(RandomizerPlans.Preview(session, reloaded, Audit()).Edits.SequenceEqual(preview.Edits), "a saved and reloaded plan replays to the same edits");
        foreach (var p in preview.Problems)
        {
            Console.WriteLine($"  problem: {p.Character} ({p.CharacterId}): {p.Reason}");
            foreach (var r in preview.Rows.Where(r => r.CharacterId == p.CharacterId && r.Module is "Starting weapon levels" or "Starting items"))
                Console.WriteLine($"    {r.Module}: {r.Field} {r.Before} -> {r.After}");
        }
        if (preview.Problems.Count > 0)
        {
            bool blocked;
            try { RandomizerPlans.Apply(session, preview, Audit(), true); blocked = false; }
            catch (EditException) { blocked = true; }
            Check(blocked && !session.CanUndo, "a plan that would leave a character without a usable weapon is refused");
            plan = plan with { Locked = [.. locked, .. preview.Problems.Select(p => p.CharacterId)] };
            preview = RandomizerPlans.Preview(session, plan, Audit());
            Console.WriteLine($"  locked the affected characters: {preview.Edits.Count} edits, {preview.Problems.Count} problems");
        }
        Check(preview.Problems.Count == 0, "with those characters locked, no character is left without a usable weapon");

        bool refused;
        try { RandomizerPlans.Apply(session, preview with { Edits = preview.Edits.Skip(1).ToList() }, Audit(), true); refused = false; }
        catch (EditException) { refused = true; }
        Check(refused && !session.CanUndo, "a changed preview is refused and changes nothing");

        var before = preview.Edits.Select(e => session.Value(e.Table, e.Id, e.Field)).ToArray();
        Check(RandomizerPlans.Apply(session, preview, Audit(), true), "the plan applies");
        Check(preview.Edits.All(e => session.Value(e.Table, e.Id, e.Field) == e.Value), "every edit is in the project");
        session.Undo();
        Check(!session.CanUndo, "one undo step removes the whole plan");
        Check(preview.Edits.Select(e => session.Value(e.Table, e.Id, e.Field)).SequenceEqual(before), "undo restores every value");
        session.Redo();
        Check(preview.Edits.All(e => session.Value(e.Table, e.Id, e.Field) == e.Value), "redo puts it back");
        {
            // Different characters per part, with starting items limited too: each part touches only its own list, gives the same
            // values for those characters as when it covers everyone, and the plan saves and replays exactly.
            var ids = disc.Characters.OrderBy(c => c.Id).Select(c => c.Id).ToArray();
            int[] first = ids.Take(12).ToArray(), odd = ids.Where((_, i) => i % 2 == 1).ToArray(), late = ids.Skip(20).ToArray();
            var full = RandomizerPlans.Preset("Balanced", 1729);
            var mixed = full with
            {
                StartingItems = full.StartingItems! with { UnitIds = first },
                StatBonuses = full.StatBonuses! with { UnitIds = odd },
                GrowthRates = full.GrowthRates! with { UnitIds = late },
                StartingProficiency = full.StartingProficiency! with { UnitIds = odd },
                ProficiencyGrowth = full.ProficiencyGrowth! with { UnitIds = first },
                Locked = [ids[1]],
            };
            var a = RandomizerPlans.Preview(new EditSession(disc), mixed, Audit());
            var b = RandomizerPlans.Preview(new EditSession(disc), full with { Locked = [ids[1]] }, Audit());
            var parts = new (string Module, int[] Members)[] { ("Starting items", first), ("Stat bonuses", odd), ("Growth rates", late),
                ("Starting weapon levels", odd), ("Weapon growth", first) };
            bool inside = parts.All(p => a.Rows.Where(r => r.Module == p.Module).All(r => p.Members.Contains(r.CharacterId) && r.CharacterId != ids[1]));
            bool same = parts.All(p => a.Rows.Where(r => r.Module == p.Module).SequenceEqual(
                b.Rows.Where(r => r.Module == p.Module && p.Members.Contains(r.CharacterId))));
            Console.WriteLine($"  per-part characters: {a.Edits.Count} edits (" + string.Join(", ", parts.Select(p => $"{p.Module} {a.Rows.Count(r => r.Module == p.Module)}")) + ")");
            Check(inside, "with different characters per part, each part changes only its own characters and never a locked one");
            Check(same, "each part gives its chosen characters the same values as when it covers everyone");
            string json = RandomizerPlans.ToJson(mixed);
            Check(RandomizerPlans.ToJson(RandomizerPlans.FromJson(json)) == json &&
                  RandomizerPlans.Preview(new EditSession(disc), RandomizerPlans.FromJson(json), Audit()).Edits.SequenceEqual(a.Edits),
                "a plan with different characters per part saves exactly and replays to the same edits");
        }
        {
            // Seed sweep: every preset, 40 seeds each, plus custom rosters and older weapon level parts. A zero level must never count as
            // unable to fight; real problems (nothing to attack or heal with) are counted and listed.
            int plans = 0, problems = 0, lostSlots = 0, zeroWarnings = 0;
            var rng = new Random(20261126);
            var ids = disc.Characters.Select(c => c.Id).OrderBy(i => i).ToArray();
            foreach (string presetName in RandomizerPlans.PresetNames)
                for (int seed = 1; seed <= 40; seed++)
                {
                    var swept = RandomizerPlans.Preset(presetName, seed * 7919);
                    if (seed % 4 == 0) swept = swept with { StartingProficiency = R0ProficiencyProfile.StartingVariance(seed) with { MaximumChange = 30 },
                        StatBonuses = swept.StatBonuses! with { UnitIds = ids.Where(_ => rng.Next(2) == 0).ToArray() } };
                    var p = RandomizerPlans.Preview(new EditSession(disc), swept, null);
                    plans++; problems += p.Problems.Count;
                    lostSlots += p.Warnings.Count(w => w.Contains("could not be used", StringComparison.Ordinal));
                    zeroWarnings += p.Warnings.Count(w => w.Contains("would become 0", StringComparison.Ordinal));
                    foreach (var problem in p.Problems) Console.WriteLine($"    sweep problem ({presetName} {seed}): {problem.Character}: {problem.Reason}");
                }
            Console.WriteLine($"  sweep: {plans} plans, {problems} characters with nothing to attack or heal with, {lostSlots} unusable-slot warnings, {zeroWarnings} zero-level warnings");
            Check(problems == 0, "no plan in the sweep leaves a character with nothing to attack or heal with");
            Check(zeroWarnings > 0, "the sweep reached zero levels, so the zero-level handling was exercised");
        }
        foreach (string presetName in RandomizerPlans.PresetNames)
        {
            var p = RandomizerPlans.Preview(new EditSession(disc), RandomizerPlans.Preset(presetName, 1729), Audit());
            int weakCount = p.Warnings.Count(w => w.Contains("Weak, but still usable", StringComparison.Ordinal));
            Console.WriteLine($"  preset {presetName}: {p.Edits.Count} edits in {p.Rows.Select(r => r.CharacterId).Distinct().Count()} characters, {p.Problems.Count} problems, {weakCount} weak warnings, {p.ScriptReferences.Count} script-referenced items");
            foreach (var problem in p.Problems) Console.WriteLine($"    problem: {problem.Character}: {problem.Reason}");
            Check(p.Problems.Count == 0, $"preset {presetName} leaves every character a usable weapon");
    Check(p.Rows.All(r => r.Before != r.After), $"preset {presetName} lists only values that change ({p.Rows.Count} of {p.Edits.Count} edits)");
        }
        {
            // Originals leave the extra slots empty; populate slot 6 only in a disposable project.
            var extra = new EditSession(disc);
            var pool = Randomizer.Pool(extra);
            var ids = pool.Select(x => x.Id).ToHashSet();
            var candidate = (from unit in disc.Characters.OrderBy(c => c.Id)
                             where extra.Value(EditSession.Characters, unit.Id, "item6") == 0
                             from slot in Enumerable.Range(1, CharacterFields.VerifiedItemSlots)
                             let itemId = extra.Value(EditSession.Characters, unit.Id, $"item{slot}")
                             where ids.Contains(itemId)
                             let item = pool.First(x => x.Id == itemId)
                             where pool.Count(x => x.Type == item.Type) > 1
                             select (unit.Id, Item: item)).FirstOrDefault();
            Check(candidate.Item is not null, "an occupied original slot can seed a disposable extended-inventory fixture");
            if (candidate.Item is not null)
            {
                extra.Apply(EditSession.Characters, candidate.Id, new Dictionary<string, int> {
                    ["item6"] = candidate.Item.Id, ["item6Uses"] = candidate.Item.Uses,
                });
                bool changed = false, same = true;
                for (int seed = 0; seed < 32 && !changed; seed++)
                {
                    var five = new RandomizeOptions(seed) { Weapons = true, Ammunition = true, Other = true,
                        Bags = true, LimitLevel = false, UnitIds = [candidate.Id] };
                    var eight = five with { ExtendedInventory = true };
                    var a = RandomizerR0.PreviewStartingItems(extra, five);
                    var b = RandomizerR0.PreviewStartingItems(extra, eight);
                    bool extraField(FieldEdit e) => e.Table == EditSession.Characters &&
                        new[] { "item6", "item6Uses", "item7", "item7Uses", "item8", "item8Uses" }.Contains(e.Field);
                    same &= a.FieldEdits.SequenceEqual(b.FieldEdits.Where(e => !extraField(e))) &&
                        !a.FieldEdits.Any(extraField) && b.Replacements.All(e => e.Table != EditSession.Characters || e.Slot != 5 || e.UnitId == candidate.Id);
                    changed = b.Replacements.Any(e => e.Table == EditSession.Characters && e.Slot == 5);
                    if (!changed) continue;
                    var wide = new RandomizerPlan(RandomizerPlan.CurrentVersion, "Extended inventory", eight, null, null, null, null, []);
                    var loaded = RandomizerPlans.FromJson(RandomizerPlans.ToJson(wide));
                    Check(loaded.StartingItems == eight, "the saved plan retains extended inventory and its roster");
                    Check(RandomizerPlans.Preview(extra, loaded).Edits.SequenceEqual(b.FieldEdits), "the plan includes the same occupied extended slot as its module");
                }
                Check(same && changed, "opt-in randomizes an occupied slot 6 while preserving all old inventory and bag draws");
            }
        }
        Console.WriteLine(ok ? "randomizer plan check passed" : "randomizer plan check FAILED");
        return ok ? 0 : 1;
    }
}
