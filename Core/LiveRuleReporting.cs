namespace Berwick.Core;

/// <summary>How much of an exported rule can be represented by live PINE writes.</summary>
public enum LiveRuleSupport { Full, Partial, ExportOnly }

public static class LiveRuleReporting
{
    public static LiveRuleSupport Support(GameRule rule)
    {
        int total = rule.Sites.Count, live = rule.Sites.Count(s => s.Live);
        return live == 0 ? LiveRuleSupport.ExportOnly
            : live == total ? LiveRuleSupport.Full : LiveRuleSupport.Partial;
    }

    /// <summary>Report complete, incomplete and skipped live changes without claiming export-only edits are active.</summary>
    public static string Summary(int fullyApplied, int partiallyApplied, int exportOnly, int incompatible)
    {
        if (fullyApplied < 0 || partiallyApplied < 0 || exportOnly < 0 || incompatible < 0)
            throw new ArgumentOutOfRangeException(nameof(fullyApplied));
        if (fullyApplied + partiallyApplied + exportOnly + incompatible == 0)
            return "No changed game rules to apply.";
        var parts = new List<string>();
        if (fullyApplied > 0) parts.Add($"{fullyApplied} live-capable rule(s) applied or verified in the running game.");
        if (partiallyApplied > 0) parts.Add($"{partiallyApplied} rule(s) applied only in part; the complete effect requires an exported image and restart.");
        if (exportOnly > 0) parts.Add($"{exportOnly} export-only rule(s) were not applied to the running game; export and boot the edited image.");
        if (incompatible > 0) parts.Add($"{incompatible} live rule(s) were not applied because the running code did not match a known setting.");
        return string.Join(" ", parts);
    }
}
