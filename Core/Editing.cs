using System.Text.Json;
using System.Text.Json.Serialization;

namespace Berwick.Core;

public sealed record ItemField(string Key, string Label, int Offset, int Bit, int Width, int Min, int Max);

public sealed class EditException(string message) : Exception(message);

public static class ItemEncoder
{
    public static IReadOnlyList<ItemField> Fields { get; } = Array.AsReadOnly(new ItemField[]
    {
        new("might", "Might", 0, 5, 6, 0, 63),
        new("accuracy", "Accuracy", 1, 7, 7, 0, 127),
        new("weight", "Weight", 2, 6, 5, 0, 31),
        new("minRange", "Minimum range", 4, 0, 4, 0, 15),
        new("maxRange", "Maximum range", 3, 3, 5, 0, 31),
        new("critical", "Critical", 5, 0, 7, 0, 127),
        new("level", "Level", 6, 6, 6, 0, 63),
        new("price", "Base price", 8, 0, 16, 0, 65535),
        new("durability", "Durability", 21, 4, 3, 0, 6),
        new("uses", "Durability pool", 5, 7, 7, 0, 127),
    });

    public static ItemField Field(string key) =>
        Fields.FirstOrDefault(f => f.Key == key) ?? throw new EditException($"Unknown item field \"{key}\".");

    public static int Read(ReadOnlySpan<byte> record, ItemField field) => ItemDecoder.Bits(record, field.Offset, field.Bit, field.Width);

    public static void Write(Span<byte> record, ItemField field, int value)
    {
        if (value < field.Min || value > field.Max)
            throw new EditException($"{field.Label} must be between {field.Min} and {field.Max}.");
        int bytes = (field.Bit + field.Width + 7) / 8;
        if (field.Offset + bytes > record.Length) throw new InvalidDataException("An item record is incomplete.");
        uint current = 0;
        for (int n = 0; n < bytes; n++) current |= (uint)record[field.Offset + n] << (8 * n);
        uint mask = ((1u << field.Width) - 1) << field.Bit;
        current = (current & ~mask) | ((uint)value << field.Bit & mask);
        for (int n = 0; n < bytes; n++) record[field.Offset + n] = (byte)(current >> (8 * n));
    }

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

public sealed record FieldChange(string Table, int Id, string Field, int Original, int Value);

public sealed class EditSession
{
    private sealed record Step(IReadOnlyList<(int Id, string Field, int Before, int After)> Edits);

    private readonly InspectionResult disc;
    private readonly Dictionary<int, ItemRecord> originals;
    private readonly Dictionary<(int Id, string Field), int> values = [];
    private readonly List<Step> steps = [];
    private int position, savedPosition;

    public EditSession(InspectionResult disc)
    {
        if (!disc.Supported) throw new EditException("Only a verified disc can be edited.");
        this.disc = disc;
        originals = disc.Items.ToDictionary(i => i.Id);
    }

    public string? FilePath { get; private set; }
    public bool IsDirty => position != savedPosition;
    public bool CanUndo => position > 0;
    public bool CanRedo => position < steps.Count;
    public int ChangeCount => values.Count;

    public int OriginalValue(int id, string field) => ItemEncoder.Read(Bytes(id).Span, ItemEncoder.Field(field));
    public int Value(int id, string field) => values.TryGetValue((id, field), out int v) ? v : OriginalValue(id, field);
    public bool IsEdited(int id) => values.Keys.Any(k => k.Id == id);

    private ReadOnlyMemory<byte> Bytes(int id) =>
        disc.ItemBytes.TryGetValue(id, out var bytes) ? bytes : throw new EditException($"Item 0x{id:X3} is not on this disc.");

    public IReadOnlyList<FieldChange> Changes =>
        values.OrderBy(v => v.Key.Id).ThenBy(v => ItemEncoder.Fields.ToList().FindIndex(f => f.Key == v.Key.Field))
              .Select(v => new FieldChange("items", v.Key.Id, v.Key.Field, OriginalValue(v.Key.Id, v.Key.Field), v.Value)).ToList();

