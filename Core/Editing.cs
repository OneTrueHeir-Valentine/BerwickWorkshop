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
    /// <summary>A stored value with its own meaning, shown as text instead of a number (for example a terrain that cannot be entered).</summary>
    public int? SpecialValue { get; init; }
    public string SpecialText { get; init; } = "";
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
        if (value < field.Min || value > field.Max) throw new EditException(RangeError(field));
        int bytes = (field.Bit + field.Width + 7) / 8;
        if (field.Offset + bytes > record.Length) throw new InvalidDataException("A record is incomplete.");
        uint current = 0;
        for (int n = 0; n < bytes; n++) current |= (uint)record[field.Offset + n] << (8 * n);
        uint mask = ((1u << field.Width) - 1) << field.Bit;
        current = (current & ~mask) | ((uint)value << field.Bit & mask);
        for (int n = 0; n < bytes; n++) record[field.Offset + n] = (byte)(current >> (8 * n));
    }

    /// <summary>The allowed range as shown to the user, including a special value when the field has one.</summary>
    public static string RangeText(TableField field)
    {
        int max = field.SpecialValue == field.Max ? field.Max - 1 : field.Max;
        string range = $"{Format(field, field.Min)} to {Format(field, max)}";
        return field.SpecialValue is int ? $"{range}, or {field.SpecialText}" : range;
    }

    public static string RangeError(TableField field)
    {
        int max = field.SpecialValue == field.Max ? field.Max - 1 : field.Max;
        string range = $"{field.Label} must be between {Format(field, field.Min)} and {Format(field, max)}";
        return field.SpecialValue is int ? $"{range}, or {field.SpecialText} if it cannot be entered." : range + ".";
    }

    public static string Format(TableField field, int value) =>
        field.SpecialValue == value ? field.SpecialText
        : field.IsFlag ? (value != 0 ? "Yes" : "No")
        : field.Choices is { } choices && value - field.Min >= 0 && value - field.Min < choices.Count ? choices[value - field.Min]
        : field.Display == 1 ? value.ToString(CultureInfo.InvariantCulture)
        : (value * field.Display).ToString(field.Display < 1 ? "0.#" : "0", CultureInfo.InvariantCulture);

    public static bool TryParse(TableField field, string text, out int value, out string error)
    {
        value = 0; error = "";
        text = text.Trim().Replace(",", "");
        if (field.SpecialValue is int special && (text == field.SpecialText || text.Equals("x", StringComparison.OrdinalIgnoreCase)))
        { value = special; return true; }
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
        if (raw < field.Min || raw > field.Max || raw == field.SpecialValue) { error = RangeError(field); return false; }
        value = (int)raw;
        return true;
    }
}

public static class ItemEncoder
{
    public static IReadOnlyList<TableField> Fields { get; } = Array.AsReadOnly(new TableField[]
    {
        new("type", "Type", 0, 0, 5, 0, 24) { Choices = ItemDecoder.Types },
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
        new("bracket", "Growth bracket", 2, 5, 2, 1, 3) { Choices = ["None", "Loose (±3)", "Tight"] },
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
        new("movementType", "Movement type", 6, 1, 8, 0, TerrainData.Types - 1) { Choices = TerrainData.TypeNames },
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
    public const string Items = "items", Characters = "characters", Growths = "growths", Classes = "classes", Terrain = "terrain", Rules = "rules";
    private static readonly string[] TableOrder = [Items, Characters, Growths, Classes, Terrain, Rules];

    /// <summary>Everything the project says about one record: explicit field values, a record it was copied from, and a display name.</summary>
    private sealed record RecordState(IReadOnlyDictionary<string, int> Values, int? CopyFrom, string? Name)
    {
        public bool SameAs(RecordState other) => CopyFrom == other.CopyFrom && Name == other.Name
            && Values.Count == other.Values.Count && Values.All(v => other.Values.TryGetValue(v.Key, out int o) && o == v.Value);
    }
    private sealed record Step(IReadOnlyList<(string Table, int Id, RecordState Before, RecordState After)> Records);

    private readonly InspectionResult disc;
    private readonly Dictionary<string, EditTable> tables;
    private readonly Dictionary<int, ItemRecord> originalItems;
    private readonly Dictionary<int, CharacterRecord> originalCharacters;
    private readonly Dictionary<int, ClassRecord> originalClasses;
    private readonly Dictionary<(string Table, int Id, string Field), int> values = [];
    private readonly Dictionary<(string Table, int Id), int> copies = [];
    private readonly Dictionary<(string Table, int Id), string> names = [];
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
            [Terrain] = new(Terrain, "movement type", TerrainData.Fields, disc.TerrainBytes),
            [Rules] = new(Rules, "rule", GameRules.All.Select(r => r.Field).ToList(), disc.RuleWords),
        };
    }

