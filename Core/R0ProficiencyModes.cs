using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

/// <summary>Starting proficiency points and proficiency growth increments are different
/// underlying fields. R0 never treats a shared shield growth channel as three independent
/// growths, and it never assumes that a class can use equipment solely from an item ID.</summary>
public enum R0ProficiencyTarget { StartingProficiencies, ProficiencyGrowths }
public enum R0ProficiencyCompatibility { PreserveTrainedChannels, AllowClassPermittedChannels }

public sealed record R0ProficiencyProfile(
    string Name, R0StatAlgorithm Mode, R0ProficiencyTarget Target,
    R0ProficiencyCompatibility Compatibility, int Seed, int MaximumChange,
    int TransferAttempts, int[] UnitIds, string[] Fields)
{
    /// <summary>
    /// Version 1 is the original behaviour, kept exactly for replay: a trained level can be lowered to zero. Version 2 keeps
    /// every level the character already has training in at or above <see cref="TrainedMinimum"/>.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;
    public string StreamAlgorithm { get; init; } = R0SeedStreams.Algorithm;

    /// <summary>
    /// Version 2 only: the lowest a trained channel may go, in whole weapon levels (starting levels) or growth steps (weapon
    /// growth). A channel already below it is never lowered further and never raised by this rule. Zero in version 1 files.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TrainedMinimum { get; init; }

    public const int KeepTrainedSchema = 2;

    /// <summary>The same profile as version 2, keeping trained levels at one or more. Its preview must be made again.</summary>
    public R0ProficiencyProfile Migrated() => this with { SchemaVersion = KeepTrainedSchema, TrainedMinimum = Math.Max(TrainedMinimum, 1) };

    // Starting skill numbers are stored in tenths; the growth record contains one
    // 4-bit channel for all shields, not a separate value for each shield size.
    public static readonly string[] StartingKeys = [
        "knifeLevel", "swordLevel", "spearLevel", "axeLevel", "bowLevel", "crossbowLevel",
        "fireLevel", "thunderLevel", "windLevel", "holyLevel", "darkLevel",
        "smallshieldLevel", "mediumshieldLevel", "largeshieldLevel"
    ];
    public static readonly string[] GrowthKeys = [
        "knifeGrowth", "swordGrowth", "spearGrowth", "axeGrowth", "bowGrowth", "crossbowGrowth",
        "fireGrowth", "thunderGrowth", "windGrowth", "holyGrowth", "darkGrowth", "shieldGrowth"
    ];
    public static R0ProficiencyProfile StartingVariance(int seed = 1729) =>
        new("Starting proficiency variance", R0StatAlgorithm.BoundedVariance,
            R0ProficiencyTarget.StartingProficiencies, R0ProficiencyCompatibility.PreserveTrainedChannels,
            seed, 5, 32, [], (string[])StartingKeys.Clone());
    public static R0ProficiencyProfile StartingBudget(int seed = 1729) =>
        StartingVariance(seed) with { Name = "Starting proficiency budget", Mode = R0StatAlgorithm.BudgetRedistribution };
    public static R0ProficiencyProfile GrowthVariance(int seed = 1729) =>
        new("Proficiency growth variance", R0StatAlgorithm.BoundedVariance,
            R0ProficiencyTarget.ProficiencyGrowths, R0ProficiencyCompatibility.PreserveTrainedChannels,
            seed, 2, 32, [], (string[])GrowthKeys.Clone());
    public static R0ProficiencyProfile GrowthBudget(int seed = 1729) =>
        GrowthVariance(seed) with { Name = "Proficiency growth budget", Mode = R0StatAlgorithm.BudgetRedistribution };

    public void Validate()
    {
        if (SchemaVersion is not (1 or KeepTrainedSchema) || StreamAlgorithm != R0SeedStreams.Algorithm)
            throw new EditException("Unsupported R0 proficiency profile format or stream algorithm.");
        if (SchemaVersion == 1 && TrainedMinimum != 0)
            throw new EditException("A trained-level minimum needs a version 2 profile.");
        if (SchemaVersion == KeepTrainedSchema && TrainedMinimum is < 1 or > 15)
            throw new EditException("Keep trained levels at 1 to 15.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl))
            throw new EditException("Use a short profile name without control characters.");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Target) || !Enum.IsDefined(Compatibility) ||
            Seed < 0 || MaximumChange is < 1 or > 50 || TransferAttempts is < 1 or > 512)
            throw new EditException("Invalid proficiency algorithm, compatibility, seed, change bound or transfer count.");
        string[] allowed = Target == R0ProficiencyTarget.StartingProficiencies ? StartingKeys : GrowthKeys;
        if (Fields is null || UnitIds is null || Fields.Length is < 1 or > 14 ||
            UnitIds.Length > 2048 || Fields.Any(x => x is null || !allowed.Contains(x, StringComparer.Ordinal)) ||
            Fields.Distinct(StringComparer.Ordinal).Count() != Fields.Length)
            throw new EditException("Choose unique proficiency fields supported by this target.");
        if (Mode == R0StatAlgorithm.BudgetRedistribution && Fields.Length < 2)
            throw new EditException("A proficiency budget requires at least two fields.");
        if (UnitIds.Any(id => id is < 1 or > 65535) || UnitIds.Distinct().Count() != UnitIds.Length)
            throw new EditException("Choose valid, unique character IDs.");
    }
}

