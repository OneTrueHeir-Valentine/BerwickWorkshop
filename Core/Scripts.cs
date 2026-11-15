using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Berwick.Core;

/// <summary>One command in an event script. Every argument is itself a list of commands.</summary>
public sealed class ScriptNode
{
    public int Command { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
    public int? Value { get; init; }
    public string? Text { get; init; }
    public int? Variable { get; init; }
    public int Locals { get; init; }
    public IReadOnlyList<IReadOnlyList<ScriptNode>> Args { get; init; } = [];
    /// <summary>Bytes that are not commands (kept so the script can be shown in full).</summary>
    public byte[]? Data { get; init; }
}

/// <summary>Event scripts: each command is u32 size, u32 command number, a u32 size per argument, then each argument
/// as a nested list of commands. The argument count is the one that makes the sizes add up. A few commands
/// carry their own fields: 1 a number, 2 a text, 3 a variable, 4 an assignment, 5 an event.</summary>
public static class ScriptParser
{
    public const int Number = 1, TextValue = 2, Variable = 3, Assign = 4, Event = 5, If = 6, While = 7, Break = 0x17, RunEvent = 0x18, Wait = 0x2C;
    private static readonly Encoding ShiftJis = CreateShiftJis();

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public static IReadOnlyList<ScriptNode> Parse(ReadOnlySpan<byte> data) => Block(data, 0, data.Length);

    private static List<ScriptNode> Block(ReadOnlySpan<byte> d, int start, int end)
    {
        var list = new List<ScriptNode>();
        int o = start;
        while (o < end)
        {
            var node = Record(d, o, end);
            list.Add(node); o += node.Size;
        }
        if (o != end) throw new InvalidDataException("A script command runs past its block.");
        return list;
    }

    private static int U32(ReadOnlySpan<byte> d, int o) =>
        o >= 0 && o + 4 <= d.Length ? BinaryPrimitives.ReadInt32LittleEndian(d[o..]) : throw new InvalidDataException("A script ends inside a command.");

    private static ScriptNode Record(ReadOnlySpan<byte> d, int o, int limit)
    {
        int size = U32(d, o), cmd = U32(d, o + 4);
        if (size < 8 || size > limit - o) throw new InvalidDataException("A script command has an invalid size.");
        switch (cmd)
        {
            case Number:
                if (size != 12) throw new InvalidDataException("A script number has an invalid size.");
                return new() { Command = cmd, Offset = o, Size = size, Value = U32(d, o + 8) };
            case TextValue:
            {
                int n = Math.Min(U32(d, o + 8), size - 12);
                var raw = d.Slice(o + 12, Math.Max(n, 0));
                int nul = raw.IndexOf((byte)0);
                return new() { Command = cmd, Offset = o, Size = size, Text = ShiftJis.GetString(nul >= 0 ? raw[..nul] : raw) };
            }
            case Variable:
                return new() { Command = cmd, Offset = o, Size = size, Variable = U32(d, o + 8) };
            case Assign:
            {
                int n = U32(d, o + 12);
                if (16 + n != size) throw new InvalidDataException("A script assignment has an invalid size.");
                return new() { Command = cmd, Offset = o, Size = size, Variable = U32(d, o + 8), Args = [Block(d, o + 16, o + size)] };
            }
            case Event:
            {
                int body = U32(d, o + 16);
                if (20 + body != size) throw new InvalidDataException("A script event has an invalid size.");
                return new() { Command = cmd, Offset = o, Size = size, Locals = U32(d, o + 12), Args = [Block(d, o + 20, o + size)] };
            }
        }
        for (int n = 0; 8 + 4 * n <= size; n++)
        {
            long sum = 0;
            for (int i = 0; i < n; i++) sum += (uint)U32(d, o + 8 + 4 * i);
            if (8 + 4L * n + sum != size) continue;
            try
            {
                var args = new List<IReadOnlyList<ScriptNode>>();
                int a = o + 8 + 4 * n;
                for (int i = 0; i < n; i++)
                {
                    int s = U32(d, o + 8 + 4 * i);
                    args.Add(s == 0 ? [] : Block(d, a, a + s)); a += s;
                }
                return new() { Command = cmd, Offset = o, Size = size, Args = args };
            }
            catch (InvalidDataException) { }
        }
        return new() { Command = cmd, Offset = o, Size = size, Data = d.Slice(o + 8, size - 8).ToArray() };
    }
}

/// <summary>Readable text for event scripts.</summary>
public static class ScriptText
{
    /// <summary>Names for commands whose meaning is known from the game program.</summary>
    public static IReadOnlyDictionary<int, string> Names { get; } = new Dictionary<int, string>
    {
        [ScriptParser.Break] = "break",
        [ScriptParser.RunEvent] = "run event",
        [ScriptParser.Wait] = "wait",
    };

