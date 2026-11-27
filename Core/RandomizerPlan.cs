using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

/// <summary>
/// One Randomizer configuration that runs several existing modules together: starting items, personal stat bonuses, personal
/// growth rates, starting weapon levels and weapon growth. Each module keeps its own profile, seed and random streams, so a
/// module gives exactly the same result inside a plan as on its own. Locked characters are left as they are by every module.
/// </summary>
public sealed record RandomizerPlan(
    int Version, string Name,
    RandomizeOptions? StartingItems,
    R0StatProfile? StatBonuses,
    R0StatProfile? GrowthRates,
    R0ProficiencyProfile? StartingProficiency,
    R0ProficiencyProfile? ProficiencyGrowth,
    IReadOnlyList<int> Locked)
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Optional: warn (never block) when a character's best usable carried weapon would end below this many whole weapon levels.
    /// Zero turns the warning off and is not written to the file, so older plans read and save exactly as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WarnBelowLevel { get; init; }
}

/// <summary>One changed value in a plan preview, named for the reader.</summary>
public sealed record RandomizerPlanRow(int CharacterId, string Character, string Module, string Table, string Field, string Before, string After);

/// <summary>A character the plan would leave unable to use any weapon it carries, and why.</summary>
public sealed record RandomizerPlanProblem(int CharacterId, string Character, string Reason);

public sealed record RandomizerPlanPreview(
    RandomizerPlan Plan, string SourceSha256, int ChangeCountAtGeneration,
    IReadOnlyList<FieldEdit> Edits, IReadOnlyList<RandomizerPlanRow> Rows,
    IReadOnlyList<string> Warnings, IReadOnlyList<RandomizerPlanProblem> Problems,
    IReadOnlyList<RandomizerItemReference> ScriptReferences)
{
    public bool CanApply => Problems.Count == 0 && Edits.Count > 0;
}

public static class RandomizerPlans
{
    /// <summary>Ready-made plans. Every part has its own seed, taken from the one given, and weapon levels keep training.</summary>
    public static readonly IReadOnlyList<string> PresetNames = ["Light", "Balanced", "Wild"];

    public static RandomizerPlan Preset(string name, int seed)
    {
        if (seed is < 0 or > int.MaxValue - 4) throw new EditException("Use a seed from 0 to 2147483643.");
        R0StatProfile Bonuses(int change) => new(name + " stat bonuses", R0StatAlgorithm.BoundedVariance, R0StatTarget.PersonalBonuses,
            seed + 1, change, 24, [], (string[])R0StatProfile.BonusFieldKeys.Clone());
        R0StatProfile Growths(R0StatAlgorithm mode, int change) => (mode == R0StatAlgorithm.BoundedVariance
            ? R0StatProfile.VariancePreset(seed + 2) : R0StatProfile.BudgetPreset(seed + 2)) with { Name = name + " growth rates", MaximumChange = change };
        R0ProficiencyProfile Levels(R0StatAlgorithm mode, int change, int lowest) => (R0ProficiencyProfile.StartingVariance(seed + 3) with
            { Name = name + " weapon levels", Mode = mode, MaximumChange = change }).Migrated() with { TrainedMinimum = lowest };
        R0ProficiencyProfile Growth(R0StatAlgorithm mode, int change) => (R0ProficiencyProfile.GrowthVariance(seed + 4) with
            { Name = name + " weapon growth", Mode = mode, MaximumChange = change }).Migrated();
        return name switch
        {
            "Light" => new(RandomizerPlan.CurrentVersion, "Light", null, Bonuses(1), Growths(R0StatAlgorithm.BoundedVariance, 5),
                Levels(R0StatAlgorithm.BoundedVariance, 2, 1), Growth(R0StatAlgorithm.BoundedVariance, 1), []) { WarnBelowLevel = 2 },
            "Balanced" => new(RandomizerPlan.CurrentVersion, "Balanced", new RandomizeOptions(seed), Bonuses(2), Growths(R0StatAlgorithm.BudgetRedistribution, 10),
                Levels(R0StatAlgorithm.BudgetRedistribution, 5, 1), Growth(R0StatAlgorithm.BudgetRedistribution, 2), []) { WarnBelowLevel = 2 },
            "Wild" => new(RandomizerPlan.CurrentVersion, "Wild", new RandomizeOptions(seed) { Other = true, LimitLevel = false }, Bonuses(5),
                Growths(R0StatAlgorithm.BoundedVariance, 25), Levels(R0StatAlgorithm.BoundedVariance, 10, 1), Growth(R0StatAlgorithm.BoundedVariance, 4), []),
            _ => throw new EditException($"There is no Randomizer preset called \"{name}\"."),
        };
    }

