using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Berwick.Core;

/// <summary>
/// A text font: which character each glyph number shows. Text archives store 16-bit glyph numbers; a number with the top bit
/// set is a control code (0x8000 | operation &lt;&lt; 8 | argument count) followed by its argument words. Code 0x8300 starts a new
/// line. In text form a line break is a new line and any other code is written {XX} or {XX:a,b}, XX its high byte in hex.
/// The English translation draws a space with ㌔ and reuses three kana for É, ö and è; those apply only to the English disc.
/// </summary>
public sealed partial class TextFont
{
    private static readonly Encoding Sjis = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    private static readonly (string Stored, string Shown)[] EnglishMarks = [("㌔", " "), ("ャ", "É"), ("ァ", "ö"), ("ズ", "è")];
    public const int LineBreak = 0x8300;
    private readonly string?[] glyphs;
    private readonly Dictionary<string, int> lookup = new(StringComparer.Ordinal);
    private readonly List<string> longest;

    public bool English { get; }
    public int Count => glyphs.Length;

    private TextFont(ushort[] codes, bool english)
    {
        English = english;
        glyphs = new string?[codes.Length];
        Span<byte> pair = stackalloc byte[2];
        for (int g = 0; g < codes.Length; g++)
        {
            if (codes[g] == 0) continue;
            pair[0] = (byte)(codes[g] >> 8); pair[1] = (byte)codes[g];
            glyphs[g] = Shown(Sjis.GetString(pair));
        }
        for (int g = 0; g < glyphs.Length; g++) if (glyphs[g] is { Length: > 0 } shown && shown != "�") lookup.TryAdd(shown, g);
        longest = lookup.Keys.Where(k => k.Length > 1).OrderByDescending(k => k.Length).ToList();
    }

    /// <summary>A font from an archive's own sjistab.dat: one Shift-JIS code per glyph number.</summary>
    public static TextFont FromGlyphTable(ReadOnlySpan<byte> sjistab, bool english)
    {
        var codes = new ushort[sjistab.Length / 2];
        for (int g = 0; g < codes.Length; g++) codes[g] = BinaryPrimitives.ReadUInt16LittleEndian(sjistab[(g * 2)..]);
        return new TextFont(codes, english);
    }

    /// <summary>A font from the shared sjis.tbl (msg/commfont.ar): the glyph number of each Shift-JIS code, 189 per lead byte
    /// (trail bytes 0x40 to 0xFC) for lead bytes 0x81 to 0x9F and 0xE0 to 0xEF.</summary>
    public static TextFont FromCodeTable(ReadOnlySpan<byte> table, bool english)
    {
        int count = table.Length / 2;
        var leads = Enumerable.Range(0x81, 0x1F).Concat(Enumerable.Range(0xE0, 0x10)).ToArray();
        int most = 0;
        for (int i = 0; i < count; i++) most = Math.Max(most, BinaryPrimitives.ReadUInt16LittleEndian(table[(i * 2)..]));
        var codes = new ushort[most + 1];
        for (int i = 0; i < count && i / 189 < leads.Length; i++)
        {
            int g = BinaryPrimitives.ReadUInt16LittleEndian(table[(i * 2)..]);
            if (g < codes.Length && codes[g] == 0) codes[g] = (ushort)(leads[i / 189] << 8 | 0x40 + i % 189);
        }
        return new TextFont(codes, english);
    }

    private string Shown(string stored)
    {
        if (English) foreach (var (s, shown) in EnglishMarks) if (stored == s) return shown;
        // Full-width letters and marks read as ordinary ones; a glyph that would become several characters (such as …) stays as
        // it is, so every glyph is one character and text always reads back to the same glyphs.
        string normal = stored.Normalize(NormalizationForm.FormKC);
        return normal.Length == 1 ? normal : stored;
    }

