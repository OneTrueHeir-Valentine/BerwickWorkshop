using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace Berwick.Core;

/// <summary>Stable, documented independent keyed streams. NOT System.Random; version 1 never changes its output.</summary>
public static class R0SeedStreams
{
    public const string Algorithm = "fnv1a64-splitmix64-v1";
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong Byte(ulong hash, byte value) => unchecked((hash ^ value) * FnvPrime);
    private static ulong Text(ulong hash, string s)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(s)) hash = Byte(hash, b);
        return Byte(hash, 0); // explicit domain separator
    }
    private static ulong Number(ulong hash, ulong number)
    {
        for (int i = 0; i < 8; i++) { hash = Byte(hash, (byte)(number & 255)); number >>= 8; }
        return hash;
    }
    public static ulong Word(int seed, string module, string table, int entityId, int slot)
    {
        ulong h = FnvOffset;
        h = Number(h, unchecked((ulong)(uint)seed));
        h = Text(h, module);
        h = Text(h, table);
        h = Number(h, unchecked((ulong)(uint)entityId));
        h = Number(h, unchecked((ulong)(uint)slot));
        h = unchecked(h + 0x9E3779B97F4A7C15UL);
        h = unchecked((h ^ (h >> 30)) * 0xBF58476D1CE4E5B9UL);
        h = unchecked((h ^ (h >> 27)) * 0x94D049BB133111EBUL);
        return h ^ (h >> 31);
    }
    /// <summary>Stable deterministic pool selection, with negligible 64-bit modulo bias; not cryptographic.</summary>
    public static int Choice(int seed, string module, string table, int entityId, int slot, int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        // The field's draw is independent of how many other records or modules are enabled.
        // The tiny modulo bias of a 64-bit draw is immaterial for realistic item pools; not used for cryptography.
        return (int)(Word(seed, module, table, entityId, slot) % (ulong)count);
    }
}

public sealed record R0StartingItemChange(string Table, int UnitId, int Slot, int FromItem, int ToItem,
    int FromUses, int ToUses);
public sealed record R0ExpectedValue(string Table, int RecordId, string Field, int Value);

/// <summary>A no-write preview. Its serialized values remain review evidence, not a gameplay-acceptance claim.</summary>
public sealed record R0StartingItemPreview(
    int SchemaVersion, string Algorithm, string SourceSha256, string ProjectStateSha256,
    string ScriptAuditSha256, int ChangeCountAtGeneration, RandomizeOptions Options, IReadOnlyList<FieldEdit> FieldEdits,
    IReadOnlyList<R0StartingItemChange> Replacements,
    IReadOnlyList<R0ExpectedValue> ExpectedValues,
    IReadOnlyList<RandomizerItemReference> ScriptReferences,
    bool ScriptAuditComplete, IReadOnlyList<int> UnreadableEvents)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// R0 CORE prototype: source-bound seeded immutable preview and guarded single-step application
/// for the EXISTING starting-item module. No ISO export, no new category or UI switch here.
/// A script reference is a warning, NOT proof of a softlock or an automatic global exclusion.
/// </summary>
public static class RandomizerR0
{
    private const int Arrow = 21, Bolt = 22, Accessory = 23, Consumable = 24;
    private sealed record IssuedPreview(R0StartingItemPreview Value, ScriptDependencyIndex? PreviewAudit);
    private static readonly ConditionalWeakTable<EditSession, IssuedPreview> LastPreview = new();

