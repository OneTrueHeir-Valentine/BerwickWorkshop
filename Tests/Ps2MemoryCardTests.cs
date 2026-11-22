using System.Buffers.Binary;
using System.Text;
using Berwick.Core;

public static class Ps2MemoryCardTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Raw PS2 memory card lists nested save files without writing", () =>
        {
            byte[] image = Build(false);
            byte[] before = (byte[])image.Clone();
            using var stream = new MemoryStream(image, writable: false);
            var found = Ps2MemoryCardInspector.Inspect(stream);
            if (found.PageSize != 512 || found.PagesPerCluster != 2 || found.HasSpareBytes
                || found.Entries.Count != 2 || found.WriteSupported || found.GameSaveFieldsDecoded)
                throw new Exception("The read-only filesystem report has unexpected geometry or edit authority.");
            if (found.Entries[0].Path != "/BWSAVE" || !found.Entries[0].IsDirectory
                || found.Entries[1].Path != "/BWSAVE/DATA" || found.Entries[1].IsDirectory
                || found.Entries[1].Size != 128)
                throw new Exception("The nested card directory/file chain was not enumerated.");
            if (!image.SequenceEqual(before))
                throw new Exception("Inspecting a card mutated its input.");
        });
        test("PS2 memory card handles spare-byte pages without misreading clusters", () =>
        {
            using var stream = new MemoryStream(Build(true), writable: false);
            var found = Ps2MemoryCardInspector.Inspect(stream);
            if (!found.HasSpareBytes || found.Entries.Count != 2
                || found.Entries[1].Path != "/BWSAVE/DATA")
                throw new Exception("ECC page spacing corrupted read-only directory traversal.");
        });
        test("PS2 card inspector refuses corrupt header, geometry and directory loops", () =>
        {
            byte[] original = Build(false);
            void Reject(Action<byte[]> mutate)
            {
                var b = (byte[])original.Clone(); mutate(b);
                try
                {
                    Ps2MemoryCardInspector.Inspect(new MemoryStream(b, writable: false));
                }
                catch (InvalidDataException) { return; }
                throw new Exception("Invalid card format or directory chain was accepted.");
            }
            Reject(b => b[0] = 0);
            Reject(b => Write16(b, 0x28, 256));
            Reject(b => Write32(b, 0x30, 2048));
            Reject(b => Write32(b, 0x34, 1000));
            Reject(b => Write32(b, 0x3C, 1));
            Reject(b => Write32(b, 0x50, 5000));
            Reject(b => Write32(b, 2 * 1024 + 0, 0x80000000)); // root FAT cycle
            Reject(b => Write32(b, 4 * 1024 + 4, 20000)); // impossible root entry count
        });
        test("PS2 card inspector rejects unsupported or truncated images", () =>
        {
            byte[] image = Build(false);
            try
            {
                Ps2MemoryCardInspector.Inspect(new MemoryStream(image[..^1], writable: false));
                throw new Exception("Truncated image was accepted.");
            }
            catch (InvalidDataException) { }
            try
            {
                Ps2MemoryCardInspector.Inspect(new MemoryStream(new byte[128], writable: false));
                throw new Exception("Noncard image was accepted.");
            }
            catch (InvalidDataException) { }
        });
    }

    private static byte[] Build(bool spare)
    {
        const int clusters = 64, page = 512, perCluster = 2;
        int physicalPage = spare ? 528 : 512;
        var bytes = new byte[clusters * perCluster * physicalPage];
        void WriteCluster(uint cluster, int offset, ReadOnlySpan<byte> content)
        {
            for (int i = 0; i < content.Length; i++)
            {
                int logical = offset + i;
                int position = checked((int)cluster * perCluster * physicalPage
                    + logical / page * physicalPage + logical % page);
                bytes[position] = content[i];
            }
        }
        void Put16(uint cluster, int at, ushort val)
        {
            Span<byte> v = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(v, val);
            WriteCluster(cluster, at, v);
        }
        void Put32(uint cluster, int at, uint val)
        {
            Span<byte> v = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(v, val);
            WriteCluster(cluster, at, v);
        }
        WriteCluster(0, 0, Encoding.ASCII.GetBytes("Sony PS2 Memory Card Format "));
        WriteCluster(0, 0x1C, Encoding.ASCII.GetBytes("1.2.0.0"));
        Put16(0, 0x28, 512);
        Put16(0, 0x2A, 2);
        Put16(0, 0x2C, 16);
        Put32(0, 0x30, clusters);
        Put32(0, 0x34, 4);
        Put32(0, 0x38, clusters - 4);
        Put32(0, 0x3C, 0);
        Put32(0, 0x50, 1); // indirect FAT pointer
        WriteCluster(0, 0x150, new byte[] { 2 });
        Put32(1, 0, 2); // indirect points to FAT cluster
        Put32(2, 0, 0x80000003); // root continuation in rel cluster 3
        Put32(2, 4, 0x80000004); // nested continuation in rel cluster 4
        Put32(2, 8, uint.MaxValue); // file payload final
        Put32(2, 12, uint.MaxValue);
        Put32(2, 16, uint.MaxValue);
        void Entry(uint cluster, int slot, string name, ushort mode, uint size, uint first)
        {
            int off = slot * 512;
            Put16(cluster, off, mode);
            Put32(cluster, off + 4, size);
            Put32(cluster, off + 0x10, first);
            WriteCluster(cluster, off + 0x40, Encoding.ASCII.GetBytes(name));
        }
        Entry(4, 0, ".", 0x8020, 3, 0);
        Entry(4, 1, "..", 0x8020, 0, 0);
        Entry(7, 0, "BWSAVE", 0x8020, 3, 1);
        Entry(5, 0, ".", 0x8020, 0, 0);
        Entry(5, 1, "..", 0x8020, 0, 0);
        Entry(8, 0, "DATA", 0x8010, 128, 2);
        return bytes;
    }
    private static void Write16(byte[] b, int i, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i, 2), value);
    private static void Write32(byte[] b, int i, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i, 4), value);
}