    // Rule records hold one instruction word; their value is the rule's setting rather than a bit field.
    private static int ReadValue(string table, int id, ReadOnlySpan<byte> bytes, TableField field) =>
        table == Rules ? GameRules.ById(id).Decode(bytes) ?? GameRules.ById(id).Default : FieldCodec.Read(bytes, field);

    private static void WriteValue(string table, int id, Span<byte> bytes, TableField field, int value)
    {
        if (table == Rules) GameRules.ById(id).EncodeBytes(value).CopyTo(bytes);
        else FieldCodec.Write(bytes, field, value);
    }

    public bool IsRuleAvailable(int id) => disc.RuleWords.ContainsKey(id);

    public InspectionResult Disc => disc;
    public string? FilePath { get; private set; }
    public bool IsDirty => position != savedPosition;
    public bool CanUndo => position > 0;
    public bool CanRedo => position < steps.Count;
    public int ChangeCount => values.Count + copies.Count + names.Count;

    public EditTable Table(string name) => tables.TryGetValue(name, out var t) ? t : throw new EditException("This project contains changes this version cannot open.");

    private ReadOnlyMemory<byte> Record(string table, int id) =>
        Table(table).Records.TryGetValue(id, out var bytes) ? bytes : throw new EditException($"{Capitalize(Table(table).Noun)} 0x{id:X3} is not on this disc.");
    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The bytes a record starts from: the disc record, or the record it was copied from.</summary>
    private ReadOnlyMemory<byte> BaseRecord(string table, int id) => copies.TryGetValue((table, id), out int from) ? Record(table, from) : Record(table, id);

    public int OriginalValue(string table, int id, string field) => ReadValue(table, id, Record(table, id).Span, Table(table).Field(field));
    private int BaseValue(string table, int id, string field) => ReadValue(table, id, BaseRecord(table, id).Span, Table(table).Field(field));
    public int Value(string table, int id, string field) => values.TryGetValue((table, id, field), out int v) ? v : BaseValue(table, id, field);
    public bool IsEdited(string table, int id) => values.Keys.Any(k => k.Table == table && k.Id == id) || copies.ContainsKey((table, id)) || names.ContainsKey((table, id));
    public int? CopiedFrom(string table, int id) => copies.TryGetValue((table, id), out int from) ? from : null;
    public string? CustomName(string table, int id) => names.TryGetValue((table, id), out var name) ? name : null;

    public byte[] EditedBytes(string table, int id)
    {
        var bytes = BaseRecord(table, id).ToArray();
        var t = Table(table);
        foreach (var ((tableName, recordId, field), value) in values)
            if (tableName == table && recordId == id) WriteValue(table, id, bytes, t.Field(field), value);
        return bytes;
    }

    public IReadOnlyList<(string Table, int Id)> EditedRecords =>
        values.Keys.Select(k => (k.Table, k.Id)).Concat(copies.Keys).Concat(names.Keys).Distinct()
              .OrderBy(k => Array.IndexOf(TableOrder, k.Item1)).ThenBy(k => k.Item2).ToList();