    private static string ProjectFingerprint(EditSession session) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.ToJson())));

    private static bool SameReferences(IReadOnlyList<RandomizerItemReference> a,
        IReadOnlyList<RandomizerItemReference> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.ItemId == pair.Second.ItemId
            && pair.First.Events.SequenceEqual(pair.Second.Events));

    private static bool SamePreview(R0StartingItemPreview expected, R0StartingItemPreview found) =>
        expected.SchemaVersion == found.SchemaVersion && expected.Algorithm == found.Algorithm
        && string.Equals(expected.SourceSha256, found.SourceSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ProjectStateSha256, found.ProjectStateSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ScriptAuditSha256, found.ScriptAuditSha256, StringComparison.OrdinalIgnoreCase)
        && expected.ChangeCountAtGeneration == found.ChangeCountAtGeneration
        && expected.Options == found.Options
        && expected.FieldEdits.SequenceEqual(found.FieldEdits)
        && expected.Replacements.SequenceEqual(found.Replacements)
        && expected.ExpectedValues.SequenceEqual(found.ExpectedValues)
        && SameReferences(expected.ScriptReferences, found.ScriptReferences)
        && expected.ScriptAuditComplete == found.ScriptAuditComplete
        && expected.UnreadableEvents.SequenceEqual(found.UnreadableEvents);

    private static bool InScope(RandomizeOptions o, ItemRecord item) => item.Type switch
    {
        Arrow or Bolt => o.Ammunition,
        Accessory or Consumable => o.Other,
        < Arrow => o.Weapons,
        _ => false,
    };

    public static R0StartingItemPreview PreviewStartingItems(EditSession session, RandomizeOptions options,
        ScriptDependencyIndex? scriptIndex = null) => BuildPreview(session, options, scriptIndex, register: true);

    private static R0StartingItemPreview BuildPreview(EditSession session, RandomizeOptions options,
        ScriptDependencyIndex? scriptIndex, bool register)
    {
        if (options.Seed < 0) throw new EditException("R0 seed must be nonnegative.");
        var pool = Randomizer.Pool(session).OrderBy(x => x.Id).ToArray();
        var ids = pool.Select(x => x.Id).ToHashSet();
        var allItems = session.EditedItems().ToDictionary(x => x.Id);
        var edits = new List<FieldEdit>();
        var changes = new List<R0StartingItemChange>();
        var expected = new List<R0ExpectedValue>();
        void Pick(string table, int unitId, int slot, string itemKey, string usesKey)
        {
            int from = session.Value(table, unitId, itemKey), uses = session.Value(table, unitId, usesKey);
            if (from <= 0 || !allItems.TryGetValue(from, out var original) || !ids.Contains(from)
                || !InScope(options, original)) return;
            var optionsForType = pool.Where(x => x.Type == original.Type
                && (!options.LimitLevel || x.Level <= original.Level)).ToArray();
            if (optionsForType.Length == 0) return;
            var to = optionsForType[R0SeedStreams.Choice(options.Seed, "starting-items", table, unitId, slot, optionsForType.Length)];
            int toUses = uses == original.Uses ? to.Uses : Math.Min(uses, to.Uses);
            if (from == to.Id && uses == toUses) return;
            expected.Add(new(table, unitId, itemKey, from));
            expected.Add(new(table, unitId, usesKey, uses));
            edits.Add(new(table, unitId, itemKey, to.Id));
            edits.Add(new(table, unitId, usesKey, toUses));
            changes.Add(new(table, unitId, slot, from, to.Id, uses, toUses));
        }
        foreach (var unit in session.Disc.Characters.OrderBy(x => x.Id))
        {
            for (int slot = 0; slot < CharacterFields.ItemSlots; slot++)
                Pick(EditSession.Characters, unit.Id, slot, $"item{slot + 1}", $"item{slot + 1}Uses");
            if (!options.Bags || !session.Disc.BagBytes.ContainsKey(unit.Id)) continue;
            int bagCount = session.Value(EditSession.Bags, unit.Id, "bagCount");
            for (int slot = 0; slot < bagCount; slot++)
                Pick(EditSession.Bags, unit.Id, slot, BagData.ItemKey(slot), BagData.UsesKey(slot));
        }
        bool complete = scriptIndex is not null && scriptIndex.IndexedEvents > 0
            && scriptIndex.UnreadableEvents.Count == 0;
        var risk = scriptIndex is null ? Array.Empty<RandomizerItemReference>()
            : Randomizer.ScriptImpact(session, edits, scriptIndex).ToArray();
        var preview = new R0StartingItemPreview(2, R0SeedStreams.Algorithm, session.Disc.Sha256,
            ProjectFingerprint(session), scriptIndex?.SourceSha256 ?? "", session.ChangeCount, options,
            edits.AsReadOnly(), changes.AsReadOnly(), expected.AsReadOnly(), risk,
            complete, scriptIndex?.UnreadableEvents.ToArray() ?? Array.Empty<int>());
        if (register)
        {
            LastPreview.Remove(session);
            LastPreview.Add(session, new IssuedPreview(preview, scriptIndex));
        }
        return preview;
    }

    /// <summary>Only the most recently generated preview for this session can be applied.</summary>
    public static bool Apply(EditSession session, R0StartingItemPreview preview,
        ScriptDependencyIndex freshScriptIndex,
        bool permitScriptReferences = false, bool permitIncompleteScriptAudit = false)
    {
        if (preview is null || preview.SchemaVersion != 2 || preview.Algorithm != R0SeedStreams.Algorithm)
            throw new EditException("Unknown R0 preview format or seed algorithm. Regenerate the preview.");
        if (!LastPreview.TryGetValue(session, out var issued) || !ReferenceEquals(issued.Value, preview))
            throw new EditException("This preview was not issued for the current session or has been superseded. Regenerate it.");
        if (freshScriptIndex is null || freshScriptIndex.IndexedEvents == 0
            || string.IsNullOrWhiteSpace(freshScriptIndex.SourceSha256)
            || string.IsNullOrWhiteSpace(preview.ScriptAuditSha256))
            throw new EditException("A new source-script audit is required before applying this preview.");
        if (ReferenceEquals(freshScriptIndex, issued.PreviewAudit))
            throw new EditException("The preview audit was reused instead of rebuilt. Regenerate the preview and audit.");
        if (!string.Equals(session.Disc.Sha256, preview.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || session.ChangeCount != preview.ChangeCountAtGeneration
            || !string.Equals(ProjectFingerprint(session), preview.ProjectStateSha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException("The source disc or project edits changed. Regenerate the preview.");
        if (!string.Equals(freshScriptIndex.SourceSha256, preview.ScriptAuditSha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException("The source-script audit changed since preview generation. Regenerate the preview.");
        var replay = BuildPreview(session, preview.Options, freshScriptIndex, register: false);
        if (!SamePreview(preview, replay))
            throw new EditException("The preview differs from the deterministic replay. Regenerate it.");
        if (!replay.ScriptAuditComplete && !permitIncompleteScriptAudit)
            throw new EditException("The source-script audit is incomplete; explicit approval required.");
        if (replay.ScriptReferences.Count > 0 && !permitScriptReferences)
            throw new EditException("Script-referenced item IDs require explicit approval before applying.");
        LastPreview.Remove(session);
        return session.Apply(replay.FieldEdits); // Use only recomputed edits in one undo step.
    }
}
