using Berwick.Core;

internal static class EventCatalogueTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Event catalogue preserves typed auxiliary entries and all battle associations", () =>
        {
            var (data, types) = Fixture();
            var catalogue = EventCatalogue.Parse(data, types, new HashSet<int> { 1, 12, 13 });
            Check(catalogue.Entries.Count == 5 && catalogue.EmptySlots == 4091, "Populated or empty slots were lost");
            Check(catalogue.Entries[0] is { Slot: 0, Kind: 1, Offset: 0 } && catalogue.Entries[0].Words.SequenceEqual(new[] { 1, 1 }), "Initial auxiliary record was skipped or misread");
            Check(catalogue.Entries[1].Kind == 1, "Odd-slot type nibble was read incorrectly");
            Check(catalogue.BattleEvents.Select(entry => entry.Event).SequenceEqual(new[] { 12, 13 }), "Battle events were lost or auxiliary entries became battles");
            Check(catalogue.BattleEvents[0].Battle == 26 && catalogue.BattleEvents[0].Type == 13 && catalogue.BattleEvents[0].Args.SequenceEqual(new[] { 1 }), "Battle condition layout changed");
            var references = BattlePlacementReferences.Analyze(
                [new(12, 0, [UnitPlacementTests.Launch(26, "first.ar"), UnitPlacementTests.Appear(5, 2)]), new(13, 1, [UnitPlacementTests.Launch(27, "second.ar"), UnitPlacementTests.Appear(5, 2)])],
                catalogue.BattleEvents, file => file == "first.ar" ? 0 : 1);
            Check(references.Problems.Count == 0 && references.ArmyBattles[5].SetEquals([26, 27]), "Complete-catalogue associations were not preserved");
        });
        test("Event catalogue requires all 4096 slots and exact EOF", () =>
        {
            var (data, types) = Fixture();
            Reject(data[..^4], types);
            Reject(data[..^1], types);
            Reject([.. data, 0, 0, 0, 0], types);
            Reject([], types);
        });
        test("Event catalogue refuses missing or unknown packed types", () =>
        {
            var (data, types) = Fixture();
            Reject(data, types[..^1]);
            Reject(data, [.. types, 0]);
            types[0] = 0x21;
            Reject(data, types);
            types[0] = 0x12;
            Reject(data, types);
            types[0] = 0x31;
            Reject(data, types);
            types[0] = 0x11;
            types[1] = 0x20;
            Reject(data, types);
        });
        test("Event catalogue refuses invalid lengths without scanning for another record", () =>
        {
            var (data, types) = Fixture();
            foreach (int size in new[] { -4, 4, 9, int.MaxValue - 3 })
            {
                var malformed = (byte[])data.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(malformed, size);
                Reject(malformed, types);
            }
        });
        test("Event catalogue refuses inconsistent counts in either record kind", () =>
        {
            var (data, types) = Fixture();
            var catalogue = EventCatalogue.Parse(data, types, new HashSet<int> { 1, 12, 13 });
            foreach (var entry in catalogue.Entries.Where(entry => entry.Slot is 0 or 480))
            {
                var malformed = (byte[])data.Clone();
                int countOffset = entry.Offset + (entry.Kind == 0 ? 8 : 4);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(countOffset), 999);
                Reject(malformed, types);
            }
        });
        test("Event catalogue refuses unresolved targets while retaining data-only entries", () =>
        {
            var (data, types) = Fixture();
            Reject(data, types, new HashSet<int> { 12, 13 });
            Reject(data, types, new HashSet<int> { 1, 12 });
            var catalogue = EventCatalogue.Parse(data, types, new HashSet<int> { 1, 12, 13 });
            Check(catalogue.Entries.Single(entry => entry.Slot == 3905).Words[0] == 0, "Null-target data entry disappeared");
        });
    }

    private static (byte[] Data, byte[] Types) Fixture()
    {
        var entries = new Dictionary<int, (int Kind, int[] Words)>
        {
            [0] = (1, [1, 1]),
            [1] = (1, [3, 1, 2, 111]),
            [480] = (0, [(12 << 16) | 26, 2, 13, 1]),
            [481] = (0, [(13 << 16) | 27, 2, 13, 1]),
            [3905] = (0, [0, 3, 20, 126, 1024])
        };
        var types = Enumerable.Repeat((byte)0x22, EventCatalogue.TypeBytes).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int slot = 0; slot < EventCatalogue.Slots; slot++)
        {
            if (!entries.TryGetValue(slot, out var entry))
            {
                writer.Write(0);
                continue;
            }
            int shift = 4 * (slot % 2);
            types[slot / 2] = (byte)((types[slot / 2] & ~(15 << shift)) | entry.Kind << shift);
            writer.Write(entry.Words.Length * 4);
            foreach (int word in entry.Words)
                writer.Write(word);
        }
        return (stream.ToArray(), types);
    }

    private static void Reject(byte[] data, byte[] types, IReadOnlySet<int>? scripts = null)
    {
        try { EventCatalogue.Parse(data, types, scripts ?? new HashSet<int> { 1, 12, 13 }); }
        catch (EditException) { return; }
        throw new Exception("Malformed or unresolved catalogue was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