    /// <summary>Operators, read from the game's handlers: 8 and 9 stop early like "and" and "or".</summary>
    public static IReadOnlyDictionary<int, string> Operators { get; } = new Dictionary<int, string>
    {
        [8] = "and", [9] = "or", [0x0A] = "+", [0x0B] = "-", [0x0C] = "*", [0x0D] = "/", [0x0E] = "%",
        [0x0F] = "==", [0x10] = "!=", [0x11] = ">=", [0x12] = ">", [0x13] = "<=", [0x14] = "<",
    };

    // The game's own names for its commands, from the program file in top-level archive 16 (the same on both editions):
    // one name per command number, in order, from "PredefinedEmpty" for command 0. The first 0x18 are the script's own
    // syntax, which the text shows in its own form.
    private const int NameArchive = 16, CommandCount = 0x4EF, SyntaxCommands = 0x18;

    /// <summary>The game's names for its script commands, by command number, or null when the disc does not have the list.</summary>
    public static IReadOnlyList<string>? ReadCommandNames(GameArchives archives)
    {
        if (archives.Count <= NameArchive) return null;
        var data = archives.Top(NameArchive);
        if (data.Length < 4 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F') return null;
        int p = data.AsSpan().IndexOf("PredefinedEmpty\0"u8);
        if (p < 0) return null;
        var names = new List<string>(CommandCount);
        while (names.Count < CommandCount)
        {
            int end = Array.IndexOf(data, (byte)0, p);
            if (end <= p) return null;
            string name = Encoding.ASCII.GetString(data, p, end - p);
            if (!name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')) return null;
            names.Add(name);
            for (p = end; p < data.Length && data[p] == 0; p++) { }
        }
        return names;
    }

    public static string Name(int command, IReadOnlyList<string>? names = null) =>
        Names.TryGetValue(command, out var n) ? n : names is not null && command >= SyntaxCommands && command < names.Count ? names[command] : $"command{command}";

    public static string Format(IReadOnlyList<ScriptNode> nodes, IReadOnlyList<string>? names = null)
    {
        var sb = new StringBuilder();
        foreach (var n in nodes) Statement(n, 0, sb, names);
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, int depth, string text) => sb.Append(' ', depth * 4).Append(text).Append("\r\n");

    private static void Statement(ScriptNode n, int depth, StringBuilder sb, IReadOnlyList<string>? names)
    {
        switch (n.Command)
        {
            case ScriptParser.Event:
                Line(sb, depth, n.Locals == 1 ? "event (1 local value)" : $"event ({n.Locals} local values)");
                foreach (var s in n.Args[0]) Statement(s, depth + 1, sb, names);
                return;
            case ScriptParser.Assign:
                if (Inline(n.Args[0], names) is { } value) Line(sb, depth, $"v{n.Variable} = {value}");
                else { Line(sb, depth, $"v{n.Variable} ="); foreach (var s in n.Args[0]) Statement(s, depth + 1, sb, names); }
                return;
            case ScriptParser.While when n.Args.Count == 2:
                Line(sb, depth, $"while {Inline(n.Args[0], names) ?? "(condition)"}");
                if (Inline(n.Args[0], names) is null) foreach (var s in n.Args[0]) Statement(s, depth + 2, sb, names);
                foreach (var s in n.Args[1]) Statement(s, depth + 1, sb, names);
                return;
            case ScriptParser.If when n.Args.Count is 2 or 3:
                Line(sb, depth, $"if {Inline(n.Args[0], names) ?? "(condition)"}");
                if (Inline(n.Args[0], names) is null) foreach (var s in n.Args[0]) Statement(s, depth + 2, sb, names);
                foreach (var s in n.Args[1]) Statement(s, depth + 1, sb, names);
                if (n.Args.Count == 3 && n.Args[2].Count > 0)
                {
                    Line(sb, depth, "else");
                    foreach (var s in n.Args[2]) Statement(s, depth + 1, sb, names);
                }
                return;
        }
        if (Expression(n, names) is { } e && e.Length <= 100) { Line(sb, depth, e); return; }
        if (n.Data is { } data) { Line(sb, depth, $"{Name(n.Command, names)} [{Convert.ToHexString(data)}]"); return; }
        Line(sb, depth, $"{Name(n.Command, names)}(");
        for (int i = 0; i < n.Args.Count; i++)
        {
            if (Inline(n.Args[i], names) is { } v) Line(sb, depth + 1, v + (i + 1 < n.Args.Count ? "," : ""));
            else
            {
                Line(sb, depth + 1, "{");
                foreach (var s in n.Args[i]) Statement(s, depth + 2, sb, names);
                Line(sb, depth + 1, "}" + (i + 1 < n.Args.Count ? "," : ""));
            }
        }
        Line(sb, depth, ")");
    }

    private static string? Inline(IReadOnlyList<ScriptNode> block, IReadOnlyList<string>? names) =>
        block.Count == 0 ? "nothing" : block.Count == 1 ? Expression(block[0], names) : null;

    private static string? Expression(ScriptNode n, IReadOnlyList<string>? names)
    {
        if (n.Value is int v) return v.ToString();
        if (n.Text is { } t) return "\"" + t.Replace("\r", "").Replace("\n", "\\n") + "\"";
        if (n.Command == ScriptParser.Variable) return $"v{n.Variable}";
        if (n.Command is ScriptParser.Assign or ScriptParser.Event or ScriptParser.If or ScriptParser.While || n.Data is not null) return null;
        if (Operators.TryGetValue(n.Command, out var op) && n.Args.Count == 2 && Inline(n.Args[0], names) is { } left && Inline(n.Args[1], names) is { } right)
        {
            string text = $"({left} {op} {right})";
            return text.Length <= 100 ? text : null;
        }
        if (n.Command == 0x15 && n.Args.Count == 1 && Inline(n.Args[0], names) is { } neg) return $"-{neg}";
        if (n.Command == 0x16 && n.Args.Count == 1 && Inline(n.Args[0], names) is { } inner) return $"not {inner}";
        if (n.Command == ScriptParser.Break && n.Args.Count == 0) return "break";
        if (n.Command == ScriptParser.RunEvent && n.Args.Count == 1 && Inline(n.Args[0], names) is { } ev) return $"run event {ev}";
        if (n.Command == ScriptParser.Wait && n.Args.Count == 1 && Inline(n.Args[0], names) is { } frames) return frames == "1" ? "wait 1 frame" : $"wait {frames} frames";
        var parts = new List<string>();
        foreach (var a in n.Args)
        {
            if (Inline(a, names) is not { } s) return null;
            parts.Add(s);
        }
        string call = $"{Name(n.Command, names)}({string.Join(", ", parts)})";
        return call.Length <= 100 ? call : null;
    }
}

/// <summary>The event scripts on a disc (files e0000001.dat and on), found in the top-level archives that hold them.</summary>
public sealed class EventLibrary
{
    private static readonly Regex EventName = new(@"^e(\d{7})\.dat$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly GameArchives archives;
    private readonly Dictionary<int, int> topOf = new();
    private readonly Dictionary<int, byte[]> cache = new();

    public IReadOnlyList<int> Numbers { get; }

    public EventLibrary(GameArchives archives)
    {
        this.archives = archives;
        for (int i = 0; i < archives.Count; i++)
        {
            var header = archives.TopRange(i, 0, 16);
            if (!Tarc.IsArchive(header)) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int names = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (count <= 0 || count > 64 || names <= 0) continue;
            var head = archives.TopRange(i, names, count * 32);
            int p = 0, found = 0;
            for (int k = 0; k < count && p < head.Length; k++)
            {
                int end = Array.IndexOf(head, (byte)0, p);
                if (end < 0) break;
                var m = EventName.Match(Encoding.ASCII.GetString(head, p, end - p));
                if (m.Success) { topOf.TryAdd(int.Parse(m.Groups[1].Value), i); found++; }
                p = end + 1;
            }
        }
        Numbers = topOf.Keys.Order().ToList();
        CommandNames = ScriptText.ReadCommandNames(archives);
    }

    public byte[] Bytes(int number)
    {
        if (cache.TryGetValue(number, out var b)) return b;
        if (!topOf.TryGetValue(number, out int top)) throw new InvalidDataException($"Event {number} is not on this disc.");
        var archive = archives.Top(top);
        foreach (var e in Tarc.Parse(archive))
            if (EventName.Match(e.Name) is { Success: true } m && topOf.ContainsKey(int.Parse(m.Groups[1].Value)) && !cache.ContainsKey(int.Parse(m.Groups[1].Value)))
                cache[int.Parse(m.Groups[1].Value)] = Tarc.Extract(archive, e);
        return cache.TryGetValue(number, out b) ? b : throw new InvalidDataException($"Event {number} could not be read.");
    }

    /// <summary>The game's names for its script commands, or null when the disc does not have them.</summary>
    public IReadOnlyList<string>? CommandNames { get; }

    public string Text(int number) => ScriptText.Format(ScriptParser.Parse(Bytes(number)), CommandNames);
}
