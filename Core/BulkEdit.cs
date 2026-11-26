using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

/// <summary>What a bulk edit step does to a field's value, in the units the editor shows.</summary>
public enum BulkOperation { Set, Add, Multiply, Clamp }

/// <summary>How the final value is rounded when it falls between two values the field can hold.</summary>
public enum BulkRounding { Nearest, Down, Up }

/// <summary>One step of a recipe. Amount and Upper are in shown units (a weapon level of 2.5, a Mind growth of 15%).</summary>
public sealed record BulkStep(string Field, BulkOperation Operation, decimal Amount, decimal? Upper = null);

/// <summary>
/// Which records a recipe changes. A record matches when it is named, has one of the item types, or belongs to one of the
/// item groups; excluded records never match. Types and groups are read from the project as it is when the preview is made.
/// Spare records and broken-weapon placeholders are left out of type and group selections unless <paramref name="IncludeUnused"/>
/// is set or they are named, in which case the preview warns about them. Personal weapons, and records whose original range differs
/// from the usual original range of the rest of the selection when a step changes range, are left out of type and group selections
/// unless they are named or <paramref name="IncludeSpecial"/> is set; then they are changed with a warning.
/// </summary>
public sealed record BulkSelection(IReadOnlyList<int> Ids, IReadOnlyList<string> ItemTypes, IReadOnlyList<string> Groups,
    IReadOnlyList<int> Excluded, bool IncludeUnused = false, bool IncludeSpecial = false);

/// <summary>A saved, repeatable set of changes. It holds record numbers and field names only, never file paths or disc offsets.</summary>
public sealed record BulkRecipe(int Version, string Name, string Table, BulkSelection Selection, IReadOnlyList<BulkStep> Steps,
    BulkRounding Rounding = BulkRounding.Nearest)
{
    /// <summary>
    /// Identifies the fields and groups the recipe was written for (<see cref="BulkEdit.Schema"/>). Record numbers are the same in
    /// both supported editions; a recipe is refused if the fields it names have changed meaning since it was saved.
    /// </summary>
    public string Schema { get; init; } = "";

    /// <summary>
    /// Version 2: steps are worked out one after another without limits, and only the final value must fit the field.
    /// Version 1 (private prototype only) refused a step that left the field's range even when a later step clamped it.
    /// </summary>
    public const int CurrentVersion = 2;
}

public enum BulkRowStatus { Changed, Unchanged, Blocked }

/// <summary>One field of one record in a preview, with values as the editor shows them and the unit they are in.</summary>
public sealed record BulkRow(int Id, string Record, string Field, string FieldLabel, string Unit, string Original, string Current, string Proposed,
    BulkRowStatus Status, string Reason)
{
    /// <summary>How the row is read aloud where a grid announces the whole row.</summary>
    public override string ToString() => $"{Record}, {FieldLabel}: {Current} to {(Proposed.Length > 0 ? Proposed : "blocked")}";
}

/// <summary>A record the selection left out, and why.</summary>
public sealed record BulkSkipped(int Id, string Record, string Reason);

/// <summary>The result of resolving a recipe against a project. It can be applied only while the project still resolves the recipe the same way.</summary>
public sealed record BulkPreview(BulkRecipe Recipe, IReadOnlyList<BulkRow> Rows, IReadOnlyList<BulkSkipped> Skipped, IReadOnlyList<string> Warnings,
    IReadOnlyList<FieldEdit> Edits)
{
    public int ChangedRecords => Rows.Where(r => r.Status == BulkRowStatus.Changed).Select(r => r.Id).Distinct().Count();
    public int BlockedRecords => Rows.Where(r => r.Status == BulkRowStatus.Blocked).Select(r => r.Id).Distinct().Count();
    public bool CanApply => BlockedRecords == 0 && Edits.Count > 0;
}

/// <summary>Named item groups, worked out from each record's data rather than from its name.</summary>
public static class BulkGroups
{
    public const string Bows = "Bows", Crossbows = "Crossbows", Shields = "Shields",
        ConsumableThrowing = "Throwing weapons used up when thrown", ReusableThrowing = "Throwing weapons that wear down";

    public static readonly IReadOnlyList<string> All = [Bows, Crossbows, Shields, ConsumableThrowing, ReusableThrowing];

