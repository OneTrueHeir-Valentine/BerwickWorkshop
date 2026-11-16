using System.Buffers.Binary;
using System.Collections;
using System.Text;

namespace Berwick.Core;

/// <summary>One number in an event script: its place among the event's numbers (in reading order), its value, the command it
/// is an argument of (and which argument, from 1), and that command as text.</summary>
public sealed record ScriptNumber(int Event, int Index, int Value, string Command, int Argument, string Line);

/// <summary>
/// Changing the numbers in an event script. A number is found by its place among the event's numbers, which stays the same
/// while only values change. The script is written back with <see cref="ScriptWriter"/> and checked by reading it again.
/// </summary>
public static class ScriptValues
{
    public static TableField Field { get; } = new("value", "Value", 0, 0, 32, int.MinValue, int.MaxValue) { Signed = true };

    /// <summary>The record key of a number: the event number times 65,536 plus its place.</summary>
    public static int Key(int eventNumber, int index) =>
        eventNumber is > 0 and < 0x8000 && index is >= 0 and < 0x10000 ? eventNumber << 16 | index : throw new EditException("There is no such number in the event scripts.");

    public static (int Event, int Index) Split(int key) => (key >>> 16, key & 0xFFFF);

    /// <summary>Every number in the script, in reading order.</summary>
    public static IReadOnlyList<ScriptNumber> Numbers(int eventNumber, IReadOnlyList<ScriptNode> nodes, IReadOnlyList<string>? names = null)
    {
        var result = new List<ScriptNumber>();
        void Walk(IReadOnlyList<ScriptNode> list, ScriptNode? parent, int argument)
        {
            foreach (var n in list)
            {
                if (n.Command == ScriptParser.Number)
                {
                    string command = parent is null ? "" : parent.Command switch
                    {
                        ScriptParser.Assign => $"v{parent.Variable} =",
                        ScriptParser.If => "if",
                        ScriptParser.While => "while",
                        _ when ScriptText.Operators.TryGetValue(parent.Command, out var op) => op,
                        _ => ScriptText.Name(parent.Command, names),
                    };
                    string line = parent is null ? "" : ScriptText.Format([parent], names).Split('\n')[0].Trim();
                    if (line.Length > 90) line = line[..87] + "...";
                    result.Add(new(eventNumber, result.Count, n.Value ?? 0, command, argument, line));
                    continue;
                }
                for (int i = 0; i < n.Args.Count; i++)
                    // Inside an event or an assignment, the command a number belongs to is the statement it sits in.
                    Walk(n.Args[i], n.Command is ScriptParser.Event ? null : n, i + 1);
            }
        }
        Walk(nodes, null, 0);
        return result;
    }

    /// <summary>The script with the given numbers (by place) changed. Throws when a place does not exist.</summary>
    public static byte[] Apply(ReadOnlySpan<byte> decoded, IReadOnlyDictionary<int, int> values)
    {
        var nodes = ScriptParser.Parse(decoded);
        int count = 0;
        ScriptNode Rebuild(ScriptNode n)
        {
            if (n.Command == ScriptParser.Number)
            {
                int place = count++;
                return values.TryGetValue(place, out int value) ? new ScriptNode { Command = n.Command, Offset = n.Offset, Size = n.Size, Value = value } : n;
            }
            if (n.Args.Count == 0) return n;
            return new ScriptNode
            {
                Command = n.Command, Offset = n.Offset, Size = n.Size, Value = n.Value, Text = n.Text, Variable = n.Variable, Locals = n.Locals, Data = n.Data,
                Args = n.Args.Select(a => (IReadOnlyList<ScriptNode>)a.Select(Rebuild).ToList()).ToList(),
            };
        }
        var rebuilt = nodes.Select(Rebuild).ToList();
        if (values.Keys.Any(k => k < 0 || k >= count)) throw new EditException("A changed number is not in this event's script.");
        var bytes = ScriptWriter.Write(rebuilt);
        // The structure is unchanged, so the script must read back with the new values in the same places.
        var check = Numbers(0, ScriptParser.Parse(bytes));
        if (check.Count != count || values.Any(v => check[v.Key].Value != v.Value)) throw new InvalidDataException("The edited script could not be verified.");
        return bytes;
    }
}

/// <summary>Where an event is stored: each top-level archive that holds it directly, and each ev_all.ar (events 1 to 1000).</summary>
public static class EventCopies
{
    public static string FileName(int eventNumber) => $"e{eventNumber:D7}.dat";

