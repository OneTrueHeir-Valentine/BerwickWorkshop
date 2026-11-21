using System.Buffers.Binary;
using System.Globalization;

namespace Berwick.Core;

/// <summary>
/// Encodes a deliberately limited subset of PlayStation 2 MIPS instructions.
/// Branches use explicit delay-slot instructions. No pseudo-instructions or automatic relocation are performed.
/// An unsupported instruction fails rather than silently producing different machine code.
/// </summary>
public static class MipsAssembly
{
    private static readonly IReadOnlyDictionary<string, int> Registers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = 0, ["at"] = 1, ["v0"] = 2, ["v1"] = 3,
            ["a0"] = 4, ["a1"] = 5, ["a2"] = 6, ["a3"] = 7,
            ["t0"] = 8, ["t1"] = 9, ["t2"] = 10, ["t3"] = 11,
            ["t4"] = 12, ["t5"] = 13, ["t6"] = 14, ["t7"] = 15,
            ["s0"] = 16, ["s1"] = 17, ["s2"] = 18, ["s3"] = 19,
            ["s4"] = 20, ["s5"] = 21, ["s6"] = 22, ["s7"] = 23,
            ["t8"] = 24, ["t9"] = 25, ["k0"] = 26, ["k1"] = 27,
            ["gp"] = 28, ["sp"] = 29, ["fp"] = 30, ["s8"] = 30, ["ra"] = 31
        };

    /// <summary>Assembles one word per statement at the stated game memory address.</summary>
    public static uint[] Assemble(string source, uint address = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((address & 3) != 0) throw new FormatException("MIPS code address must be word aligned.");

        var labels = new Dictionary<string, uint>(StringComparer.Ordinal);
        var instructions = new List<(int Line, string Text)>();
        var lines = source.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Split('#', 2)[0].Trim();
            if (text.Length == 0) continue;
            var colon = text.IndexOf(':');
            if (colon >= 0)
            {
                var label = text[..colon].Trim();
                if (!ValidLabel(label)) throw new FormatException($"Invalid MIPS label at line {i + 1}.");
                if (!labels.TryAdd(label, WordAddress(address, instructions.Count)))
                    throw new FormatException($"Duplicate MIPS label '{label}' at line {i + 1}.");
                text = text[(colon + 1)..].Trim();
                if (text.Length == 0) continue;
            }
            instructions.Add((i + 1, text));
        }

        var result = new uint[instructions.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var (line, text) = instructions[i];
            try { result[i] = Encode(text, WordAddress(address, i), labels); }
            catch (FormatException e) { throw new FormatException($"MIPS line {line}: {e.Message}", e); }
        }
        return result;
    }

    public static byte[] AssembleLittleEndian(string source, uint address = 0)
    {
        var words = Assemble(source, address);
        var bytes = new byte[checked(words.Length * 4)];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    private static uint WordAddress(uint start, int index)
    {
        var address = (long)start + (long)index * 4;
        if (address > uint.MaxValue) throw new FormatException("MIPS source exceeds the address space.");
        return (uint)address;
    }

    private static bool ValidLabel(string label) =>
        label.Length > 0 && (char.IsLetter(label[0]) || label[0] == '_') &&
        label.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                       (c >= '0' && c <= '9') || c == '_');

    private static int Register(string value)
    {
        var name = value.Trim().TrimStart('$');
        if (Registers.TryGetValue(name, out var register)) return register;
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out register) &&
            register is >= 0 and <= 31) return register;
        throw new FormatException($"Unknown MIPS register '{value}'.");
    }

    private static long Number(string value)
    {
        value = value.Trim();
        bool negative = value.StartsWith('-');
        var digits = negative ? value[1..] : value;
        long result;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(digits[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result))
                throw new FormatException($"Invalid hexadecimal number '{value}'.");
        }
        else if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            throw new FormatException($"Invalid number '{value}'.");
        return negative ? -result : result;
    }

    private static uint Immediate16(string value, bool unsigned)
    {
        var number = Number(value);
        // Explicit hex values may specify the full 16-bit instruction field.
        bool rawHex = value.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (number < (unsigned ? 0 : short.MinValue) ||
            number > (unsigned || rawHex ? ushort.MaxValue : short.MaxValue))
            throw new FormatException($"Immediate '{value}' does not fit in 16 bits.");
        return (uint)number & 0xFFFF;
    }

    private static uint Shift(string value)
    {
        var shift = Number(value);
        if (shift is < 0 or > 31) throw new FormatException("Shift must be between 0 and 31.");
        return (uint)shift;
    }

    private static uint I(uint opcode, int rs, int rt, uint immediate) =>
        opcode << 26 | (uint)rs << 21 | (uint)rt << 16 | immediate;

    private static uint R(int rs, int rt, int rd, uint shift, uint function) =>
        (uint)rs << 21 | (uint)rt << 16 | (uint)rd << 11 | shift << 6 | function;

    private static uint Encode(string text, uint pc, IReadOnlyDictionary<string, uint> labels)
    {
        int space = text.IndexOfAny([' ', '\t']);
        string name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        string[] operands = space < 0 ? [] : text[(space + 1)..]
            .Split(',', StringSplitOptions.TrimEntries);
        void Expect(int number)
        {
            if (operands.Length != number || operands.Any(x => x.Length == 0))
                throw new FormatException($"'{name}' requires {number} operand(s).");
        }

        switch (name)
        {
            case "nop":
                Expect(0);
                return 0;
            case ".word":
                Expect(1);
                var word = Number(operands[0]);
                if (word is < int.MinValue or > uint.MaxValue)
                    throw new FormatException(".word must be a 32-bit value.");
                return unchecked((uint)word);
            case "addiu":
                Expect(3);
                return I(9, Register(operands[1]), Register(operands[0]), Immediate16(operands[2], false));
            case "ori":
                Expect(3);
                return I(13, Register(operands[1]), Register(operands[0]), Immediate16(operands[2], true));
            case "lui":
                Expect(2);
                return I(15, 0, Register(operands[0]), Immediate16(operands[1], true));
            case "lw":
            case "sw":
            {
                Expect(2);
                var memory = operands[1];
                int left = memory.IndexOf('('), right = memory.IndexOf(')');
                if (left <= 0 || right != memory.Length - 1 || right <= left + 1)
                    throw new FormatException("Memory operand must be offset(base).");
                return I(name == "lw" ? 35u : 43u,
                    Register(memory[(left + 1)..right]), Register(operands[0]),
                    Immediate16(memory[..left], false));
            }
            case "addu":
            case "subu":
            case "and":
            case "or":
                Expect(3);
                return R(Register(operands[1]), Register(operands[2]), Register(operands[0]), 0,
                    name switch { "addu" => 33u, "subu" => 35u, "and" => 36u, _ => 37u });
            case "sll":
            case "srl":
                Expect(3);
                return R(0, Register(operands[1]), Register(operands[0]), Shift(operands[2]),
                    name == "sll" ? 0u : 2u);
            case "jr":
                Expect(1);
                return R(Register(operands[0]), 0, 0, 0, 8);
            case "beq":
            case "bne":
            {
                Expect(3);
                long offset;
                if (labels.TryGetValue(operands[2], out var target))
                {
                    long bytes = (long)target - ((long)pc + 4);
                    if ((bytes & 3) != 0) throw new FormatException("Branch target is not aligned.");
                    offset = bytes / 4;
                }
                else offset = Number(operands[2]);
                if (offset is < short.MinValue or > short.MaxValue)
                    throw new FormatException("Branch target is out of range.");
                return I(name == "beq" ? 4u : 5u,
                    Register(operands[0]), Register(operands[1]), (uint)offset & 0xFFFF);
            }
            case "j":
            case "jal":
            {
                Expect(1);
                var destination = labels.TryGetValue(operands[0], out var label)
                    ? label : Number(operands[0]);
                if (destination < 0 || destination > uint.MaxValue || (destination & 3) != 0 ||
                    (((long)pc + 4) & 0xF0000000L) != (destination & 0xF0000000L))
                    throw new FormatException("Jump target is unaligned or outside the current 256 MB region.");
                return (name == "j" ? 2u : 3u) << 26 | ((uint)destination >> 2 & 0x03FFFFFF);
            }
            default:
                throw new FormatException($"Unsupported MIPS instruction '{name}'.");
        }
    }
}
