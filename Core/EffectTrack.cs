using System.Buffers.Binary;
using System.Text.RegularExpressions;
namespace Berwick.Core;

public sealed record EffectRecord(int Index, ushort Flags, int? NextIndex, int DurationCounter, int Picture,
    int Texture, int OffsetX, int OffsetY, int ScaleRaw, int Depth, int ControlFlags, int Special,
    int BranchIndex, int RepeatCount, int RepeatX, int RepeatY, uint Events, string SourceHex)
{
    public bool Visible => (Flags & 1) != 0;
    public bool BlendEnabled => (Flags & 4) != 0;
    public int BlendMode => Flags >> 4 & 3;
    public bool ScaleEnabled => (Flags & 8) != 0;
    public bool FlipX => (Flags & 0x40) != 0;
    public bool FlipY => (Flags & 0x80) != 0;
}
public sealed record EffectTrack(string Motion, int Number, string HeaderHex, string? KeyframesBase64, IReadOnlyList<EffectRecord> Records)
{
    public static EffectTrack Decode(string motion, int number, byte[] header, byte[] data, byte[]? keyframes)
    {
        if (!Regex.IsMatch(motion, @"\Am[0-9]+_[0-9]+\z") || motion.Length > 40 || number < 0 || number > 255 || header.Length != 16 || data.Length == 0 || data.Length % 60 != 0 || data.Length / 60 > 256)
            throw new EditException("The effect track is incomplete or has an invalid name.");
        var records = new List<EffectRecord>();
        for (int at = 0; at < data.Length; at += 60)
        {
            int next = data[at + 2];
            if (next > data.Length / 60) throw new EditException("An effect track points outside its frame records.");
            int Short(int offset) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at + offset));
            int UShort(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + offset));
            records.Add(new(at / 60, (ushort)UShort(0), next == 0 ? null : next - 1, data[at + 3], UShort(14), data[at + 21],
                Short(4), Short(6), Short(18), (sbyte)data[at + 24], UShort(16), data[at + 22], data[at + 12], data[at + 8], (sbyte)data[at + 9], (sbyte)data[at + 10],
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 56)), Convert.ToHexString(data.AsSpan(at, 60))));
        }
        return new(motion, number, Convert.ToHexString(header), keyframes is null ? null : Convert.ToBase64String(keyframes), records.AsReadOnly());
    }
}
