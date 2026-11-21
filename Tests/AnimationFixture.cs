using Berwick.Core;
using System.Buffers.Binary;
using System.Text;

internal sealed class AnimationFixture
{
    public Dictionary<string, byte[]> Files { get; } = new()
    {
        ["m0_0_0t.dat"] = BodyFrames(1, 2, 3, 1),
        ["m1_0_0t.dat"] = BodyFrames(2),
        ["m2_1_0t.dat"] = new byte[60],
        ["m2_1_0a.dat"] = new byte[16],
        ["vupos.dat"] = Positions((-4, -2), (292, 290), (12, 14)),
        ["0.tex"] = Texture(8, 16),
        ["common.pal"] = Palette(11, 22, 33),
        ["5.pal"] = Palette(44, 55, 66),
        ["6.pal"] = Palette(77, 88, 99),
        ["vueffpos.dat"] = Positions((-3, -1), (290, 291)),
        ["1.tex"] = Texture(8, 16, 17),
        ["eff0.pal"] = Palette(10, 20, 30),
        ["eff5.pal"] = Palette(11, 22, 33),
        ["m2_1.anm"] = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
        ["m2_1_1a.dat"] = TrackHeader(),
        ["m2_1_1t.dat"] = EffectRecords(),
        ["m2_1_1k.dat"] = [9, 8, 7, 6, 5, 4, 3, 2],
        ["m2_1_2a.dat"] = TrackHeader(),
        ["m2_1_2t.dat"] = EffectRecords()
    };

    public AnimationSet Set() => AnimationSet.FromArchive(7, Archive(Files));

    private static byte[] BodyFrames(params int[] pictures)
    {
        var data = new byte[pictures.Length * 60];
        for (int i = 0; i < pictures.Length; i++)
        {
            int at = i * 60;
            data[at] = 1;
            data[at + 2] = i + 1 < pictures.Length ? (byte)(i + 2) : (byte)0;
            data[at + 3] = (byte)(3 + i);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 14), (ushort)pictures[i]);
        }
        return data;
    }

    private static byte[] EffectRecords()
    {
        var data = new byte[180];
        data[2] = 2;
        data[3] = 30;
        for (int i = 1; i < 3; i++)
        {
            int at = i * 60;
            data[at] = 0xFD;
            data[at + 2] = i == 1 ? (byte)3 : (byte)2;
            data[at + 3] = (byte)(i + 3);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at + 4), -13);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at + 6), 27);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 14), (ushort)i);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at + 18), 384);
            data[at + 21] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 56), 0xDEADBEEF);
        }
        return data;
    }

    private static byte[] TrackHeader()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 321);
        return data;
    }

    private static byte[] Palette(byte r, byte g, byte b)
    {
        var data = new byte[1024];
        new byte[] { r, g, b, 127 }.CopyTo(data, 16 * 4);
        new byte[] { 44, 55, 66, 1 }.CopyTo(data, 8 * 4);
        new byte[] { 77, 88, 99, 0 }.CopyTo(data, 9 * 4);
        return data;
    }

    private static byte[] Texture(params byte[] indices)
    {
        var data = new byte[8 * 1024];
        indices.CopyTo(data, 0);
        return data;
    }

    private static byte[] Positions(params (int X, int Y)[] pictures)
    {
        int header = 8 + (pictures.Length + 1) * 8;
        var data = new byte[header + pictures.Length * 96];
        BinaryPrimitives.WriteInt32LittleEndian(data, pictures.Length + 1);
        for (int i = 0; i < pictures.Length; i++)
        {
            int at = header + i * 96;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4 + (i + 1) * 8), at);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8 + (i + 1) * 8), 96);
            // One VIF unpack packet, containing a single 8 by 8 tile.
            data[at + 31] = 0x6C;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at + 32), 1);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 64), pictures[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at + 68), pictures[i].Y);
        }
        return data;
    }

    private static byte[] Archive(Dictionary<string, byte[]> files)
    {
        int names = 16 + files.Count * 12;
        int payload = names + files.Keys.Sum(n => Encoding.ASCII.GetByteCount(n) + 1);
        var data = new byte[payload + files.Values.Sum(v => v.Length)];
        "TARC"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), files.Count);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), names);
        int row = 16, nameAt = names, at = payload;
        foreach (var (name, bytes) in files)
        {
            nameAt += Encoding.ASCII.GetBytes(name, data.AsSpan(nameAt)) + 1;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(row), at * 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(row + 4), bytes.Length);
            bytes.CopyTo(data, at);
            at += bytes.Length;
            row += 12;
        }
        return data;
    }
}
