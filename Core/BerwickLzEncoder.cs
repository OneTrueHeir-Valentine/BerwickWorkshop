using System.Buffers.Binary;

namespace Berwick.Core;

public static partial class BerwickLz
{
    private readonly record struct Token(int Length, int Distance, bool Short);

    /// <summary>Encode a small script using the game's literal and overlapping-copy forms.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > 65536) throw new ArgumentException("Script size must be from 1 to 65536 bytes.");
        int n = data.Length;
        var previous = new int[n];
        Array.Fill(previous, -1);
        var last = new Dictionary<int, int>();
        for (int i = 0; i + 1 < n; i++)
        {
            int key = (data[i] << 8) | data[i + 1];
            if (last.TryGetValue(key, out int p)) previous[i] = p;
            last[key] = i;
        }
        // Account for control-byte boundaries as well as payload bytes when choosing copies.
        var cost = new int[(n + 1) * 8];
        var choice = new Token[n * 8];
        var distances = new int[257];
        var shortDistances = new int[6];
        for (int i = n - 1; i >= 0; i--)
        {
            Array.Clear(distances); Array.Clear(shortDistances);
            int limit = Math.Min(256, n - i), longest = 1, shortLongest = 1;
            for (int p = previous[i]; p >= 0 && i - p <= 8192; p = previous[p])
            {
                int length = 2, distance = i - p;
                while (length < limit && data[i + length] == data[i + length - distance]) length++;
                for (int len = 2; len <= length; len++)
                {
                    if (distances[len] == 0) distances[len] = distance;
                    if (distance <= 256 && len <= 5 && shortDistances[len] == 0) shortDistances[len] = distance;
                }
                longest = Math.Max(longest, length);
                if (distance <= 256) shortLongest = Math.Max(shortLongest, Math.Min(5, length));
                if (longest == limit && (shortLongest == Math.Min(5, limit) || distance > 256)) break;
            }
            for (int used = 0; used < 8; used++)
            {
                int at = i * 8 + used;
                cost[at] = 1 + ControlBytes(used, 1) + cost[(i + 1) * 8 + (used + 1) % 8];
                choice[at] = new(1, 0, false);
                void Consider(int length, int distance, bool shortCopy)
                {
                    int bits = shortCopy ? 4 : 2;
                    int payload = shortCopy ? 1 : length <= 9 ? 2 : 3;
                    int candidate = payload + ControlBytes(used, bits) + cost[(i + length) * 8 + (used + bits) % 8];
                    if (candidate < cost[at] || (candidate == cost[at] && length > choice[at].Length))
                    {
                        cost[at] = candidate;
                        choice[at] = new(length, distance, shortCopy);
                    }
                }
                for (int len = 2; len <= shortLongest; len++) Consider(len, shortDistances[len], true);
                for (int len = 3; len <= longest; len++) Consider(len, distances[len], false);
            }
        }
        var encoded = new List<byte>(cost[0]);
        int control = 0, remaining = 0;
        void Bit(bool value)
        {
            if (remaining == 0) { control = encoded.Count; encoded.Add(0); remaining = 8; }
            remaining--;
            if (value) encoded[control] |= (byte)(1 << remaining);
        }
        int position = 0, phase = 0;
        while (position < n)
        {
            var token = choice[position * 8 + phase];
            if (token.Length == 1)
            {
                Bit(true); encoded.Add(data[position]); phase = (phase + 1) % 8;
            }
            else if (token.Short)
            {
                Bit(false); Bit(false); Bit(((token.Length - 2) & 2) != 0); Bit(((token.Length - 2) & 1) != 0);
                encoded.Add((byte)(256 - token.Distance)); phase = (phase + 4) % 8;
            }
            else
            {
                Bit(false); Bit(true);
                int packed = ((8192 - token.Distance) << 3) | (token.Length <= 9 ? token.Length - 2 : 0);
                encoded.Add((byte)(packed >> 8)); encoded.Add((byte)packed);
                if (token.Length > 9) encoded.Add((byte)(token.Length - 1));
                phase = (phase + 2) % 8;
            }
            position += token.Length;
        }
        var result = new byte[8 + encoded.Count];
        BinaryPrimitives.WriteInt32LittleEndian(result, n);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), encoded.Count);
        encoded.CopyTo(result, 8);
        return result;
    }

    private static int ControlBytes(int used, int bits) => (used + bits + 7) / 8 - (used == 0 ? 0 : 1);
}
