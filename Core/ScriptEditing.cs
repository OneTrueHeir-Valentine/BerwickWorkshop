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

    /// <summary>The numbers of one event in reading order, or null when the event is not on the disc.</summary>
    public int[]? EventNumbers(int eventNumber)
    {
        lock (gate)
        {
            if (numbers.TryGetValue(eventNumber, out var known)) return known;
            if (library is null)
            {
                stream = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                library = new EventLibrary(new GameArchives(new IsoImage(stream)));
            }
            if (!library.Numbers.Contains(eventNumber)) return null;
            var list = ScriptValues.Numbers(eventNumber, ScriptParser.Parse(library.Bytes(eventNumber))).Select(n => n.Value).ToArray();
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
