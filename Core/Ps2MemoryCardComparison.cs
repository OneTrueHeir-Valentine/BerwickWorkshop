namespace Berwick.Core;

/// <summary>Read-only comparison of file metadata from two inspected PS2 card images.</summary>
public static class Ps2MemoryCardComparison
{
    public sealed record Row(BerwickSaveFileSummary File, string Status);

    public static IReadOnlyList<Row> Compare(Ps2MemoryCardInfo primary, Ps2MemoryCardInfo? other)
    {
        ArgumentNullException.ThrowIfNull(primary);
        static Dictionary<(string Directory, string Name), BerwickSaveFileSummary> Index(
            IReadOnlyList<BerwickSaveFileSummary> files)
        {
            var indexed = new Dictionary<(string, string), BerwickSaveFileSummary>();
            foreach (var file in files)
            {
                if (!indexed.TryAdd((file.Directory, file.Name), file))
                    throw new InvalidDataException("Ambiguous duplicate file identity in card inventory.");
            }
            return indexed;
        }

        var originals = Index(primary.BerwickFiles);
        var comparison = other is null ? null : Index(other.BerwickFiles);
        var entries = new List<Row>();
        foreach (var pair in originals.OrderBy(x => x.Key.Directory, StringComparer.Ordinal)
                                      .ThenBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            string status = comparison is null ? "Not compared"
                : !comparison.TryGetValue(pair.Key, out var matched) ? "Missing in comparison"
                : pair.Value.Length == matched.Length && pair.Value.Sha256 == matched.Sha256
                    ? "Same" : "Different";
            entries.Add(new Row(pair.Value, status));
        }
        if (comparison is not null)
            entries.AddRange(comparison
                .Where(pair => !originals.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key.Directory, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
                .Select(pair => new Row(pair.Value, "Only in comparison")));
        return entries.AsReadOnly();
    }
}
