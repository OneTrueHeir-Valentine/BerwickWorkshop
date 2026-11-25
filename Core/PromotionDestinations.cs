namespace Berwick.Core;

/// <summary>
/// A coordinated character-specific promotion destination. The supported pilot includes only
/// Adel and Leon: both application and eligibility VM inputs have been independently traced.
/// </summary>
public sealed record PromotionDestinationBinding(int UnitId, int CallerEvent, int ApplicationEvent,
    int Destination, int CallerLiteralIndex, int ApplicationLiteralIndex);

public static class PromotionDestinations
{
    private static readonly IReadOnlyDictionary<int, (int Application, int Town)> Verified =
        new Dictionary<int, (int, int)> { [87] = (167, 1353), [88] = (169, 1367) };

    public static bool Supports(int unitId) => Verified.ContainsKey(unitId);

    public static PromotionDestinationBinding Read(EditSession session, int unitId) =>
        ReadBinding(session, unitId, requireUnrelatedLiteralsUntouched: true);

    // Reading a minimum level must not impose the stricter destination-write policy.
    // Still verify the source, caller/application pair, and target identity.
    internal static PromotionDestinationBinding ReadForRequirements(EditSession session, int unitId) =>
        ReadBinding(session, unitId, requireUnrelatedLiteralsUntouched: false);

    private static PromotionDestinationBinding ReadBinding(EditSession session, int unitId,
        bool requireUnrelatedLiteralsUntouched)
    {
        PromotionRequirements.RequireSource(session);
        if (!Verified.TryGetValue(unitId, out var route))
            throw new EditException("This unit's complete destination-edit binding has not been verified.");
        var caller = PromotionCatalogue.Caller(unitId);
        byte[] originalCaller = session.OriginalEventScript(caller.Event)
            ?? throw new EditException("The original promotion eligibility caller is missing.");
        byte[] originalApplication = session.OriginalEventScript(route.Application)
            ?? throw new EditException("The original promotion application event is missing.");
        var originalCallerTarget = CallerTarget(unitId, caller.Event, originalCaller);
        var originalApplicationTarget = ApplicationTarget(unitId, route.Town, route.Application, originalApplication);
        if (originalCallerTarget.Destination != caller.Target || originalApplicationTarget.Destination != caller.Target)
            throw new EditException("The original promotion destination does not match the verified source.");

        byte[] currentCaller = session.EventScript(caller.Event)
            ?? throw new EditException("The promotion eligibility caller is missing.");
        byte[] currentApplication = session.EventScript(route.Application)
            ?? throw new EditException("The promotion application event is missing.");
        if (!PromotionRequirements.SameStructure(ScriptParser.Parse(originalCaller), ScriptParser.Parse(currentCaller))
            || !PromotionRequirements.SameStructure(ScriptParser.Parse(originalApplication), ScriptParser.Parse(currentApplication)))
            throw new EditException("The promotion event structure was modified; review it before changing destinations.");

        var candidate = CallerTarget(unitId, caller.Event, currentCaller);
        var actual = ApplicationTarget(unitId, route.Town, route.Application, currentApplication);
        if (candidate.Destination != actual.Destination)
            throw new EditException("Eligibility and application target different classes; manual repair is required.");

        if (requireUnrelatedLiteralsUntouched)
        {
            // A destination rewrite must not silently incorporate other numeric edits.
            // A minimum-level read does not rewrite those values and needs no such restriction.
            int originalLevelIndex = PromotionRequirements.ReadLevel(unitId, originalCaller).LiteralIndex;
            ValidateLiteralChanges(caller.Event, originalCaller, currentCaller,
                [originalCallerTarget.LiteralIndex, originalLevelIndex]);
            ValidateLiteralChanges(route.Application, originalApplication, currentApplication,
                [originalApplicationTarget.LiteralIndex]);
        }
        return new(unitId, caller.Event, route.Application, candidate.Destination,
            candidate.LiteralIndex, actual.LiteralIndex);
    }

    public static int? TryCurrentTarget(EditSession session, int unitId) =>
        Supports(unitId) ? Read(session, unitId).Destination : null;

