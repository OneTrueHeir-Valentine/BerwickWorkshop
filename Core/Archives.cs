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
}
