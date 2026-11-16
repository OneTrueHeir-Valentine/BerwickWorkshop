using System.Buffers.Binary;
using System.Text;

namespace Berwick.Core;

/// <summary>One file inside a TARC archive. Offset is from the start of the archive.</summary>
public sealed record ArchiveEntry(string Name, int Offset, int Size, bool Compressed);

/// <summary>The game's archive format: "TARC", file count, name list offset, then 12 bytes per file
/// (offset times two plus a compression flag, stored size, one more word), then the NUL-terminated names.</summary>
public static class Tarc
{
    private static readonly Encoding ShiftJis = CreateShiftJis();

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public static bool IsArchive(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[0] == (byte)'T' && data[1] == (byte)'A' && data[2] == (byte)'R' && data[3] == (byte)'C';

    public static IReadOnlyList<ArchiveEntry> Parse(ReadOnlySpan<byte> data)
    {
        if (!IsArchive(data)) throw new InvalidDataException("This is not a game archive.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int names = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (count <= 0 || count > 100000 || 16L + count * 12L > data.Length || names < 16 + count * 12 || names >= data.Length)
            throw new InvalidDataException("A game archive has an invalid file table.");
        var result = new List<ArchiveEntry>(count);
        int p = names;
        for (int i = 0; i < count; i++)
        {
            int end = data[p..].IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException("A game archive has an unterminated file name.");
            string name = ShiftJis.GetString(data.Slice(p, end));
            p += end + 1;
            uint encoded = BinaryPrimitives.ReadUInt32LittleEndian(data[(16 + i * 12)..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(20 + i * 12)..]);
            int offset = (int)(encoded >> 1);
            if (offset > data.Length || size < 0 || size > data.Length - offset)
                throw new InvalidDataException("A file in a game archive extends beyond the archive.");
            result.Add(new ArchiveEntry(name, offset, size, (encoded & 1) != 0));
        }
        return result;
    }

    public static byte[] Extract(ReadOnlySpan<byte> archive, ArchiveEntry entry)
    {
        var stored = archive.Slice(entry.Offset, entry.Size);
        return entry.Compressed ? BerwickLz.Decompress(stored) : stored.ToArray();
    }
}

/// <summary>The game's compression: output size, stored size, then control bits choosing literals, short copies
/// (distance up to 256, length 2 to 5) and long copies (13-bit distance, length 3 to 9 or a length byte).</summary>
public static partial class BerwickLz
{
    /// <summary>Decode the way the game does: no output count, stop at a long copy whose length byte is 0.
    /// Returns the output and how many payload bytes were read.</summary>
    public static (byte[] Output, int Read) DecompressToEndMarker(ReadOnlySpan<byte> data, int maxOutput)
    {
        if (data.Length < 8) throw new InvalidDataException("A compressed file is too short.");
        var buf = data[8..];
        var output = new List<byte>();
        int p = 0, flags = 0, left = 0;
        int Bit(ReadOnlySpan<byte> b)
        {
            if (left == 0)
            {
                if (p >= b.Length) throw new InvalidDataException("A compressed file has no end marker.");
                flags = b[p++]; left = 8;
            }
            left--;
            return (flags >> left) & 1;
        }
        while (true)
        {
            if (output.Count > maxOutput) throw new InvalidDataException("A compressed file has no end marker.");
            if (Bit(buf) == 1)
            {
                if (p >= buf.Length) throw new InvalidDataException("A compressed file has no end marker.");
                output.Add(buf[p++]); continue;
            }
            int length, distance;
            if (Bit(buf) == 1)
            {
                if (p + 1 >= buf.Length) throw new InvalidDataException("A compressed file has no end marker.");
                int t = (buf[p] << 8) | buf[p + 1]; p += 2;
                distance = 8192 - (t >> 3);
                if ((t & 7) != 0) length = (t & 7) + 2;
                else
                {
                    if (p >= buf.Length) throw new InvalidDataException("A compressed file has no end marker.");
                    int n = buf[p++];
                    if (n == 0) return (output.ToArray(), p);
                    length = n + 1;
                }
            }
            else
            {
                length = Bit(buf) * 2 + Bit(buf) + 2;
                if (p >= buf.Length) throw new InvalidDataException("A compressed file has no end marker.");
                distance = 256 - buf[p++];
            }
            for (int i = 0; i < length; i++)
            {
                int src = output.Count - distance;
                if (src < 0) throw new InvalidDataException("A compressed file is damaged.");
                output.Add(output[src]);
            }
        }
    }

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9) throw new InvalidDataException("A compressed file is too short.");
        int outSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (outSize <= 0 || outSize > 256 * 1024 * 1024) throw new InvalidDataException("A compressed file has an invalid size.");
        var buf = data[8..];
        var output = new byte[outSize];
        int control = 0, pos = 1, flag = 8, o = 0;
        bool Bit(ReadOnlySpan<byte> b)
        {
            if (flag == 0)
            {
                if (pos >= b.Length) throw new InvalidDataException("A compressed file ends early.");
                control = pos++; flag = 8;
            }
            flag--;
            return (b[control] & (1 << flag)) != 0;
        }
        while (o < outSize)
        {
            if (Bit(buf))
            {
                if (pos >= buf.Length) throw new InvalidDataException("A compressed file ends early.");
                output[o++] = buf[pos++];
                continue;
            }
            int length, distance;
            if (Bit(buf))
            {
                if (pos + 1 >= buf.Length) throw new InvalidDataException("A compressed file ends early.");
                int t = (buf[pos] << 8) | buf[pos + 1]; pos += 2;
                length = t & 7;
                distance = (t >> 3) | -0x2000;
                if (length != 0) length += 2;
                else
                {
                    if (pos >= buf.Length) throw new InvalidDataException("A compressed file ends early.");
                    length = buf[pos++] + 1;
                }
            }
            else
            {
                length = (Bit(buf) ? 2 : 0) + (Bit(buf) ? 1 : 0) + 2;
                if (pos >= buf.Length) throw new InvalidDataException("A compressed file ends early.");
                distance = -(256 - buf[pos++]);
            }
            for (int i = 0; i < length && o < outSize; i++)
            {
                int src = o + distance;
                if (src < 0 || src >= o) throw new InvalidDataException("A compressed file is damaged.");
                output[o] = output[src]; o++;
            }
        }
        return output;
    }
}

