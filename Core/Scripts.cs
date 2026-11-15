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

/// <summary>
/// A unit a battle's events bring in later: the unit of army file udNNN (Army) whose script number is Script, on a team, arriving
/// at X, Y from FromX, FromY (off the map when outside it), on the given turn when the script says so, from map event Event.
/// </summary>
public sealed record Reinforcement(int Army, int Script, int Team, int X, int Y, int FromX, int FromY, int? Turn, int Event);

/// <summary>An army file (udNNN) placed when a battle opens, on a team, at a placement group of the map.</summary>
public sealed record ArmyPlacement(int Army, int Team, int Group);

/// <summary>One record of the map event file: an event, the battle it belongs to (0 for every battle), its condition type and arguments.</summary>
public sealed record MapEventRecord(int Event, int Battle, int Type, int[] Args);

/// <summary>The event scripts on a disc (files e0000001.dat and on), found in the top-level archives that hold them.</summary>
public sealed class EventLibrary
{
    private static readonly Regex EventName = new(@"^e(\d{7})\.dat$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly GameArchives archives;

    /// <summary>The disc's archives the events were read from.</summary>
    public GameArchives Archives => archives;
    private readonly Dictionary<int, int> topOf = new();
    private readonly Dictionary<int, byte[]> cache = new();
    private const string MapEventFile = "evdata.dat";
    private int mapEventTop = -1;

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
                string name = Encoding.ASCII.GetString(head, p, end - p);
                var m = EventName.Match(name);
                if (m.Success) { topOf.TryAdd(int.Parse(m.Groups[1].Value), i); found++; }
                else if (name == MapEventFile && mapEventTop < 0) mapEventTop = i;
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

    /// <summary>The top-level archive that holds an event; a battle's events share one archive.</summary>
    public int TopOf(int number) => topOf.TryGetValue(number, out int top) ? top : throw new InvalidDataException($"Event {number} is not on this disc.");

    // Commands, by the game's own names: LaunchBMap and LaunchBMap2 start a battle (battle number first), PreloadUnitList,
    // PreloadUnit, AppearUnitOld, AppearUnit and AppearUnitEx name a unit file ("ud371" or "ud371.ar"), AppearUnitOld as its second argument.
    private const int LaunchBMap = 0x2DE, LaunchBMap2 = 0x2DF, PreloadUnitList = 0x167, PreloadUnit = 0x328;
    private static readonly int[] UnitCommands = [PreloadUnitList, PreloadUnit, 0x32A, 0x32B, 0x32C];
    private static readonly Regex UnitFile = new(@"^ud(\d{3})(\.ar)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The battles each army (unit file udNNN) appears in, for armies placed by events: a battle's own events are listed in the map
    /// event file (evdata.dat), and the events in the same archive as the event that starts a battle belong to that battle too.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> ArmyBattles()
    {
        var eventBattles = MapEventBattles();
        var battles = new Dictionary<int, HashSet<int>>();
        var armies = new Dictionary<int, HashSet<(int Top, int Event)>>();
        static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
        {
            foreach (var n in nodes) { yield return n; foreach (var a in n.Args) foreach (var c in Walk(a)) yield return c; }
        }
        foreach (int number in Numbers)
        {
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(Bytes(number)); } catch (InvalidDataException) { continue; }
            int top = topOf[number];
            foreach (var n in Walk(nodes))
            {
                if (n.Args.Count == 0) continue;
                if (n.Command is LaunchBMap or LaunchBMap2 && n.Args[0] is [{ Value: int battle }] && battle > 0)
                    (battles.TryGetValue(top, out var set) ? set : battles[top] = []).Add(battle);
                else if (UnitCommands.Contains(n.Command)
                    && n.Args.Select(a => a is [{ Text: { } text }] ? UnitFile.Match(text) : null).FirstOrDefault(x => x is { Success: true }) is { } m)
                    (armies.TryGetValue(int.Parse(m.Groups[1].Value), out var places) ? places : armies[int.Parse(m.Groups[1].Value)] = []).Add((top, number));
            }
        }
        var result = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var (army, places) in armies)
        {
            var found = places.Where(p => p.Top >= 0 && battles.ContainsKey(p.Top)).SelectMany(p => battles[p.Top])
                .Concat(places.SelectMany(p => eventBattles.TryGetValue(p.Event, out var b) ? b : [])).Distinct().Order().ToList();
            if (found.Count > 0) result[army] = found;
        }
        return result;
    }

    private Dictionary<int, HashSet<int>> MapEventBattles()
    {
        var result = new Dictionary<int, HashSet<int>>();
        foreach (var r in MapEvents())
            if (r.Battle != 0) (result.TryGetValue(r.Event, out var set) ? set : result[r.Event] = []).Add(r.Battle);
        return result;
    }

    private IReadOnlyList<MapEventRecord>? mapEvents;

