namespace Berwick.Core;

internal sealed record PromotionAnalysis(
    IReadOnlyList<ChangeEntry> Changes, IReadOnlyList<ReviewIssue> Warnings);

internal static class PromotionChanges
{
    public static IReadOnlyList<ChangeEntry> ForEvent(EditSession session, int eventNumber) =>
        Analyze(session, eventNumber).Changes;

    public static PromotionAnalysis Analyze(EditSession session, int eventNumber)
    {
        var paths = PromotionCatalogue.Read(session.Disc).Paths;
        var changes = new List<ChangeEntry>();
        var warnings = new List<ReviewIssue>();

        void Add(string key, string label, int before, int after) => changes.Add(new(EditSession.Scripts,
            eventNumber, $"Event {eventNumber}", key, label, before.ToString(), after.ToString(), "DATA3.DAT", "Promotion"));

        void Warn(string stage, PromotionPath? path, Exception error)
        {
            string route = path is null ? "shared event" : $"{path.CharacterName} (unit {path.UnitText})";
            warnings.Add(new("Warning", "promotion-analysis-incomplete",
                $"Event {eventNumber}: {route}, {stage}: {error.Message}", EditSession.Scripts, eventNumber));
        }

        void TryRoute(PromotionPath path, string stage, Action action)
        {
            try { action(); }
            catch (Exception error) when (error is EditException or IOException)
            {
                Warn(stage, path, error);
            }
        }

        foreach (var path in paths.Where(p => p.CallerEvent == eventNumber))
            TryRoute(path, "minimum level", () =>
            {
                var original = session.OriginalEventScript(eventNumber)
                    ?? throw new EditException("The original promotion caller is missing.");
                int before = PromotionRequirements.ReadLevel(path.UnitId, original).Level;
                int after = PromotionRequirements.ReadLevel(session, path.UnitId).Level;
                if (before != after)
                    Add("promotionLevel", $"{path.CharacterName}: promotion minimum level", before, after);
            });

        foreach (var path in paths.Where(p => p.ApplicationEvent == eventNumber && PromotionDestinations.Supports(p.UnitId)))
            TryRoute(path, "destination", () =>
            {
                var target = PromotionDestinations.Read(session, path.UnitId);
                if (target.Destination != path.DestinationClassId && eventNumber == target.ApplicationEvent)
                    Add("promotionDestination", $"{path.CharacterName}: coordinated destination class",
                        path.DestinationClassId, target.Destination);
            });

        if (eventNumber != 237 || paths.Count == 0)
            return new(changes.AsReadOnly(), warnings.AsReadOnly());

        byte[]? originalShared = session.OriginalEventScript(237);
        byte[]? currentShared = session.EventScript(237);
        if (originalShared is null || currentShared is null)
        {
            Warn("shared requirements", null, new EditException("The original or current shared promotion event is missing."));
            return new(changes.AsReadOnly(), warnings.AsReadOnly());
        }

        // Track literal indices, not destination IDs. Some destination classes share one branch.
        var comparisons = new Dictionary<int, (PromotionThreshold Field, int Before, int After)>();
        var originalUsers = new Dictionary<int, HashSet<string>>();
        var projectUsers = new Dictionary<int, HashSet<string>>();

        static void Remember(Dictionary<int, HashSet<string>> map, int index, string name)
        {
            if (!map.TryGetValue(index, out var users)) map[index] = users = new(StringComparer.Ordinal);
            users.Add(name);
        }

        static Dictionary<int, PromotionThreshold> UniqueFields(IReadOnlyList<PromotionThreshold> fields)
        {
            var result = new Dictionary<int, PromotionThreshold>();
            foreach (var field in fields)
                if (result.TryGetValue(field.LiteralIndex, out var existing))
                {
                    if (existing.Minimum != field.Minimum || existing.Selector != field.Selector)
                        throw new EditException("The same promotion literal has conflicting requirements.");
                }
                else result.Add(field.LiteralIndex, field);
            return result;
        }

        void ReadBranch(int destination)
        {
            var before = UniqueFields(PromotionPredicates.Read(destination, originalShared));
            var after = UniqueFields(PromotionPredicates.Read(destination, currentShared));
            var pending = new Dictionary<int, (PromotionThreshold Field, int Before, int After)>();
            foreach (var current in after.Values)
            {
                if (!before.TryGetValue(current.LiteralIndex, out var original))
                    throw new EditException("A shared threshold literal no longer matches its original branch.");
                if (original.Minimum == current.Minimum) continue;
                if (comparisons.TryGetValue(current.LiteralIndex, out var previous))
                {
                    if (previous.Before != original.Minimum || previous.After != current.Minimum)
                        throw new EditException("A shared threshold has conflicting values across destination branches.");
                }
                else pending.Add(current.LiteralIndex, (current, original.Minimum, current.Minimum));
            }
            foreach (var (index, value) in pending) comparisons.Add(index, value);
        }

        foreach (var path in paths)
        {
            TryRoute(path, "original shared requirements", () =>
            {
                foreach (var field in PromotionPredicates.Read(path.DestinationClassId, originalShared))
                    Remember(originalUsers, field.LiteralIndex, path.CharacterName);
                ReadBranch(path.DestinationClassId);
            });

            TryRoute(path, "project destination and shared requirements", () =>
            {
                int destination = PromotionDestinations.TryCurrentTarget(session, path.UnitId)
                    ?? path.DestinationClassId;
                foreach (var field in PromotionPredicates.Read(destination, currentShared))
                    Remember(projectUsers, field.LiteralIndex, path.CharacterName);
                ReadBranch(destination);
            });
        }

        foreach (var (index, comparison) in comparisons.OrderBy(c => c.Key))
        {
            static string Names(Dictionary<int, HashSet<string>> groups, int key) =>
                groups.TryGetValue(key, out var names) && names.Count > 0
                    ? string.Join(", ", names.OrderBy(x => x, StringComparer.Ordinal)) : "none identified";
            string completeness = warnings.Count > 0 ? "; affected-user coverage incomplete" : "";
            Add($"promotionThreshold{index}",
                $"Shared {comparison.Field.Name}, {comparison.Field.SourceClasses}; original known users: {Names(originalUsers, index)}; " +
                $"current project users: {Names(projectUsers, index)}{completeness}",
                comparison.Before, comparison.After);
        }
        return new(changes.AsReadOnly(), warnings.AsReadOnly());
    }
}
