using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Berwick.Core;

public sealed record ItemRecord(int Id, string Name, int Might, int Accuracy, int Weight, int Price, int MinRange, int MaxRange, int Critical, int Uses, int Level, int Durability, int EffectValue, int EffectSelector, string Effects)
{
    public int Type { get; init; }
    /// <summary>A name given in a project, for items created in a free slot.</summary>
    public string? CustomName { get; init; }
    public bool IsUnused => CustomName is null && Name.StartsWith("DUMMY", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => CustomName ?? (IsUnused ? $"Unused item {Id:X3}" : Regex.Replace(Name, @"(?<=[a-z])(?=[A-Z])", " ").Replace("Thiefs", "Thief's").Replace("Assassins", "Assassin's"));
    public string IdText => $"0x{Id:X3}";
    public string Category => Type < ItemDecoder.Types.Length && Type >= 0 ? ItemDecoder.Types[Type] : "Other";
    public string Range => MinRange == MaxRange ? MinRange.ToString(CultureInfo.InvariantCulture) : $"{MinRange} to {MaxRange}";
    public string DurabilityText => Durability is >= 0 and <= 6 ? new[] {"S","A","B","C","D","E","F"}[Durability] : "Unknown";
    public string EffectDescription => ItemDecoder.DescribeEffect(EffectSelector, EffectValue);
    public IReadOnlyDictionary<string,string> Fields() => new Dictionary<string,string>
    {
        ["Type"] = Category, ["Might"] = Might.ToString(), ["Accuracy"] = Accuracy.ToString(),
        ["Weight"] = Weight.ToString(), ["Price"] = Price.ToString(), ["Minimum range"] = MinRange.ToString(),
        ["Maximum range"] = MaxRange.ToString(), ["Critical"] = Critical.ToString(), ["Level"] = Level.ToString(),
        ["Durability"] = DurabilityText, ["Durability pool"] = Uses.ToString(),
        ["Effect value"] = EffectValue.ToString(), ["Effect selector"] = EffectSelector.ToString(), ["Effect flags"] = Effects
    };
}

public static class ItemDecoder
{
    public static readonly string[] Types = ["Knife", "Sword", "Blade", "Mace", "Spear", "Lance", "Fork", "Axe", "Bow", "Crossbow", "Ballista", "Fire", "Thunder", "Wind", "Holy", "Dark", "Healing", "Dark healing", "Small shield", "Medium shield", "Large shield", "Arrow", "Bolt", "Accessory", "Consumable"];
    private static readonly string[] Rates = ["Half damage", "Unknown", "Backfire", "Unknown", "Steal bonus", "Thunder damage", "Fire damage", "Wind damage", "Sword break", "Unknown", "Kill horse", "Break shield", "Unknown", "Poison", "Sleep", "Unknown", "Negate dark", "Negate critical", "Reflect damage", "Growth bonus", "Unknown", "Cripple", "Unknown", "Dark damage", "Holy damage", "Break weapon", "Block fire", "Unknown", "Block thunder", "Unknown", "Disarm", "Injury", "Unknown", "Block holy"];
    public static string DescribeEffect(int selector, int value) => selector == 0 ? "None" : selector >= 100 && selector - 100 < Rates.Length ? $"{Rates[selector - 100]}: {value}" : $"Unclassified ({selector}): {value}";
    public static int Bits(ReadOnlySpan<byte> data, int offset, int bit, int width)
    {
        if (offset < 0 || bit is < 0 or > 7 || width is < 1 or > 32 || offset + (bit + width + 7) / 8 > data.Length)
            throw new InvalidDataException("An item record is incomplete.");
        ulong value = 0;
        for (int n = 0; n < (bit + width + 7) / 8; n++) value |= (ulong)data[offset+n] << (8*n);
        return (int)(uint)((value >> bit) & ((1UL << width) - 1));
    }
    public static ItemRecord Decode(int id, string name, byte[] bytes)
    {
        if (bytes.Length != 56) throw new InvalidDataException("An item record is incomplete.");
        int B(int offset, int bit, int width) => Bits(bytes, offset, bit, width);
        return new(id, name, B(0,5,6),B(1,7,7),B(2,6,5),B(8,0,16),B(4,0,4),B(3,3,5),B(5,0,7),B(5,7,7),B(6,6,6),B(21,4,3),B(26,0,7),bytes[27],Convert.ToHexString(bytes.AsSpan(28,12))) { Type=B(0,0,5) };
    }
}

public static class Catalogue
{
    public static IReadOnlyDictionary<int,string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("items.csv")!;
        using var reader = new StreamReader(stream);
        var result = new SortedDictionary<int,string>();
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split(',');
            if (parts.Length == 2) result.Add(int.Parse(parts[1].Trim()[2..],NumberStyles.HexNumber), parts[0].Trim());
        }
        return result;
    }
}

public sealed record ItemDifference(int Id, string Field, string First, string Second);
public static class ItemComparison
{
    public static IReadOnlyList<ItemDifference> Compare(IEnumerable<ItemRecord> first, IEnumerable<ItemRecord> second)
    {
        var a=first.ToDictionary(i=>i.Id); var b=second.ToDictionary(i=>i.Id);
        var differences=new List<ItemDifference>();
        foreach (int id in a.Keys.Union(b.Keys).Order())
        {
            if (!a.TryGetValue(id,out var x)) { differences.Add(new(id,"Item","Absent","Present")); continue; }
            if (!b.TryGetValue(id,out var y)) { differences.Add(new(id,"Item","Present","Absent")); continue; }
            var fields=y.Fields();
            foreach(var field in x.Fields()) if(field.Value != fields[field.Key]) differences.Add(new(id,field.Key,field.Value,fields[field.Key]));
        }
        return differences.AsReadOnly();
    }
}
