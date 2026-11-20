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
    /// <summary>The stored value of each choice, when the stored values are not Min, Min + 1 and so on.</summary>
    public IReadOnlyList<int>? ChoiceValues { get; init; }
    public int ChoiceValue(int index) => ChoiceValues is { } v ? v[index] : Min + index;
    public int ChoiceIndex(int value)
    {
        if (ChoiceValues is not { } v) return value - Min;
        for (int i = 0; i < v.Count; i++) if (v[i] == value) return i;
        return -1;
    }
    /// <summary>The value is an item ID (0 means empty).</summary>
    public bool ItemReference { get; init; }
    /// <summary>The value is a class ID.</summary>
    public bool ClassReference { get; init; }
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
        if (field.Width == 32) return raw;
        return field.Signed && raw >= 1 << (field.Width - 1) ? raw - (1 << field.Width) : raw;
    }

    public static void Write(Span<byte> record, TableField field, int value)
    {
        if (value < field.Min || value > field.Max) throw new EditException(RangeError(field));
        int bytes = (field.Bit + field.Width + 7) / 8;
        if (field.Offset + bytes > record.Length) throw new InvalidDataException("A record is incomplete.");
        ulong current = 0;
        for (int n = 0; n < bytes; n++) current |= (ulong)record[field.Offset + n] << (8 * n);
        ulong mask = ((1UL << field.Width) - 1) << field.Bit;
        current = (current & ~mask) | ((ulong)(uint)value << field.Bit & mask);
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
        : field.Choices is { } choices && field.ChoiceIndex(value) is var index && index >= 0 && index < choices.Count ? choices[index]
        : field.ChoiceValues is not null ? $"Other ({value})"
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
            if (index >= 0) { value = field.ChoiceValue(index); return true; }
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
    // Effect flags (bytes 28 to 39), named from the items that carry them on the disc. Flags whose meaning is unclear,
    // or that mark an item's category (horse, material), are not offered.
    public static readonly (int Bit, string Name)[] Effects =
    [
        (2, "Strikes twice"), (3, "Strikes three times"), (4, "Strikes four times"), (10, "Ignores defense"), (12, "Ignores shields"),
        (13, "Ignores armor"), (14, "Ignores horse defense"), (20, "No counterattack"), (21, "Not usable after moving"), (60, "Can be thrown"),
        (27, "Drains HP"), (58, "Adds level to damage"), (56, "Cannot kill"), (84, "Cannot attack fliers"), (16, "Miracle"), (17, "Renewal"),
        (45, "Grants Hide"), (46, "Grants Overwatch"), (47, "Grants Parry"), (81, "Grants Horse-Lover"), (66, "Move +1"),
        (19, "Uses instead of durability"), (34, "Unbreakable"), (86, "Restores uses"), (32, "Personal weapon"), (30, "Infantry only"), (1, "Women only"),
        (29, "General only"), (26, "Gigas Knight only"), (69, "Paladin only"), (72, "Assassin only"), (79, "Black Rider only"), (80, "Apostle only"),
    ];

    // One special effect per item: byte 27 holds 100 + the effect number (0 for none), byte 26 its chance or amount.
    private static readonly (string Name, int Value)[] SpecialEffects =
        [("None", 0), .. ItemDecoder.EffectNames.Select((n, i) => (n, 100 + i)).Where(e => e.n != "Unknown").OrderBy(e => e.n, StringComparer.Ordinal)];

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
        new("strengthBonus", "Strength", 16, 5, 5, -16, 15) { Signed = true, Group = "Bonuses" },
        new("agilityBonus", "Agility", 13, 3, 5, -16, 15) { Signed = true, Group = "Bonuses" },
        new("defenseBonus", "Defense", 12, 0, 6, -32, 31) { Signed = true, Group = "Bonuses" },
        new("mindBonus", "Mind", 16, 0, 5, -16, 15) { Signed = true, Group = "Bonuses" },
        new("hitBonus", "Hit", 15, 0, 8, -128, 127) { Signed = true, Group = "Bonuses" },
        new("avoidBonus", "Avoid", 14, 0, 8, -128, 127) { Signed = true, Group = "Bonuses" },
        new("criticalEvade", "Critical evade", 21, 7, 8, -128, 127) { Signed = true, Group = "Bonuses" },
        new("fireResist", "Fire", 17, 6, 6, -32, 31) { Signed = true, Group = "Resistances" },
        new("thunderResist", "Thunder", 18, 4, 6, -32, 31) { Signed = true, Group = "Resistances" },
        new("windResist", "Wind", 19, 2, 6, -32, 31) { Signed = true, Group = "Resistances" },
        new("holyResist", "Holy", 20, 6, 6, -32, 31) { Signed = true, Group = "Resistances" },
        new("darkResist", "Dark", 20, 0, 6, -32, 31) { Signed = true, Group = "Resistances" },
        new("effectKind", "Effect", 27, 0, 8, 0, 255) { Group = "Special effect", Choices = SpecialEffects.Select(e => e.Name).ToArray(), ChoiceValues = SpecialEffects.Select(e => e.Value).ToArray() },
        new("effectAmount", "Chance or amount", 26, 0, 7, 0, 127) { Group = "Special effect" },
    }.Concat(Effects.Select(e => new TableField($"effect{e.Bit}", e.Name, 28 + e.Bit / 8, e.Bit % 8, 1, 0, 1) { Group = "Effects" })).ToArray());


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
        new("class", "Class", 19, 0, 8, 1, 255) { ClassReference = true },
        new("level", "Level", 20, 0, 6, 1, 63),
        new("hpBonus", "HP bonus", 22, 4, 7, 0, 127),
        new("strengthBonus", "Strength bonus", 23, 3, 5, -16, 15) { Signed = true },
        new("defenseBonus", "Defense bonus", 25, 2, 5, -16, 15) { Signed = true },
        new("agilityBonus", "Agility bonus", 24, 0, 5, -16, 15) { Signed = true },
        new("mindBonus", "Mind bonus", 25, 7, 5, -16, 15) { Signed = true },
        new("luck", "Luck", 24, 5, 5, 0, 31),
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
        new("movementType", "Movement type", 6, 1, 8, 0, TerrainData.Types - 1) { Choices = TerrainData.TypeNames },
    }.Concat(Caps.Select(c => new TableField(CharacterFields.Key(c.Name, "Cap"), c.Name, c.Offset, c.Bit, 6, 0, 63) { Group = "Weapon level caps" }))
     .Concat(new TableField[]
     {
         new("shieldSmall", "Small shields", 0x1B, 4, 1, 0, 1) { Group = "Shield sizes" },
         new("shieldMedium", "Medium shields", 0x1B, 5, 1, 0, 1) { Group = "Shield sizes" },
         new("shieldLarge", "Large shields", 0x1B, 6, 1, 0, 1) { Group = "Shield sizes" },
     })
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
    public const string Items = "items", Characters = "characters", Growths = "growths", Classes = "classes", Terrain = "terrain", TerrainKinds = "terrainKinds", Rules = "rules", StablePrices = "stablePrices", Partners = "partners", Bags = "bags", Mercenaries = "mercenaries", ArmyUnits = "armyUnits", FurniturePrices = "furniturePrices", Recipes = "recipes", Dishes = "dishes", Tastes = "tastes", Deployments = "deployments", Placements = "placements", Texts = "texts", ScriptValues = "scriptValues", Scripts = "scripts", AnimationSets = "animationSets", AnimationPalettes = "animationPalettes", MapSprites = "mapSprites";
    private static readonly string[] TableOrder = [Items, Characters, Growths, Partners, Bags, Mercenaries, ArmyUnits, Classes, Terrain, TerrainKinds, Rules, StablePrices, FurniturePrices, Recipes, Dishes, Tastes, Deployments, Placements, AnimationSets, AnimationPalettes, MapSprites, ScriptValues, Scripts, Texts];

    /// <summary>Everything the project says about one record: explicit field values, a record it was copied from, and a display name.</summary>
    private sealed record RecordState(IReadOnlyDictionary<string, int> Values, int? CopyFrom, string? Name, byte[]? Script = null)
    {
        public bool SameAs(RecordState other) => CopyFrom == other.CopyFrom && Name == other.Name
            && Values.Count == other.Values.Count && Values.All(v => other.Values.TryGetValue(v.Key, out int o) && o == v.Value)
            && (Script is null ? other.Script is null : other.Script is not null && Script.AsSpan().SequenceEqual(other.Script));
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
    // Events whose lines were changed: the whole new script, decoded.
    private readonly Dictionary<int, byte[]> scripts = [];
    private readonly ScriptValueRecords scriptRecords;
    // Game texts that were changed (keyed by their place in the list of the disc's texts): the new glyphs.
    private readonly Dictionary<int, byte[]> texts = [];
    private readonly TextRecords textRecords;
    private readonly DeploymentRecords deploymentRecords, placementRecords;
    private readonly List<Step> steps = [];
    private HashSet<int>? armyClasses, armyItems;
    private int position, savedPosition;

    public EditSession(InspectionResult disc)
    {
        if (!disc.Supported) throw new EditException("Only a verified disc can be edited.");
        this.disc = disc;
        originalItems = disc.Items.ToDictionary(i => i.Id);
        originalCharacters = disc.Characters.ToDictionary(c => c.Id);
        originalClasses = disc.Classes.ToDictionary(c => c.Id);
        scriptRecords = new ScriptValueRecords(disc.Path);
        textRecords = new TextRecords(disc.Path, disc.Edition.Contains("English", StringComparison.OrdinalIgnoreCase));
        deploymentRecords = new DeploymentRecords(disc.Path);
        placementRecords = new DeploymentRecords(disc.Path, map => Placement.Read(map));
        tables = new()
        {
            [Items] = new(Items, "item", ItemEncoder.Fields, disc.ItemBytes, ItemEncoder.Validate),
            [Characters] = new(Characters, "character", CharacterFields.Record, disc.UnitBytes, values =>
            {
                foreach (var field in CharacterFields.Record.Where(f => f.ItemReference))
                    if (values.TryGetValue(field.Key, out int item) && item != 0 && !disc.ItemBytes.ContainsKey(item))
                        return $"{field.Label}: there is no item 0x{item:X3}.";
                if (values.TryGetValue("class", out int classId) && !disc.ClassBytes.ContainsKey(classId))
                    return $"There is no class 0x{classId:X2}.";
                return "";
            }),
            [Growths] = new(Growths, "character", CharacterFields.Growth, disc.GrowthBytes),
            [Classes] = new(Classes, "class", ClassFields.Fields, disc.ClassBytes),
            [Terrain] = new(Terrain, "movement type", TerrainData.Fields, disc.TerrainBytes),
            [TerrainKinds] = new(TerrainKinds, "terrain", TerrainKindData.Fields, disc.TerrainKindBytes),
            [Rules] = new(Rules, "rule", GameRules.All.Select(r => r.Field).ToList(), disc.RuleWords),
            [StablePrices] = new(StablePrices, "horse", [Core.StablePrices.Field], disc.StablePriceBytes),
            [FurniturePrices] = new(FurniturePrices, "piece of furniture", [Core.FurniturePrices.Table.Field], disc.FurniturePriceBytes),
            // Numbers in event scripts, read from the disc when first needed.
            [ScriptValues] = new(ScriptValues, "script number", [Core.ScriptValues.Field], scriptRecords),
            [Partners] = new(Partners, "character", PartnerData.Fields, disc.PartnerBytes, PartnerData.Validate),
            [Mercenaries] = new(Mercenaries, "character", MercenaryData.Fields, disc.MercenaryBytes),
            [Recipes] = new(Recipes, "recipe", RecipeData.Fields, disc.RecipeBytes, values => RecipeData.Validate(values, id => disc.ItemBytes.ContainsKey(id))),
            [Dishes] = new(Dishes, "dish", DishData.Fields, disc.DishBytes),
            [Tastes] = new(Tastes, "character", TasteData.Fields, disc.TasteBytes),
            // Deployment hexes of each battle map, keyed by the map's top-level archive and read from the disc when first needed.
            [Deployments] = new(Deployments, "map", Deployment.Fields, deploymentRecords, Deployment.Validate),
            [Placements] = new(Placements, "map", Placement.Fields, placementRecords, Placement.Validate),
            [ArmyUnits] = new(ArmyUnits, "unit", ArmyData.Fields, disc.ArmyUnitBytes, values =>
            {
                // Some armies use classes and item numbers that are not in the lists; a unit may keep any value found in the armies.
                armyClasses ??= disc.ArmyUnitBytes.Values.Select(b => (int)b.Span[19]).ToHashSet();
                armyItems ??= disc.ArmyUnitBytes.Values.SelectMany(b => ArmyData.Fields.Where(f => f.ItemReference).Select(f => FieldCodec.Read(b.Span, f))).ToHashSet();
                foreach (var field in ArmyData.Fields.Where(f => f.ItemReference))
                    if (values.TryGetValue(field.Key, out int item) && item != 0 && !disc.ItemBytes.ContainsKey(item) && !armyItems.Contains(item))
                        return $"{field.Label}: there is no item 0x{item:X3}.";
                if (values.TryGetValue("class", out int classId) && !disc.ClassBytes.ContainsKey(classId) && !armyClasses.Contains(classId))
                    return $"There is no class 0x{classId:X2}.";
                return "";
            }),
            [AnimationSets] = new(AnimationSets, "animation row", [AnimationTable.SetField], disc.AnimationSetBytes, values =>
                values.TryGetValue(AnimationTable.SetField.Key, out int set) && !disc.AnimationSetsOnDisc.Contains(set)
                    ? $"There is no animation set {AnimationSet.FileName(set)} on this disc." : ""),
            [AnimationPalettes] = new(AnimationPalettes, "colour row", [AnimationTable.PaletteField], disc.AnimationPaletteBytes),
            [MapSprites] = new(MapSprites, "map sprite", MapSpriteData.Fields, disc.MapSpriteBytes, MapSpriteData.Validate),
            [Bags] = new(Bags, "character", BagData.Fields, disc.BagBytes, values =>
            {
                foreach (var field in BagData.Fields.Where(f => f.ItemReference))
                    if (values.TryGetValue(field.Key, out int item) && item != 0 && !disc.ItemBytes.ContainsKey(item))
                        return $"{field.Label}: there is no item 0x{item:X3}.";
                return BagData.Validate(values);
            }),
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
    public int ChangeCount => values.Count + copies.Count + names.Count + scripts.Count + texts.Count;

    public EditTable Table(string name) => tables.TryGetValue(name, out var t) ? t : throw new EditException("This project contains changes this version cannot open.");

    private ReadOnlyMemory<byte> Record(string table, int id) =>
        Table(table).Records.TryGetValue(id, out var bytes) ? bytes : throw new EditException($"{Capitalize(Table(table).Noun)} 0x{id:X3} is not on this disc.");
    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The bytes a record starts from: the disc record, or the record it was copied from.</summary>
    private ReadOnlyMemory<byte> BaseRecord(string table, int id) => copies.TryGetValue((table, id), out int from) ? Record(table, from) : Record(table, id);

    public int OriginalValue(string table, int id, string field) => ReadValue(table, id, Record(table, id).Span, Table(table).Field(field));
    private int BaseValue(string table, int id, string field) => ReadValue(table, id, BaseRecord(table, id).Span, Table(table).Field(field));
    public int Value(string table, int id, string field) => values.TryGetValue((table, id, field), out int v) ? v : BaseValue(table, id, field);
    public bool IsEdited(string table, int id) => table == Scripts ? scripts.ContainsKey(id) : table == Texts ? texts.ContainsKey(id) : values.Keys.Any(k => k.Table == table && k.Id == id) || copies.ContainsKey((table, id)) || names.ContainsKey((table, id));
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
        values.Keys.Select(k => (k.Table, k.Id)).Concat(copies.Keys).Concat(names.Keys).Concat(scripts.Keys.Select(n => (Scripts, n))).Concat(texts.Keys.Select(n => (Texts, n))).Distinct()
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
        return decoded with { ClassName = decoded.ClassId == original.ClassId ? original.ClassName : originalClasses.TryGetValue(decoded.ClassId, out var k) ? k.Name : $"Class 0x{decoded.ClassId:X2}" };
    }
    public IReadOnlyList<CharacterRecord> EditedCharacters() => disc.Characters.Select(c => EditedCharacter(c.Id)).ToList();

    public ClassRecord EditedClass(int id)
    {
        var original = originalClasses[id];
        return IsEdited(Classes, id) ? ClassDecoder.Decode(id, original.Name, EditedBytes(Classes, id)) : original;
    }
    public IReadOnlyList<ClassRecord> EditedClasses() => disc.Classes.Select(c => EditedClass(c.Id)).ToList();

    public MovementTypeRecord EditedMovementType(int id) => TerrainData.Decode(id, EditedBytes(Terrain, id));
    public TerrainKindRecord EditedTerrainKind(int id) => TerrainKindData.Decode(id, EditedBytes(TerrainKinds, id));
    public IReadOnlyList<TerrainKindRecord> EditedTerrainKinds() => disc.TerrainKinds.Select(k => EditedTerrainKind(k.Id)).ToList();
    public IReadOnlyList<MovementTypeRecord> EditedMovementTypes() => disc.MovementTypes.Select(m => EditedMovementType(m.Id)).ToList();

    // State capture for undo

    private RecordState Capture(string table, int id) => new(
        values.Where(v => v.Key.Table == table && v.Key.Id == id).ToDictionary(v => v.Key.Field, v => v.Value),
        CopiedFrom(table, id), CustomName(table, id), table == Scripts && scripts.TryGetValue(id, out var script) ? script : table == Texts && texts.TryGetValue(id, out var text) ? text : null);

    private void Restore(string table, int id, RecordState state)
    {
        if (table == Scripts)
        {
            if (state.Script is { } script) scripts[id] = script; else scripts.Remove(id);
            return;
        }
        if (table == Texts)
        {
            if (state.Script is { } text) texts[id] = text; else texts.Remove(id);
            return;
        }
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

    // Changed event scripts

    /// <summary>The event as this project has it (its new script, or the disc's with the changed numbers), or null when it is not on the disc.</summary>
    public byte[]? EventScript(int eventNumber)
    {
        if (scripts.TryGetValue(eventNumber, out var script)) return script;
        if (scriptRecords.Bytes(eventNumber) is not { } original) return null;
        var numbers = values.Where(v => v.Key.Table == ScriptValues && Core.ScriptValues.Split(v.Key.Id).Event == eventNumber)
            .ToDictionary(v => Core.ScriptValues.Split(v.Key.Id).Index, v => v.Value);
        return numbers.Count == 0 ? original : Core.ScriptValues.Apply(original, numbers);
    }

    /// <summary>The event's script on the disc, or null when it is not there.</summary>
    public byte[]? OriginalEventScript(int eventNumber) => scriptRecords.Bytes(eventNumber);

    /// <summary>True when the event's lines were changed (its whole script is kept in the project).</summary>
    public bool HasScript(int eventNumber) => scripts.ContainsKey(eventNumber);

    /// <summary>
    /// Keeps a whole new script for an event, as one undoable step. Numbers changed earlier in the event are part of the new
    /// script from then on. A script equal to the disc's removes the change.
    /// </summary>
    public bool SetScript(int eventNumber, byte[] decoded)
    {
        var original = scriptRecords.Bytes(eventNumber) ?? throw new EditException($"Event {eventNumber} is not on this disc.");
        if (eventNumber is StablePriceEvent or FurniturePriceEvent)
            throw new EditException("This event holds the shop's prices, which are changed on their own pages; its lines cannot be changed here.");
        ScriptParser.Parse(decoded);
        var numberKeys = values.Keys.Where(k => k.Table == ScriptValues && Core.ScriptValues.Split(k.Id).Event == eventNumber).Select(k => (k.Table, k.Id)).ToList();
        return Record(numberKeys.Append((Scripts, eventNumber)), () =>
        {
            foreach (var key in numberKeys) Restore(key.Table, key.Id, new RecordState(new Dictionary<string, int>(), null, null));
            if (decoded.AsSpan().SequenceEqual(original)) scripts.Remove(eventNumber); else scripts[eventNumber] = decoded.ToArray();
        });
    }

    private const int StablePriceEvent = 602, FurniturePriceEvent = 727;

    // Game texts

    /// <summary>The disc's texts; a text's id is its place in this list. Keys are entry names for sysfont.ar, else archive:entry.</summary>
    public IReadOnlyList<TextEntry> TextList => textRecords.Texts;
    public int TextId(string key) => textRecords.IdOf(key) ?? throw new EditException($"This disc has no text named {key}.");
    public string TextName(int id) => id >= 0 && id < TextList.Count ? TextList[id].Key : throw new EditException("There is no such text on this disc.");
    public TextFont? FontOfText(int id) => id >= 0 && id < TextList.Count ? textRecords.FontOf(TextList[id]) : null;
    public byte[] OriginalTextBytes(int id) => id >= 0 && id < TextList.Count ? TextList[id].Bytes : throw new EditException("There is no such text on this disc.");
    public byte[] TextBytes(int id) => texts.TryGetValue(id, out var bytes) ? bytes : OriginalTextBytes(id);
    public string Text(int id) => FontOfText(id)?.Decode(TextBytes(id)) ?? "";
    public string OriginalText(int id) => FontOfText(id)?.Decode(OriginalTextBytes(id)) ?? "";
    /// <summary>The changed texts by key, as glyph bytes.</summary>
    public IReadOnlyDictionary<string, byte[]> ChangedTexts => texts.ToDictionary(t => TextName(t.Key), t => t.Value, StringComparer.Ordinal);
    /// <summary>The changed texts by key, as text.</summary>
    public IReadOnlyDictionary<string, string> ChangedTextStrings => texts.ToDictionary(t => TextName(t.Key), t => Text(t.Key), StringComparer.Ordinal);

    /// <summary>Changes a text as one undoable step. Text equal to the disc's removes the change.</summary>
    public bool SetText(int id, string text)
    {
        var font = FontOfText(id) ?? throw new EditException("This disc's texts could not be read.");
        if (text.Length == 0) throw new EditException("A text cannot be empty.");
        if (text.Length > GameTexts.MostCharacters) throw new EditException($"A text can have at most {GameTexts.MostCharacters} characters.");
        var bytes = font.Encode(text, TextList[id].Archive.Codes);
        var original = OriginalTextBytes(id);
        return Record([(Texts, id)], () => { if (bytes.AsSpan().SequenceEqual(original)) texts.Remove(id); else texts[id] = bytes; });
    }

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
    private const int FormatVersion = 4;
    private const long MaxProjectSize = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record ProjectChange(string Table, int Id, string? Name, string Field, int Original, int Value);
    private sealed record ProjectCopy(string Table, int Id, int From);
    private sealed record ProjectName(string Table, int Id, string Name);
    private sealed record ProjectScript(int Event, string OriginalSha256, string Script);
    private sealed record ProjectText(string Name, string OriginalSha256, string Glyphs);
    private sealed record ProjectFile(string Format, int FormatVersion, string Edition, string EditionSha256, string? AppVersion,
        IReadOnlyList<ProjectChange> Changes, IReadOnlyList<ProjectCopy>? Copies = null, IReadOnlyList<ProjectName>? Names = null,
        IReadOnlyList<ProjectScript>? Scripts = null, IReadOnlyList<ProjectText>? Texts = null);

    private static string Sha(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    public string? RecordName(string table, int id, bool original = false) => (!original ? CustomName(table, id) : null) ?? table switch
    {
        Items => originalItems.TryGetValue(id, out var i) ? i.DisplayName : null,
        Recipes => disc.Items.FirstOrDefault(i => i.Id == id)?.DisplayName,
        Dishes => DishData.Name(id),
        Deployments or Placements => $"Map archive {id}",
        Texts => id >= 0 && id < TextList.Count ? $"Text {TextList[id].Key}" : null,
        Tastes => originalCharacters.TryGetValue(id, out var taster) ? taster.Name : null,
        Characters or Growths or Partners or Bags or Mercenaries => originalCharacters.TryGetValue(id, out var c) ? c.Name : null,
        Classes => originalClasses.TryGetValue(id, out var k) ? k.Name : null,
        Terrain => id < TerrainData.TypeNames.Length ? TerrainData.TypeNames[id] : null,
        TerrainKinds => id < TerrainData.Terrains ? TerrainData.TerrainLabel(id) : null,
        Rules => GameRules.All.FirstOrDefault(r => r.Id == id)?.Name,
        FurniturePrices => Core.FurniturePrices.Names.TryGetValue(id, out var piece) ? piece : null,
        ScriptValues => $"Event {Core.ScriptValues.Split(id).Event}, number {Core.ScriptValues.Split(id).Index + 1}",
        AnimationSets or AnimationPalettes => AnimationRowName(id),
        MapSprites => originalClasses.TryGetValue(id, out var spriteClass) ? spriteClass.Name : GameNames.Class(id),
        ArmyUnits => disc.Armies.SelectMany(a => a.Units).FirstOrDefault(u => u.Key == id) is { } unit ? $"{disc.Armies.First(a => a.Id == unit.Army).Label}, {unit.Name}" : null,
        _ => null,
    };

    private string AnimationRowName(int id)
    {
        var (palette, classId, row) = AnimationTable.SplitId(id);
        string cls = originalClasses.TryGetValue(classId, out var k) ? k.Name : GameNames.Class(classId) ?? $"Class {classId}";
        return $"{cls}, {(palette ? "colour" : "animation")} row {row + 1}";
    }

    public string ToJson(string? appVersion = null)
    {
        bool extended = copies.Count > 0 || names.Count > 0;
        // Projects with changed scripts need this version; others stay readable by older ones.
        int version = texts.Count > 0 ? FormatVersion : scripts.Count > 0 ? 3 : extended ? 2 : 1;
        return JsonSerializer.Serialize(new ProjectFile(FormatName, version, disc.Edition, disc.Sha256, appVersion,
            values.OrderBy(v => Array.IndexOf(TableOrder, v.Key.Table)).ThenBy(v => v.Key.Id)
                  .Select(v => new ProjectChange(v.Key.Table, v.Key.Id, RecordName(v.Key.Table, v.Key.Id), v.Key.Field, OriginalValue(v.Key.Table, v.Key.Id, v.Key.Field), v.Value)).ToList(),
            extended ? copies.OrderBy(c => c.Key.Id).Select(c => new ProjectCopy(c.Key.Table, c.Key.Id, c.Value)).ToList() : null,
            extended ? names.OrderBy(n => n.Key.Id).Select(n => new ProjectName(n.Key.Table, n.Key.Id, n.Value)).ToList() : null,
            scripts.Count > 0 ? scripts.OrderBy(s => s.Key).Select(s => new ProjectScript(s.Key, Sha(scriptRecords.Bytes(s.Key)!), Convert.ToBase64String(s.Value))).ToList() : null,
            texts.Count > 0 ? texts.OrderBy(t => t.Key).Select(t => new ProjectText(TextName(t.Key), Sha(OriginalTextBytes(t.Key)), Convert.ToBase64String(t.Value))).ToList() : null), Json);
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
        foreach (var script in file.Scripts ?? [])
        {
            if (script is null || script.Script is null || script.OriginalSha256 is null) throw new EditException("This project contains an empty script.");
            var original = session.scriptRecords.Bytes(script.Event) ?? throw new EditException($"This project changes event {script.Event}, which is not on this disc.");
            if (!string.Equals(Sha(original), script.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new EditException($"The disc does not match this project: event {script.Event} differs from what the project expects.");
            byte[] decoded;
            try { decoded = Convert.FromBase64String(script.Script); ScriptParser.Parse(decoded); }
            catch (Exception e) when (e is FormatException or InvalidDataException) { throw new EditException($"This project's script for event {script.Event} is damaged."); }
            if (session.values.Keys.Any(k => k.Table == ScriptValues && Core.ScriptValues.Split(k.Id).Event == script.Event) || !session.scripts.TryAdd(script.Event, decoded))
                throw new EditException($"This project changes event {script.Event} twice.");
        }
        foreach (var text in file.Texts ?? [])
        {
            if (text is null || text.Name is null || text.Glyphs is null || text.OriginalSha256 is null) throw new EditException("This project contains an empty text.");
            int id = session.TextId(text.Name);
            if (!string.Equals(Sha(session.OriginalTextBytes(id)), text.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new EditException($"The disc does not match this project: text {text.Name} differs from what the project expects.");
            byte[] glyphs;
            try { glyphs = Convert.FromBase64String(text.Glyphs); }
            catch (FormatException) { throw new EditException($"This project's text {text.Name} is damaged."); }
            bool codes = session.TextList[id].Archive.Codes;
            if (glyphs.Length == 0 || glyphs.Length % 2 != 0 || glyphs.Length > GameTexts.MostCharacters * 4 || session.FontOfText(id) is not { } font
                || Enumerable.Range(0, glyphs.Length / 2).Any(i => BitConverter.ToUInt16(glyphs, i * 2) is var g && (g >= 0x8000 ? !codes : g >= font.Count)))
                throw new EditException($"This project's text {text.Name} is damaged.");
            if (!session.texts.TryAdd(id, glyphs)) throw new EditException($"This project changes text {text.Name} twice.");
        }
        foreach (var (tableName, id) in session.EditedRecords.Where(r => r.Table != Scripts && r.Table != Texts))
        {
            var table = session.Table(tableName);
            string problem = table.Validate(table.Fields.ToDictionary(f => f.Key, f => session.Value(tableName, id, f.Key)));
            if (problem.Length > 0) throw new EditException($"{Capitalize(table.Noun)} 0x{id:X3}: {problem}");
        }
        return session;
    }
}