    public static IReadOnlyList<StableCopy> Locate(GameArchives archives, int eventNumber)
    {
        var copies = new List<StableCopy>();
        string file = FileName(eventNumber);
        for (int i = 0; i < archives.Count; i++)
        {
            var header = archives.TopRange(i, 0, 16);
            if (!Tarc.IsArchive(header)) continue;
            int count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            int names = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (count <= 0 || count > 20000 || names <= 0) continue;
            var list = archives.TopRange(i, names, count * 40);
            var found = new List<string>();
            for (int p = 0, k = 0; k < count && p < list.Length; k++)
            {
                int end = Array.IndexOf(list, (byte)0, p);
                if (end < 0) break;
                found.Add(Encoding.ASCII.GetString(list, p, end - p)); p = end + 1;
            }
            if (found.Contains(file)) copies.Add(new(i, null));
            if (eventNumber <= 1000 && found.Contains("ev_all.ar")) copies.Add(new(i, "ev_all.ar"));
        }
        return copies;
    }

    public static byte[] Extract(byte[] top, int eventNumber, StableCopy copy)
    {
        var archive = top;
        if (copy.Parent is { } parent) archive = Tarc.Extract(top, Single(Tarc.Parse(top), parent));
        return Tarc.Extract(archive, Single(Tarc.Parse(archive), FileName(eventNumber)));
    }

    /// <summary>
    /// The top-level archive with the event replaced. When the event fits its space only its bytes and stored size change;
    /// otherwise the files after it move and the archive becomes longer.
    /// </summary>
    public static byte[] Replace(byte[] top, int eventNumber, StableCopy copy, byte[] edited)
    {
        if (copy.Parent is not { } parent) return ArchiveEditing.ReplaceOrRepack(top, FileName(eventNumber), edited);
        var archive = Tarc.Extract(top, Single(Tarc.Parse(top), parent));
        return ArchiveEditing.ReplaceOrRepack(top, parent, ArchiveEditing.ReplaceOrRepack(archive, FileName(eventNumber), edited));
    }

    /// <summary>Every copy, read and required to agree, and the decoded event.</summary>
    public static (byte[] Decoded, IReadOnlyList<StableCopy> Copies) Load(GameArchives archives, int eventNumber)
    {
        var copies = Locate(archives, eventNumber);
        if (copies.Count == 0) throw new InvalidDataException($"Event {eventNumber} was not found on this disc.");
        byte[]? first = null;
        foreach (var copy in copies)
        {
            var decoded = Extract(archives.Top(copy.Top), eventNumber, copy);
            if (first is null) first = decoded;
            else if (!first.AsSpan().SequenceEqual(decoded)) throw new InvalidDataException($"The copies of event {eventNumber} differ.");
        }
        return (first!, copies);
    }

    /// <summary>
    /// Throws an <see cref="EditException"/> when the edited event cannot be written: an archive holding the game's base data
    /// (fixedTops) can only grow up to its next sector.
    /// </summary>
    public static void CheckFits(GameArchives archives, int eventNumber, byte[] edited, IReadOnlySet<int> fixedTops) =>
        ArchiveEditing.Patches(archives, Locate(archives, eventNumber).Select(copy => (copy.Top, (Func<byte[], byte[]>)(top => Replace(top, eventNumber, copy, edited)))), fixedTops);

    private static ArchiveEntry Single(IReadOnlyList<ArchiveEntry> entries, string name)
    {
        var matches = entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw new InvalidDataException("An event could not be found in its archive.");
    }
}

/// <summary>
/// The numbers of the event scripts as edit records (four bytes each, keyed by <see cref="ScriptValues.Key"/>), read from the
/// disc when first asked for. Only lookups by key are supported; the records are not listed.
/// </summary>
public sealed class ScriptValueRecords(string discPath) : IReadOnlyDictionary<int, ReadOnlyMemory<byte>>
{
    private readonly object gate = new();
    private readonly Dictionary<int, int[]> numbers = [];
    private EventLibrary? library;
    private FileStream? stream;

    private readonly Dictionary<int, byte[]> bytes = [];

    /// <summary>The event's decoded script as the disc has it, or null when the event is not on the disc.</summary>
    public byte[]? Bytes(int eventNumber)
    {
        lock (gate)
        {
            if (bytes.TryGetValue(eventNumber, out var known)) return known;
            if (library is null)
            {
                stream = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                library = new EventLibrary(new GameArchives(new IsoImage(stream)));
            }
            if (!library.Numbers.Contains(eventNumber)) return null;
            return bytes[eventNumber] = library.Bytes(eventNumber);
        }
    }

