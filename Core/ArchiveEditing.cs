using System.Buffers.Binary;

namespace Berwick.Core;

public static class ArchiveEditing
{
    // Archive layout on the disc: the 16-byte header, 12 bytes per file, then the files in table order, each starting at the end
    // of the one before plus 4 rounded down to a multiple of 4 (so 1 to 4 zero bytes follow each file; the first starts right
    // after the table), then the names at that same rounding after the last file. The archive ends with the names.
    private static int After(int end) => (end + 4) & ~3;

    /// <summary>
    /// Replace one child, moving the files after it when it no longer fits its space. The archive may become longer. Throws when
    /// the archive does not have the usual layout.
    /// </summary>
    public static byte[] Repack(ReadOnlySpan<byte> archive, string name, ReadOnlySpan<byte> decoded)
    {
        var entries = Tarc.Parse(archive);
        int index = entries.ToList().FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new EditException("The requested file was not found in the archive.");
        if (entries.Count(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) != 1) throw new EditException("The archive contains duplicate file names.");
        var stored = entries[index].Compressed ? BerwickLz.Compress(decoded) : decoded.ToArray();
        var result = Relayout(archive, entries, index, stored);
        var updated = Tarc.Parse(result);
        for (int i = 0; i < entries.Count; i++)
        {
            if (updated[i].Name != entries[i].Name || updated[i].Compressed != entries[i].Compressed) throw new InvalidDataException("The edited archive could not be verified.");
            var expected = i == index ? stored : archive.Slice(entries[i].Offset, entries[i].Size).ToArray();
            if (!result.AsSpan(updated[i].Offset, updated[i].Size).SequenceEqual(expected)) throw new InvalidDataException("The edited archive could not be verified.");
        }
        if (!Tarc.Extract(result, updated[index]).AsSpan().SequenceEqual(decoded)) throw new InvalidDataException("The edited archive could not be verified.");
        if (entries[index].Compressed)
        {
            var (game, read) = BerwickLz.DecompressToEndMarker(stored, decoded.Length);
            if (!game.AsSpan().SequenceEqual(decoded) || read != stored.Length - 8) throw new InvalidDataException("The edited archive could not be verified.");
        }
        return result;
    }

