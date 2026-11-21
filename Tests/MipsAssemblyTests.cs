using Berwick.Core;

internal static class MipsAssemblyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("MIPS assembler reproduces known heap and battle instruction words", () =>
        {
            var original = MipsAssembly.Assemble("addiu $v0, $v0, 0x0800", 0x00126114);
            var reserved = MipsAssembly.Assemble("addiu $v0, $v0, 0x1800", 0x00126114);
            Equal(0x24420800u, original.Single());
            Equal(0x24421800u, reserved.Single());

            var guard = GameRules.ById(10);
            var index = guard.Sites.Select((site, i) => (site, i))
                .Single(pair => pair.site.Address == 0x00126114).i;
            Equal(original[0], guard.Encode(0)[index]);
            Equal(reserved[0], guard.Encode(1)[index]);

            var more = MipsAssembly.Assemble("""
                lw $s7, 56($s2)
                addu $s1, $v1, $v0
                jal 0x0022CB80
                nop
                """, 0x0022A640);
            Sequence([0x8E570038u, 0x00628821u, 0x0C08B2E0u, 0u], more);
        });

        test("MIPS assembler resolves labels and preserves explicit delay slots", () =>
        {
            var code = MipsAssembly.Assemble("""
                first:
                addiu $v0, $v0, 1
                beq $v0, $zero, first
                nop
                """, 0x00126114);
            Sequence([0x24420001u, 0x1040FFFEu, 0u], code);
            var bytes = MipsAssembly.AssembleLittleEndian("addiu $v0, $v0, 0x0800", 0x00126114);
            if (!bytes.SequenceEqual(new byte[] { 0x00, 0x08, 0x42, 0x24 }))
                throw new Exception("MIPS bytes were not little endian.");
        });

        test("MIPS assembler rejects unknown instructions and unsafe targets", () =>
        {
            MustFail("li $v0, 12");
            MustFail("addiu $notareg, $v0, 10");
            MustFail("addiu $v0, $v0, 100000");
            MustFail("j 0x10000000", 0x00126114);
            MustFail("beq $zero, $zero, 32768");
            MustFail("same:\nsame:\nnop");
            MustFail("lw $v0, 40000($sp)");
            MustFail("nop", 3);
            MustFail(".word 0x100000000");
        });
    }

    private static void Equal(uint expected, uint actual)
    {
        if (expected != actual)
            throw new Exception($"Expected 0x{expected:X8}, got 0x{actual:X8}.");
    }

    private static void Sequence(IReadOnlyList<uint> expected, IReadOnlyList<uint> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception($"Expected {string.Join(",", expected.Select(x => x.ToString("X8")))}, got {string.Join(",", actual.Select(x => x.ToString("X8")))}.");
    }

    private static void MustFail(string source, uint address = 0)
    {
        try { _ = MipsAssembly.Assemble(source, address); }
        catch (FormatException) { return; }
        throw new Exception("Invalid MIPS source was accepted.");
    }
}