    public static RandomizerPlanPreview Preview(EditSession session, RandomizerPlan plan, ScriptDependencyIndex? scriptIndex = null)
    {
        Check(session, plan);
        var locked = plan.Locked.ToHashSet();
        var names = session.Disc.Characters.ToDictionary(c => c.Id, c => c.Name);
        var edits = new List<FieldEdit>();
        var rows = new List<RandomizerPlanRow>();
        var warnings = new List<string>();
        IReadOnlyList<RandomizerItemReference> scriptReferences = [];

        void Add(string module, IEnumerable<FieldEdit> found, Func<FieldEdit, string> before, Func<FieldEdit, string> after)
        {
            foreach (var e in found)
            {
                if (locked.Contains(e.Id)) continue;
                edits.Add(e);
                // A part may also write a value that stays the same (an item's uses when only the item changes); it is applied
                // as the part gives it, but only values that change are listed and counted.
                if (session.Value(e.Table, e.Id, e.Field) == e.Value) continue;
                var field = session.Table(e.Table).Field(e.Field);
                rows.Add(new(e.Id, names.GetValueOrDefault(e.Id, $"Character {e.Id}"), module, e.Table, field.Label, before(e), after(e)));
            }
        }
        string Format(FieldEdit e, int value) => FieldCodec.Format(session.Table(e.Table).Field(e.Field), value);
        string Item(int id) => id == 0 ? "Empty" : session.EditedItem(id).Name;

        if (plan.StartingItems is { } items)
        {
            var p = RandomizerR0.PreviewStartingItems(session, items, scriptIndex);
            Add("Starting items", p.FieldEdits,
                e => e.Field.EndsWith("Uses") ? Format(e, session.Value(e.Table, e.Id, e.Field)) : Item(session.Value(e.Table, e.Id, e.Field)),
                e => e.Field.EndsWith("Uses") ? Format(e, e.Value) : Item(e.Value));
            scriptReferences = p.ScriptReferences;
            if (scriptIndex is null) warnings.Add("Starting items were previewed without a check of the game's events, so items that events give or take are not listed.");
        }
        void Stats(string module, R0StatProfile? profile)
        {
            if (profile is null) return;
            var p = R0StatRandomizer.Preview(session, profile);
            Add(module, p.FieldEdits, e => Format(e, session.Value(e.Table, e.Id, e.Field)), e => Format(e, e.Value));
        }
        void Proficiency(string module, R0ProficiencyProfile? profile)
        {
            if (profile is null) return;
            var p = R0ProficiencyRandomizer.Preview(session, profile);
            Add(module, p.FieldEdits, e => Format(e, session.Value(e.Table, e.Id, e.Field)), e => Format(e, e.Value));
            warnings.AddRange(p.Warnings);
        }
        Stats("Stat bonuses", plan.StatBonuses);
        Stats("Growth rates", plan.GrowthRates);
        Proficiency("Starting weapon levels", plan.StartingProficiency);
        Proficiency("Weapon growth", plan.ProficiencyGrowth);
        if (locked.Count > 0) warnings.Add($"{locked.Count} locked {(locked.Count == 1 ? "character is" : "characters are")} left as they are.");
        if (plan.WarnBelowLevel > 0) warnings.AddRange(Weak(session, edits, plan.WarnBelowLevel));
        foreach (string zeroed in ZeroedTrainedLevels(session, plan))
            warnings.Add($"{zeroed} would become 0. The character can still attack with it, with very low accuracy; an older profile allows this only when you confirm it.");
        warnings.AddRange(LostSlots(session, edits));

        return new(plan, session.Disc.Sha256, session.ChangeCount, edits.AsReadOnly(), rows.AsReadOnly(), warnings.AsReadOnly(),
            Usability(session, edits), scriptReferences);
    }