    public string Decode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        int words = data.Length / 2;
        for (int i = 0; i < words;)
        {
            int g = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            i++;
            if (g >= 0x8000)
            {
                if (g == LineBreak) { text.Append('\n'); continue; }
                int argc = g & 0xFF;
                var args = new List<int>();
                for (int k = 0; k < argc && i < words; k++, i++) args.Add(BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]));
                text.Append('{').Append((g >> 8).ToString("X2"));
                if (args.Count > 0) text.Append(':').Append(string.Join(",", args));
                text.Append('}');
                continue;
            }
            text.Append(g < glyphs.Length && glyphs[g] is { } shown ? shown : "�");
        }
        return text.ToString();
    }

    [GeneratedRegex(@"\G\{([0-9A-Fa-f]{2})(?::(\d{1,5}(?:,\d{1,5})*))?\}")]
    private static partial Regex Code();

    private sealed record Piece(string? Character, int[]? Words);

    private List<Piece> Split(string text)
    {
        var parts = new List<Piece>();
        text = text.Replace("\r\n", "\n");
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '\n') { parts.Add(new(null, [LineBreak])); i++; continue; }
            if (text[i] == '{' && Code().Match(text, i) is { Success: true } m)
            {
                int high = Convert.ToInt32(m.Groups[1].Value, 16);
                var args = m.Groups[2].Success ? m.Groups[2].Value.Split(',').Select(int.Parse).ToArray() : [];
                if (high < 0x80 || args.Length > 0xFF || args.Any(a => a > 0xFFFF)) throw new EditException($"{m.Value} is not a code the game uses.");
                parts.Add(new(null, [high << 8 | args.Length, .. args]));
                i += m.Length; continue;
            }
            string? multi = longest.FirstOrDefault(k => string.CompareOrdinal(text, i, k, 0, k.Length) == 0);
            if (multi is not null) { parts.Add(new(multi, null)); i += multi.Length; continue; }
            int length = char.IsSurrogatePair(text, i) ? 2 : 1;
            parts.Add(new(text.Substring(i, length), null));
            i += length;
        }
        return parts;
    }

    /// <summary>The characters of the text this font has no glyph for.</summary>
    public IReadOnlyList<string> Missing(string text)
    {
        try { return Split(text).Where(p => p.Character is { } c && !lookup.ContainsKey(c)).Select(p => p.Character!).Distinct().ToList(); }
        catch (EditException) { return ["{"]; }
    }

    /// <summary>Glyph bytes for the text. Codes are allowed only where the archive uses them.</summary>
    public byte[] Encode(string text, bool codes)
    {
        var parts = Split(text);
        var missing = parts.Where(p => p.Character is { } c && !lookup.ContainsKey(c)).Select(p => p.Character!).Distinct().ToList();
        if (missing.Count > 0) throw new EditException($"The game's font has no {string.Join(" ", missing.Select(m => m == " " ? "space" : m))}. Use other characters.");
        if (!codes && parts.Any(p => p.Words is not null)) throw new EditException("This text cannot have line breaks or codes.");
        var words = parts.SelectMany(p => p.Words ?? [lookup[p.Character!]]).ToList();
        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)words[i]);
        return bytes;
    }
}

/// <summary>A text archive: sysfont.ar sits inside a top-level archive; the msg archives are top-level files.</summary>
public sealed record TextArchive(string Path, bool Nested, bool SharedFont, bool Codes, string Kind);

/// <summary>One text: its archive, its entry name and key (the entry name for sysfont.ar, otherwise archive:entry).</summary>
public sealed record TextEntry(TextArchive Archive, string Name, byte[] Bytes)
{
    public string Key => Archive.Nested ? Name : $"{Archive.Path}:{Name}";
}