public static class R0ProficiencyProfileFiles
{
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };
    public static string ToJson(R0ProficiencyProfile profile)
    {
        profile.Validate();
        return JsonSerializer.Serialize(profile, Options);
    }
    public static R0ProficiencyProfile FromJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new EditException("Proficiency profile exceeds 64 KiB.");
        try
        {
            var p = JsonSerializer.Deserialize<R0ProficiencyProfile>(json, Options)
                ?? throw new EditException("Empty proficiency profile.");
            p.Validate();
            return p;
        }
        catch (JsonException ex) { throw new EditException("Invalid proficiency profile: " + ex.Message); }
    }
    public static void SaveNew(string path, R0ProficiencyProfile profile)
    {
        if (!path.EndsWith(".bwr0proficiency.json", StringComparison.OrdinalIgnoreCase))
            throw new EditException("Choose a .bwr0proficiency.json profile filename.");
        string contents = ToJson(profile);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(contents);
    }
    public static R0ProficiencyProfile Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new EditException("Proficiency profile exceeds 64 KiB.");
        return FromJson(File.ReadAllText(path, Encoding.UTF8));
    }
}

public sealed record R0ProficiencyChange(int UnitId, string UnitName, string Table,
    string Field, int BeforeRaw, int AfterRaw);

public sealed record R0ProficiencyPreview(int SchemaVersion, string StreamAlgorithm,
    string SourceSha256, int ChangeCountAtGeneration, R0ProficiencyProfile Profile,
    IReadOnlyList<FieldEdit> FieldEdits, IReadOnlyList<R0ExpectedValue> ExpectedValues,
    IReadOnlyList<R0ProficiencyChange> Changes, IReadOnlyList<string> Warnings)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    });
}

public static class R0ProficiencyMath
{
    /// <summary>
    /// The lowest value a channel may be given. Version 1 uses the field minimum, as it always did. Version 2 keeps a
    /// trained channel (above zero) at the trained minimum, or at its own value when that is already lower, and never
    /// above the class limit. The bound goes into the algorithm itself, so nothing is clamped afterwards.
    /// </summary>
    public static int Floor(int schemaVersion, int trainedMinimum, int before, int fieldMinimum, int maximum, int step)
    {
        if (schemaVersion != R0ProficiencyProfile.KeepTrainedSchema || before <= 0) return fieldMinimum;
        return Math.Max(fieldMinimum, Math.Min(before, Math.Min(maximum, trainedMinimum * step)));
    }