    public IReadOnlyList<FieldChange> Changes =>
        values.OrderBy(v => Array.IndexOf(TableOrder, v.Key.Table)).ThenBy(v => v.Key.Id)
              .ThenBy(v => Table(v.Key.Table).Fields.ToList().FindIndex(f => f.Key == v.Key.Field))
              .Select(v => new FieldChange(v.Key.Table, v.Key.Id, v.Key.Field, BaseValue(v.Key.Table, v.Key.Id, v.Key.Field), v.Value)).ToList();

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
        if (!IsEdited(Items, id)) return original;
        var decoded = ItemDecoder.Decode(id, original.Name, EditedBytes(Items, id));
        return CustomName(Items, id) is { } name ? decoded with { CustomName = name } : decoded;
    }
    public IReadOnlyList<ItemRecord> EditedItems() => disc.Items.Select(i => EditedItem(i.Id)).ToList();

    public CharacterRecord EditedCharacter(int id)
    {
        var original = originalCharacters[id];
        if (!IsEdited(Characters, id) && !IsEdited(Growths, id)) return original;
        var itemNames = EditedItems().ToDictionary(i => i.Id, i => i.DisplayName);
        var decoded = CharacterDecoder.Decode(new(id, original.Name, 0), EditedBytes(Characters, id), EditedBytes(Growths, id), itemNames);
        return decoded with { ClassName = original.ClassName };
    }
    public IReadOnlyList<CharacterRecord> EditedCharacters() => disc.Characters.Select(c => EditedCharacter(c.Id)).ToList();

    public ClassRecord EditedClass(int id)
    {
        var original = originalClasses[id];
        return IsEdited(Classes, id) ? ClassDecoder.Decode(id, original.Name, EditedBytes(Classes, id)) : original;
    }
    public IReadOnlyList<ClassRecord> EditedClasses() => disc.Classes.Select(c => EditedClass(c.Id)).ToList();

    public MovementTypeRecord EditedMovementType(int id) => TerrainData.Decode(id, EditedBytes(Terrain, id));
    public IReadOnlyList<MovementTypeRecord> EditedMovementTypes() => disc.MovementTypes.Select(m => EditedMovementType(m.Id)).ToList();

    // State capture for undo

    private RecordState Capture(string table, int id) => new(
        values.Where(v => v.Key.Table == table && v.Key.Id == id).ToDictionary(v => v.Key.Field, v => v.Value),
        CopiedFrom(table, id), CustomName(table, id));

    private void Restore(string table, int id, RecordState state)
    {
        foreach (var key in values.Keys.Where(k => k.Table == table && k.Id == id).ToList()) values.Remove(key);
        if (state.CopyFrom is int from) copies[(table, id)] = from; else copies.Remove((table, id));
        if (state.Name is { } name) names[(table, id)] = name; else names.Remove((table, id));
        foreach (var (field, value) in state.Values) values[(table, id, field)] = value;
    }

    private void Set(string table, int id, string field, int value)
    {
        if (value == BaseValue(table, id, field)) values.Remove((table, id, field));
        else values[(table, id, field)] = value;
    }

    /// <summary>Runs a change against the session, records it as one undo step, and returns false when nothing changed.</summary>
    private bool Record(IEnumerable<(string Table, int Id)> records, Action change)
    {
        var touched = records.Distinct().ToList();
        var before = touched.Select(r => Capture(r.Table, r.Id)).ToList();
        try { change(); }
        catch { for (int i = 0; i < touched.Count; i++) Restore(touched[i].Table, touched[i].Id, before[i]); throw; }
        var entries = touched.Select((r, i) => (r.Table, r.Id, Before: before[i], After: Capture(r.Table, r.Id)))
                             .Where(e => !e.Before.SameAs(e.After)).ToList();
        if (entries.Count == 0) return false;
        steps.RemoveRange(position, steps.Count - position);
        if (savedPosition > position) savedPosition = -1;
        steps.Add(new Step(entries));
        position++;
        return true;
    }

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
                if (e.Value < field.Min || e.Value > field.Max) throw new EditException(FieldCodec.RangeError(field));
                merged[e.Field] = e.Value;
            }
            string problem = t.Validate(merged);
            if (problem.Length > 0) throw new EditException(problem);
        }
        return Record(edits.Select(e => (e.Table, e.Id)), () => { foreach (var e in edits) Set(e.Table, e.Id, e.Field, e.Value); });
    }

    public void Undo()
    {
        if (!CanUndo) return;
        position--;
        foreach (var r in steps[position].Records) Restore(r.Table, r.Id, r.Before);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        foreach (var r in steps[position].Records) Restore(r.Table, r.Id, r.After);
        position++;
    }

    /// <summary>Puts records back the way the disc has them (fields, copies and names), as one undoable step.</summary>
    public void Revert(params (string Table, int Id)[] records) =>
        Record(records, () =>
        {
            foreach (var (table, id) in records)
                Restore(table, id, new RecordState(new Dictionary<string, int>(), null, null));
        });
    public void Revert(string table, int id) => Revert((table, id));

    // New items

    /// <summary>Placeholder item slots that no character starts with and that this project has not already used.</summary>
    public IReadOnlyList<int> FreeItemSlots()
    {
        var carried = new HashSet<int>(EditedCharacters().SelectMany(c => c.Items).Select(i => i.ItemId));
        return disc.Items.Where(i => i.IsUnused && !IsEdited(Items, i.Id) && !carried.Contains(i.Id)).Select(i => i.Id).ToList();
    }

    /// <summary>Turns a free slot into a new item: a full copy of another item's record plus a name, as one undoable step.</summary>
    public void CreateItem(int slot, int template, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new EditException("Give the new item a name.");
        if (name.Length > 40) throw new EditException("Item names can be at most 40 characters.");
        if (!FreeItemSlots().Contains(slot)) throw new EditException($"Slot 0x{slot:X3} is not a free item slot.");
        if (!originalItems.ContainsKey(template) || EditedItem(template).IsUnused) throw new EditException("Choose an existing item to start from.");
        // Copy the template as it currently stands in this project, including its own edits.
        var source = Capture(Items, template);
        Record([(Items, slot)], () => Restore(Items, slot, new RecordState(new Dictionary<string, int>(source.Values), source.CopyFrom ?? template, name)));
    }

    public void Rename(string table, int id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new EditException("Names cannot be empty.");
        if (name.Length > 40) throw new EditException("Names can be at most 40 characters.");
        _ = Record(table, id);
        Record([(table, id)], () => names[(table, id)] = name);
    }

    // Project files

    private const string FormatName = "Berwick Workshop project";
    private const int FormatVersion = 2;
    private const long MaxProjectSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record ProjectChange(string Table, int Id, string? Name, string Field, int Original, int Value);
    private sealed record ProjectCopy(string Table, int Id, int From);
    private sealed record ProjectName(string Table, int Id, string Name);
    private sealed record ProjectFile(string Format, int FormatVersion, string Edition, string EditionSha256, string? AppVersion,
        IReadOnlyList<ProjectChange> Changes, IReadOnlyList<ProjectCopy>? Copies = null, IReadOnlyList<ProjectName>? Names = null);

    private string? RecordName(string table, int id) => CustomName(table, id) ?? table switch
    {
        Items => originalItems.TryGetValue(id, out var i) ? i.DisplayName : null,
        Characters or Growths => originalCharacters.TryGetValue(id, out var c) ? c.Name : null,
        Classes => originalClasses.TryGetValue(id, out var k) ? k.Name : null,
        Terrain => id < TerrainData.TypeNames.Length ? TerrainData.TypeNames[id] : null,
        Rules => GameRules.All.FirstOrDefault(r => r.Id == id)?.Name,
        _ => null,
    };

    public string ToJson(string? appVersion = null)
    {
        bool extended = copies.Count > 0 || names.Count > 0;
        return JsonSerializer.Serialize(new ProjectFile(FormatName, extended ? FormatVersion : 1, disc.Edition, disc.Sha256, appVersion,
            values.OrderBy(v => Array.IndexOf(TableOrder, v.Key.Table)).ThenBy(v => v.Key.Id)
                  .Select(v => new ProjectChange(v.Key.Table, v.Key.Id, RecordName(v.Key.Table, v.Key.Id), v.Key.Field, OriginalValue(v.Key.Table, v.Key.Id, v.Key.Field), v.Value)).ToList(),
            extended ? copies.OrderBy(c => c.Key.Id).Select(c => new ProjectCopy(c.Key.Table, c.Key.Id, c.Value)).ToList() : null,
            extended ? names.OrderBy(n => n.Key.Id).Select(n => new ProjectName(n.Key.Table, n.Key.Id, n.Value)).ToList() : null), Json);
    }

    public void Save(string path, string? appVersion = null)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, Path.GetFullPath(disc.Path), StringComparison.OrdinalIgnoreCase))
            throw new EditException("Choose a project file name. The original disc image is never overwritten.");
        if (!Path.GetExtension(path).Equals(".bwproj", StringComparison.OrdinalIgnoreCase))
            throw new EditException("Save the project with a .bwproj extension. Disc images are created with Export disc.");
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        bool ownsTemp = false;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemp = true;
                using var writer = new StreamWriter(file, leaveOpen: true);
                writer.Write(ToJson(appVersion));
                writer.Flush();
                file.Flush(true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (ownsTemp)
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
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
        if (file.FormatVersion < 1 || file.FormatVersion > FormatVersion) throw new EditException("This project was saved by a newer version of Berwick Workshop.");
        if (!string.Equals(file.EditionSha256, disc.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException($"This project was made for the {file.Edition} disc. Open that disc first, then open the project.");
        var session = new EditSession(disc);
        foreach (var copy in file.Copies ?? [])
        {
            if (copy is null) throw new EditException("This project contains an empty item copy.");
            var table = session.Table(copy.Table ?? "");
            if (copy.Table != Items) throw new EditException("This project contains changes this version cannot open.");
            if (!table.Records.ContainsKey(copy.Id) || !table.Records.ContainsKey(copy.From)) throw new EditException($"This project creates an item from a slot that is not on this disc.");
            if (!session.originalItems[copy.Id].IsUnused) throw new EditException($"This project places a new item in slot 0x{copy.Id:X3}, which is already a real item on this disc.");
            if (!session.copies.TryAdd((copy.Table!, copy.Id), copy.From)) throw new EditException("This project lists the same new item twice.");
        }
        foreach (var name in file.Names ?? [])
        {
            if (name is null) throw new EditException("This project contains an empty name entry.");
            var table = session.Table(name.Table ?? "");
            if (!table.Records.ContainsKey(name.Id)) throw new EditException($"This project names {table.Noun} 0x{name.Id:X3}, which is not on this disc.");
            if (string.IsNullOrWhiteSpace(name.Name) || name.Name.Trim().Length > 40) throw new EditException("This project contains a name that is empty or too long.");
            session.names[(name.Table!, name.Id)] = name.Name.Trim();
        }
        var seen = new HashSet<(string, int, string)>();
        foreach (var change in file.Changes)
        {
            if (change is null) throw new EditException("This project contains an empty change.");
            var table = session.Table(change.Table ?? "");
            if (!table.Records.ContainsKey(change.Id)) throw new EditException($"This project changes {table.Noun} 0x{change.Id:X3}, which is not on this disc.");
            var field = table.Field(change.Field);
            if (change.Table == Rules && GameRules.ById(change.Id).Key != change.Field) throw new EditException("This project contains a rule change this version cannot open.");
            if (!seen.Add((change.Table!, change.Id, change.Field))) throw new EditException("This project lists the same change twice.");
            if (session.OriginalValue(change.Table!, change.Id, change.Field) != change.Original)
                throw new EditException($"The disc does not match this project: {field.Label} of {table.Noun} 0x{change.Id:X3} differs from what the project expects.");
            if (change.Value < field.Min || change.Value > field.Max)
                throw new EditException($"{Capitalize(table.Noun)} 0x{change.Id:X3}: {FieldCodec.RangeError(field)}");
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
