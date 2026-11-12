using System.Buffers.Binary;
using System.Text;

namespace Berwick.Core;

public sealed record IsoEntry(string Name, long Offset, long Length);

public sealed class IsoImage : IDisposable
{
    private readonly Stream stream;
    public IReadOnlyList<IsoEntry> Entries { get; }

    public IsoImage(Stream stream)
    {
        this.stream = stream;
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 17 * 2048)
            throw new InvalidDataException("This file is not a complete ISO image.");
        var header = ReadRange(16 * 2048, 2048, 2048);
        if (header[0] != 1 || header[6] != 1 || Encoding.ASCII.GetString(header, 1, 5) != "CD001")
            throw new InvalidDataException("Open an uncompressed ISO image of Berwick Saga.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(128)) != 2048 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(130)) != 2048)
            throw new InvalidDataException("This disc uses an unsupported sector format.");
        long sectors = Number(header, 80);
        if (sectors < 17 || sectors * 2048 > stream.Length)
            throw new InvalidDataException("The ISO image is incomplete.");
        if (header[156] < 34 || (header[181] & 2) == 0)
            throw new InvalidDataException("The disc directory is invalid.");
        var directory = ReadRange(Number(header, 158) * 2048, Number(header, 166), 8 * 1024 * 1024);
        var files = new List<IsoEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < directory.Length;)
        {
            int length = directory[p];
            if (length == 0) { p = (p / 2048 + 1) * 2048; continue; }
            if (length < 34 || p + length > directory.Length || p % 2048 + length > 2048)
                throw new InvalidDataException("The disc directory contains an invalid entry.");
            var r = directory.AsSpan(p, length);
            int nameLength = r[32];
            if (nameLength == 0 || 33 + nameLength > length || r[1] != 0 || (r[25] & 0x80) != 0 || r[26] != 0 || r[27] != 0)
                throw new InvalidDataException("This disc directory format is not supported.");
            long offset = Number(r, 2) * 2048, size = Number(r, 10);
            CheckRange(offset, size);
            if ((r[25] & 2) == 0)
            {
                string name = Encoding.ASCII.GetString(r.Slice(33, nameLength)).Split(';')[0];
                if (!names.Add(name)) throw new InvalidDataException("The disc contains duplicate file entries.");
                files.Add(new(name, offset, size));
            }
            p += length;
        }
        Entries = files.AsReadOnly();
    }

    private static long Number(ReadOnlySpan<byte> bytes, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
        if (value != BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]))
            throw new InvalidDataException("The disc directory contains conflicting sizes.");
        return value;
    }
    private void CheckRange(long offset, long size)
    {
        if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
            throw new InvalidDataException("A disc file extends beyond the end of the image.");
    }
    public byte[] ReadRange(long offset, long size, int limit)
    {
        CheckRange(offset, size);
        if (size > limit || size > int.MaxValue) throw new InvalidDataException("A disc file is larger than expected.");
        var bytes = new byte[(int)size];
        stream.Position = offset;
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException) { throw new InvalidDataException("The ISO image is incomplete."); }
        return bytes;
    }
    public byte[] ReadFile(string name, int limit)
    {
        var entry = Entries.FirstOrDefault(e => e.Name.Equals(name.Split(';')[0], StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidDataException($"The disc does not contain {name}.");
        return ReadRange(entry.Offset, entry.Length, limit);
    }
    public void Dispose() => stream.Dispose();
}