    public byte[] EditedBytes(int id)
    {
        var bytes = Bytes(id).ToArray();
        foreach (var ((itemId, field), value) in values)
            if (itemId == id) ItemEncoder.Write(bytes, ItemEncoder.Field(field), value);
        return bytes;
    }

    public ItemRecord EditedItem(int id)
    {
        var original = originals[id];
        return IsEdited(id) ? ItemDecoder.Decode(id, original.Name, EditedBytes(id)) : original;
    }

    public IReadOnlyList<ItemRecord> EditedItems() => disc.Items.Select(i => EditedItem(i.Id)).ToList();

    /// <summary>Applies several field values to one item as a single undoable step. Returns false when nothing changed.</summary>
    public bool Apply(int id, IReadOnlyDictionary<string, int> newValues)
    {
        _ = Bytes(id);
        var merged = ItemEncoder.Fields.ToDictionary(f => f.Key, f => Value(id, f.Key));
        foreach (var (key, value) in newValues) { ItemEncoder.Field(key); merged[key] = value; }
        string problem = ItemEncoder.Validate(merged);
        if (problem.Length > 0) throw new EditException(problem);
        var edits = newValues.Where(v => Value(id, v.Key) != v.Value).Select(v => (id, v.Key, Value(id, v.Key), v.Value)).ToList();
        if (edits.Count == 0) return false;
        steps.RemoveRange(position, steps.Count - position);
        if (savedPosition > position) savedPosition = -1;
        steps.Add(new Step(edits));
        Redo();
        return true;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        position--;
        foreach (var e in steps[position].Edits) Set(e.Id, e.Field, e.Before);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        foreach (var e in steps[position].Edits) Set(e.Id, e.Field, e.After);
        position++;
    }

    public void Revert(int id)
    {
        var fields = ItemEncoder.Fields.Where(f => values.ContainsKey((id, f.Key))).ToDictionary(f => f.Key, f => OriginalValue(id, f.Key));
        if (fields.Count > 0) Apply(id, fields);
    }

    private void Set(int id, string field, int value)
    {
        if (value == OriginalValue(id, field)) values.Remove((id, field));
        else values[(id, field)] = value;
    }

    // Project files

    private const string FormatName = "Berwick Workshop project";
    private const int FormatVersion = 1;
    private const long MaxProjectSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record ProjectChange(string Table, int Id, string? Name, string Field, int Original, int Value);
    private sealed record ProjectFile(string Format, int FormatVersion, string Edition, string EditionSha256, string? AppVersion, IReadOnlyList<ProjectChange> Changes);

    public string ToJson(string? appVersion = null) => JsonSerializer.Serialize(new ProjectFile(FormatName, FormatVersion, disc.Edition, disc.Sha256, appVersion,
        Changes.Select(c => new ProjectChange(c.Table, c.Id, originals[c.Id].DisplayName, c.Field, c.Original, c.Value)).ToList()), Json);

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
        var seen = new HashSet<(int, string)>();
        foreach (var change in file.Changes)
        {
            if (change.Table != "items") throw new EditException("This project contains changes this version cannot open.");
            if (!disc.ItemBytes.ContainsKey(change.Id)) throw new EditException($"This project changes item 0x{change.Id:X3}, which is not on this disc.");
            var field = ItemEncoder.Field(change.Field);
            if (!seen.Add((change.Id, change.Field))) throw new EditException("This project lists the same change twice.");
            if (session.OriginalValue(change.Id, change.Field) != change.Original)
                throw new EditException($"The disc does not match this project: {field.Label} of item 0x{change.Id:X3} differs from what the project expects.");
            session.Set(change.Id, change.Field, change.Value);
        }
        foreach (int id in session.values.Keys.Select(k => k.Id).Distinct())
        {
            string problem = ItemEncoder.Validate(ItemEncoder.Fields.ToDictionary(f => f.Key, f => session.Value(id, f.Key)));
            if (problem.Length > 0) throw new EditException($"Item 0x{id:X3}: {problem}");
        }
        return session;
    }
}
