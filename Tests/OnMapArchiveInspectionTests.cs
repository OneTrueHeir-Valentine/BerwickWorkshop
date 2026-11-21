using System.Buffers.Binary;
using System.Text;

internal static class OnMapArchiveInspectionTests
{
    public static void Run(Action<string, Action> test)
    {
        test("On-map archive inventory preserves inner names and bytes", () =>
        {
            var archive = new byte[48];
            "TARC"u8.CopyTo(archive);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(8), 28);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(16), 80);
            BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(20), 4);
            Encoding.ASCII.GetBytes("idle.bin\0").CopyTo(archive, 28);
            archive[40] = 1;
            archive[41] = 2;
            archive[42] = 3;
            archive[43] = 4;

            var items = OnMapArchiveInspection.Describe(archive);
            if (items.Count != 1 || items[0].Name != "idle.bin" ||
                items[0].StoredSize != 4 || items[0].Compressed ||
                items[0].Prefix != "01020304")
                throw new Exception("On-map inventory did not preserve the TARC entry");
        });
        test("On-map archive inventory rejects unsupported input", () =>
        {
            bool refused = false;
            try { OnMapArchiveInspection.Describe(new byte[16]); }
            catch (InvalidDataException) { refused = true; }
            if (!refused) throw new Exception("A malformed archive was accepted");
        });
    }
}