    /// <summary>The archive laid out again with one child's stored bytes replaced (the same bytes give back the same archive).</summary>
    public static byte[] Relayout(ReadOnlySpan<byte> archive, IReadOnlyList<ArchiveEntry> entries, int index, ReadOnlySpan<byte> stored)
    {
        int count = entries.Count, table = 16 + count * 12;
        int namesStart = BinaryPrimitives.ReadInt32LittleEndian(archive[8..]), namesEnd = namesStart;
        for (int i = 0; i < count; i++)
        {
            int end = namesEnd < archive.Length ? archive[namesEnd..].IndexOf((byte)0) : -1;
            if (end < 0) throw new EditException("The archive's names are incomplete.");
            namesEnd += end + 1;
        }
        // Only an archive whose files are in table order without overlapping, followed by the names, is rebuilt, so nothing the
        // game reads is lost. (A file rewritten in place can leave a larger gap after it; laying out again closes it.)
        bool usual = namesEnd == archive.Length && entries[0].Offset >= table && namesStart >= entries[^1].Offset + entries[^1].Size;
        for (int i = 1; i < count && usual; i++) usual = entries[i].Offset >= entries[i - 1].Offset + entries[i - 1].Size;
        if (!usual) throw new EditException("This archive is not laid out the usual way, so its files cannot be moved.");
        var parts = new List<(int Offset, byte[] Bytes)>();
        int at = table;
        for (int i = 0; i < count; i++)
        {
            var bytes = i == index ? stored.ToArray() : archive.Slice(entries[i].Offset, entries[i].Size).ToArray();
            if (i > 0) at = After(parts[^1].Offset + parts[^1].Bytes.Length);
            parts.Add((at, bytes));
        }
        int names = After(parts[^1].Offset + parts[^1].Bytes.Length);
        var result = new byte[names + (namesEnd - namesStart)];
        archive[..table].CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), names);
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16 + i * 12), (uint)parts[i].Offset << 1 | (entries[i].Compressed ? 1u : 0u));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20 + i * 12), parts[i].Bytes.Length);
            parts[i].Bytes.CopyTo(result, parts[i].Offset);
        }
        archive[namesStart..namesEnd].CopyTo(result.AsSpan(names));
        return result;
    }

    /// <summary>Replace one child in place when it fits its space, otherwise move the files after it (see <see cref="Repack"/>).</summary>
    public static byte[] ReplaceOrRepack(ReadOnlySpan<byte> archive, string name, ReadOnlySpan<byte> decoded)
    {
        try { return Replace(archive, name, decoded); }
        catch (EditException e) when (e.Message.Contains("does not fit")) { return Repack(archive, name, decoded); }
    }

    /// <summary>
    /// Disc patches for edited top-level archives. An archive that keeps its length is patched where it changed. A longer one
    /// grows into the zero bytes up to its next sector, or, when it needs more, moves to the unused end of DATA3.DAT; its index
    /// row is updated either way. Archives that hold tables read at fixed places (fixedTops) only grow in place.
    /// </summary>
    public static IReadOnlyList<DiscPatch> Patches(GameArchives archives, IEnumerable<(int Top, Func<byte[], byte[]> Edit)> edits, IReadOnlySet<int> fixedTops)
    {
        var patches = new List<DiscPatch>();
        uint free = archives.FreeSector();
        foreach (var group in edits.GroupBy(x => x.Top).OrderBy(g => g.Key))
        {
            var original = archives.Top(group.Key);
            var updated = original;
            foreach (var (_, edit) in group) updated = edit(updated);
            var (sector, size) = archives.Row(group.Key);
            long start = archives.TopOffset(group.Key);
            long capacity = (size + (long)GameArchives.Sector - 1) / GameArchives.Sector * GameArchives.Sector;
            if (updated.Length > capacity)
            {
                if (fixedTops.Contains(group.Key))
                    throw new EditException("An edited event is also stored with the game's base data, where there is no room for it to grow. Choose a value that keeps the script nearer its original size.");
                long need = (updated.Length + (long)GameArchives.Sector - 1) / GameArchives.Sector * GameArchives.Sector;
                if ((free * (long)GameArchives.Sector) + need > archives.Data3Length) throw new EditException("There is no unused space left on the disc for the edited events.");
                // The unused end of DATA3.DAT holds one repeated filler byte (zero on the English disc, 0x80 on the Japanese one).
                var target = archives.ReadData3(free * (long)GameArchives.Sector, updated.Length);
                if (target.Any(b => b != target[0])) throw new EditException("The space after the game's archives is not empty on this disc, so an archive cannot be moved there.");
                patches.Add(new(archives.Data3Offset + free * (long)GameArchives.Sector, updated, target));
                patches.Add(new(archives.RowOffset(group.Key) + 4, [.. BitConverter.GetBytes(free), .. BitConverter.GetBytes((uint)updated.Length)], [.. BitConverter.GetBytes(sector), .. BitConverter.GetBytes(size)]));
                free += (uint)(need / GameArchives.Sector);
                continue;
            }
            // Past the old end the disc holds padding up to the next sector.
            var before = archives.ReadData3(sector * (long)GameArchives.Sector, Math.Max(original.Length, updated.Length));
            if (!before.AsSpan(0, original.Length).SequenceEqual(original)) throw new InvalidDataException("An archive could not be read back from the disc.");
            for (int i = 0; i < updated.Length; i++)
            {
                if (updated[i] == before[i]) continue;
                int first = i;
                while (i < updated.Length && updated[i] != before[i]) i++;
                patches.Add(new(start + first, updated.AsSpan(first, i - first).ToArray(), before.AsSpan(first, i - first).ToArray()));
            }
            if (updated.Length < original.Length) throw new InvalidDataException("An archive became shorter.");
            if (updated.Length != original.Length)
                patches.Add(new(archives.RowOffset(group.Key) + 8, BitConverter.GetBytes((uint)updated.Length), BitConverter.GetBytes(size)));
        }
        return patches;
    }

    /// <summary>Top-level archives that hold the tables read at fixed places in DATA3.DAT (items, units, classes and so on).</summary>
    public static IReadOnlySet<int> FixedTops(GameArchives archives, GameProfile? profile)
    {
        var tops = new HashSet<int>();
        if (profile is null) return tops;
        foreach (long at in new[] { profile.ItemTable, profile.SecondItemTable, profile.UnitTable, profile.SecondUnitTable, profile.GrowthTable, profile.SecondGrowthTable,
                     profile.ClassTable, profile.SecondClassTable, profile.TerrainTable, profile.SecondTerrainTable, profile.PartnerTable, profile.SecondPartnerTable })
            if (at > 0 && archives.TopAt(at) is int top and >= 0) tops.Add(top);
        return tops;
    }
    /// <summary>Replace one child without moving any file or increasing its recorded storage.</summary>
    public static byte[] Replace(ReadOnlySpan<byte> archive, string name, ReadOnlySpan<byte> decoded)
    {
        var entries = Tarc.Parse(archive);
        if (entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new EditException("The archive contains duplicate file names.");
        int namesStart = BinaryPrimitives.ReadInt32LittleEndian(archive[8..]), namesEnd = namesStart;
        for (int i = 0; i < entries.Count; i++)
        {
            if (namesEnd >= archive.Length) throw new EditException("The archive's names are incomplete.");
            int end = archive[namesEnd..].IndexOf((byte)0);
            if (end < 0) throw new EditException("The archive's names are incomplete.");
            namesEnd += end + 1;
        }
        // File data may sit before or after the name list; it must not overlap the header, the entry table, the names or other files.
        var ranges = new List<(int Start, int End)> { (0, 16 + entries.Count * 12), (namesStart, namesEnd) };
        ranges.AddRange(entries.Where(e => e.Size > 0).Select(e => (e.Offset, checked(e.Offset + e.Size))));
        ranges.Sort();
        for (int i = 1; i < ranges.Count; i++)
            if (ranges[i].Start < ranges[i - 1].End) throw new EditException("The archive contains overlapping file data.");
        int index = entries.ToList().FindIndex(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new EditException("The requested file was not found in the archive.");
        var entry = entries[index];
        var original = Tarc.Extract(archive, entry);
        if (decoded.SequenceEqual(original)) return archive.ToArray();
        var stored = entry.Compressed ? BerwickLz.Compress(decoded) : decoded.ToArray();
        if (stored.Length > entry.Size)
            throw new EditException("The edited script does not fit its existing archive space. No files were changed.");
        var result = archive.ToArray();
        stored.CopyTo(result.AsSpan(entry.Offset));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20 + index * 12), stored.Length);
        var updated = Tarc.Parse(result)[index];
        if (!Tarc.Extract(result, updated).AsSpan().SequenceEqual(decoded))
            throw new InvalidDataException("The edited archive could not be verified.");
        if (entry.Compressed)
        {
            // Check it the way the game reads it too: everything up to the end marker, nothing after it.
            var (game, read) = BerwickLz.DecompressToEndMarker(stored, decoded.Length);
            if (!game.AsSpan().SequenceEqual(decoded) || read != stored.Length - 8)
                throw new InvalidDataException("The edited archive could not be verified.");
        }
        return result;
    }
}
