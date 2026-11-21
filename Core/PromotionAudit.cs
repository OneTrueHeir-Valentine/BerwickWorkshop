namespace Berwick.Core;

/// <summary>A candidate script operation. Its name is evidence for investigation, not proof of promotion semantics.</summary>
public sealed record PromotionCommandUse(int Event, int Offset, int Command, string Name, string Kind, int ArgumentCount, string LiteralArguments, bool InCondition);

/// <summary>One run-event expression. Target is null when the call is computed or cannot be resolved locally.</summary>
public sealed record PromotionEventCall(int Event, int Offset, int? Target);

public sealed record PromotionAuditReport(
    IReadOnlyList<PromotionCommandUse> Commands,
    IReadOnlyList<PromotionEventCall> EventCalls,
    IReadOnlyList<string> Errors);

/// <summary>
/// Read-only source investigation for class changes. It collects potential commands and literal event calls;
/// it does not identify promotion flags, execute script logic, or infer writable promotion offsets.
/// </summary>
public static class PromotionAudit
{
    private static readonly (string Keyword, string Kind)[] Keywords =
    [
        ("promot", "promotion"), ("class", "class"), ("job", "class"),
        ("level", "level"), ("weapon", "proficiency"), ("shield", "proficiency"),
        ("skill", "skill"), ("flag", "flag"), ("chapter", "chapter"),
        ("recruit", "recruitment"), ("member", "recruitment"), ("party", "recruitment"),
        ("status", "status"), ("stat", "statistic")
    ];

    /// <summary>Indexed observations from parsed event nodes, suitable for fixture-based tests.</summary>
    public static PromotionAuditReport Inspect(
        IReadOnlyList<string> names,
        IEnumerable<(int Event, IReadOnlyList<ScriptNode> Nodes)> scripts,
        IReadOnlyList<string>? errors = null)
    {
        var commands = new List<PromotionCommandUse>();
        var calls = new List<PromotionEventCall>();

        void Walk(IEnumerable<ScriptNode> nodes, int eventNumber, bool inCondition)
        {
            foreach (var node in nodes)
            {
                if (node.Command == ScriptParser.RunEvent)
                {
                    int? target = node.Args.Count == 1 &&
                        node.Args[0] is [{ Command: ScriptParser.Number, Value: int literal }]
                        ? literal : null;
                    calls.Add(new(eventNumber, node.Offset, target));
                }

                if (node.Command > ScriptParser.Break && node.Data is null)
                {
                    string name = ScriptText.Name(node.Command, names);
                    string? kind = Keywords.FirstOrDefault(k => name.Contains(k.Keyword, StringComparison.OrdinalIgnoreCase)).Kind;
                    if (kind is not null)
                    {
                        string literals = string.Join(";", node.Args.Select(a =>
                            a is [{ Command: ScriptParser.Number, Value: int number }] ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?"));
                        commands.Add(new(eventNumber, node.Offset, node.Command, name, kind, node.Args.Count, literals, inCondition));
                    }
                }

                for (int i = 0; i < node.Args.Count; i++)
                    Walk(node.Args[i], eventNumber,
                        inCondition || (node.Command is ScriptParser.If or ScriptParser.While && i == 0));
            }
        }

        foreach (var (eventNumber, nodes) in scripts)
            Walk(nodes, eventNumber, false);

        return new(
            commands.OrderBy(c => c.Event).ThenBy(c => c.Offset).ThenBy(c => c.Command).ToArray(),
            calls.OrderBy(c => c.Event).ThenBy(c => c.Offset).ToArray(),
            errors?.ToArray() ?? []);
    }

    /// <summary>Reads all discoverable original-disc event scripts; malformed scripts are reported rather than silently ignored.</summary>
    public static PromotionAuditReport Inspect(EventLibrary library)
    {
        if (library.CommandNames is null)
            throw new InvalidDataException("The original executable command-name table could not be read.");
        var scripts = new List<(int Event, IReadOnlyList<ScriptNode> Nodes)>();
        var errors = new List<string>();
        foreach (int number in library.Numbers)
        {
            try { scripts.Add((number, ScriptParser.Parse(library.Bytes(number)))); }
            catch (InvalidDataException e) { errors.Add($"Event {number}: {e.Message}"); }
        }
        return Inspect(library.CommandNames, scripts, errors);
    }
}
