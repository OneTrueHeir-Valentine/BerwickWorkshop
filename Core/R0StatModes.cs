using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

/// <summary>Changes personal bonuses or personal growth rates, not class-adjusted combat values.
/// Schema 1 profiles work on stored numbers, so their Mind growth changes are in stored points
/// (two per percentage point). Schema 2 profiles change growth rates in the percentages the game
/// uses and draw from the "v2" streams below; they are only used for growth rates. Saved schema 1
/// profiles are replayed unchanged. Budget conservation keeps the selected total, which is not a
/// claim of equal game balance.</summary>
public enum R0StatAlgorithm { BoundedVariance, BudgetRedistribution }
public enum R0StatTarget { GrowthRates, PersonalBonuses }

public sealed record R0StatProfile(
    string Name, R0StatAlgorithm Mode, R0StatTarget Target, int Seed,
    int MaximumChange, int TransferAttempts, int[] UnitIds, string[] Fields)
{
    public const int StoredPointSchema = 1, PercentSchema = 2;
    public int SchemaVersion { get; init; } = StoredPointSchema;
    /// <summary>A schema 1 growth profile: Mind growth is changed in stored points.</summary>
    [JsonIgnore] public bool UsesStoredMindPoints => SchemaVersion == StoredPointSchema && Target == R0StatTarget.GrowthRates;
    public string StreamAlgorithm { get; init; } = R0SeedStreams.Algorithm;

    public static readonly string[] GrowthFieldKeys =
        ["hpGrowth", "strengthGrowth", "defenseGrowth", "agilityGrowth", "mindGrowth"];
    public static readonly string[] BonusFieldKeys =
        ["hpBonus", "strengthBonus", "defenseBonus", "agilityBonus", "mindBonus"];

    public static R0StatProfile VariancePreset(int seed = 1729) =>
        new("Bounded growth variance", R0StatAlgorithm.BoundedVariance,
            R0StatTarget.GrowthRates, seed, 10, 24, [], (string[])GrowthFieldKeys.Clone()) { SchemaVersion = PercentSchema };

    public static R0StatProfile BudgetPreset(int seed = 1729) =>
        new("Conserved growth budget", R0StatAlgorithm.BudgetRedistribution,
            R0StatTarget.GrowthRates, seed, 10, 24, [], (string[])GrowthFieldKeys.Clone()) { SchemaVersion = PercentSchema };

    public void Validate()
    {
        if (SchemaVersion is not (StoredPointSchema or PercentSchema) || StreamAlgorithm != R0SeedStreams.Algorithm)
            throw new EditException("Unrecognized R0 stat-profile version or deterministic algorithm.");
        if (SchemaVersion == PercentSchema && Target != R0StatTarget.GrowthRates)
            throw new EditException("Percentage profiles change growth rates only. Personal bonus profiles use version 1.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Name.Any(char.IsControl))
            throw new EditException("The profile needs a short name without control characters.");
        if (!Enum.IsDefined(Mode) || !Enum.IsDefined(Target) || Seed < 0 || MaximumChange is < 1 or > 63
            || TransferAttempts is < 1 or > 256)
            throw new EditException("Invalid R0 stat profile mode, seed, magnitude or transfer attempts.");
        if (Fields is null || UnitIds is null || Fields.Length == 0 || Fields.Length > 5 || UnitIds.Length > 2048)
            throw new EditException("Invalid R0 stat profile field or unit selection.");
        string[] allowed = Target == R0StatTarget.GrowthRates ? GrowthFieldKeys : BonusFieldKeys;
        if (Fields.Any(field => field is null || !allowed.Contains(field, StringComparer.Ordinal))
            || Fields.Distinct(StringComparer.Ordinal).Count() != Fields.Length)
            throw new EditException("A selected field is not a permitted personal stat in this profile.");
        if (Mode == R0StatAlgorithm.BudgetRedistribution && Fields.Length < 2)
            throw new EditException("Budget redistribution needs at least two selected stats.");
        if (UnitIds.Any(id => id is < 1 or > 65535) || UnitIds.Distinct().Count() != UnitIds.Length)
            throw new EditException("Unit IDs must be unique positive values.");
    }
}

/// <summary>Profiles are distinct JSON files, never executable code or implicit project edits.</summary>
public static class R0StatProfileFiles
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false
    };

    public static string ToJson(R0StatProfile profile)
    {
        profile.Validate();
        return JsonSerializer.Serialize(profile, Options);
    }

    public static R0StatProfile FromJson(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new EditException("R0 stat profile is too large.");
        try
        {
            var profile = JsonSerializer.Deserialize<R0StatProfile>(json, Options)
                ?? throw new EditException("Empty R0 stat profile.");
            profile.Validate();
            return profile;
        }
        catch (JsonException ex) { throw new EditException("Invalid R0 stat-profile JSON: " + ex.Message); }
    }

    public static void SaveNew(string path, R0StatProfile profile)
    {
        string json = ToJson(profile);
        if (!path.EndsWith(".bwr0stats.json", StringComparison.OrdinalIgnoreCase))
            throw new EditException("Use a .bwr0stats.json file for the R0 stat profile.");
        // CreateNew avoids overwriting an existing profile. A failed write can leave a partial
        // new file, but cannot corrupt an existing profile or the user's game/project.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        writer.Write(json);
    }

    public static R0StatProfile Load(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length > 65536) throw new EditException("R0 stat profile exceeds 64 KiB.");
        return FromJson(File.ReadAllText(path, Encoding.UTF8));
    }
}