    /// <summary>
    /// Applies the whole plan as one undo step. The plan is previewed again first and must give the same result; any character the
    /// plan would leave without a usable weapon stops it. Script-referenced starting items need <paramref name="permitScriptReferences"/>.
    /// </summary>
    public static bool Apply(EditSession session, RandomizerPlanPreview preview, ScriptDependencyIndex? scriptIndex = null, bool permitScriptReferences = false,
        bool allowZeroedTrainedLevels = false)
    {
        if (!string.Equals(preview.SourceSha256, session.Disc.Sha256, StringComparison.OrdinalIgnoreCase) || preview.ChangeCountAtGeneration != session.ChangeCount)
            throw new EditException("The project changed after this preview was made. Preview the plan again.");
        var fresh = Preview(session, preview.Plan, scriptIndex);
        if (!fresh.Edits.SequenceEqual(preview.Edits) || !fresh.Rows.SequenceEqual(preview.Rows) || !fresh.Problems.SequenceEqual(preview.Problems))
            throw new EditException("The preview does not match its plan any more. Preview the plan again.");
        if (fresh.Problems.Count > 0)
            throw new EditException("Some characters would have nothing they can attack or heal with. Lock them or change the plan, then preview again.");
        var zeroed = ZeroedTrainedLevels(session, fresh.Plan);
        if (zeroed.Count > 0 && !allowZeroedTrainedLevels)
            throw new EditException("An older weapon level profile in this plan lowers trained levels to zero (" + string.Join(", ", zeroed) +
                "). Those characters can still attack, with very low accuracy. Confirm that this is what you want, or turn on keeping trained levels, then preview again.");
        if (fresh.ScriptReferences.Count > 0 && !permitScriptReferences)
            throw new EditException("Some new starting items are also given or checked by the game's events. Review them and confirm before applying.");
        return fresh.Edits.Count > 0 && session.Apply(fresh.Edits);
    }

    /// <summary>Characters (not locked) whose trained levels a version 1 weapon level profile in the plan lowers to zero.</summary>
    public static IReadOnlyList<int> ZeroedTrainedCharacters(EditSession session, RandomizerPlan plan)
    {
        var locked = plan.Locked.ToHashSet();
        return new[] { plan.StartingProficiency, plan.ProficiencyGrowth }.OfType<R0ProficiencyProfile>()
            .SelectMany(p => R0ProficiencyRandomizer.ZeroedTrainedChannels(R0ProficiencyRandomizer.Preview(session, p)))
            .Select(c => c.UnitId).Where(id => !locked.Contains(id)).Distinct().Order().ToList();
    }

    /// <summary>Trained levels (above zero) that a version 1 weapon level profile in the plan lowers to zero, for characters not locked.</summary>
    public static IReadOnlyList<string> ZeroedTrainedLevels(EditSession session, RandomizerPlan plan)
    {
        var locked = plan.Locked.ToHashSet();
        return new[] { plan.StartingProficiency, plan.ProficiencyGrowth }.OfType<R0ProficiencyProfile>()
            .SelectMany(p => R0ProficiencyRandomizer.ZeroedTrainedChannels(R0ProficiencyRandomizer.Preview(session, p)))
            .Where(c => !locked.Contains(c.UnitId))
            .Select(c => $"{c.UnitName} {session.Table(c.Table).Field(c.Field).Label}").ToList();
    }

    /// <summary>For each part that is turned on: how many characters it may change (its own list, or everyone when empty), and how many of those are locked.</summary>
    public static IReadOnlyList<(string Part, int Count, int Total, int Locked)> Scopes(EditSession session, RandomizerPlan plan)
    {
        int total = session.Disc.Characters.Count;
        var locked = plan.Locked.ToHashSet();
        (string, int, int, int) Scope(string part, IReadOnlyList<int>? ids)
        {
            var members = ids is { Count: > 0 } ? ids.ToHashSet() : session.Disc.Characters.Select(c => c.Id).ToHashSet();
            return (part, members.Count, total, members.Count(locked.Contains));
        }
        var scopes = new List<(string, int, int, int)>();
        if (plan.StartingItems is { } items) scopes.Add(Scope("Starting items", items.UnitIds));
        if (plan.StatBonuses is { } bonuses) scopes.Add(Scope("Stat bonuses", bonuses.UnitIds));
        if (plan.GrowthRates is { } growths) scopes.Add(Scope("Growth rates", growths.UnitIds));
        if (plan.StartingProficiency is { } levels) scopes.Add(Scope("Starting weapon levels", levels.UnitIds));
        if (plan.ProficiencyGrowth is { } growth) scopes.Add(Scope("Weapon growth", growth.UnitIds));
        return scopes;
    }