    // Steps: starting proficiencies use 10 stored tenths per point; growths use
    // one 4-bit increment per 10 percentage points shown by Workshop.
    public static int[] Bounded(int seed, string table, int unitId,
        IReadOnlyList<string> keys, IReadOnlyList<int> original,
        IReadOnlyList<int> min, IReadOnlyList<int> max, int maximumChange, int step)
    {
        int[] result = original.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            int choices = 2 * maximumChange + 1;
            int draw = R0SeedStreams.Choice(seed, "proficiency/variance/v1/" + keys[i],
                table, unitId, 0, choices) - maximumChange;
            result[i] = Math.Clamp(original[i] + draw * step, min[i], max[i]);
            // If the original contains fractional tenths, keep that fractional
            // part: do not introduce a non-step-sized cap correction.
            int actualDelta = result[i] - original[i];
            result[i] = original[i] + (actualDelta / step) * step;
        }
        return result;
    }

    public static int[] Redistribute(int seed, string table, int unitId,
        IReadOnlyList<string> keys, IReadOnlyList<int> original,
        IReadOnlyList<int> min, IReadOnlyList<int> max, int maximumChange, int attempts, int step)
    {
        if (original.Count < 2) return original.ToArray();
        var result = original.ToArray();
        var lower = Enumerable.Range(0, result.Length)
            .Select(i => Math.Max(min[i], original[i] - maximumChange * step)).ToArray();
        var upper = Enumerable.Range(0, result.Length)
            .Select(i => Math.Min(max[i], original[i] + maximumChange * step)).ToArray();
        for (int k = 0; k < attempts; k++)
        {
            int donor = R0SeedStreams.Choice(seed, "proficiency/budget/v1/donor", table, unitId, k, result.Length);
            int n = R0SeedStreams.Choice(seed, "proficiency/budget/v1/receiver", table, unitId, k, result.Length - 1);
            int recipient = n >= donor ? n + 1 : n;
            int canGive = (result[donor] - lower[donor]) / step;
            int canTake = (upper[recipient] - result[recipient]) / step;
            int available = Math.Min(canGive, canTake);
            if (available <= 0) continue;
            int count = 1 + R0SeedStreams.Choice(seed, "proficiency/budget/v1/points", table, unitId, k, available);
            result[donor] -= count * step;
            result[recipient] += count * step;
        }
        return result;
    }
}

public static class R0ProficiencyRandomizer
{
    /// <summary>Changes in a preview that take a trained level (above zero) down to zero. Version 2 previews never have any.</summary>
    public static IReadOnlyList<R0ProficiencyChange> ZeroedTrainedChannels(R0ProficiencyPreview preview) =>
        preview.Changes.Where(c => c.BeforeRaw > 0 && c.AfterRaw == 0).ToList();

    // A class cap describes maximum permitted proficiency. It is not, by itself,
    // evidence that a previously untrained character can equip every listed item.
    private static string CapField(string field)
    {
        string name = field.EndsWith("Level", StringComparison.Ordinal) ? field[..^5] : field[..^6];
        if (name == "shield") return "";
        return name + "Cap";
    }
    private static int ClassCap(EditSession session, int classId, string field)
    {
        if (field == "shieldGrowth")
            return new[] { "smallshieldCap", "mediumshieldCap", "largeshieldCap" }
                .Max(key => session.Value(EditSession.Classes, classId, key));
        return session.Value(EditSession.Classes, classId, CapField(field));
    }

