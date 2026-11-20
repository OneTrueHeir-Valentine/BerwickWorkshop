namespace Berwick.Core;

public sealed record ReviewIssue(string Severity, string Code, string Message, string Table = "", int? Id = null);
public sealed record FileChange(string File, long Offset, int Length, string Original, string Current);

public sealed record ProjectReview(IReadOnlyList<ChangeEntry> Changes, IReadOnlyList<ReviewIssue> Issues,
    IReadOnlyList<DiscPatch> Patches, IReadOnlyList<FileChange> Files)
{
    public bool CanExport => Issues.All(i => i.Severity != "Error");

    public static ProjectReview Build(EditSession session)
    {
        var changes = ChangeManifest.Build(session);
        var issues = DependencyChecks.Analyze(session).ToList();
        IReadOnlyList<DiscPatch> patches = [];
        try
        {
            patches = Exporter.PatchesFor(session, session.Disc);
            issues.AddRange(PatchChecks.Analyze(patches, session.Disc.Size));
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException)
        {
            issues.Add(new("Error", "export-plan", ex.Message));
        }
        var files = patches.Select(p => new FileChange(
            session.Disc.Files.FirstOrDefault(f => p.Offset >= f.Offset && p.Offset - f.Offset <= f.Length && p.Bytes.LongLength <= f.Length - (p.Offset - f.Offset))?.Name ?? "Disc directory or padding",
            p.Offset, p.Bytes.Length, p.Expected is null ? "Not supplied" : Convert.ToHexString(p.Expected), Convert.ToHexString(p.Bytes))).ToList();
        return new(changes, issues.AsReadOnly(), patches, files.AsReadOnly());
    }
}

public static class PatchChecks
{
    public static IReadOnlyList<ReviewIssue> Analyze(IEnumerable<DiscPatch> patches, long sourceLength)
    {
        var issues = new List<ReviewIssue>();
        long end = 0;
        foreach (var patch in patches.OrderBy(p => p.Offset))
        {
            if (patch.Expected is { } expected && expected.Length != patch.Bytes.Length)
                issues.Add(new("Error", "patch-original", $"The change at 0x{patch.Offset:X} has an incomplete original value."));
            if (patch.Offset < 0 || patch.Bytes.Length == 0 || patch.Offset > sourceLength || patch.Bytes.LongLength > sourceLength - patch.Offset)
            {
                issues.Add(new("Error", "patch-bounds", $"The change at 0x{patch.Offset:X} falls outside the disc image."));
                continue;
            }
            if (patch.Offset < end)
                issues.Add(new("Error", "patch-overlap", $"Two changes overlap at disc offset 0x{patch.Offset:X}."));
            end = Math.Max(end, patch.Offset + patch.Bytes.Length);
        }
        return issues.AsReadOnly();
    }
}