    private static readonly int Bow = Array.IndexOf(ItemDecoder.Types, "Bow"), Crossbow = Array.IndexOf(ItemDecoder.Types, "Crossbow");
    private static readonly int[] ShieldTypes = [Array.IndexOf(ItemDecoder.Types, "Small shield"), Array.IndexOf(ItemDecoder.Types, "Medium shield"), Array.IndexOf(ItemDecoder.Types, "Large shield")];
    // Weapon types that attack (Knife through Dark) and the three shield sizes. Healing, accessories and consumables are not weapons here.
    private static readonly HashSet<int> WeaponTypes = [.. Enumerable.Range(0, Array.IndexOf(ItemDecoder.Types, "Dark") + 1), .. ShieldTypes];
    private const string Thrown = "effect60", UsesInstead = "effect19", Unbreakable = "effect34", Personal = "effect32";

    /// <summary>A weapon made for one character (the "Personal weapon" flag), such as Serenia's bow.</summary>
    public static bool IsPersonal(EditSession session, int id) => session.Value(EditSession.Items, id, Personal) == 1;

    public static bool Contains(EditSession session, string group, int id)
    {
        int V(string key) => session.Value(EditSession.Items, id, key);
        int type = V("type");
        return group switch
        {
            Bows => type == Bow,
            Crossbows => type == Crossbow,
            Shields => ShieldTypes.Contains(type),
            ConsumableThrowing => V(Thrown) == 1 && V(UsesInstead) == 1,
            ReusableThrowing => V(Thrown) == 1 && V(UsesInstead) == 0,
            _ => throw new EditException($"There is no item group called \"{group}\"."),
        };
    }

    /// <summary>
    /// A record the game uses only as the broken form of a weapon or shield: no might, no price, no uses and unbreakable.
    /// Unpriced, unbreakable magic and staves keep their might or are not weapons here, so they do not match.
    /// </summary>
    public static bool IsBrokenPlaceholder(EditSession session, int id)
    {
        int V(string key) => session.Value(EditSession.Items, id, key);
        return WeaponTypes.Contains(V("type")) && V("might") == 0 && V("price") == 0 && V("uses") == 0 && V(Unbreakable) == 1;
    }
}

/// <summary>
/// Deterministic changes to many records at once. A recipe is resolved into a preview that lists every record and field,
/// then applied through <see cref="EditSession.Apply(IReadOnlyList{FieldEdit})"/> as a single undo step.
/// </summary>
public static class BulkEdit
{
    /// <summary>Fields a recipe may change, per table. Experimental fields, references, flags and skills are not offered yet.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SupportedFields = new Dictionary<string, IReadOnlyList<string>>
    {
        [EditSession.Items] =
        [
            "might", "accuracy", "weight", "minRange", "maxRange", "critical", "level", "price", "durability", "uses",
            "strengthBonus", "agilityBonus", "defenseBonus", "mindBonus", "hitBonus", "avoidBonus", "criticalEvade",
            "fireResist", "thunderResist", "windResist", "holyResist", "darkResist",
        ],
        [EditSession.Characters] =
        [
            "level", "hpBonus", "strengthBonus", "defenseBonus", "agilityBonus", "mindBonus", "luck",
            .. CharacterFields.Record.Where(f => f.Group == "Weapon levels").Select(f => f.Key),
        ],
        [EditSession.Growths] =
        [
            "hpGrowth", "strengthGrowth", "defenseGrowth", "agilityGrowth", MindGrowth.Field,
            .. CharacterFields.Growth.Where(f => f.Group == "Weapon growth").Select(f => f.Key),
        ],
    };

    /// <summary>The unit a field's shown value is in.</summary>
    public static string Unit(string table, TableField field) => table switch
    {
        EditSession.Growths => "%",
        EditSession.Characters when field.Group == "Weapon levels" => "weapon level",
        EditSession.Characters when field.Key.EndsWith("Bonus") || field.Key == "luck" => "points above the class base",
        EditSession.Items when field.Key == "price" => "gold",
        EditSession.Items when field.Key is "minRange" or "maxRange" => "hexes",
        EditSession.Items when field.Key == "durability" => "grade",
        _ => "",
    };

