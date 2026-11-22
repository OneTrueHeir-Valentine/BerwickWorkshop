using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Berwick.Core;

/// <summary>Read-only information from a PS2 memory-card file system, not decoded game save data.</summary>
public sealed record Ps2MemoryCardEntry(string Path, bool IsDirectory, uint Size, uint FirstCluster);
public sealed record Ps2MemoryCardInfo(
    string Version, int PageSize, int PagesPerCluster, int PagesPerBlock,
    uint ClustersPerCard, uint AllocationOffset, bool HasSpareBytes,
    string Sha256, IReadOnlyList<Ps2MemoryCardEntry> Entries)
{
    public bool WriteSupported => false;
    public bool GameSaveFieldsDecoded => false;
}

/// <summary>
/// Bounded, read-only inspection of standard raw PS2 memory-card images.
/// Based on the documented Sony/PCSX2 superblock, indirect FAT, and directory format.
/// Rejects unsupported layouts instead of guessing at save offsets.
/// </summary>
public static class Ps2MemoryCardInspector
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format ");
    private const int DirectoryEntrySize = 512;
    private const int MaxDirectoryEntries = 16384;
    private const int MaxDepth = 12;

    public static Ps2MemoryCardInfo Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Specify a memory-card image path.", nameof(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Inspect(stream);
    }

    public static Ps2MemoryCardInfo Inspect(Stream stream)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("Memory-card inspection requires a readable, seekable stream.");
        if (stream.Length < 512 || stream.Length > 256L * 1024 * 1024)
            throw new InvalidDataException("The card image size is outside supported inspection bounds.");

        stream.Position = 0;
        var super = new byte[512];
        stream.ReadExactly(super);
        if (!super.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The PS2 memory-card superblock magic is missing.");

        int pageSize = U16(super, 0x28), pagesPerCluster = U16(super, 0x2A);
        int pagesPerBlock = U16(super, 0x2C);
        uint clusters = U32(super, 0x30), allocation = U32(super, 0x34);
        uint allocationEnd = U32(super, 0x38), root = U32(super, 0x3C);
        int cardType = super[0x150];

        if ((pageSize != 512 && pageSize != 1024)
            || (pageSize == 512 && pagesPerCluster is not (1 or 2))
            || (pageSize == 1024 && pagesPerCluster != 1)
            || pagesPerBlock is < 1 or > 16
            || clusters is < 16 or > 262144
            || allocation == 0 || allocation >= clusters
            || allocationEnd == 0 || allocationEnd > clusters - allocation
            || root != 0 || cardType != 2)
            throw new InvalidDataException("The PS2 card geometry or root metadata is unsupported.");

        long pages = (long)clusters * pagesPerCluster;
        long expectedWithoutSpare = pages * pageSize;
        int spareSize = pageSize / 128 * 4;
        bool spare;
        if (stream.Length == expectedWithoutSpare)
            spare = false;
        else if (stream.Length == pages * (pageSize + spareSize))
            spare = true;
        else
            throw new InvalidDataException("Memory-card length does not agree with the superblock geometry.");

        int physicalPageSize = pageSize + (spare ? spareSize : 0);
        int clusterSize = checked(pageSize * pagesPerCluster);
        var ifc = new uint[32];
        for (int i = 0; i < ifc.Length; i++) ifc[i] = U32(super, 0x50 + i * 4);
        if (ifc[0] >= clusters)
            throw new InvalidDataException("The indirect FAT table is outside the card.");
        string version = Encoding.ASCII.GetString(super, 0x1C, 12).TrimEnd('\0', ' ');

        byte[] ReadPhysicalCluster(uint physicalCluster)
        {
            if (physicalCluster >= clusters)
                throw new InvalidDataException("A referenced card cluster is outside the image.");
            var bytes = new byte[clusterSize];
            for (int page = 0; page < pagesPerCluster; page++)
            {
                long at = checked(((long)physicalCluster * pagesPerCluster + page) * physicalPageSize);
                stream.Position = at;
                stream.ReadExactly(bytes.AsSpan(page * pageSize, pageSize));
            }
            return bytes;
        }

        uint ReadFat(uint relativeCluster)
        {
            if (relativeCluster >= allocationEnd)
                throw new InvalidDataException("A directory cluster is outside the allocated data area.");
            int words = clusterSize / 4;
            uint flat = relativeCluster / (uint)words;
            uint indirectTable = flat / (uint)words;
            uint indirectOffset = flat % (uint)words;
            uint fatOffset = relativeCluster % (uint)words;
            if (indirectTable >= ifc.Length || ifc[indirectTable] >= clusters)
                throw new InvalidDataException("An indirect FAT pointer is outside the card.");
            byte[] indirect = ReadPhysicalCluster(ifc[indirectTable]);
            uint fatCluster = U32(indirect, checked((int)indirectOffset * 4));
            if (fatCluster >= clusters)
                throw new InvalidDataException("A FAT cluster pointer is outside the card.");
            return U32(ReadPhysicalCluster(fatCluster), checked((int)fatOffset * 4));
        }

        var results = new List<Ps2MemoryCardEntry>();
        var visited = new HashSet<uint>();
        void ReadDirectory(uint initial, uint? knownLength, string prefix, int depth)
        {
            if (depth > MaxDepth || initial >= allocationEnd || !visited.Add(initial))
                throw new InvalidDataException("The card contains an unsafe directory loop or depth.");
            int perCluster = clusterSize / DirectoryEntrySize;
            if (perCluster < 1) throw new InvalidDataException("Directory cluster cannot hold a directory entry.");

            byte[] initialBytes = ReadPhysicalCluster(allocation + initial);
            uint entries = knownLength ?? U32(initialBytes, 0x04);
            if (entries is < 2 or > MaxDirectoryEntries)
                throw new InvalidDataException("Directory entry count is outside supported bounds.");
            uint clusterIndex = initial;
            var seenChain = new HashSet<uint>();
            int read = 0;
            while (read < entries)
            {
                if (!seenChain.Add(clusterIndex))
                    throw new InvalidDataException("A directory FAT chain contains a cycle.");
                var clusterBytes = ReadPhysicalCluster(allocation + clusterIndex);
                for (int slot = 0; slot < perCluster && read < entries; slot++, read++)
                {
                    var entry = clusterBytes.AsSpan(slot * DirectoryEntrySize, DirectoryEntrySize);
                    ushort mode = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                    string name = Encoding.ASCII.GetString(entry.Slice(0x40, 32));
                    name = name.Split('\0')[0];
                    if (read == 0 && name != "." || read == 1 && name != "..")
                        throw new InvalidDataException("A directory is missing its dot entries.");
                    if (read < 2 || (mode & 0x8000) == 0) continue;
                    if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\')
                        || name.Contains("..", StringComparison.Ordinal) || name.Any(char.IsControl))
                        throw new InvalidDataException("Unsafe or malformed memory-card directory name.");

                    bool directory = (mode & 0x20) != 0;
                    bool file = (mode & 0x10) != 0;
                    if (directory == file) throw new InvalidDataException("Invalid file type in card directory.");
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4));
                    uint first = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(0x10, 4));
                    if (directory ? (size is < 2 or > MaxDirectoryEntries || first >= allocationEnd)
                                  : (size > 0 && first >= allocationEnd
                                      || size > (long)allocationEnd * clusterSize))
                        throw new InvalidDataException("A file or directory entry has invalid bounds.");

                    if (results.Count >= MaxDirectoryEntries)
                        throw new InvalidDataException("Too many memory-card entries.");
                    string child = prefix + "/" + name;
                    results.Add(new(child, directory, size, first));
                    if (directory) ReadDirectory(first, size, child, depth + 1);
                }
                if (read >= entries) break;
                uint next = ReadFat(clusterIndex);
                if (next == uint.MaxValue || (next & 0x80000000) == 0)
                    throw new InvalidDataException("A directory FAT chain ends before its entries.");
                clusterIndex = next & 0x7FFFFFFF;
                if (clusterIndex >= allocationEnd)
                    throw new InvalidDataException("A directory FAT link is outside the data area.");
            }
        }

        ReadDirectory(root, null, "", 0);
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new(version, pageSize, pagesPerCluster, pagesPerBlock, clusters, allocation,
            spare, hash, results.AsReadOnly());
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int at) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4));
}
