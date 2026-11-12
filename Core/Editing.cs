using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

/// <summary>A packed bit field inside a fixed-size record. Shown value = stored value x Display.</summary>
public sealed record TableField(string Key, string Label, int Offset, int Bit, int Width, int Min, int Max)
{
    public bool Signed { get; init; }
    public double Display { get; init; } = 1;
    public string Group { get; init; } = "";
    /// <summary>Names for each stored value from Min upward, when the field is a choice.</summary>
    public IReadOnlyList<string>? Choices { get; init; }
    /// <summary>The value is an item ID (0 means empty).</summary>
    public bool ItemReference { get; init; }
    public bool IsFlag => Width == 1 && Min == 0 && Max == 1;
}

public sealed class EditException(string message) : Exception(message);

public static class FieldCodec
{
    public static int Read(ReadOnlySpan<byte> record, TableField field)
    {
        int raw = ItemDecoder.Bits(record, field.Offset, field.Bit, field.Width);
        return field.Signed && raw >= 1 << (field.Width - 1) ? raw - (1 << field.Width) : raw;
    }

    public static void Write(Span<byte> record, TableField field, int value)
    {
        if (value < field.Min || value > field.Max)
            throw new EditException($"{field.Label} must be between {Format(field, field.Min)} and {Format(field, field.Max)}.");
        int bytes = (field.Bit + field.Width + 7) / 8;
        if (field.Offset + bytes > record.Length) throw new InvalidDataException("A record is incomplete.");
        uint current = 0;
        for (int n = 0; n < bytes; n++) current |= (uint)record[field.Offset + n] << (8 * n);
        uint mask = ((1u << field.Width) - 1) << field.Bit;
        current = (current & ~mask) | ((uint)value << field.Bit & mask);
        for (int n = 0; n < bytes; n++) record[field.Offset + n] = (byte)(current >> (8 * n));
    }

    public static string Format(TableField field, int value) =>
        field.IsFlag ? (value != 0 ? "Yes" : "No")
        : field.Choices is { } choices && value - field.Min >= 0 && value - field.Min < choices.Count ? choices[value - field.Min]
        : field.Display == 1 ? value.ToString(CultureInfo.InvariantCulture)
        : (value * field.Display).ToString(field.Display < 1 ? "0.#" : "0", CultureInfo.InvariantCulture);

    public static bool TryParse(TableField field, string text, out int value, out string error)
    {
        value = 0; error = "";
        text = text.Trim().Replace(",", "");
        if (field.Choices is { } choices)
        {
            int index = choices.ToList().FindIndex(c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) { value = field.Min + index; return true; }
        }
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal shown))
        { error = $"{field.Label} must be a number."; return false; }
        decimal raw = shown / (decimal)field.Display;
        if (raw != decimal.Truncate(raw))
        {
            error = field.Display > 1 ? $"{field.Label} must be a multiple of {field.Display}." : field.Display < 1 ? $"{field.Label} can have at most one decimal place." : $"{field.Label} must be a whole number.";
            return false;
        }
        if (raw < field.Min || raw > field.Max) { error = $"{field.Label} must be between {Format(field, field.Min)} and {Format(field, field.Max)}."; return false; }
        value = (int)raw;
        return true;
    }
}

public static class ItemEncoder
{
    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("might", "Might", 0, 5, 6, 0, 63),
        new("accuracy", "Accuracy", 1, 7, 7, 0, 127),
        new("weight", "Weight", 2, 6, 5, 0, 31),
        new("minRange", "Minimum range", 4, 0, 4, 0, 15),
        new("maxRange", "Maximum range", 3, 3, 5, 0, 31),
        new("critical", "Critical", 5, 0, 7, 0, 127),
        new("level", "Level", 6, 6, 6, 0, 63),
        new("price", "Base price", 8, 0, 16, 0, 65535),
        new("durability", "Durability", 21, 4, 3, 0, 6) { Choices = ["S", "A", "B", "C", "D", "E", "F"] },
        new("uses", "Durability pool", 5, 7, 7, 0, 127),
    });

    public static TableField Field(string key) =>
        Fields.FirstOrDefault(f => f.Key == key) ?? throw new EditException($"Unknown item field \"{key}\".");
    public static int Read(ReadOnlySpan<byte> record, TableField field) => FieldCodec.Read(record, field);
    public static void Write(Span<byte> record, TableField field, int value) => FieldCodec.Write(record, field, value);

    public static string Validate(IReadOnlyDictionary<string, int> values)
    {
        foreach (var (key, value) in values)
        {
            var field = Field(key);
            if (value < field.Min || value > field.Max) return $"{field.Label} must be between {field.Min} and {field.Max}.";
        }
        if (values.TryGetValue("minRange", out int min) && values.TryGetValue("maxRange", out int max) && min > max)
            return "Minimum range cannot be greater than maximum range.";
        return "";
    }
}