    /// <summary>The numbers of one event in reading order, or null when the event is not on the disc.</summary>
    public int[]? EventNumbers(int eventNumber)
    {
        lock (gate)
        {
            if (numbers.TryGetValue(eventNumber, out var known)) return known;
            if (Bytes(eventNumber) is not { } script) return null;
            var list = ScriptValues.Numbers(eventNumber, ScriptParser.Parse(script)).Select(n => n.Value).ToArray();
            return numbers[eventNumber] = list;
        }
    }

    public bool TryGetValue(int key, out ReadOnlyMemory<byte> value)
    {
        var (eventNumber, index) = ScriptValues.Split(key);
        value = default;
        if (eventNumber <= 0 || EventNumbers(eventNumber) is not { } list || index >= list.Length) return false;
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, list[index]);
        value = bytes;
        return true;
    }

    public bool ContainsKey(int key) => TryGetValue(key, out _);
    public ReadOnlyMemory<byte> this[int key] => TryGetValue(key, out var v) ? v : throw new KeyNotFoundException();
    public IEnumerable<int> Keys => throw new NotSupportedException();
    public IEnumerable<ReadOnlyMemory<byte>> Values => throw new NotSupportedException();
    public int Count => throw new NotSupportedException();
    public IEnumerator<KeyValuePair<int, ReadOnlyMemory<byte>>> GetEnumerator() => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One line of a script as the Lines list shows it: its depth, its text, and where its command sits.</summary>
public sealed record ScriptLine(int Depth, string Text, IReadOnlyList<(int Arg, int Item)> Path, bool Block);