/// <summary>One changed value. Growth changes from a schema 2 profile are in percent; schema 1
/// changes and personal bonuses are stored numbers.</summary>
public sealed record R0StatChange(int UnitId, string UnitName, string Table, string Field,
    int Before, int After);

public sealed record R0StatPreview(int SchemaVersion, string StreamAlgorithm, string SourceSha256,
    int ChangeCountAtGeneration, R0StatProfile Profile,
    IReadOnlyList<FieldEdit> FieldEdits, IReadOnlyList<R0ExpectedValue> ExpectedValues,
    IReadOnlyList<R0StatChange> Changes)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    });
}

public static class R0StatMath
{
    // All calculations are integer-only. Field and record selections use independent R0 streams.
    public static int[] Bounded(int seed, string table, int unitId, IReadOnlyList<string> fields,
        IReadOnlyList<int> original, IReadOnlyList<int> minima, IReadOnlyList<int> maxima, int magnitude, string stream = "v1")
    {
        int[] result = original.ToArray();
        for (int i = 0; i < fields.Count; i++)
        {
            int draw = R0SeedStreams.Choice(seed, $"stats/variance/{stream}/" + fields[i], table, unitId, 0, magnitude * 2 + 1);
            result[i] = Math.Clamp(original[i] + draw - magnitude, minima[i], maxima[i]);
        }
        return result;
    }

    /// <summary>Per-unit exact conservation of the selected values (stored numbers for schema 1, percentages for schema 2).
    /// Each field stays within magnitude of its original and the source field's legal bounds.
    /// Attempts may result in no changes for saturated/degenerate input.</summary>
    public static int[] Redistribute(int seed, string table, int unitId, IReadOnlyList<string> fields,
        IReadOnlyList<int> original, IReadOnlyList<int> minima, IReadOnlyList<int> maxima,
        int magnitude, int attempts, string stream = "v1")
    {
        int n = original.Count;
        if (n < 2) throw new EditException("At least two stats are required for redistribution.");
        int[] result = original.ToArray();
        int[] lows = Enumerable.Range(0, n).Select(i => Math.Max(minima[i], original[i] - magnitude)).ToArray();
        int[] highs = Enumerable.Range(0, n).Select(i => Math.Min(maxima[i], original[i] + magnitude)).ToArray();
        for (int step = 0; step < attempts; step++)
        {
            int donor = R0SeedStreams.Choice(seed, $"stats/budget/{stream}/donor", table, unitId, step, n);
            int receiverOrdinal = R0SeedStreams.Choice(seed, $"stats/budget/{stream}/receiver", table, unitId, step, n - 1);
            int receiver = receiverOrdinal >= donor ? receiverOrdinal + 1 : receiverOrdinal;
            int available = Math.Min(result[donor] - lows[donor], highs[receiver] - result[receiver]);
            if (available <= 0) continue;
            int amount = 1 + R0SeedStreams.Choice(seed, $"stats/budget/{stream}/amount", table, unitId, step, available);
            result[donor] -= amount;
            result[receiver] += amount;
        }
        return result;
    }
}