public static class CharacterFields
{
    private static readonly (string Name, int Offset, int Bit)[] Levels =
    [
        ("Knife", 36, 0), ("Sword", 37, 2), ("Spear", 38, 4), ("Axe", 40, 0), ("Bow", 41, 2), ("Crossbow", 42, 4),
        ("Fire", 44, 0), ("Thunder", 45, 2), ("Wind", 46, 4), ("Holy", 48, 0), ("Dark", 49, 2),
        ("Small shield", 50, 4), ("Medium shield", 52, 0), ("Large shield", 53, 2),
    ];

    public static string Key(string weapon, string suffix) => weapon.ToLowerInvariant().Replace(" ", "") + suffix;

    /// <summary>Starting inventory slots that are proven in use on the disc (the record has room for eight).</summary>
    public const int ItemSlots = 5;

    public static IEnumerable<TableField> SkillFlags(int offset, string group) =>
        CharacterDecoder.SkillBits.Select((name, i) => (name, i)).Where(s => s.name.Length > 0)
            .Select(s => new TableField($"skill{s.i}", s.name, offset + s.i / 8, s.i % 8, 1, 0, 1) { Group = group });

    private static IEnumerable<TableField> ItemSlotFields() => Enumerable.Range(0, ItemSlots).SelectMany(slot => new[]
    {
        new TableField($"item{slot + 1}", $"Slot {slot + 1}", 0xBC + slot * 8, 0, 16, 0, 0xFFFF) { Group = "Starting items", ItemReference = true },
        new TableField($"item{slot + 1}Uses", $"Slot {slot + 1} durability or uses", 0xBC + slot * 8 + 2, 4, 8, 0, 255) { Group = "Starting items" },
    });

    /// <summary>Fields in the starting unit record.</summary>
    public static IReadOnlyList<TableField> Record { get; } = Array.AsReadOnly(new TableField[]
    {
        new("level", "Level", 20, 0, 6, 1, 63),
        new("hpBonus", "HP bonus", 22, 4, 7, 0, 127),
        new("strengthBonus", "Strength bonus", 23, 3, 5, -16, 15) { Signed = true },
        new("defenseBonus", "Defense bonus", 25, 2, 5, -16, 15) { Signed = true },
        new("agilityBonus", "Agility bonus", 24, 0, 5, -16, 15) { Signed = true },
        new("mindBonus", "Mind bonus", 25, 7, 5, -16, 15) { Signed = true },
    }.Concat(Levels.Select(l => new TableField(Key(l.Name, "Level"), l.Name, l.Offset, l.Bit, 10, 0, 1023) { Display = 0.1, Group = "Weapon levels" }))
     .Concat(SkillFlags(56, "Personal skills")).Concat(ItemSlotFields()).ToArray());

    /// <summary>Fields in the 32-byte growth record.</summary>
    public static IReadOnlyList<TableField> Growth { get; } = Array.AsReadOnly(new TableField[]
    {
        new("hpGrowth", "HP growth", 0, 0, 7, 0, 127),
        new("strengthGrowth", "Strength growth", 0, 7, 7, 0, 127),
        new("defenseGrowth", "Defense growth", 1, 6, 7, 0, 127),
        new("agilityGrowth", "Agility growth", 4, 2, 7, 0, 127),
        new("mindGrowth", "Mind growth (stored value)", 5, 0, 7, 0, 127),
        new("bracket", "Growth bracket", 2, 5, 2, 1, 3) { Choices = ["None", "Loose", "Tight"] },
    }.Concat(CharacterDecoder.GrowthWeapons.Select((w, i) => new TableField(Key(w, "Growth"), w, 8 + i / 2, 4 * (i % 2), 4, 0, 15) { Display = 10, Group = "Weapon growth" })).ToArray());
}

