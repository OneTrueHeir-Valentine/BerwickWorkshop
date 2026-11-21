using Berwick.Core;
using System.Buffers.Binary;

internal static class ShieldGuardAssemblyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Shield guard source fixture matches the original quarter-Might patch", () =>
        {
            using var stream = typeof(ShieldGuardAssemblyTests).Assembly.GetManifestResourceStream(
                "Berwick.Tests.ShieldGuardQuarter") ?? throw new Exception("Shield guard fixture is missing.");
            using var reader = new StreamReader(stream);
            string hex = string.Concat(reader.ReadToEnd().Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            byte[] expected = Convert.FromHexString(hex);
            if (expected.Length != 56)
                throw new Exception("The quarter-Might cave fixture must be 14 instructions.");

            var rule = GameRules.ById(10);
            var patched = rule.Encode(1);
            for (int i = 0; i < 14; i++)
            {
                uint address = 0x001481C8u + (uint)(i * 4);
                int index = -1;
                for (int j = 0; j < rule.Sites.Count; j++)
                    if (rule.Sites[j].Address == address) { index = j; break; }
                if (index < 0) throw new Exception($"Shield rule no longer owns 0x{address:X8}.");
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(i * 4));
                if (patched[index] != word)
                    throw new Exception($"Shield cave differs at 0x{address:X8}: expected 0x{word:X8}, got 0x{patched[index]:X8}.");
            }
        });
    }
}
