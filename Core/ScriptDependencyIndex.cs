using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Berwick.Core;

/// <summary>
/// A source-observed command family. An opcode name is evidence of a call site,
/// not proof of its gameplay outcome or a safe encoding for every argument.
/// </summary>
public enum ScriptDependencyKind
{
    Flags, Quests, Classes, Skills, InventoryOperations, ItemReferences, Shops, Horses, Units
}

/// <summary>One argument of a native event command. Complex expressions are deliberately left unresolved.</summary>
public sealed record ScriptDependencyArgument(int Argument, int? Value, string SourceType,
    int? NumberIndex)
{
    public bool SourceNumber => Value.HasValue && NumberIndex.HasValue;
    public bool IsResolved => Value.HasValue;
}

/// <summary>One named native operation found in one event, with optional direct source literals.</summary>
public sealed record ScriptDependencySite(int Event, int Opcode, string Command,
    ScriptDependencyKind Kind, bool Conditional, IReadOnlyList<ScriptDependencyArgument> Arguments,
    ChangeEvidence Evidence)
{
    public bool HasComputedArguments => Arguments.Any(a => !a.IsResolved);
    public IReadOnlyList<int> EditableNumberIndices => Arguments
        .Where(a => a.SourceNumber)
        .Select(a => a.NumberIndex!.Value).Distinct().ToArray();
}

/// <summary>
/// Read-only index of significant script calls from the CURRENT OPENED DISC, including
/// project script overrides supplied by the caller. Never uses archived research as a
/// substitute for source verification. A missing record is not a proof of absence.
/// </summary>
public sealed class ScriptDependencyIndex
{
    private static readonly IReadOnlyDictionary<int, ScriptDependencyKind> Families =
        new Dictionary<int, ScriptDependencyKind>
        {
            // Event flags, including condition reads and range resets.
            [0x22] = ScriptDependencyKind.Flags,
            [0x77] = ScriptDependencyKind.Flags, [0x79] = ScriptDependencyKind.Flags,
            [0x7A] = ScriptDependencyKind.Flags,
            // Quest availability, dispatch, history and rewards. These are operations,
            // not authoritative quest-table relationships.
            [0x27] = ScriptDependencyKind.Quests, [0xAC] = ScriptDependencyKind.Quests,
            [0xDF] = ScriptDependencyKind.Quests, [0xE0] = ScriptDependencyKind.Quests,
            [0xE1] = ScriptDependencyKind.Quests, [0xE2] = ScriptDependencyKind.Quests,
            [0xE3] = ScriptDependencyKind.Quests, [0xE4] = ScriptDependencyKind.Quests,
            [0xE5] = ScriptDependencyKind.Quests, [0xE6] = ScriptDependencyKind.Quests,
            [0xE9] = ScriptDependencyKind.Quests,
            [0xEB] = ScriptDependencyKind.Quests, [0xEC] = ScriptDependencyKind.Quests,
            [0xED] = ScriptDependencyKind.Quests, [0xEE] = ScriptDependencyKind.Quests,
            [0xF3] = ScriptDependencyKind.Quests, [0xF6] = ScriptDependencyKind.Quests,
            [0xF7] = ScriptDependencyKind.Quests, [0xF8] = ScriptDependencyKind.Quests,
            [0xF9] = ScriptDependencyKind.Quests, [0xFC] = ScriptDependencyKind.Quests,
            [0xFD] = ScriptDependencyKind.Quests, [0xFE] = ScriptDependencyKind.Quests,
            [0xFF] = ScriptDependencyKind.Quests, [0x100] = ScriptDependencyKind.Quests,
            [0x101] = ScriptDependencyKind.Quests, [0x102] = ScriptDependencyKind.Quests,
            // Class changes and presentation. Not interchangeable with promotions.
            [0x1F] = ScriptDependencyKind.Classes,
            [0x14A] = ScriptDependencyKind.Classes, [0x1D4] = ScriptDependencyKind.Classes,
            [0x3C0] = ScriptDependencyKind.Classes, [0x3C2] = ScriptDependencyKind.Classes,
            // Skills and scripted reward operations.
            [0x16A] = ScriptDependencyKind.Skills, [0x16B] = ScriptDependencyKind.Skills,
            [0x3AB] = ScriptDependencyKind.Skills, [0x3AC] = ScriptDependencyKind.Skills,
            [0x3AD] = ScriptDependencyKind.Skills, [0x3AE] = ScriptDependencyKind.Skills,
            [0x1A8] = ScriptDependencyKind.InventoryOperations, [0x1AE] = ScriptDependencyKind.InventoryOperations,
            [0x1AF] = ScriptDependencyKind.InventoryOperations, [0x37C] = ScriptDependencyKind.InventoryOperations,
            [0x380] = ScriptDependencyKind.InventoryOperations, [0x381] = ScriptDependencyKind.InventoryOperations,
            // item2int is an item reference, NOT proof of a grant.
            [0x20] = ScriptDependencyKind.ItemReferences,
            [0x1E] = ScriptDependencyKind.Units,
            [0x49A] = ScriptDependencyKind.Shops, [0x49B] = ScriptDependencyKind.Shops,
            [0x49C] = ScriptDependencyKind.Shops, [0x49D] = ScriptDependencyKind.Shops,
            [0x4A1] = ScriptDependencyKind.Shops,
            [0x4A5] = ScriptDependencyKind.Horses, [0x4A6] = ScriptDependencyKind.Horses,
            [0x1B1] = ScriptDependencyKind.Horses, [0x350] = ScriptDependencyKind.Horses
        };