public static class ClassFields
{
    private static readonly (string Name, int Offset, int Bit)[] Caps =
    [
        ("Knife", 28, 7), ("Sword", 29, 5), ("Spear", 30, 3), ("Axe", 31, 1), ("Bow", 32, 0), ("Crossbow", 32, 6),
        ("Fire", 33, 4), ("Thunder", 34, 2), ("Wind", 35, 0), ("Holy", 36, 0), ("Dark", 36, 6),
        ("Small shield", 37, 4), ("Medium shield", 38, 2), ("Large shield", 39, 0),
    ];

    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("hp", "HP", 2, 5, 5, 0, 31),
        new("strength", "Strength", 0, 0, 5, 0, 31),
        new("defense", "Defense", 1, 2, 5, 0, 31),
        new("agility", "Agility", 0, 5, 5, 0, 31),
        new("mind", "Mind", 2, 0, 5, 0, 31),
        new("move", "Move", 8, 0, 4, 0, 15),
        new("experience", "Experience value", 4, 0, 7, 0, 127),
    }.Concat(Caps.Select(c => new TableField(CharacterFields.Key(c.Name, "Cap"), c.Name, c.Offset, c.Bit, 6, 0, 63) { Group = "Weapon level caps" }))
     .Concat(CharacterFields.SkillFlags(12, "Class skills")).ToArray());
}

/// <summary>One editable table: its records as read from the disc and the fields that may change.</summary>
public sealed class EditTable(string name, string noun, IReadOnlyList<TableField> fields, IReadOnlyDictionary<int, ReadOnlyMemory<byte>> records,
    Func<IReadOnlyDictionary<string, int>, string>? validate = null)
{
    public string Name { get; } = name;
    public string Noun { get; } = noun;
    public IReadOnlyList<TableField> Fields { get; } = fields;
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> Records { get; } = records;
    public TableField Field(string key) => Fields.FirstOrDefault(f => f.Key == key) ?? throw new EditException($"Unknown {Noun} field \"{key}\".");
    public string Validate(IReadOnlyDictionary<string, int> values) => validate?.Invoke(values) ?? "";
}

public sealed record FieldChange(string Table, int Id, string Field, int Original, int Value);
public sealed record FieldEdit(string Table, int Id, string Field, int Value);

public sealed class EditSession
{
    public const string Items = "items", Characters = "characters", Growths = "growths", Classes = "classes";
    private static readonly string[] TableOrder = [Items, Characters, Growths, Classes];

    private sealed record Step(IReadOnlyList<(string Table, int Id, string Field, int Before, int After)> Edits);

    private readonly InspectionResult disc;
    private readonly Dictionary<string, EditTable> tables;
    private readonly Dictionary<int, ItemRecord> originalItems;
    private readonly Dictionary<int, CharacterRecord> originalCharacters;
    private readonly Dictionary<int, ClassRecord> originalClasses;
    private readonly Dictionary<(string Table, int Id, string Field), int> values = [];
    private readonly List<Step> steps = [];
    private int position, savedPosition;

    public EditSession(InspectionResult disc)
    {
        if (!disc.Supported) throw new EditException("Only a verified disc can be edited.");
        this.disc = disc;
        originalItems = disc.Items.ToDictionary(i => i.Id);
        originalCharacters = disc.Characters.ToDictionary(c => c.Id);
        originalClasses = disc.Classes.ToDictionary(c => c.Id);
        tables = new()
        {
            [Items] = new(Items, "item", ItemEncoder.Fields, disc.ItemBytes, ItemEncoder.Validate),
            [Characters] = new(Characters, "character", CharacterFields.Record, disc.UnitBytes, values =>
            {
                foreach (var field in CharacterFields.Record.Where(f => f.ItemReference))
                    if (values.TryGetValue(field.Key, out int item) && item != 0 && !disc.ItemBytes.ContainsKey(item))
                        return $"{field.Label}: there is no item 0x{item:X3}.";
                return "";
            }),
            [Growths] = new(Growths, "character", CharacterFields.Growth, disc.GrowthBytes),
            [Classes] = new(Classes, "class", ClassFields.Fields, disc.ClassBytes),
        };
    }

    public InspectionResult Disc => disc;
    public string? FilePath { get; private set; }
    public bool IsDirty => position != savedPosition;
    public bool CanUndo => position > 0;
    public bool CanRedo => position < steps.Count;
    public int ChangeCount => values.Count;

    public EditTable Table(string name) => tables.TryGetValue(name, out var t) ? t : throw new EditException("This project contains changes this version cannot open.");

    private ReadOnlyMemory<byte> Record(string table, int id) =>
        Table(table).Records.TryGetValue(id, out var bytes) ? bytes : throw new EditException($"{Capitalize(Table(table).Noun)} 0x{id:X3} is not on this disc.");
    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    public int OriginalValue(string table, int id, string field) => FieldCodec.Read(Record(table, id).Span, Table(table).Field(field));
    public int Value(string table, int id, string field) => values.TryGetValue((table, id, field), out int v) ? v : OriginalValue(table, id, field);
    public bool IsEdited(string table, int id) => values.Keys.Any(k => k.Table == table && k.Id == id);

