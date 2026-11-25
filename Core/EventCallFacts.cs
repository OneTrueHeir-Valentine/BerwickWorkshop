using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Berwick.Core;

public sealed record ComputedEventCall(int Event, int Offset, IReadOnlyList<int> Targets);

public sealed record EventCallFacts(string GraphSha256, IReadOnlyList<ComputedEventCall> Calls)
{
    public static EventCallFacts? ForOriginal(InspectionResult disc)
    {
        var profile = Profiles.Match(disc.Sha256);
        if (profile is null || disc.IsModified || disc.ExecutableSha256 != profile.ExecutableSha256)
            return null;
        string? graph = disc.Sha256 switch
        {
            "77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320" =>
                "65862EE30D85C2B89B2E106096D0EB5835FBADA497A870607E2223C15A19615B",
            "d7355857377114af3c7734f43250eea2a01cef755531aff04df0a4ccff215111" =>
                "6D5ECF6F077D1D77D2ED008A169072A34A133A171C265C73E015E8911655DE8D",
            _ => null
        };
        if (graph is null)
            return null;
        // Original mercenary hashes resolve to 142..150; guarded recruitment callers supply 768 or 963.
        int[] checks = Enumerable.Range(142, 9).ToArray();
        return new(graph, [new(133, 0x2B0, checks), new(133, 0x450, [768, 963]), new(151, 0x328, checks)]);
    }

    public static string Fingerprint(IReadOnlyList<PlacementEventSource> sources)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[16];
        foreach (var source in sources.OrderBy(source => source.Number))
        {
            var bytes = ScriptWriter.Write(source.Nodes);
            BinaryPrimitives.WriteInt32LittleEndian(header, source.Number);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), source.Archive);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), source.EncodedBytes?.Length ?? -1);
            hash.AppendData(header);
            hash.AppendData(bytes);
            // Retain header fields that the parsed view does not interpret.
            if (source.EncodedBytes is { } encoded)
                hash.AppendData(encoded.Span);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public IReadOnlyDictionary<(int Event, int Offset), IReadOnlyList<int>> Verify(IReadOnlyList<PlacementEventSource> sources)
    {
        if (Calls.GroupBy(call => (call.Event, call.Offset)).Any(group => group.Count() != 1) ||
            Calls.Any(call => call.Targets.Count == 0) || Fingerprint(sources) != GraphSha256)
            return new Dictionary<(int, int), IReadOnlyList<int>>();
        return Calls.ToDictionary(call => (call.Event, call.Offset), call => call.Targets);
    }
}
