namespace Berwick.Core;

/// <summary>A literal event edge. Computed run-event targets are intentionally not inferred.</summary>
public sealed record EventCallEdge(int Caller, int Target);

/// <summary>
/// Source-accurate, read-only reverse index of direct literal run-event commands.
/// Edges include commands in conditional branches but do not claim those branches execute.
/// </summary>
public sealed class EventReferenceIndex
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> callers;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> callees;
    public IReadOnlyList<int> DynamicCallers { get; }
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> commandSites;
    public IReadOnlyList<int> IndexedCommands => commandSites.Keys.Order().ToArray();
    public IReadOnlyList<int> EventsUsingCommand(int command) =>
        commandSites.TryGetValue(command, out var events) ? events : Array.Empty<int>();
    public IReadOnlyList<int> UnreadableEvents { get; }
    public int IndexedEvents { get; }
    public int LiteralEdges { get; }

    private EventReferenceIndex(Dictionary<int, IReadOnlyList<int>> incoming,
        Dictionary<int, IReadOnlyList<int>> outgoing, IReadOnlyList<int> computedEvents,
        IReadOnlyList<int> unreadable, int indexedEvents, int literalEdges,
        IReadOnlyDictionary<int, IReadOnlyList<int>> commandSites)
    {
        callers = incoming;
        this.commandSites = commandSites;
        callees = outgoing;
        DynamicCallers = computedEvents;
        UnreadableEvents = unreadable;
        IndexedEvents = indexedEvents;
        LiteralEdges = literalEdges;
    }

    public IReadOnlyList<int> Callers(int eventNumber) =>
        callers.TryGetValue(eventNumber, out var found) ? found : Array.Empty<int>();
    public IReadOnlyList<int> Calls(int eventNumber) =>
        callees.TryGetValue(eventNumber, out var found) ? found : Array.Empty<int>();

    public static EventReferenceIndex Build(IEnumerable<int> eventNumbers, Func<int, byte[]> script)
    {
        var incoming = new Dictionary<int, HashSet<int>>();
        var outgoing = new Dictionary<int, IReadOnlyList<int>>();
        var computedEvents = new List<int>();
        var commands = new Dictionary<int, HashSet<int>>();
        var unreadable = new List<int>();
        int indexed = 0, edges = 0;
        foreach (var number in eventNumbers.Distinct().Order())
        {
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(script(number)); }
            catch (Exception ex) when (ex is InvalidDataException or EditException)
            {
                unreadable.Add(number);
                continue;
            }
            indexed++;
            void IndexCommands(IReadOnlyList<ScriptNode> block)
            {
                foreach (var node in block)
                {
                    if (node.Command >= 0x18)
                    {
                        if (!commands.TryGetValue(node.Command, out var locations))
                            commands[node.Command] = locations = [];
                        locations.Add(number);
                    }
                    foreach (var argument in node.Args) IndexCommands(argument);
                }
            }
            IndexCommands(nodes);
            var references = DirectCalls(nodes);
            if (references.ComputedCalls != 0) computedEvents.Add(number);
            var targets = references.LiteralTargets.Distinct().Order().ToArray();
            outgoing[number] = targets;
            foreach (var target in targets)
            {
                if (!incoming.TryGetValue(target, out var uses)) incoming[target] = uses = [];
                uses.Add(number);
                edges++;
            }
        }
        return new EventReferenceIndex(
            incoming.ToDictionary(p => p.Key, p => (IReadOnlyList<int>)p.Value.Order().ToArray()),
            outgoing, computedEvents.AsReadOnly(), unreadable.AsReadOnly(), indexed, edges,
            commands.ToDictionary(x => x.Key, x => (IReadOnlyList<int>)x.Value.Order().ToArray()));
    }

    public static (IReadOnlyList<int> LiteralTargets, int ComputedCalls) DirectCalls(IReadOnlyList<ScriptNode> script)
    {
        var targets = new List<int>();
        int computed = 0;
        void Walk(IReadOnlyList<ScriptNode> block)
        {
            foreach (var node in block)
            {
                if (node.Command == ScriptParser.RunEvent)
                {
                    if (node.Args is [[{ Command: ScriptParser.Number, Value: int target }]]
                        && target >= 0)
                        targets.Add(target);
                    else computed++;
                }
                foreach (var argument in node.Args) Walk(argument);
            }
        }
        Walk(script);
        return (targets.AsReadOnly(), computed);
    }
}