    public byte[] EditedBytes(string table, int id)
    {
        var bytes = Record(table, id).ToArray();
        var t = Table(table);
        foreach (var ((tableName, recordId, field), value) in values)
            if (tableName == table && recordId == id) FieldCodec.Write(bytes, t.Field(field), value);
        return bytes;
    }

    public IReadOnlyList<(string Table, int Id)> EditedRecords =>
        values.Keys.Select(k => (k.Table, k.Id)).Distinct().OrderBy(k => Array.IndexOf(TableOrder, k.Table)).ThenBy(k => k.Id).ToList();

    public IReadOnlyList<FieldChange> Changes =>
        values.OrderBy(v => Array.IndexOf(TableOrder, v.Key.Table)).ThenBy(v => v.Key.Id)
              .ThenBy(v => Table(v.Key.Table).Fields.ToList().FindIndex(f => f.Key == v.Key.Field))
              .Select(v => new FieldChange(v.Key.Table, v.Key.Id, v.Key.Field, OriginalValue(v.Key.Table, v.Key.Id, v.Key.Field), v.Value)).ToList();

    // Item shortcuts kept for the item pages and existing callers.
    public int OriginalValue(int id, string field) => OriginalValue(Items, id, field);
    public int Value(int id, string field) => Value(Items, id, field);
    public bool IsEdited(int id) => IsEdited(Items, id);
    public byte[] EditedBytes(int id) => EditedBytes(Items, id);
    public bool Apply(int id, IReadOnlyDictionary<string, int> newValues) => Apply(Items, id, newValues);
    public void Revert(int id) => Revert(Items, id);

    public ItemRecord EditedItem(int id)
    {
        var original = originalItems[id];
        return IsEdited(Items, id) ? ItemDecoder.Decode(id, original.Name, EditedBytes(Items, id)) : original;
    }
    public IReadOnlyList<ItemRecord> EditedItems() => disc.Items.Select(i => EditedItem(i.Id)).ToList();

    public CharacterRecord EditedCharacter(int id)
    {
        var original = originalCharacters[id];
        if (!IsEdited(Characters, id) && !IsEdited(Growths, id)) return original;
        var names = disc.Items.ToDictionary(i => i.Id, i => i.DisplayName);
        var decoded = CharacterDecoder.Decode(new(id, original.Name, 0), EditedBytes(Characters, id), EditedBytes(Growths, id), names);
        return decoded with { ClassName = original.ClassName };
    }
    public IReadOnlyList<CharacterRecord> EditedCharacters() => disc.Characters.Select(c => EditedCharacter(c.Id)).ToList();

    public ClassRecord EditedClass(int id)
    {
        var original = originalClasses[id];
        return IsEdited(Classes, id) ? ClassDecoder.Decode(id, original.Name, EditedBytes(Classes, id)) : original;
    }
    public IReadOnlyList<ClassRecord> EditedClasses() => disc.Classes.Select(c => EditedClass(c.Id)).ToList();

    public bool Apply(string table, int id, IReadOnlyDictionary<string, int> newValues) =>
        Apply(newValues.Select(v => new FieldEdit(table, id, v.Key, v.Value)).ToList());

    /// <summary>Applies field values, possibly across several records and tables, as a single undoable step. Returns false when nothing changed.</summary>
    public bool Apply(IReadOnlyList<FieldEdit> edits)
    {
        foreach (var group in edits.GroupBy(e => (e.Table, e.Id)))
        {
            var t = Table(group.Key.Table);
            _ = Record(group.Key.Table, group.Key.Id);
            var merged = t.Fields.ToDictionary(f => f.Key, f => Value(group.Key.Table, group.Key.Id, f.Key));
            foreach (var e in group)
            {
                var field = t.Field(e.Field);
                if (e.Value < field.Min || e.Value > field.Max)
                    throw new EditException($"{field.Label} must be between {FieldCodec.Format(field, field.Min)} and {FieldCodec.Format(field, field.Max)}.");
                merged[e.Field] = e.Value;
            }
            string problem = t.Validate(merged);
            if (problem.Length > 0) throw new EditException(problem);
        }
        var changes = edits.Where(e => Value(e.Table, e.Id, e.Field) != e.Value)
                           .Select(e => (e.Table, e.Id, e.Field, Value(e.Table, e.Id, e.Field), e.Value)).ToList();
        if (changes.Count == 0) return false;
        steps.RemoveRange(position, steps.Count - position);
        if (savedPosition > position) savedPosition = -1;
        steps.Add(new Step(changes));
        Redo();
        return true;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        position--;
        foreach (var e in steps[position].Edits) Set(e.Table, e.Id, e.Field, e.Before);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        foreach (var e in steps[position].Edits) Set(e.Table, e.Id, e.Field, e.After);
        position++;
    }

