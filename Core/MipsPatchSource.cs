using System.Reflection;

namespace Berwick.Core;

/// <summary>Loads audited MIPS source bundled in this assembly, without accessing the local filesystem.</summary>
internal static class MipsPatchSource
{
    public static uint Single(string resource, uint address)
    {
        using var stream = typeof(MipsPatchSource).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing MIPS patch resource '{resource}'.");
        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd();
        if (source.Length > 4096)
            throw new InvalidDataException($"MIPS patch resource '{resource}' is too large.");
        var words = MipsAssembly.Assemble(source, address);
        if (words.Length != 1)
            throw new InvalidDataException($"MIPS patch resource '{resource}' must contain exactly one instruction.");
        return words[0];
    }
}