    private readonly IReadOnlyList<ScriptDependencySite> sites;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<ScriptDependencySite>> byEvent;
    public IReadOnlyList<ScriptDependencySite> Sites => sites;
    public IReadOnlyList<int> UnreadableEvents { get; }
    public int IndexedEvents { get; }
    /// <summary>Digest of every raw event in the audited source, including unreadable scripts.</summary>
    public string SourceSha256 { get; }
    public int SourceReferencedItemCount => ItemReferences.Count;

    /// <summary>Item IDs occurring as literal item2int(number) expressions, not automatically quest items.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> ItemReferences { get; }

    private ScriptDependencyIndex(IReadOnlyList<ScriptDependencySite> found, int indexed,
        IReadOnlyList<int> unreadable, string sourceSha256)
    {
        sites = found;
        IndexedEvents = indexed;
        SourceSha256 = sourceSha256;
        UnreadableEvents = unreadable;
        byEvent = sites.GroupBy(s => s.Event).ToDictionary(g => g.Key,
            g => (IReadOnlyList<ScriptDependencySite>)g.ToArray());
        ItemReferences = sites.Where(s => s.Opcode == 0x20)
            .SelectMany(s => s.Arguments.Where(a => a.Value is >= 0)
                .Select(a => (Item: a.Value!.Value, Event: s.Event)))
            .GroupBy(x => x.Item)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(x => x.Event).Distinct().Order().ToArray());
    }

    public IReadOnlyList<ScriptDependencySite> InEvent(int eventNumber) =>
        byEvent.TryGetValue(eventNumber, out var found) ? found : Array.Empty<ScriptDependencySite>();

    public IReadOnlyList<ScriptDependencySite> Filter(ScriptDependencyKind? kind, bool verifiedOnly = false) =>
        sites.Where(s => (kind is null || s.Kind == kind)
            && (!verifiedOnly || s.Evidence.Interpretation == EvidenceLevel.Verified)).ToArray();

    public static ScriptDependencyIndex Build(IEnumerable<int> events, Func<int, byte[]> script,
        IReadOnlyList<string>? nativeCommandNames = null)
    {
        var sites = new List<ScriptDependencySite>();
        var unreadable = new List<int>();
        int indexed = 0;
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sourceHash.AppendData(Encoding.ASCII.GetBytes("berwick-script-audit-v1"));
        static void AppendInt(IncrementalHash hash, int value)
        {
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(encoded, value);
            hash.AppendData(encoded);
        }
        foreach (int number in events.Distinct().Order())
        {
            AppendInt(sourceHash, number);
            byte[] bytes;
            try { bytes = script(number); }
            catch (Exception ex) when (ex is InvalidDataException or EditException)
            {
                sourceHash.AppendData(new byte[] { 0 });
                unreadable.Add(number);
                continue;
            }
            sourceHash.AppendData(new byte[] { 1 });
            AppendInt(sourceHash, bytes.Length);
            sourceHash.AppendData(bytes);
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(bytes); }
            catch (Exception ex) when (ex is InvalidDataException or EditException)
            { unreadable.Add(number); continue; }
            indexed++;
            // NumberIndex is exactly the index used by the existing Numbers editor.
            var offsets = new Dictionary<int, int>();
            int counter = 0;
            void ScanNumbers(IReadOnlyList<ScriptNode> block)
            {
                foreach (var node in block)
                {
                    if (node.Command == ScriptParser.Number) offsets[node.Offset] = counter++;
                    foreach (var arg in node.Args) ScanNumbers(arg);
                }
            }
            ScanNumbers(nodes);
            void ScanOperations(IReadOnlyList<ScriptNode> block, bool conditional)
            {
                foreach (var node in block)
                {
                    if (Families.TryGetValue(node.Command, out var kind))
                    {
                        var args = new List<ScriptDependencyArgument>();
                        for (int a = 0; a < node.Args.Count; a++)
                        {
                            ScriptNode? expr = node.Args[a].Count == 1 ? node.Args[a][0] : null;
                            if (expr is { Command: ScriptParser.Number, Value: int v })
                                args.Add(new(a + 1, v, "literal", offsets.GetValueOrDefault(expr.Offset)));
                            else if (expr is not null && (expr.Command is 0x1E or 0x1F or 0x20 or 0x22 or 0x27 or 0x29)
                                && expr.Args is [[{ Command: ScriptParser.Number, Value: int typed } inner]])
                                args.Add(new(a + 1, typed, ScriptText.Name(expr.Command, nativeCommandNames),
                                    offsets.GetValueOrDefault(inner.Offset)));
                            else args.Add(new(a + 1, null, "computed or complex", null));
                        }
                        bool hasSourceLiteral = args.Any(a => a.SourceNumber);
                        sites.Add(new(number, node.Command, ScriptText.Name(node.Command, nativeCommandNames),
                            kind, conditional, args.AsReadOnly(),
                            new(EvidenceLevel.SourceObserved,
                                hasSourceLiteral ? EvidenceBinding.ExperimentalBound : EvidenceBinding.Unbound,
                                EvidenceAcceptance.Untested,
                                hasSourceLiteral
                                    ? "Only explicitly identified source-number operands are structurally editable; computed operands and gameplay effects are unverified."
                                    : "No directly writable numeric operand is identified; this operation is read-only research.")));
                    }
                    bool nested = conditional || node.Command is ScriptParser.If or ScriptParser.While;
                    foreach (var arg in node.Args) ScanOperations(arg, nested);
                }
            }
            ScanOperations(nodes, false);
        }
        return new(sites.AsReadOnly(), indexed, unreadable.AsReadOnly(),
            Convert.ToHexString(sourceHash.GetHashAndReset()));
    }
}

/// <summary>
/// Read-only randomizer cross-reference audit. A scripted item reference is a warning,
/// not an automatic ban or a claim that the item is progression-critical.
/// </summary>
public sealed record RandomizerItemReference(int ItemId, IReadOnlyList<int> Events);
public static class RandomizerDependencyAudit
{
    public static IReadOnlyList<RandomizerItemReference> Inspect(IReadOnlyDictionary<int, IReadOnlyList<int>> sourceItems,
        IEnumerable<(int OldItem, int NewItem)> replacements)
    {
        var touched = replacements.SelectMany(p => new[] { p.OldItem, p.NewItem })
            .Where(id => id > 0).Distinct().Order();
        return touched.Where(sourceItems.ContainsKey).Select(id => new RandomizerItemReference(id, sourceItems[id])).ToArray();
    }
}