    /// <summary>Restores every field of the given records to the disc values, as one undoable step.</summary>
    public void Revert(params (string Table, int Id)[] records)
    {
        var edits = values.Keys.Where(k => records.Contains((k.Table, k.Id)))
                          .Select(k => new FieldEdit(k.Table, k.Id, k.Field, OriginalValue(k.Table, k.Id, k.Field))).ToList();
        if (edits.Count > 0) Apply(edits);
    }
    public void Revert(string table, int id) => Revert((table, id));

    private void Set(string table, int id, string field, int value)
    {
        if (value == OriginalValue(table, id, field)) values.Remove((table, id, field));
        else values[(table, id, field)] = value;
    }

    // Project files

    private const string FormatName = "Berwick Workshop project";
    private const int FormatVersion = 1;
    private const long MaxProjectSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record ProjectChange(string Table, int Id, string? Name, string Field, int Original, int Value);
    private sealed record ProjectFile(string Format, int FormatVersion, string Edition, string EditionSha256, string? AppVersion, IReadOnlyList<ProjectChange> Changes);

    private string? RecordName(string table, int id) => table switch
    {
        Items => originalItems.TryGetValue(id, out var i) ? i.DisplayName : null,
        Characters or Growths => originalCharacters.TryGetValue(id, out var c) ? c.Name : null,
        Classes => originalClasses.TryGetValue(id, out var k) ? k.Name : null,
        _ => null,
    };

    public string ToJson(string? appVersion = null) => JsonSerializer.Serialize(new ProjectFile(FormatName, FormatVersion, disc.Edition, disc.Sha256, appVersion,
        Changes.Select(c => new ProjectChange(c.Table, c.Id, RecordName(c.Table, c.Id), c.Field, c.Original, c.Value)).ToList()), Json);

    public void Save(string path, string? appVersion = null)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, ToJson(appVersion));
        File.Move(temp, path, overwrite: true);
        FilePath = path;
        savedPosition = position;
    }

    public static EditSession Load(InspectionResult disc, string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxProjectSize) throw new EditException("This file is too large to be a Berwick Workshop project.");
        var session = FromJson(disc, File.ReadAllText(path));
        session.FilePath = path;
        return session;
    }

    public static EditSession FromJson(InspectionResult disc, string json)
    {
        ProjectFile? file;
        try { file = JsonSerializer.Deserialize<ProjectFile>(json, Json); }
        catch (JsonException) { throw new EditException("This file is not a Berwick Workshop project."); }
        if (file is null || file.Format != FormatName || file.Changes is null) throw new EditException("This file is not a Berwick Workshop project.");
        if (file.FormatVersion != FormatVersion) throw new EditException("This project was saved by a newer version of Berwick Workshop.");
        if (!string.Equals(file.EditionSha256, disc.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException($"This project was made for the {file.Edition} disc. Open that disc first, then open the project.");
        var session = new EditSession(disc);
        var seen = new HashSet<(string, int, string)>();
        foreach (var change in file.Changes)
        {
            var table = session.Table(change.Table ?? "");
            if (!table.Records.ContainsKey(change.Id)) throw new EditException($"This project changes {table.Noun} 0x{change.Id:X3}, which is not on this disc.");
            var field = table.Field(change.Field);
            if (!seen.Add((change.Table!, change.Id, change.Field))) throw new EditException("This project lists the same change twice.");
            if (session.OriginalValue(change.Table!, change.Id, change.Field) != change.Original)
                throw new EditException($"The disc does not match this project: {field.Label} of {table.Noun} 0x{change.Id:X3} differs from what the project expects.");
            if (change.Value < field.Min || change.Value > field.Max)
                throw new EditException($"{Capitalize(table.Noun)} 0x{change.Id:X3}: {field.Label} must be between {FieldCodec.Format(field, field.Min)} and {FieldCodec.Format(field, field.Max)}.");
            session.Set(change.Table!, change.Id, change.Field, change.Value);
        }
        foreach (var (tableName, id) in session.EditedRecords)
        {
            var table = session.Table(tableName);
            string problem = table.Validate(table.Fields.ToDictionary(f => f.Key, f => session.Value(tableName, id, f.Key)));
            if (problem.Length > 0) throw new EditException($"{Capitalize(table.Noun)} 0x{id:X3}: {problem}");
        }
        return session;
    }
}