public static class R0StatRandomizer
{
    public static R0StatPreview Preview(EditSession session, R0StatProfile profile)
    {
        profile.Validate();
        // Snapshot mutable profile lists so later UI edits cannot silently redefine this preview.
        profile = profile with { UnitIds = (int[])profile.UnitIds.Clone(), Fields = (string[])profile.Fields.Clone() };
        string table = profile.Target == R0StatTarget.GrowthRates ? EditSession.Growths : EditSession.Characters;
        var fields = profile.Fields.Select(key => session.Table(table).Field(key)).ToArray();
        var units = session.Disc.Characters.OrderBy(c => c.Id).ToArray();
        var lookup = units.ToDictionary(c => c.Id);
        if (profile.UnitIds.Any(id => !lookup.ContainsKey(id)))
            throw new EditException("One or more chosen units do not exist on this source disc.");
        var ids = profile.UnitIds.Length == 0 ? units.Select(u => u.Id).ToHashSet() : profile.UnitIds.ToHashSet();
        var edits = new List<FieldEdit>();
        var expected = new List<R0ExpectedValue>();
        var changes = new List<R0StatChange>();
        // Schema 2 works in the percentages the game uses; Mind growth is converted from and back to
        // its stored form only at this boundary. Expected values stay in stored form.
        bool percent = profile.SchemaVersion == R0StatProfile.PercentSchema;
        string stream = percent ? "v2" : "v1";
        bool[] doubled = fields.Select(f => percent && f.StoredDoubled).ToArray();
        bool mindSelected = table == EditSession.Growths && profile.Fields.Contains(MindGrowth.Field, StringComparer.Ordinal);
        foreach (var unit in units.Where(u => ids.Contains(u.Id)))
        {
            if (mindSelected && MindGrowth.HasUnsupportedEncoding(session.EditedBytes(EditSession.Growths, unit.Id)))
                throw new EditException(MindGrowth.UnsupportedEncodingMessage(unit.Name) + " Leave Mind or this character out of the selection.");
            int[] stored = fields.Select(f => session.Value(table, unit.Id, f.Key)).ToArray();
            int[] before = stored.Select((v, i) => doubled[i] ? MindGrowth.Percent(v) : v).ToArray();
            int[] lows = fields.Select((f, i) => doubled[i] ? MindGrowth.Percent(f.Min) : f.Min).ToArray();
            int[] highs = fields.Select((f, i) => doubled[i] ? MindGrowth.MaxPercent : f.Max).ToArray();
            int[] after = profile.Mode == R0StatAlgorithm.BoundedVariance
                ? R0StatMath.Bounded(profile.Seed, table, unit.Id, profile.Fields, before, lows, highs, profile.MaximumChange, stream)
                : R0StatMath.Redistribute(profile.Seed, table, unit.Id, profile.Fields, before, lows, highs, profile.MaximumChange, profile.TransferAttempts, stream);
            if (profile.Mode == R0StatAlgorithm.BudgetRedistribution && before.Sum() != after.Sum())
                throw new EditException("Conserved budget invariant failed; no changes were applied.");
            for (int i = 0; i < fields.Length; i++)
            {
                if (before[i] == after[i]) continue;
                int value = doubled[i] ? MindGrowth.Stored(after[i], stored[i]) : after[i];
                edits.Add(new FieldEdit(table, unit.Id, fields[i].Key, value));
                expected.Add(new R0ExpectedValue(table, unit.Id, fields[i].Key, stored[i]));
                changes.Add(new R0StatChange(unit.Id, unit.Name, table, fields[i].Key, before[i], after[i]));
            }
        }
        return new R0StatPreview(profile.SchemaVersion, R0SeedStreams.Algorithm, session.Disc.Sha256, session.ChangeCount,
            profile, edits.AsReadOnly(), expected.AsReadOnly(), changes.AsReadOnly());
    }

    public static bool Apply(EditSession session, R0StatPreview preview)
    {
        if (preview.Profile is null || preview.SchemaVersion != preview.Profile.SchemaVersion
            || preview.StreamAlgorithm != R0SeedStreams.Algorithm)
            throw new EditException("Unsupported R0 stat-preview version.");
        if (!string.Equals(preview.SourceSha256, session.Disc.Sha256, StringComparison.OrdinalIgnoreCase)
            || preview.ChangeCountAtGeneration != session.ChangeCount)
            throw new EditException("The project or source changed after preview. Regenerate before applying.");
        foreach (var v in preview.ExpectedValues)
            if (session.Value(v.Table, v.RecordId, v.Field) != v.Value)
                throw new EditException("A previewed value changed. Regenerate before applying.");
        // Fail closed if a preview or profile was mutated after generation.
        var fresh = Preview(session, preview.Profile);
        if (!fresh.FieldEdits.SequenceEqual(preview.FieldEdits)
            || !fresh.ExpectedValues.SequenceEqual(preview.ExpectedValues)
            || !fresh.Changes.SequenceEqual(preview.Changes))
            throw new EditException("The preview contents no longer match the deterministic profile.");
        return session.Apply(fresh.FieldEdits);
    }
}