    public static R0ProficiencyPreview Preview(EditSession session, R0ProficiencyProfile profile)
    {
        profile.Validate();
        profile = profile with { UnitIds = (int[])profile.UnitIds.Clone(), Fields = (string[])profile.Fields.Clone() };
        string table = profile.Target == R0ProficiencyTarget.StartingProficiencies
            ? EditSession.Characters : EditSession.Growths;
        int step = profile.Target == R0ProficiencyTarget.StartingProficiencies ? 10 : 1;
        var fieldKeys = profile.Fields.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var resolved = fieldKeys.Select(x => session.Table(table).Field(x)).ToArray();
        var characters = session.Disc.Characters.OrderBy(c => c.Id).ToArray();
        var lookup = characters.ToDictionary(c => c.Id);
        if (profile.UnitIds.Any(id => !lookup.ContainsKey(id)))
            throw new EditException("At least one selected character was not found in the original disc.");
        HashSet<int> selected = profile.UnitIds.Length == 0
            ? characters.Select(c => c.Id).ToHashSet() : profile.UnitIds.ToHashSet();
        var edits = new List<FieldEdit>();
        var expected = new List<R0ExpectedValue>();
        var changes = new List<R0ProficiencyChange>();
        var warnings = new List<string>();
        foreach (var unit in characters.Where(c => selected.Contains(c.Id)))
        {
            int classId = session.Value(EditSession.Characters, unit.Id, "class");
            string className = session.Disc.Classes.FirstOrDefault(c => c.Id == classId)?.Name ?? $"class {classId}";
            var indexes = new List<int>();
            var current = new List<int>();
            var minima = new List<int>();
            var maxima = new List<int>();
            for (int i = 0; i < resolved.Length; i++)
            {
                int before = session.Value(table, unit.Id, resolved[i].Key);
                int cap = ClassCap(session, classId, fieldKeys[i]);
                if (cap == 0 || (before == 0 && profile.Compatibility == R0ProficiencyCompatibility.PreserveTrainedChannels))
                {
                    if (before > 0 && cap == 0)
                        warnings.Add($"{unit.Name}: {fieldKeys[i]} was left unchanged because the {className} class cannot use it.");
                    continue;
                }
                int maximum = Math.Min(resolved[i].Max, profile.Target == R0ProficiencyTarget.StartingProficiencies
                    ? cap * 10 : 15);
                if (before > maximum)
                {
                    warnings.Add($"{unit.Name}: {fieldKeys[i]} was left unchanged because it is above the {className} class limit.");
                    continue;
                }
                indexes.Add(i); current.Add(before); maxima.Add(maximum);
                minima.Add(R0ProficiencyMath.Floor(profile.SchemaVersion, profile.TrainedMinimum, before, resolved[i].Min, maximum, step));
            }
            if (indexes.Count == 0) continue;
            string[] eligibleFields = indexes.Select(i => fieldKeys[i]).ToArray();
            int[] next = profile.Mode == R0StatAlgorithm.BoundedVariance
                ? R0ProficiencyMath.Bounded(profile.Seed, table, unit.Id, eligibleFields,
                    current, minima, maxima, profile.MaximumChange, step)
                : R0ProficiencyMath.Redistribute(profile.Seed, table, unit.Id, eligibleFields,
                    current, minima, maxima, profile.MaximumChange, profile.TransferAttempts, step);
            if (profile.Mode == R0StatAlgorithm.BudgetRedistribution && current.Sum() != next.Sum())
                throw new EditException("Per-character proficiency budget failed conservation.");
            for (int p = 0; p < indexes.Count; p++)
            {
                if (current[p] == next[p]) continue;
                string field = fieldKeys[indexes[p]];
                edits.Add(new FieldEdit(table, unit.Id, field, next[p]));
                expected.Add(new R0ExpectedValue(table, unit.Id, field, current[p]));
                changes.Add(new R0ProficiencyChange(unit.Id, unit.Name, table, field, current[p], next[p]));
            }
        }
        if (profile.Compatibility == R0ProficiencyCompatibility.AllowClassPermittedChannels)
            warnings.Add("Experimental: class caps do not prove weapon training, equipment permission, or story safety. Review every newly opened proficiency.");
        return new R0ProficiencyPreview(profile.SchemaVersion, R0SeedStreams.Algorithm, session.Disc.Sha256,
            session.ChangeCount, profile, edits.AsReadOnly(), expected.AsReadOnly(),
            changes.AsReadOnly(), warnings.AsReadOnly());
    }

    public static bool Apply(EditSession session, R0ProficiencyPreview preview, bool allowZeroedTrainedLevels = false)
    {
        if (preview.SchemaVersion is not (1 or R0ProficiencyProfile.KeepTrainedSchema) || preview.SchemaVersion != preview.Profile.SchemaVersion ||
            preview.StreamAlgorithm != R0SeedStreams.Algorithm ||
            !string.Equals(preview.SourceSha256, session.Disc.Sha256, StringComparison.OrdinalIgnoreCase) ||
            preview.ChangeCountAtGeneration != session.ChangeCount)
            throw new EditException("R0 proficiency source or project changed since preview. Regenerate first.");
        foreach (var prior in preview.ExpectedValues)
            if (session.Value(prior.Table, prior.RecordId, prior.Field) != prior.Value)
                throw new EditException("A proficiency value changed since preview. Regenerate first.");
        var fresh = Preview(session, preview.Profile);
        if (!fresh.FieldEdits.SequenceEqual(preview.FieldEdits) ||
            !fresh.ExpectedValues.SequenceEqual(preview.ExpectedValues) ||
            !fresh.Changes.SequenceEqual(preview.Changes) ||
            !fresh.Warnings.SequenceEqual(preview.Warnings))
            throw new EditException("R0 proficiency preview is no longer reproducible.");
        // A level of zero still lets a character attack (with very low accuracy), so an older profile may do this, but only when
        // the user confirms it; version 2 profiles never do.
        if (ZeroedTrainedChannels(fresh).Count > 0 && !allowZeroedTrainedLevels)
            throw new EditException("This version 1 profile would lower a trained weapon level to zero (" +
                string.Join(", ", ZeroedTrainedChannels(fresh).Select(c => $"{c.UnitName} {c.Field}")) +
                "). Those characters can still attack, with very low accuracy. Confirm that this is what you want, or convert the profile to version 2, which keeps trained levels, and preview again.");
        return session.Apply(fresh.FieldEdits);
    }
}