    /// <summary>
    /// The map event file (evdata.dat): records of a byte count, then a word with the event number in its top 16 bits and the battle
    /// number in the low 16 (0 for every battle), then the number of words that follow (the condition type and its arguments).
    /// Type 1 is a spot: menu kind, unit (0 for any), x, y.
    /// </summary>
    public IReadOnlyList<MapEventRecord> MapEvents()
    {
        if (mapEvents is not null) return mapEvents;
        var result = new List<MapEventRecord>();
        byte[] data = [];
        if (mapEventTop >= 0)
            try
            {
                var archive = archives.Top(mapEventTop);
                data = Tarc.Extract(archive, Tarc.Parse(archive).Single(e => e.Name == MapEventFile));
            }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { data = []; }
        for (int o = 0; o + 12 <= data.Length; o += 4)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o));
            uint head = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));
            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 8));
            if (size < 12 || size % 4 != 0 || o + 4 + size > data.Length || count != size / 4 - 2) continue;
            int number = (int)(head >> 16);
            if (!topOf.ContainsKey(number)) continue;
            var words = new int[count];
            for (int i = 0; i < count; i++) words[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(o + 12 + i * 4));
            result.Add(new(number, (int)(head & 0xFFFF), words[0], words[1..]));
        }
        return mapEvents = result;
    }

    // Events of type 13 in the map event file run as a battle opens (their argument orders them). AppearUnitEx (0x32C) places a
    // whole army file: AppearUnitEx("udNNN", team, placement group, ...).
    private const int OpeningEvent = 13, AppearUnitEx = 0x32C;

    /// <summary>The army files a battle's opening events place, with the team and the map's placement group for each.</summary>
    public IReadOnlyList<ArmyPlacement> StartingPlacements(int battle)
    {
        var result = new List<ArmyPlacement>();
        foreach (int number in MapEvents().Where(r => r.Battle == battle && r.Type == OpeningEvent).Select(r => r.Event).Distinct().Order())
        {
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(Bytes(number)); } catch (InvalidDataException) { continue; }
            foreach (var n in WalkNodes(nodes))
                if (n.Command == AppearUnitEx && n.Args.Count >= 3 && n.Args[0] is [{ Text: { } file }] && UnitFile.Match(file) is { Success: true } m
                    && n.Args[1] is [{ Value: int team }] && n.Args[2] is [{ Value: int group }])
                    result.Add(new ArmyPlacement(int.Parse(m.Groups[1].Value), team, group));
        }
        return result;
    }

    // Later arrivals: AppearUnit(id, "udNNN", team, from x, from y, to x, to y, flag) brings the unit whose script number
    // (record offset 0x0C) is id in from the first hex to the second; AppearUnitOld(id, "udNNN", team, x, y, flag) places it.
    // The turn is read from the script: a value set from GetCurrentTurn or GameGetTurn compared with == around the call.
    private const int AppearUnitOld = 0x32A, AppearUnit = 0x32B, EqualTo = 0x0F;

    /// <summary>
    /// Units a battle's events bring in after it opens, with the hex they arrive at and, where the script compares the turn
    /// with a number, the turn.
    /// </summary>
    public IReadOnlyList<Reinforcement> Reinforcements(int battle)
    {
        var result = new List<Reinforcement>();
        var turnCommands = (CommandNames ?? []).Select((name, i) => (name, i)).Where(c => c.name is "GetCurrentTurn" or "GameGetTurn").Select(c => c.i).ToHashSet();
        foreach (var record in MapEvents().Where(r => r.Battle == battle && r.Type != OpeningEvent).Select(r => r.Event).Distinct().Order())
            CollectArrivals(record, record, null, turnCommands, result, [], 0);
        return result.DistinctBy(r => (r.Army, r.Script, r.Team, r.X, r.Y, r.Turn)).ToList();
    }

    private void CollectArrivals(int number, int source, int? turn, HashSet<int> turnCommands, List<Reinforcement> result, HashSet<int> visiting, int depth)
    {
        if (depth > 3 || !visiting.Add(number) || !topOf.ContainsKey(number)) return;
        IReadOnlyList<ScriptNode> nodes;
        try { nodes = ScriptParser.Parse(Bytes(number)); } catch (InvalidDataException) { visiting.Remove(number); return; }
        var turnValues = new HashSet<int>();
        void Walk(IEnumerable<ScriptNode> list, int? currentTurn)
        {
            foreach (var n in list)
            {
                if (n.Command == ScriptParser.Assign && n.Variable is int v && n.Args.Count == 1 && n.Args[0] is [{ } call] && turnCommands.Contains(call.Command)) turnValues.Add(v);
                if (n.Command == ScriptParser.If && n.Args.Count >= 2)
                {
                    int? inner = currentTurn;
                    if (n.Args[0] is [{ Command: EqualTo } test] && test.Args.Count == 2 && test.Args[0] is [{ Command: ScriptParser.Variable, Variable: int tv }]
                        && turnValues.Contains(tv) && test.Args[1] is [{ Value: int t }]) inner = t;
                    Walk(n.Args[1], inner);
                    if (n.Args.Count == 3) Walk(n.Args[2], currentTurn);
                    continue;
                }
                if (n.Command == AppearUnit && n.Args.Count >= 7 && Arrival(n, 3, 5) is { } a) result.Add(a with { Turn = currentTurn, Event = source });
                else if (n.Command == AppearUnitOld && n.Args.Count >= 5 && Arrival(n, 3, 3) is { } b) result.Add(b with { Turn = currentTurn, Event = source });
                else if (n.Command == ScriptParser.RunEvent && n.Args.Count == 1 && n.Args[0] is [{ Value: int inner }]) CollectArrivals(inner, source, currentTurn, turnCommands, result, visiting, depth + 1);
                foreach (var arg in n.Args) Walk(arg, currentTurn);
            }
        }
        Walk(nodes, turn);
        visiting.Remove(number);
    }

    private static Reinforcement? Arrival(ScriptNode n, int from, int to)
    {
        if (n.Args[0] is not [{ Value: int script }] || n.Args[1] is not [{ Text: { } file }] || UnitFile.Match(file) is not { Success: true } m || n.Args[2] is not [{ Value: int team }]) return null;
        if (n.Args[from] is not [{ Value: int fx }] || n.Args[from + 1] is not [{ Value: int fy }] || n.Args[to] is not [{ Value: int x }] || n.Args[to + 1] is not [{ Value: int y }]) return null;
        return new Reinforcement(int.Parse(m.Groups[1].Value), script, team, x, y, fx, fy, null, 0);
    }

    // Map animation: CreateBackGroudTree(0, event) starts an event that loops over frames, each switching map layers with
    // DisplayLayer(layer, 1 or 0).
    private const int DisplayLayer = 0x2D1, CreateBackgroundAnimation = 0x2D2;

    /// <summary>
    /// The map layers shown when a battle starts: the first of the battle's events (by number, following the events they
    /// run) that starts the map's animation, and the layers that animation's first frame turns on. Null when no event of the
    /// battle starts one.
    /// </summary>
    public IReadOnlyList<int>? StartingLayers(int battle)
    {
        var seen = new HashSet<int>();
        foreach (int number in MapEvents().Where(r => r.Battle == battle).Select(r => r.Event).Distinct().Order())
            if (FindAnimation(number, seen, 0) is int animation) return FirstFrame(animation);
        return null;
    }

    private int? FindAnimation(int number, HashSet<int> seen, int depth)
    {
        if (depth > 4 || !seen.Add(number) || !topOf.ContainsKey(number)) return null;
        IReadOnlyList<ScriptNode> nodes;
        try { nodes = ScriptParser.Parse(Bytes(number)); } catch (InvalidDataException) { return null; }
        foreach (var n in WalkNodes(nodes))
        {
            if (n.Command == CreateBackgroundAnimation && n.Args.Count >= 2 && n.Args[1] is [{ Value: int animation }]) return animation;
            if (n.Command == ScriptParser.RunEvent && n.Args.Count == 1 && n.Args[0] is [{ Value: int inner }] && FindAnimation(inner, seen, depth + 1) is int found) return found;
        }
        return null;
    }

    private IReadOnlyList<int> FirstFrame(int animation)
    {
        var on = new List<int>();
        if (!topOf.ContainsKey(animation)) return on;
        IReadOnlyList<ScriptNode> nodes;
        try { nodes = ScriptParser.Parse(Bytes(animation)); } catch (InvalidDataException) { return on; }
        var set = new HashSet<int>();
        foreach (var n in WalkNodes(nodes))
            if (n.Command == DisplayLayer && n.Args.Count == 2 && n.Args[0] is [{ Value: int layer }] && n.Args[1] is [{ Value: int shown }])
            {
                if (!set.Add(layer)) break;
                if (shown != 0) on.Add(layer);
            }
        return on;
    }

    private static IEnumerable<ScriptNode> WalkNodes(IEnumerable<ScriptNode> nodes)
    {
        foreach (var n in nodes) { yield return n; foreach (var a in n.Args) foreach (var c in WalkNodes(a)) yield return c; }
    }

    /// <summary>
    /// The map file each battle loads, from the events that start battles: LaunchBMap with the battle number and the file, or
    /// LaunchBMap2 with a mode first (the demo battle on the title screen uses it).
    /// </summary>
    public IReadOnlyDictionary<int, string> BattleMapFiles()
    {
        var result = new Dictionary<int, string>();
        static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
        {
            foreach (var n in nodes) { yield return n; foreach (var a in n.Args) foreach (var c in Walk(a)) yield return c; }
        }
        foreach (int number in Numbers)
        {
            IReadOnlyList<ScriptNode> nodes;
            try { nodes = ScriptParser.Parse(Bytes(number)); } catch (InvalidDataException) { continue; }
            foreach (var n in Walk(nodes))
            {
                int first = n.Command == LaunchBMap2 ? 1 : n.Command == LaunchBMap ? 0 : -1;
                if (first >= 0 && n.Args.Count >= first + 2 && n.Args[first] is [{ Value: int battle }] && n.Args[first + 1] is [{ Text: { } file }] && battle > 0)
                    result.TryAdd(battle, file);
            }
        }
        return result;
    }
}
