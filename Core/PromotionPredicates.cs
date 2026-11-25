namespace Berwick.Core;

public sealed record PromotionThreshold(int Selector, string Name, string SourceClasses, bool RequireAll,
    int Minimum, int LiteralIndex)
{
    public bool Editable => Selector != 17;
}

public static class PromotionPredicates
{
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [3] = "Sword", [5] = "Spear", [6] = "Axe", [7] = "Bow", [8] = "Crossbow",
        [9] = "Fire", [10] = "Thunder", [11] = "Wind", [12] = "Holy",
        [14] = "Small shield", [15] = "Medium shield", [17] = "Derived Mind"
    };

    public static IReadOnlyList<PromotionThreshold> Read(EditSession session, int unitId)
    {
        PromotionRequirements.RequireSource(session);
        int destination = PromotionDestinations.TryCurrentTarget(session, unitId) ?? PromotionCatalogue.Caller(unitId).Target;
        var original = session.OriginalEventScript(237) ?? throw new EditException("The original shared promotion event is missing.");
        var current = session.EventScript(237) ?? throw new EditException("The shared promotion event is missing.");
        var originalNodes = ScriptParser.Parse(original);
        var currentNodes = ScriptParser.Parse(current);
        if (!PromotionRequirements.SameStructure(originalNodes, currentNodes))
            throw new EditException("Shared promotion logic was changed. Review event 237 before editing thresholds.");

        var allowed = new HashSet<int>();
        foreach (int target in PromotionCatalogue.Read(session.Disc).Paths.Select(p => p.DestinationClassId).Distinct())
            foreach (var field in Read(target, original))
                if (field.Editable) allowed.Add(field.LiteralIndex);
        var before = ScriptValues.Numbers(237, originalNodes);
        var after = ScriptValues.Numbers(237, currentNodes);
        if (before.Zip(after).Any(p => p.First.Value != p.Second.Value && !allowed.Contains(p.First.Index)))
            throw new EditException("A shared promotion selector, class condition or other protected value was changed.");
        return Read(destination, current);
    }

    public static bool SetThreshold(EditSession session, int unitId, int literalIndex, int value)
    {
        var fields = Read(session, unitId);
        var field = fields.SingleOrDefault(f => f.LiteralIndex == literalIndex)
            ?? throw new EditException("This threshold does not belong to the selected promotion.");
        if (field.Minimum == value) return false;
        int destination = PromotionDestinations.TryCurrentTarget(session, unitId) ?? PromotionCatalogue.Caller(unitId).Target;
        var edited = Change(destination, session.EventScript(237)!, literalIndex, value);
        // Preserve existing individually edited script numbers as separate undo steps.
        if (!session.HasScript(237))
            return session.Apply(EditSession.ScriptValues, ScriptValues.Key(237, literalIndex),
                new Dictionary<string, int> { ["value"] = value });
        return session.SetScript(237, edited);
    }

    public static IReadOnlyList<int> KnownUsers(EditSession session, int unitId, int literalIndex)
    {
        if (!Read(session, unitId).Any(f => f.LiteralIndex == literalIndex))
            throw new EditException("This threshold does not belong to the selected promotion.");
        var original = session.OriginalEventScript(237)!;
        return PromotionCatalogue.Read(session.Disc).Paths
            .Where(p => Read(PromotionDestinations.TryCurrentTarget(session, p.UnitId)
                ?? p.DestinationClassId, original).Any(f => f.LiteralIndex == literalIndex))
            .Select(p => p.UnitId).Distinct().Order().ToArray();
    }

    public static IReadOnlyList<PromotionThreshold> Read(int destination, ReadOnlySpan<byte> script)
    {
        var nodes = ScriptParser.Parse(script);
        if (nodes is not [{ Command: ScriptParser.Event, Args: [var body] }])
            throw new EditException("The shared promotion script must contain one event.");
        var branches = body.Where(n => n.Command == ScriptParser.If && n.Args.Count == 3
            && MatchesDestination(n.Args[0], destination)).ToList();
        if (branches.Count != 1)
            throw new EditException("The shared destination branch is missing or ambiguous.");
        var result = new List<PromotionThreshold>();
        void Walk(IReadOnlyList<ScriptNode> block, Dictionary<int, int> inherited, string sourceClasses)
        {
            var selectors = new Dictionary<int, int>(inherited);
            foreach (var node in block)
            {
                if (node is { Command: ScriptParser.Assign, Variable: int variable, Args: [[{ Command: 328 } info]] })
                {
                    if (info.Args is not [[{ Command: ScriptParser.Variable, Variable: 3 }],
                        [{ Command: ScriptParser.Variable, Variable: 0 }], [{ Command: ScriptParser.Number, Value: int selector }]])
                        throw new EditException("A promotion stat query has computed or unsupported arguments.");
                    selectors[variable] = selector;
                }
                if (node.Command != ScriptParser.If) continue;
                if (node.Args.Count != 3 || node.Args[0].Count != 1)
                    throw new EditException("A shared promotion condition has an unsupported structure.");
                var condition = node.Args[0][0];
                if (ContainsComparison(condition))
                {
                    if (node.Args[1] is not [{ Command: ScriptParser.Assign, Variable: 2,
                        Args: [[{ Command: ScriptParser.Number, Value: 0 }]] }] || node.Args[2].Count != 0)
                        throw new EditException("A promotion threshold has an unsupported result branch.");
                    int combination = condition.Command is 8 or 9 ? condition.Command : 9;
                    void ReadComparisons(ScriptNode expression)
                    {
                        if (expression.Command == combination && expression.Args is [[var left], [var right]])
                        {
                            ReadComparisons(left);
                            ReadComparisons(right);
                            return;
                        }
                        if (expression is not { Command: 0x14, Args: [[{ Command: ScriptParser.Variable, Variable: int stat }],
                            [{ Command: ScriptParser.Number, Value: int minimum } literal]] }
                            || !selectors.TryGetValue(stat, out int selector) || !Names.TryGetValue(selector, out string? name)
                            || minimum is < 0 or > 63)
                            throw new EditException("A promotion threshold uses unsupported values or mixed condition grouping.");
                        result.Add(new(selector, name, sourceClasses, combination == 9, minimum,
                            PromotionRequirements.NumberIndex(nodes, literal)));
                    }
                    ReadComparisons(condition);
                    continue;
                }
                string nested = sourceClasses;
                if (condition is { Command: 0x0F, Args: [[{ Command: ScriptParser.Variable }],
                    [{ Command: 31, Args: [[{ Command: ScriptParser.Number, Value: int classId }]] }]] })
                    nested = $"Source class {classId}";
                else if (condition is { Command: 0x16, Args: [[{ Command: ScriptParser.Variable }]] })
                    nested = "Other source classes";
                Walk(node.Args[1], selectors, nested);
                Walk(node.Args[2], selectors, sourceClasses);
            }
        }
        Walk(branches[0].Args[1], [], "All source classes");
        return result.AsReadOnly();
    }

    public static byte[] Change(int destination, ReadOnlySpan<byte> script, int literalIndex, int value)
    {
        if (value is < 0 or > 63) throw new EditException("Proficiency requirements must be between 0 and 63 whole points.");
        var field = Read(destination, script).SingleOrDefault(f => f.LiteralIndex == literalIndex)
            ?? throw new EditException("The selected promotion threshold is missing.");
        if (!field.Editable) throw new EditException("Derived Mind editing is not verified.");
        if (field.Minimum == value) return script.ToArray();
        var changed = ScriptValues.Apply(script, new Dictionary<int, int> { [literalIndex] = value });
        if (Read(destination, changed).Single(f => f.LiteralIndex == literalIndex).Minimum != value)
            throw new EditException("The promotion threshold could not be verified after editing.");
        return changed;
    }

    private static bool MatchesDestination(IReadOnlyList<ScriptNode> condition, int destination)
    {
        if (condition is not [var node]) return false;
        if (node.Command == 9 && node.Args.Count == 2)
            return MatchesDestination(node.Args[0], destination) || MatchesDestination(node.Args[1], destination);
        return node is { Command: 0x0F, Args: [[{ Command: ScriptParser.Variable, Variable: 1 }],
            [{ Command: 31, Args: [[{ Command: ScriptParser.Number, Value: int value }]] }]] } && value == destination;
    }

    private static bool ContainsComparison(ScriptNode node) =>
        node.Command == 0x14 || node.Args.Any(a => a.Any(ContainsComparison));
}