    private static string[] SelectedFields(string[] original, string[] selected) =>
        original.ToHashSet(StringComparer.Ordinal).SetEquals(selected) ? (string[])original.Clone() : selected;

    /// <summary>Change the visible settings without replacing the saved algorithm, transfer count or other profile settings.</summary>
    public static R0StatProfile UpdateStatSettings(R0StatProfile original, int seed, R0StatAlgorithm mode, int maximumChange, int[] ids, string[] fields)
        => original with { Seed = seed, Mode = mode, MaximumChange = maximumChange, UnitIds = ids, Fields = SelectedFields(original.Fields, fields) };

    /// <summary>Keep the saved compatibility policy and transfer count; the explicit minimum selects the proficiency schema.</summary>
    public static R0ProficiencyProfile UpdateProficiencySettings(R0ProficiencyProfile original, int seed, R0StatAlgorithm mode,
        int maximumChange, int minimum, int[] ids, string[] fields)
        => original with { Seed = seed, Mode = mode, MaximumChange = maximumChange, UnitIds = ids, Fields = SelectedFields(original.Fields, fields),
            SchemaVersion = minimum == 0 ? 1 : R0ProficiencyProfile.KeepTrainedSchema, TrainedMinimum = minimum };

    public static string ToJson(RandomizerPlan plan) => JsonSerializer.Serialize(plan, Json);

    public static RandomizerPlan FromJson(string json)
    {
        RandomizerPlan? plan;
        try { plan = JsonSerializer.Deserialize<RandomizerPlan>(json, Json); }
        catch (JsonException) { throw new EditException("This file is not a Randomizer plan."); }
        if (plan is null || plan.Version != RandomizerPlan.CurrentVersion || plan.Locked is null)
            throw new EditException("This Randomizer plan was made by a version that this one cannot read.");
        ValidateShape(plan);
        return plan;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static void ValidateShape(RandomizerPlan plan)
    {
        if (plan.Locked is null) throw new EditException("The plan has no locked character list.");
        if (plan.Version != RandomizerPlan.CurrentVersion) throw new EditException("This Randomizer plan was made by a version that this one cannot read.");
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 100 || plan.Name.Any(char.IsControl))
            throw new EditException("Give the plan a short name without control characters.");
        if (plan.WarnBelowLevel is < 0 or > 30) throw new EditException("Warn below a weapon level from 0 (off) to 30.");
        if (plan.Locked.Distinct().Count() != plan.Locked.Count) throw new EditException("A character is locked twice.");
        plan.StatBonuses?.Validate(); plan.GrowthRates?.Validate(); plan.StartingProficiency?.Validate(); plan.ProficiencyGrowth?.Validate();
        if (plan.StartingItems is null && plan.StatBonuses is null && plan.GrowthRates is null && plan.StartingProficiency is null && plan.ProficiencyGrowth is null)
            throw new EditException("Turn on at least one part of the plan.");
        if (plan.StatBonuses is { Target: not R0StatTarget.PersonalBonuses }) throw new EditException("The stat bonus part must use a stat bonus profile.");
        if (plan.GrowthRates is { Target: not R0StatTarget.GrowthRates }) throw new EditException("The growth rate part must use a growth rate profile.");
        if (plan.StartingProficiency is { Target: not R0ProficiencyTarget.StartingProficiencies }) throw new EditException("The starting weapon level part must use a starting weapon level profile.");
        if (plan.ProficiencyGrowth is { Target: not R0ProficiencyTarget.ProficiencyGrowths }) throw new EditException("The weapon growth part must use a weapon growth profile.");
        if (plan.StartingItems is { } items && (items.Seed < 0 ||
            items.UnitIds is { } selected && selected.Distinct().Count() != selected.Length))
            throw new EditException("The starting item seed or character list is invalid.");
    }

    /// <summary>Validate every part before displaying or previewing a saved plan.</summary>
    public static void Check(EditSession session, RandomizerPlan plan)
    {
        ValidateShape(plan);
        var ids = session.Disc.Characters.Select(c => c.Id).ToHashSet();
        foreach (var selection in new[] { plan.Locked, plan.StartingItems?.UnitIds, plan.StatBonuses?.UnitIds,
                     plan.GrowthRates?.UnitIds, plan.StartingProficiency?.UnitIds, plan.ProficiencyGrowth?.UnitIds })
            if (selection is not null && selection.Any(id => !ids.Contains(id)))
                throw new EditException("A character in the plan is not on this disc.");
    }

