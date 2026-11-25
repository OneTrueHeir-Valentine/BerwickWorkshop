using System.Buffers.Binary;

namespace Berwick.Core;

public sealed record EventCatalogueEntry(int Slot, int Kind, int Offset, IReadOnlyList<int> Words);

public sealed class EventCatalogue
{
    public const int Slots = 4096;
    public const int TypeBytes = Slots / 2;
    public IReadOnlyList<EventCatalogueEntry> Entries { get; }
    public IReadOnlyList<MapEventRecord> BattleEvents { get; }
    public int EmptySlots => Slots - Entries.Count;

    private EventCatalogue(List<EventCatalogueEntry> entries, List<MapEventRecord> battleEvents)
    {
        Entries = entries;
        BattleEvents = battleEvents;
    }

    public static EventCatalogue Parse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> types, IReadOnlySet<int> scripts)
    {
        if (types.Length != TypeBytes)
            throw new EditException("The event type table must contain 4096 packed types.");
        var entries = new List<EventCatalogueEntry>();
        var battleEvents = new List<MapEventRecord>();
        int offset = 0;
        for (int slot = 0; slot < Slots; slot++)
        {
            int kind = (types[slot / 2] >> (4 * (slot % 2))) & 15;
            if (kind is not (0 or 1 or 2) || offset > data.Length - 4)
                throw Invalid(slot, "unknown type or missing length");
            int start = offset;
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            offset += 4;
            if (size == 0)
            {
                if (kind != 2)
                    throw Invalid(slot, "a populated type has no payload");
                continue;
            }
            if (kind == 2)
                throw Invalid(slot, "an empty type has a payload");
            int minimum = kind == 0 ? 12 : 8;
            if (size < minimum || size % 4 != 0 || (long)offset + size > data.Length)
                throw Invalid(slot, "invalid payload length");
            var words = new int[size / 4];
            for (int i = 0; i < words.Length; i++)
                words[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + i * 4)..]);
            int countIndex = kind == 0 ? 1 : 0;
            if (words[countIndex] != words.Length - countIndex - 1)
                throw Invalid(slot, "payload word count does not match its length");
            int script = kind == 0 ? (int)((uint)words[0] >> 16) : words[1];
            if (script != 0 && !scripts.Contains(script))
                throw Invalid(slot, "unresolved script target");
            entries.Add(new(slot, kind, start, words));
            // Kind 1 has a separate non-battle layout; a zero target is a data-only entry.
            if (kind == 0 && script != 0)
                battleEvents.Add(new(script, words[0] & 65535, words[2], words[3..]));
            offset += size;
        }
        if (offset != data.Length)
            throw new EditException("The event catalogue has data after its 4096 slots.");
        return new(entries, battleEvents);
    }

    private static EditException Invalid(int slot, string reason) =>
        new($"Event catalogue slot {slot} is unsupported: {reason}.");
}