/// <summary>
/// The game's texts that use a Shift-JIS font table: sysfont.ar (names, explanations, quest titles, status words; 4,164
/// entries, its own table, no codes), msg/mission.ar (mission and quest texts) and msg/evitem.ar (event items), each with its
/// own table, and msg/guidemsg.ar and the dialogue archives msg/evmNNNN.ar, which use the shared table sjis.tbl of
/// msg/commfont.ar. Help and hint texts (helpmsg.ar, hintNNN.ar) use fonts without a table and are not included.
/// </summary>
public static class GameTexts
{
    public static readonly TextArchive SystemArchive = new("sysfont.ar", true, false, false, "");
    private static readonly HashSet<string> NotText = new(StringComparer.OrdinalIgnoreCase) { "font.ttx", "font0.ttx", "font1.ttx", "fontsize.dat", "fontinfo.dat", "sjistab.dat", "kerning.dat", "sjis.tbl" };
    public const int MostCharacters = 2000;

    /// <summary>The top-level archives that hold sysfont.ar.</summary>
    public static IReadOnlyList<int> SystemCopies(GameArchives archives) =>
        Enumerable.Range(0, archives.Count).Where(i => TopNames.Read(archives, i).Contains(SystemArchive.Path)).ToList();

    private static ArchiveEntry Entry(byte[] archive, string name) =>
        Tarc.Parse(archive).Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The msg archives with texts, by path, in the order they are listed.</summary>
    public static IReadOnlyList<(TextArchive Archive, int Top)> MessageArchives(GameArchives archives)
    {
        var tops = archives.FolderFiles("msg").ToDictionary(f => f.Hash, f => f.Top);
        int? TopOf(string name) => tops.TryGetValue(GameArchives.NameHash(name) & 0x07FFFFFF, out int t) ? t : null;
        var result = new List<(TextArchive, int)>();
        foreach (var (name, shared, kind) in new[] { ("mission.ar", false, "Mission and quest texts"), ("evitem.ar", false, "Event item texts"), ("guidemsg.ar", true, "Guide messages") })
            if (TopOf(name) is int top) result.Add((new TextArchive("msg/" + name, false, shared, true, kind), top));
        for (int n = 0; n < 10000; n++)
            if (TopOf($"evm{n:D4}.ar") is int top) result.Add((new TextArchive($"msg/evm{n:D4}.ar", false, true, true, "Dialogue"), top));
        return result;
    }