/// <summary>The disc's archive index: DATA4.DAT holds, from byte 368, 12 bytes per top-level archive
/// (one word, the sector in DATA3.DAT, the size).</summary>
public sealed class GameArchives
{
    private const int IndexStart = 368, SectorSize = 2048;
    private readonly IsoImage iso;
    private readonly long data3, data4;
    public int Count { get; }

    public GameArchives(IsoImage iso)
    {
        this.iso = iso;
        var d3 = iso.Entries.FirstOrDefault(e => e.Name.Equals("DATA3.DAT", StringComparison.OrdinalIgnoreCase));
        var d4 = iso.Entries.FirstOrDefault(e => e.Name.Equals("DATA4.DAT", StringComparison.OrdinalIgnoreCase));
        if (d3 is null || d4 is null || d4.Length < IndexStart) throw new InvalidDataException("The disc's archive index is missing.");
        data3 = d3.Offset; data4 = d4.Offset;
        Count = (int)((d4.Length - IndexStart) / 12);
    }

    public byte[] Top(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        var row = iso.ReadRange(data4 + IndexStart + index * 12L, 12, 12);
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8));
        return size == 0 ? [] : iso.ReadRange(data3 + sector * (long)SectorSize, size, 256 * 1024 * 1024);
    }

    /// <summary>Where a top-level archive starts in the disc image.</summary>
    public long TopOffset(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        var row = iso.ReadRange(data4 + IndexStart + index * 12L, 12, 12);
        return data3 + BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4)) * (long)SectorSize;
    }

    /// <summary>Part of a top-level archive, so its file names can be listed without reading it whole.</summary>
    public byte[] TopRange(int index, int offset, int length)
    {
        var row = iso.ReadRange(data4 + IndexStart + index * 12L, 12, 12);
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8));
        if (offset < 0 || offset >= size) return [];
        return iso.ReadRange(data3 + sector * (long)SectorSize + offset, Math.Min(size - (uint)offset, (uint)length), length);
    }

    /// <summary>A top-level archive's index row: its sector in DATA3.DAT and its size.</summary>
    public (uint Sector, uint Size) Row(int index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        var row = iso.ReadRange(data4 + IndexStart + index * 12L, 12, 12);
        return (BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(8)));
    }

    /// <summary>Where a top-level archive's index row is in the disc image (the sector word is 4 bytes in, the size 8).</summary>
    public long RowOffset(int index) => data4 + IndexStart + index * 12L;

    public long Data3Offset => data3;

    /// <summary>Raw bytes of DATA3.DAT, including the padding after an archive and the unused space at the end.</summary>
    public byte[] ReadData3(long position, int length) => iso.ReadRange(data3 + position, length, length);
    public long Data3Length => iso.Entries.First(e => e.Name.Equals("DATA3.DAT", StringComparison.OrdinalIgnoreCase)).Length;
    public const int Sector = SectorSize;

    /// <summary>The first sector after every top-level archive; DATA3.DAT is unused from there to its end.</summary>
    public uint FreeSector()
    {
        var rows = iso.ReadRange(data4 + IndexStart, Count * 12, Count * 12);
        long end = 0;
        for (int i = 0; i < Count; i++)
        {
            uint sector = BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(i * 12 + 4)), size = BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(i * 12 + 8));
            if (size > 0) end = Math.Max(end, sector * (long)SectorSize + size);
        }
        return (uint)((end + SectorSize - 1) / SectorSize);
    }

    /// <summary>The top-level archive that holds a position in DATA3.DAT, or -1.</summary>
    public int TopAt(long data3Position)
    {
        var rows = iso.ReadRange(data4 + IndexStart, Count * 12, Count * 12);
        for (int i = 0; i < Count; i++)
        {
            long start = BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(i * 12 + 4)) * (long)SectorSize;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(i * 12 + 8));
            if (data3Position >= start && data3Position < start + size) return i;
        }
        return -1;
    }

    // The index starts with the folder count and file count, then 20 bytes per folder (its name, NUL-terminated within the first
    // 10 bytes). Each file's first word is its folder number times 2^27 plus the low 27 bits of the hash of its name.
    private const int FolderRecord = 20, FolderName = 10;

    /// <summary>The top-level archive for a path such as "bmap/bmap009.ar", or -1 when the disc has no such file.</summary>
    public int Find(string path)
    {
        int slash = path.LastIndexOf('/');
        string folder = slash < 0 ? "" : path[..slash], name = path[(slash + 1)..];
        uint hash = NameHash(name) & 0x07FFFFFF;
        foreach (var (top, fileHash) in FolderFiles(folder))
            if (fileHash == hash) return top;
        return -1;
    }

    /// <summary>The top-level archives in a folder, each with the low 27 bits of its name's hash.</summary>
    public IReadOnlyList<(int Top, uint Hash)> FolderFiles(string folder)
    {
        var header = iso.ReadRange(data4, IndexStart, IndexStart);
        int folders = BinaryPrimitives.ReadInt32LittleEndian(header);
        int number = -1;
        for (int k = 0; k < folders && 8 + (k + 1) * FolderRecord <= IndexStart; k++)
        {
            var raw = header.AsSpan(8 + k * FolderRecord, FolderName);
            int end = raw.IndexOf((byte)0);
            if (Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]).Equals(folder, StringComparison.OrdinalIgnoreCase)) { number = k; break; }
        }
        var result = new List<(int, uint)>();
        if (number < 0) return result;
        var rows = iso.ReadRange(data4 + IndexStart, Count * 12, Count * 12);
        for (int i = 0; i < Count; i++)
        {
            uint key = BinaryPrimitives.ReadUInt32LittleEndian(rows.AsSpan(i * 12));
            if (key >> 27 == (uint)number) result.Add((i, key & 0x07FFFFFF));
        }
        return result;
    }

    /// <summary>The game's hash of a file name: short names are their bytes, longer ones a shift register with feedback 0x1201.</summary>
    public static uint NameHash(string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        uint s = 0;
        if (bytes.Length < 5) { foreach (byte c in bytes) s = s << 8 | c; return s; }
        s = (uint)bytes.Length;
        foreach (byte c in bytes)
        {
            s ^= (uint)c << 8;
            for (int i = 0; i < 8; i++) { s <<= 1; if ((s & 0x04000000) != 0) s ^= 0x1201; }
        }
        return s;
    }
}