    /// <summary>Records whose name contains the text, or whose number is the text (decimal or 0x hexadecimal).</summary>
    public static IReadOnlyList<(int Id, string Name)> Search(EditSession session, string table, string text)
    {
        text = text.Trim();
        int? number = int.TryParse(text, out int d) ? d
            : text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[2..], NumberStyles.HexNumber, null, out int h) ? h : null;
        return session.Table(table).Records.Keys.Order().Select(id => (id, RecordName(session, table, id)))
            .Where(r => r.id == number || (text.Length > 0 && r.Item2.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static BulkPreview Preview(EditSession session, BulkRecipe recipe)
    {
        ValidateRecipe(session, recipe);
        var table = session.Table(recipe.Table);
        var rows = new List<BulkRow>();
        var skipped = new List<BulkSkipped>();
        var warnings = new List<string>();
        var edits = new List<FieldEdit>();
        var types = recipe.Selection.ItemTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = recipe.Selection.Ids.ToHashSet();
        var excluded = recipe.Selection.Excluded.ToHashSet();
        var fields = recipe.Steps.Select(s => s.Field).Distinct().Select(table.Field).ToList();
        bool items = recipe.Table == EditSession.Items;
        bool MatchesFilter(int id) => items && (types.Contains(ItemDecoder.Types[session.Value(EditSession.Items, id, "type")]) ||
                                                recipe.Selection.Groups.Any(g => BulkGroups.Contains(session, g, id)));
        // Ranges are judged on the original disc's values, so changing ranges (with this or another recipe) never changes which
        // records a range recipe picks.
        (int Min, int Max) Range(int id) => (session.OriginalValue(EditSession.Items, id, "minRange"), session.OriginalValue(EditSession.Items, id, "maxRange"));
        string ShowRange((int Min, int Max) r) =>
            $"{FieldCodec.Format(table.Field("minRange"), r.Min)} to {FieldCodec.Format(table.Field("maxRange"), r.Max)}";

        // The original range most of the ordinary records in the type and group selection share. A record with another range
        // (Serenia's bow among the bows) is kept out of a range change unless it is named or special records are included, so a
        // group recipe never overwrites it unnoticed. When two or more ranges are equally common there is no usual range: nothing
        // is left out for its range, and the preview says so.
        (int Min, int Max)? usualRange = null;
        if (items && fields.Any(f => f.Key is "minRange" or "maxRange"))
        {
            var counts = table.Records.Keys.Where(id => MatchesFilter(id) && !named.Contains(id) && !excluded.Contains(id) &&
                    !session.EditedItem(id).IsUnused && !BulkGroups.IsBrokenPlaceholder(session, id) && !BulkGroups.IsPersonal(session, id))
                .GroupBy(Range).Select(g => (g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Key).ToList();
            if (counts.Count > 0 && (counts.Count == 1 || counts[0].Count > counts[1].Count)) usualRange = counts[0].Key;
            else if (counts.Count > 1)
                warnings.Add("These records share no single usual range (" + string.Join(", ", counts.TakeWhile(c => c.Count == counts[0].Count)
                    .Select(c => $"{ShowRange(c.Key)}: {c.Count}")) + "), so none was left out for its range. Name or exclude records to narrow the selection.");
        }
        bool special = recipe.Selection.IncludeSpecial;

        foreach (int id in table.Records.Keys.Order())
        {
            string name = RecordName(session, recipe.Table, id);
            bool byFilter = MatchesFilter(id);
            if (!named.Contains(id) && !byFilter) continue;
            if (excluded.Contains(id)) { skipped.Add(new(id, name, "Excluded by the recipe.")); continue; }
            if (items && (session.EditedItem(id).IsUnused || BulkGroups.IsBrokenPlaceholder(session, id)))
            {
                string what = session.EditedItem(id).IsUnused ? "a spare record that the game does not use" : "the broken form of a weapon or shield, not equipment to use";
                if (!named.Contains(id) && !recipe.Selection.IncludeUnused) { skipped.Add(new(id, name, $"Left out: {what}.")); continue; }
                warnings.Add($"{name} (item {id}) is {what}.");
            }
            if (items && BulkGroups.IsPersonal(session, id))
            {
                if (!named.Contains(id) && !special) { skipped.Add(new(id, name, "Left out: a personal weapon, made for one character. Name it, or include personal weapons and own ranges, to change it.")); continue; }
                warnings.Add($"{name} (item {id}) is a personal weapon, made for one character.");
            }
            if (usualRange is { } usual && Range(id) != usual)
            {
                if (!named.Contains(id) && !special)
                {
                    skipped.Add(new(id, name, $"Left out: its range ({ShowRange(Range(id))}) differs from the usual {ShowRange(usual)} of this selection. Name it, or include personal weapons and own ranges, to change it."));
                    continue;
                }
                warnings.Add($"{name} (item {id}) has its own range ({ShowRange(Range(id))}), not the usual {ShowRange(usual)}.");
            }
            if (recipe.Table == EditSession.Growths && fields.Any(f => f.Key == MindGrowth.Field) &&
                MindGrowth.HasUnsupportedEncoding(session.EditedBytes(EditSession.Growths, id)))
            {
                skipped.Add(new(id, name, MindGrowth.UnsupportedEncodingMessage(name)));
                continue;
            }

            var current = table.Fields.ToDictionary(f => f.Key, f => session.Value(recipe.Table, id, f.Key));
            var proposed = new Dictionary<string, int>(current);
            var reasons = new Dictionary<string, string>();
            foreach (var field in fields)
            {
                var steps = recipe.Steps.Where(s => s.Field == field.Key).ToList();
                if (Calculate(field, current[field.Key], steps, recipe.Rounding, out int value, out string reason)) proposed[field.Key] = value;
                else reasons[field.Key] = reason;
            }
            string recordProblem = reasons.Count == 0 ? table.Validate(proposed) : "";
            foreach (var field in fields)
            {
                int before = current[field.Key], after = proposed[field.Key];
                string reason = reasons.GetValueOrDefault(field.Key) ?? recordProblem;
                var status = reason.Length > 0 ? BulkRowStatus.Blocked : after == before ? BulkRowStatus.Unchanged : BulkRowStatus.Changed;
                rows.Add(new(id, name, field.Key, field.Label, Unit(recipe.Table, field), FieldCodec.Format(field, session.OriginalValue(recipe.Table, id, field.Key)),
                    FieldCodec.Format(field, before), status == BulkRowStatus.Blocked ? "" : FieldCodec.Format(field, after), status, reason));
                if (status == BulkRowStatus.Changed) edits.Add(new(recipe.Table, id, field.Key, after));
            }
        }
        return new(recipe, rows.AsReadOnly(), skipped.AsReadOnly(), warnings.AsReadOnly(), edits.AsReadOnly());
    }

    /// <summary>
    /// Applies a preview as one undo step. The recipe is resolved again first; if the project now resolves it differently
    /// (other values, other records in a type or group, a changed or tampered preview), nothing is applied. Refuses while any
    /// row is blocked. Returns false when nothing would change.
    /// </summary>
    public static bool Apply(EditSession session, BulkPreview preview)
    {
        var fresh = Preview(session, preview.Recipe);
        if (!fresh.Rows.SequenceEqual(preview.Rows) || !fresh.Skipped.SequenceEqual(preview.Skipped) ||
            !fresh.Warnings.SequenceEqual(preview.Warnings) || !fresh.Edits.SequenceEqual(preview.Edits))
            throw new EditException("The project changed after this preview was made, or the preview does not match its recipe. Preview the recipe again before applying it.");
        if (fresh.BlockedRecords > 0)
            throw new EditException("Some records cannot take this change. Exclude them or change the recipe, then preview again.");
        return fresh.Edits.Count > 0 && session.Apply(fresh.Edits);
    }

    public static byte[] Save(BulkRecipe recipe) => JsonSerializer.SerializeToUtf8Bytes(recipe with { Schema = Schema(recipe.Table) }, Json);

    /// <summary>
    /// A short identity of a table's bulk-editable fields (key, place in the record, range, units, choices) and the item groups.
    /// It changes only when what a recipe's field names or group names mean changes.
    /// </summary>
    public static string Schema(string table)
    {
        if (!SupportedFields.TryGetValue(table, out var keys)) return "";
        var t = table switch
        {
            EditSession.Items => ItemEncoder.Fields,
            EditSession.Characters => CharacterFields.Record,
            EditSession.Growths => CharacterFields.Growth,
            _ => [],
        };
        var text = new System.Text.StringBuilder(table);
        foreach (string key in keys)
        {
            var f = t.Single(x => x.Key == key);
            text.Append($"|{f.Key},{f.Offset},{f.Bit},{f.Width},{f.Min},{f.Max},{f.Signed},{f.Display.ToString(CultureInfo.InvariantCulture)},{f.StoredDoubled},{string.Join("/", f.Choices ?? [])}");
        }
        if (table == EditSession.Items) text.Append("|groups:" + string.Join("/", BulkGroups.All) + ":data-v3");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>A named selection saved on its own, to reuse in other recipes for the same table.</summary>
    public sealed record SavedSelection(int Version, string Name, string Table, BulkSelection Selection);

    public static byte[] SaveSelection(string name, string table, BulkSelection selection) =>
        JsonSerializer.SerializeToUtf8Bytes(new SavedSelection(1, name, table, selection), Json);

    public static SavedSelection LoadSelection(ReadOnlySpan<byte> json)
    {
        SavedSelection? saved;
        try { saved = JsonSerializer.Deserialize<SavedSelection>(json, Json); }
        catch (JsonException) { throw new EditException("This file is not a saved selection."); }
        if (saved is null || saved.Version != 1 || saved.Selection is null || saved.Selection.Ids is null || saved.Selection.ItemTypes is null ||
            saved.Selection.Groups is null || saved.Selection.Excluded is null || !SupportedFields.ContainsKey(saved.Table ?? ""))
            throw new EditException("This saved selection is incomplete or was made by a version that this one cannot read.");
        return saved;
    }

    public static BulkRecipe Load(ReadOnlySpan<byte> json)
    {
        BulkRecipe? recipe;
        try { recipe = JsonSerializer.Deserialize<BulkRecipe>(json, Json); }
        catch (JsonException) { throw new EditException("This file is not a bulk edit recipe."); }
        if (recipe is { Version: 1 })
            throw new EditException("This is a version 1 bulk edit recipe from an early test build. Steps in version 2 are worked out together and only the final value is checked, so the old recipe cannot be read as it is. Make the recipe again with the same records and steps, and preview it before applying.");
        if (recipe is null || recipe.Version != BulkRecipe.CurrentVersion)
            throw new EditException("This recipe was made by a version that this one cannot read.");
        if (recipe.Selection is null || recipe.Steps is null || recipe.Selection.Ids is null || recipe.Selection.ItemTypes is null ||
            recipe.Selection.Groups is null || recipe.Selection.Excluded is null || string.IsNullOrEmpty(recipe.Schema) ||
            recipe.Steps.Any(step => step is null))
            throw new EditException("This bulk edit recipe is incomplete.");
        return recipe;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void ValidateRecipe(EditSession session, BulkRecipe recipe)
    {
        if (recipe.Version != BulkRecipe.CurrentVersion) throw new EditException("This recipe was made by a version that this one cannot read.");
        if (recipe.Table is not { } table || !SupportedFields.TryGetValue(table, out var allowed)) throw new EditException("Bulk edits do not support this table yet.");
        if (!string.IsNullOrEmpty(recipe.Schema) && recipe.Schema != Schema(table))
            throw new EditException("This recipe was made for fields or groups that have changed since (for example how groups treat personal weapons and items with their own range), so it is not applied. Load its selection and steps into a new recipe, check the preview, and save it again.");
        if (recipe.Steps.Count == 0) throw new EditException("The recipe has no changes.");
        if (!Enum.IsDefined(recipe.Rounding)) throw new EditException("The recipe has an unknown rounding option.");
        var s = recipe.Selection;
        if (s.Ids.Count == 0 && s.ItemTypes.Count == 0 && s.Groups.Count == 0) throw new EditException("The recipe selects no records.");
        ValidateSelection(session, table, s);
        foreach (var step in recipe.Steps)
        {
            if (step is null || !allowed.Contains(step.Field)) throw new EditException("The recipe contains an unsupported field.");
            if (!Enum.IsDefined(step.Operation)) throw new EditException("The recipe has an unknown operation.");
            var field = session.Table(table).Field(step.Field);
            if ((field.Choices is not null || field.IsFlag) && step.Operation != BulkOperation.Set)
                throw new EditException($"{field.Label} is a choice, so it can only be set.");
            if (step.Operation == BulkOperation.Clamp && (step.Upper is not { } upper || upper < step.Amount))
                throw new EditException($"The limits for {field.Label} need a lowest value that is not above the highest.");
        }
    }

    public static void ValidateSelection(EditSession session, string table, BulkSelection s)
    {
        if (table is null || !SupportedFields.ContainsKey(table)) throw new EditException("Bulk edits do not support this table yet.");
        if ((s.ItemTypes.Count > 0 || s.Groups.Count > 0) && table != EditSession.Items) throw new EditException("Only items can be selected by type or group.");
        foreach (string type in s.ItemTypes)
            if (!ItemDecoder.Types.Contains(type, StringComparer.OrdinalIgnoreCase)) throw new EditException($"There is no item type called \"{type}\".");
        foreach (string group in s.Groups)
            if (!BulkGroups.All.Contains(group)) throw new EditException($"There is no item group called \"{group}\".");
        foreach (int id in s.Ids.Concat(s.Excluded))
            if (!session.Table(table).Records.ContainsKey(id)) throw new EditException($"Record {id} is not in this table.");
    }

    private static string RecordName(EditSession session, string table, int id) => table switch
    {
        EditSession.Items => session.EditedItem(id).Name,
        EditSession.Characters or EditSession.Growths => session.EditedCharacter(id).Name,
        _ => $"Record {id}",
    };

    /// <summary>
    /// Works out a field's new stored value. The steps run in order on the shown value without limits; the result is rounded
    /// once and must then fit the field. Nothing is clipped unless the recipe has a Clamp step.
    /// </summary>
    private static bool Calculate(TableField field, int stored, IReadOnlyList<BulkStep> steps, BulkRounding rounding, out int value, out string reason)
    {
        value = stored; reason = "";
        if (field.Choices is { } choices)
        {
            var step = steps[^1];
            if (step.Amount != decimal.Truncate(step.Amount) || step.Amount < 0 || step.Amount >= choices.Count)
            { reason = $"{field.Label} has no choice number {step.Amount}."; return false; }
            value = field.ChoiceValue((int)step.Amount);
            return true;
        }
        decimal shown = field.StoredDoubled ? MindGrowth.Percent(stored) : stored * (decimal)field.Display;
        try
        {
            shown = RunSteps(shown, steps);
        }
        catch (OverflowException)
        {
            reason = $"{field.Label} becomes too large to calculate with these steps.";
            return false;
        }
        if (field.StoredDoubled)
        {
            decimal percent = Round(shown, rounding);
            if (percent < MindGrowth.Percent(field.Min) || percent > Math.Min(MindGrowth.Percent(field.Max), MindGrowth.MaxPercent))
            { reason = $"{field.Label} would be {percent}, outside {FieldCodec.RangeText(field)}."; return false; }
            value = MindGrowth.Stored((int)percent, stored);
            return true;
        }
        decimal raw;
        try { raw = Round(shown / (decimal)field.Display, rounding); }
        catch (OverflowException)
        { reason = $"{field.Label} becomes too large to calculate with these steps."; return false; }
        if (raw < field.Min || raw > field.Max || raw == field.SpecialValue)
        {
            reason = $"{field.Label} would be {(raw * (decimal)field.Display).ToString(CultureInfo.InvariantCulture)}, outside {FieldCodec.RangeText(field)}.";
            return false;
        }
        value = (int)raw;
        return true;
    }

    /// <summary>Runs the steps in order on the shown value, with no limits along the way.</summary>
    private static decimal RunSteps(decimal shown, IReadOnlyList<BulkStep> steps)
    {
        foreach (var step in steps)
            shown = step.Operation switch
            {
                BulkOperation.Set => step.Amount,
                BulkOperation.Add => shown + step.Amount,
                BulkOperation.Multiply => shown * step.Amount,
                BulkOperation.Clamp => Math.Clamp(shown, step.Amount, step.Upper!.Value),
                _ => throw new ArgumentOutOfRangeException(nameof(steps)),
            };
        return shown;
    }

    private static decimal Round(decimal value, BulkRounding rounding) => rounding switch
    {
        BulkRounding.Down => Math.Floor(value),
        BulkRounding.Up => Math.Ceiling(value),
        _ => Math.Round(value, MidpointRounding.AwayFromZero),
    };
}