/// <summary>
/// Changing the lines of an event script: removing, duplicating, moving and adding commands. A line is found by its path from
/// the event: for each level, which argument of the command holds the list (the event's body, the branches of an if, the body
/// of a while) and the line's place in that list.
/// </summary>
public static class ScriptLines
{
    /// <summary>Every line in reading order. Lines inside if and while are listed under them; "else" starts an else branch.</summary>
    public static IReadOnlyList<ScriptLine> Lines(IReadOnlyList<ScriptNode> nodes, IReadOnlyList<string>? names = null)
    {
        var result = new List<ScriptLine>();
        void Walk(IReadOnlyList<ScriptNode> list, int depth, List<(int, int)> path, int arg)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i];
                var here = new List<(int, int)>(path) { (arg, i) };
                bool block = n.Command is ScriptParser.If or ScriptParser.While;
                result.Add(new(depth, ScriptText.Format([n], names).Split('\n')[0].Trim(), here, block));
                if (n.Command == ScriptParser.If && n.Args.Count >= 2)
                {
                    Walk(n.Args[1], depth + 1, here, 1);
                    if (n.Args.Count == 3 && n.Args[2].Count > 0)
                    {
                        result.Add(new(depth, "else", [.. here, (2, -1)], false));
                        Walk(n.Args[2], depth + 1, here, 2);
                    }
                }
                else if (n.Command == ScriptParser.While && n.Args.Count == 2) Walk(n.Args[1], depth + 1, here, 1);
            }
        }
        if (nodes is [{ Command: ScriptParser.Event } e]) Walk(e.Args[0], 0, [], 0);
        return result;
    }

    private static ScriptNode With(ScriptNode n, int arg, IReadOnlyList<ScriptNode> list) => new()
    {
        Command = n.Command, Value = n.Value, Text = n.Text, Variable = n.Variable, Locals = n.Locals, Data = n.Data,
        Args = n.Args.Select((a, i) => i == arg ? list : a).ToList(),
    };

    // Rebuild the tree with the list that holds the line changed by the given function.
    private static byte[] Change(ReadOnlySpan<byte> decoded, IReadOnlyList<(int Arg, int Item)> path, Func<List<ScriptNode>, int, List<ScriptNode>> change)
    {
        var nodes = ScriptParser.Parse(decoded);
        if (nodes is not [{ Command: ScriptParser.Event } root] || path.Count == 0 || path.Any(p => p.Item < 0)) throw new EditException("Choose a line of the script.");
        ScriptNode Down(ScriptNode parent, int level)
        {
            var (arg, item) = path[level];
            if (arg >= parent.Args.Count) throw new EditException("Choose a line of the script.");
            var list = parent.Args[arg].ToList();
            if (item >= list.Count) throw new EditException("Choose a line of the script.");
            if (level == path.Count - 1) return With(parent, arg, change(list, item));
            list[item] = Down(list[item], level + 1);
            return With(parent, arg, list);
        }
        var bytes = ScriptWriter.Write([Down(root, 0)]);
        ScriptParser.Parse(bytes);
        return bytes;
    }

    public static byte[] Remove(ReadOnlySpan<byte> decoded, IReadOnlyList<(int Arg, int Item)> path) =>
        Change(decoded, path, (list, i) => { list.RemoveAt(i); return list; });

    public static byte[] Duplicate(ReadOnlySpan<byte> decoded, IReadOnlyList<(int Arg, int Item)> path) =>
        Change(decoded, path, (list, i) => { list.Insert(i + 1, list[i]); return list; });

    /// <summary>Moves a line up (-1) or down (+1) within its own list.</summary>
    public static byte[] Move(ReadOnlySpan<byte> decoded, IReadOnlyList<(int Arg, int Item)> path, int by) =>
        Change(decoded, path, (list, i) =>
        {
            int to = i + by;
            if (to < 0 || to >= list.Count) throw new EditException(by < 0 ? "This is already the first line of its part of the script." : "This is already the last line of its part of the script.");
            (list[i], list[to]) = (list[to], list[i]);
            return list;
        });

    /// <summary>Adds a command after the given line.</summary>
    public static byte[] InsertAfter(ReadOnlySpan<byte> decoded, IReadOnlyList<(int Arg, int Item)> path, ScriptNode command) =>
        Change(decoded, path, (list, i) => { list.Insert(i + 1, command); return list; });

    /// <summary>
    /// A command from its number and arguments typed as text: whole numbers (such as 12, -1 or 0x2A), text in double quotes,
    /// or local values (v0, v1 and so on).
    /// </summary>
    public static ScriptNode Command(int command, string arguments)
    {
        var args = new List<IReadOnlyList<ScriptNode>>();
        var text = arguments.Trim();
        int at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at >= text.Length) break;
            if (text[at] == '"')
            {
                int end = text.IndexOf('"', at + 1);
                if (end < 0) throw new EditException("A text argument is missing its closing quote.");
                args.Add([new ScriptNode { Command = ScriptParser.TextValue, Text = text[(at + 1)..end] }]);
                at = end + 1;
            }
            else
            {
                int end = text.IndexOf(',', at); if (end < 0) end = text.Length;
                string item = text[at..end].Trim();
                if (item.Length > 1 && (item[0] == 'v' || item[0] == 'V') && int.TryParse(item[1..], out int local) && local >= 0)
                    args.Add([new ScriptNode { Command = ScriptParser.Variable, Variable = local }]);
                else if (TryNumber(item, out int value)) args.Add([new ScriptNode { Command = ScriptParser.Number, Value = value }]);
                else throw new EditException($"\"{item}\" is not a number, a text in quotes or a local value such as v0.");
                at = end;
            }
            while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            if (at < text.Length)
            {
                if (text[at] != ',') throw new EditException("Separate the arguments with commas.");
                at++;
                if (text[at..].Trim().Length == 0) throw new EditException("An argument is missing after the last comma.");
            }
        }
        return new ScriptNode { Command = command, Args = args };
    }

    private static bool TryNumber(string s, out int value)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            bool negative = s[0] == '-';
            bool ok = uint.TryParse(s[(negative ? 3 : 2)..], System.Globalization.NumberStyles.HexNumber, null, out uint raw);
            value = ok ? (negative ? -(int)raw : (int)raw) : 0;
            return ok;
        }
        return int.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The argument counts each command is used with in the given scripts (the game only reads what a command expects).</summary>
    public static IReadOnlyDictionary<int, IReadOnlySet<int>> ArgumentCounts(IEnumerable<byte[]> scripts)
    {
        var result = new Dictionary<int, HashSet<int>>();
        void Walk(IEnumerable<ScriptNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.Command > ScriptParser.Event && n.Data is null) (result.TryGetValue(n.Command, out var set) ? set : result[n.Command] = []).Add(n.Args.Count);
                foreach (var a in n.Args) Walk(a);
            }
        }
        foreach (var s in scripts) { try { Walk(ScriptParser.Parse(s)); } catch (InvalidDataException) { } }
        return result.ToDictionary(r => r.Key, r => (IReadOnlySet<int>)r.Value);
    }
}