    public static bool SetDestination(EditSession session, int unitId, int destination)
    {
        var binding = Read(session, unitId);
        if (binding.Destination == destination) return false;
        if (!session.EditedClasses().Any(c => c.Id == destination))
            throw new EditException($"Destination class 0x{destination:X2} is absent from this source.");
        // The shared proficiency helper must have a real matching destination branch.
        // A fall-through success is not evidence of a valid promotion.
        var shared = session.OriginalEventScript(237)
            ?? throw new EditException("The original shared promotion helper is missing.");
        _ = PromotionPredicates.Read(destination, shared);

        byte[] caller = ScriptValues.Apply(session.EventScript(binding.CallerEvent)!,
            new Dictionary<int, int> { [binding.CallerLiteralIndex] = destination });
        byte[] application = ScriptValues.Apply(session.EventScript(binding.ApplicationEvent)!,
            new Dictionary<int, int> { [binding.ApplicationLiteralIndex] = destination });
        // Validate the new pair before mutating the edit session. A single Undo restores both.
        if (CallerTarget(unitId, binding.CallerEvent, caller).Destination != destination
            || ApplicationTarget(unitId, Verified[unitId].Town, binding.ApplicationEvent, application).Destination != destination)
            throw new EditException("The coordinated promotion destination could not be verified.");
        return session.SetScripts(new Dictionary<int, byte[]>
        {
            [binding.CallerEvent] = caller, [binding.ApplicationEvent] = application
        });
    }

    public static (int Destination, int LiteralIndex) CallerTarget(int unitId, int eventNumber, ReadOnlySpan<byte> script)
    {
        var nodes = ScriptParser.Parse(script);
        if (nodes is not [{ Command: ScriptParser.Event }])
            throw new EditException("The promotion caller must contain one event.");
        int candidate = -1;
        ScriptNode? literal = null;
        void Walk(IReadOnlyList<ScriptNode> block)
        {
            for (int i = 0; i < block.Count; i++)
            {
                if (block[i] is { Command: ScriptParser.Assign, Variable: 0,
                    Args: [[{ Command: ScriptParser.RunEvent, Args: [[{ Command: ScriptParser.Number, Value: 236 }]] }]] }
                    && i >= 4
                    && block[i - 4] is { Command: ScriptParser.Assign, Variable: -1,
                        Args: [[{ Command: 30, Args: [[{ Command: ScriptParser.Number, Value: int actor }]] }]] }
                    && actor == unitId
                    && block[i - 3] is { Command: ScriptParser.Assign, Variable: -2,
                        Args: [[{ Command: ScriptParser.Number, Value: 0 }]] }
                    && block[i - 2] is { Command: ScriptParser.Assign, Variable: -3,
                        Args: [[{ Command: 31, Args: [[{ Command: ScriptParser.Number, Value: int target } number]] }]] }
                    && block[i - 1] is { Command: ScriptParser.Assign, Variable: -4,
                        Args: [[{ Command: ScriptParser.Number, Value: >= 1 and <= 63 }]] })
                {
                    if (literal is not null) throw new EditException("The promotion destination is ambiguous.");
                    candidate = target;
                    literal = number;
                }
                foreach (var child in block[i].Args) Walk(child);
            }
        }
        Walk(nodes);
        if (literal is null) throw new EditException($"Event {eventNumber} has no verified promotion destination argument.");
        return (candidate, PromotionRequirements.NumberIndex(nodes, literal));
    }

    public static (int Destination, int LiteralIndex) ApplicationTarget(int unitId, int town, int eventNumber,
        ReadOnlySpan<byte> script)
    {
        var nodes = ScriptParser.Parse(script);
        if (nodes is not [{ Command: ScriptParser.Event, Locals: 0, Args: [var body] }]
            || body is not
            [
                { Command: ScriptParser.Assign, Variable: -1,
                    Args: [[{ Command: 30, Args: [[{ Command: ScriptParser.Number, Value: int actor }]] }]] },
                { Command: ScriptParser.Assign, Variable: -3,
                    Args: [[{ Command: 31, Args: [[{ Command: ScriptParser.Number, Value: int target } value]] }]] },
                { Command: ScriptParser.RunEvent, Args: [[{ Command: ScriptParser.Number, Value: 188 }]] },
                { Command: ScriptParser.RunEvent, Args: [[{ Command: ScriptParser.Number, Value: int scene }]] }
            ] || actor != unitId || scene != town)
            throw new EditException($"Event {eventNumber} does not match its source-verified application route.");
        return (target, PromotionRequirements.NumberIndex(nodes, value));
    }

    private static void ValidateLiteralChanges(int eventNumber, ReadOnlySpan<byte> original,
        ReadOnlySpan<byte> current, IReadOnlyCollection<int> allowed)
    {
        var before = ScriptValues.Numbers(eventNumber, ScriptParser.Parse(original));
        var after = ScriptValues.Numbers(eventNumber, ScriptParser.Parse(current));
        if (before.Count != after.Count || before.Zip(after).Any(pair =>
            pair.First.Value != pair.Second.Value && !allowed.Contains(pair.First.Index)))
            throw new EditException("Other promotion event numbers were modified; resolve them before changing its destination.");
    }
}
