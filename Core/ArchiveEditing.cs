using System.Buffers.Binary;

namespace Berwick.Core;

public static class ArchiveEditing
{
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
        return result;
    }
}