    /// <summary>
    /// The best starting weapon level among the items a character can use under the loadout check, by <see cref="WeaponFamilies"/>, in stored tenths;
    /// null when nothing carried has a weapon level.
    /// </summary>
    private static int? BestUsableLevel(EditSession session, int unitId, Func<string, string, int> value)
    {
        int? best = null;
        foreach (var slot in Loadouts.Read(session, unitId, value).Where(s => s.Viable))
        {
            if (WeaponFamilies.LevelFor(ItemDecoder.Types[session.Value(EditSession.Items, slot.ItemId, "type")]) is not { } level) continue;
            best = Math.Max(best ?? 0, value(EditSession.Characters, level));
        }
        return best;
    }

    /// <summary>
    /// Weak is not the same as unusable: a character with a low but trained level can still fight. These warnings name characters
    /// whose best usable carried weapon would end below the chosen level; they never stop the plan.
    /// </summary>
    private static IEnumerable<string> Weak(EditSession session, IReadOnlyList<FieldEdit> edits, int levels)
    {
        foreach (var unit in session.Disc.Characters.OrderBy(c => c.Id))
        {
            var mine = edits.Where(e => e.Id == unit.Id && e.Table == EditSession.Characters).ToList();
            if (mine.Count == 0) continue;
            int After(string table, string field) => mine.LastOrDefault(e => e.Field == field)?.Value ?? session.Value(table, unit.Id, field);
            if (BestUsableLevel(session, unit.Id, After) is int best && best < levels * 10)
                yield return $"{unit.Name}: best usable weapon level would be {best / 10m:0.#}, below the {levels} you chose. Weak, but still usable.";
        }
    }

    /// <summary>
    /// Characters who could attack or heal with something they carry before the plan and with nothing after it (<see cref="Loadouts"/>):
    /// the class cannot use any weapon they carry, or a bow or crossbow has lost its ammunition. A weapon level of zero is not a
    /// failure: the game still lets the character attack, with very low accuracy (observed in game). Characters with nothing to judge
    /// before the plan are left alone, and low levels are warnings (<see cref="RandomizerPlan.WarnBelowLevel"/>).
    /// </summary>
    private static IReadOnlyList<RandomizerPlanProblem> Usability(EditSession session, IReadOnlyList<FieldEdit> edits)
    {
        var problems = new List<RandomizerPlanProblem>();
        var byUnit = edits.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var unit in session.Disc.Characters.OrderBy(c => c.Id))
        {
            if (!byUnit.TryGetValue(unit.Id, out var mine)) continue;
            int After(string table, string field) => mine.LastOrDefault(e => e.Table == table && e.Field == field)?.Value ?? session.Value(table, unit.Id, field);
            var before = Loadouts.Read(session, unit.Id);
            var after = Loadouts.Read(session, unit.Id, After);
            if (before.Any(s => s.Viable) && !after.Any(s => s.Viable))
                problems.Add(new(unit.Id, unit.Name, "Would have nothing to attack or heal with (" +
                    string.Join("; ", after.Where(s => s.Use != LoadoutUse.Other).Select(s => $"{s.Item}: {s.Reason}")) +
                    "). Lock this character or leave its items alone."));
        }
        return problems.AsReadOnly();
    }

    /// <summary>Carried items the character could use before the plan and cannot after it, while something else still works.</summary>
    private static IEnumerable<string> LostSlots(EditSession session, IReadOnlyList<FieldEdit> edits)
    {
        foreach (var group in edits.Where(e => e.Table == EditSession.Characters).GroupBy(e => e.Id).OrderBy(g => g.Key))
        {
            int After(string table, string field) => group.LastOrDefault(e => e.Field == field)?.Value ?? session.Value(table, group.Key, field);
            var before = Loadouts.Read(session, group.Key).ToDictionary(s => s.Slot);
            var after = Loadouts.Read(session, group.Key, After);
            if (!after.Any(s => s.Viable)) continue; // reported as a problem instead
            string name = session.Disc.Characters.First(c => c.Id == group.Key).Name;
            foreach (var slot in after.Where(s => !s.Viable && s.Use != LoadoutUse.Other && before.TryGetValue(s.Slot, out var was) && was.Viable))
                yield return $"{name}: {slot.Item} in slot {slot.Slot} could not be used ({slot.Reason}); other items still work.";
        }
    }
}
