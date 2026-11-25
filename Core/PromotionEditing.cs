namespace Berwick.Core;

public sealed record PromotionLevelBinding(int UnitId, int CallerEvent, int Level, int LiteralIndex);

public static class PromotionRequirements
{
    public static PromotionLevelBinding ReadLevel(EditSession session, int unitId)
    {
        RequireSource(session);
        var caller = PromotionCatalogue.Caller(unitId);
        var original = session.OriginalEventScript(caller.Event)
            ?? throw new EditException("The original promotion caller is missing.");
        if (ReadLevel(unitId, original).Level != caller.Minimum)
            throw new EditException("The original promotion minimum does not match this source.");
        var current = session.EventScript(caller.Event)
            ?? throw new EditException("The current promotion caller is missing.");
        if (!SameStructure(ScriptParser.Parse(original), ScriptParser.Parse(current)))
            throw new EditException("This promotion caller's structure was changed. Review its event before editing requirements.");
        int expectedTarget = PromotionDestinations.Supports(unitId)
            ? PromotionDestinations.ReadForRequirements(session, unitId).Destination : caller.Target;
        return ReadLevel(unitId, current, expectedTarget);
    }

    public static bool SetLevel(EditSession session, int unitId, int level)
    {
        var binding = ReadLevel(session, unitId);
        if (binding.Level == level) return false;
        var current = session.EventScript(binding.CallerEvent)!;
        int target = PromotionDestinations.Supports(unitId)
            ? PromotionDestinations.ReadForRequirements(session, unitId).Destination : PromotionCatalogue.Caller(unitId).Target;
        var edited = ChangeLevel(unitId, current, level, target);
        // Preserve literal-only edits so the project review remains granular.
        if (!session.HasScript(binding.CallerEvent))
            return session.Apply(EditSession.ScriptValues, ScriptValues.Key(binding.CallerEvent, binding.LiteralIndex),
                new Dictionary<string, int> { ["value"] = level });
        return session.SetScript(binding.CallerEvent, edited);
    }

    public static PromotionLevelBinding ReadLevel(int unitId, ReadOnlySpan<byte> script, int? expectedTarget = null)
    {
        var caller = PromotionCatalogue.Caller(unitId);
        var nodes = ScriptParser.Parse(script);
        if (nodes is not [{ Command: ScriptParser.Event }])
            throw new EditException("The promotion caller must contain one event.");

        var candidates = new List<ScriptNode>();
        int helperCalls = 0;
        void Walk(IReadOnlyList<ScriptNode> block)
        {
            for (int i = 0; i < block.Count; i++)
            {
                var node = block[i];
                if (node.Command == ScriptParser.RunEvent)
                {
                    if (node.Args is not [[{ Command: ScriptParser.Number, Value: int target }]])
                        throw new EditException("This promotion caller contains an unresolved event call.");
                    if (target == 236) helperCalls++;
                }
                if (node is { Command: ScriptParser.Assign, Args: [[{ Command: ScriptParser.RunEvent } call]] }
                    && call.Args is [[{ Command: ScriptParser.Number, Value: 236 }]] && i >= 4)
                {
                    var unit = Assigned(block[i - 4], -1);
                    var source = Assigned(block[i - 3], -2);
                    var destination = Assigned(block[i - 2], -3);
                    var level = Assigned(block[i - 1], -4);
                    if (unit is { Command: 30, Args: [[{ Command: ScriptParser.Number, Value: int id }]] }
                        && id == unitId && source is { Command: ScriptParser.Number, Value: 0 }
                        && destination is { Command: 31, Args: [[{ Command: ScriptParser.Number, Value: int classId }]] }
                        && classId == (expectedTarget ?? caller.Target) && level is { Command: ScriptParser.Number, Value: >= 1 and <= 63 })
                        candidates.Add(level);
                }
                foreach (var argument in node.Args) Walk(argument);
            }
        }
        Walk(nodes);
        if (helperCalls != 1 || candidates.Count != 1)
            throw new EditException("The promotion caller's unit, class and level arguments are missing, changed or ambiguous.");
        var literal = candidates[0];
        int index = NumberIndex(nodes, literal);
        return new(unitId, caller.Event, literal.Value!.Value, index);
    }

    public static byte[] ChangeLevel(int unitId, ReadOnlySpan<byte> script, int level, int? expectedTarget = null)
    {
        if (level is < 1 or > 63) throw new EditException("Minimum level must be between 1 and 63.");
        var binding = ReadLevel(unitId, script, expectedTarget);
        if (binding.Level == level) return script.ToArray();
        var edited = ScriptValues.Apply(script, new Dictionary<int, int> { [binding.LiteralIndex] = level });
        if (ReadLevel(unitId, edited, expectedTarget).Level != level)
            throw new EditException("The changed promotion minimum could not be verified.");
        return edited;
    }

    private static ScriptNode? Assigned(ScriptNode node, int variable) =>
        node.Command == ScriptParser.Assign && node.Variable == variable && node.Args is [[var value]] ? value : null;

    internal static int NumberIndex(IReadOnlyList<ScriptNode> nodes, ScriptNode target)
    {
        int index = 0, found = -1;
        void Walk(IReadOnlyList<ScriptNode> block)
        {
            foreach (var node in block)
            {
                if (node.Command == ScriptParser.Number)
                {
                    if (ReferenceEquals(node, target)) found = index;
                    index++;
                }
                foreach (var argument in node.Args) Walk(argument);
            }
        }
        Walk(nodes);
        if (found < 0) throw new EditException("The promotion minimum is not a script literal.");
        return found;
    }

    internal static void RequireSource(EditSession session)
    {
        if (!PromotionCatalogue.Read(session.Disc).OriginalSourceVerified)
            throw new EditException("Promotion editing requires a verified original Japanese retail or English v2.3 source.");
    }

    internal static bool SameStructure(IReadOnlyList<ScriptNode> original, IReadOnlyList<ScriptNode> current)
    {
        if (original.Count != current.Count) return false;
        for (int i = 0; i < original.Count; i++)
        {
            var a = original[i];
            var b = current[i];
            if (a.Command != b.Command || a.Variable != b.Variable || a.Locals != b.Locals
                || a.Text != b.Text || a.Args.Count != b.Args.Count
                || (a.Data is null) != (b.Data is null)
                || (a.Data is not null && !a.Data.AsSpan().SequenceEqual(b.Data))) return false;
            for (int j = 0; j < a.Args.Count; j++)
                if (!SameStructure(a.Args[j], b.Args[j])) return false;
        }
        return true;
    }
}