    /// <summary>Every text and each archive's font.</summary>
    public static (IReadOnlyList<TextEntry> Texts, IReadOnlyDictionary<string, TextFont> Fonts) Load(GameArchives archives, bool english)
    {
        var texts = new List<TextEntry>();
        var fonts = new Dictionary<string, TextFont>(StringComparer.Ordinal);
        foreach (int top in SystemCopies(archives))
        {
            try
            {
                var data = archives.Top(top);
                var archive = Tarc.Extract(data, Entry(data, SystemArchive.Path));
                var entries = Tarc.Parse(archive);
                fonts[SystemArchive.Path] = TextFont.FromGlyphTable(Tarc.Extract(archive, entries.Single(e => e.Name == "sjistab.dat")), english);
                texts.AddRange(entries.Where(e => !NotText.Contains(e.Name)).Select(e => new TextEntry(SystemArchive, e.Name, Tarc.Extract(archive, e))));
                break;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
        }
        var messages = MessageArchives(archives);
        TextFont? shared = null;
        int commfont = archives.Find("msg/commfont.ar");
        if (commfont >= 0)
        {
            var data = archives.Top(commfont);
            if (Tarc.Parse(data).FirstOrDefault(e => e.Name == "sjis.tbl") is { } table) shared = TextFont.FromCodeTable(Tarc.Extract(data, table), english);
        }
        foreach (var (archive, top) in messages)
        {
            byte[] data;
            IReadOnlyList<ArchiveEntry> entries;
            try { data = archives.Top(top); entries = Tarc.Parse(data); } catch (InvalidDataException) { continue; }
            TextFont? font = archive.SharedFont ? shared : entries.FirstOrDefault(e => e.Name == "sjistab.dat") is { } own ? TextFont.FromGlyphTable(Tarc.Extract(data, own), english) : null;
            if (font is null) continue;
            fonts[archive.Path] = font;
            texts.AddRange(entries.Where(e => !NotText.Contains(e.Name)).Select(e => new TextEntry(archive, e.Name, Tarc.Extract(data, e))));
        }
        return (texts, fonts);
    }

    /// <summary>What a text is, from its archive and entry name.</summary>
    public static string Kind(TextEntry text)
    {
        if (!text.Archive.Nested) return text.Archive.Kind;
        string stem = new(text.Name.TakeWhile(c => !char.IsDigit(c)).ToArray());
        return stem switch
        {
            "it" => "Item names",
            "cl" => "Class names",
            "un" => "Unit names",
            "skill" => "Skill names",
            "skill_exp" or "skill_exp_opt" => "Skill explanations",
            "foods" => "Dish names",
            "food_eff_" or "food_eff_hp" => "Meal effects",
            "evitem" => "Event items",
            "quest" => "Quest titles",
            "quest_s" => "Quest summaries",
            "mission" => "Mission titles",
            "chap_title__" or "chap__" or "chap_no__" => "Chapters",
            "map_no__" => "Maps",
            "town_facility" or "town_area" => "Town",
            "move_type_" => "Movement types",
            "st" => "Status and menu words",
            _ => "Other texts",
        };
    }

    /// <summary>Disc edits for changed texts (by key): every copy of sysfont.ar, and each msg archive in place.</summary>
    public static IReadOnlyList<(int Top, Func<byte[], byte[]> Edit)> Edits(GameArchives archives, IReadOnlyDictionary<string, byte[]> changed)
    {
        var edits = new List<(int, Func<byte[], byte[]>)>();
        var system = changed.Where(c => !c.Key.Contains(':')).ToDictionary(c => c.Key, c => c.Value);
        if (system.Count > 0)
            foreach (int top in SystemCopies(archives))
                edits.Add((top, data =>
                {
                    var archive = Tarc.Extract(data, Entry(data, SystemArchive.Path));
                    foreach (var (name, bytes) in system.OrderBy(t => t.Key, StringComparer.Ordinal))
                    {
                        if (!Tarc.Parse(archive).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new EditException($"This disc has no text named {name}.");
                        archive = ArchiveEditing.ReplaceOrRepack(archive, name, bytes);
                    }
                    return ArchiveEditing.ReplaceOrRepack(data, SystemArchive.Path, archive);
                }));
        foreach (var group in changed.Where(c => c.Key.Contains(':')).GroupBy(c => c.Key[..c.Key.IndexOf(':')]))
        {
            int top = archives.Find(group.Key);
            if (top < 0) throw new EditException($"This disc has no {group.Key}.");
            var items = group.Select(c => (Name: c.Key[(c.Key.IndexOf(':') + 1)..], c.Value)).ToList();
            edits.Add((top, data =>
            {
                foreach (var (name, bytes) in items.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (!Tarc.Parse(data).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new EditException($"This disc has no text {group.Key}:{name}.");
                    data = ArchiveEditing.ReplaceOrRepack(data, name, bytes);
                }
                return data;
            }));
        }
        return edits;
    }
}

/// <summary>The disc's texts and fonts, read when first needed.</summary>
public sealed class TextRecords(string discPath, bool english)
{
    private readonly object gate = new();
    private List<TextEntry>? texts;
    private Dictionary<string, int>? index;
    private IReadOnlyDictionary<string, TextFont>? fonts;

    private void Load()
    {
        if (texts is not null) return;
        using var stream = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var loaded = GameTexts.Load(new GameArchives(new IsoImage(stream)), english);
        texts = loaded.Texts.ToList();
        fonts = loaded.Fonts;
        index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < texts.Count; i++) index.TryAdd(texts[i].Key, i);
    }

    public IReadOnlyList<TextEntry> Texts { get { lock (gate) { Load(); return texts!; } } }
    public int? IdOf(string key) { lock (gate) { Load(); return index!.TryGetValue(key, out int at) ? at : null; } }
    public TextFont? FontOf(TextEntry text) { lock (gate) { Load(); return fonts!.TryGetValue(text.Archive.Path, out var f) ? f : null; } }
}
