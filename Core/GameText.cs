using System.Buffers.Binary;
using System.Text;

namespace Berwick.Core;

/// <summary>
/// A text font: the archive's own glyph table (sjistab.dat, one Shift-JIS code per glyph number). Text is stored as 16-bit
/// glyph numbers with no control codes. The English translation's font draws a space with glyph 80 (stored as ㌔) and reuses
/// three kana for É, ö and è; those only apply when the font's first glyphs are the Latin capitals.
/// </summary>
public sealed class TextFont
{
    private static readonly Encoding Sjis = CodePagesEncodingProvider.Instance.GetEncoding(932)!;
    private static readonly (string Stored, string Shown)[] EnglishMarks = [("㌔", " "), ("ャ", "É"), ("ァ", "ö"), ("ズ", "è")];
    private readonly string[] glyphs;
    private readonly Dictionary<string, int> lookup = new(StringComparer.Ordinal);

    public bool English { get; }
    public int Count => glyphs.Length;

    public TextFont(ReadOnlySpan<byte> sjistab)
    {
        glyphs = new string[sjistab.Length / 2];
        Span<byte> pair = stackalloc byte[2];
        for (int g = 0; g < glyphs.Length; g++)
        {
            ushort code = BinaryPrimitives.ReadUInt16LittleEndian(sjistab[(g * 2)..]);
            pair[0] = (byte)(code >> 8); pair[1] = (byte)code;
            glyphs[g] = Sjis.GetString(pair);
        }
        English = glyphs.Length > 26 && glyphs[0] == "Ａ" && glyphs[25] == "Ｚ";
        for (int g = 0; g < glyphs.Length; g++) lookup.TryAdd(Shown(glyphs[g]), g);
    }

    private string Shown(string stored)
    {
        if (English) foreach (var (s, shown) in EnglishMarks) if (stored == s) return shown;
        return stored.Normalize(NormalizationForm.FormKC);
    }

    public string Decode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder();
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int g = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            text.Append(g < glyphs.Length ? Shown(glyphs[g]) : "�");
        }
        return text.ToString();
    }

    /// <summary>The characters of the text this font has no glyph for.</summary>
    public IReadOnlyList<string> Missing(string text) => Split(text).Where(c => !lookup.ContainsKey(c)).Distinct().ToList();

    public byte[] Encode(string text)
    {
        var parts = Split(text);
        var missing = parts.Where(c => !lookup.ContainsKey(c)).Distinct().ToList();
        if (missing.Count > 0) throw new EditException($"The game's font has no {string.Join(" ", missing.Select(m => m == " " ? "space" : m))}. Use other characters.");
        var bytes = new byte[parts.Count * 2];
        for (int i = 0; i < parts.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)lookup[parts[i]]);
        return bytes;
    }

    // Characters as the font sees them: each glyph shows as one character, except a few that normalise to more (such as
    // Japanese square symbols); those are matched first.
    private List<string> Split(string text)
    {
        var parts = new List<string>();
        var longest = lookup.Keys.Where(k => k.Length > 1).OrderByDescending(k => k.Length).ToList();
        for (int i = 0; i < text.Length;)
        {
            string? multi = longest.FirstOrDefault(k => string.CompareOrdinal(text, i, k, 0, k.Length) == 0);
            if (multi is not null) { parts.Add(multi); i += multi.Length; continue; }
            int length = char.IsSurrogatePair(text, i) ? 2 : 1;
            parts.Add(text.Substring(i, length));
            i += length;
        }
        return parts;
    }
}

/// <summary>
/// The game's short texts in sysfont.ar: names of items, classes, units, skills and dishes, status and menu words, town
/// facility texts, skill explanations, quest titles and summaries, chapter and map titles and more (4,164 entries, the same
/// names on both discs, with sjistab.dat as their font). Every copy of the archive is changed together on export.
/// </summary>
public static class SystemText
{
    public const string ArchiveName = "sysfont.ar";
    private static readonly HashSet<string> NotText = new(StringComparer.OrdinalIgnoreCase) { "font.ttx", "fontsize.dat", "fontinfo.dat", "sjistab.dat" };

    /// <summary>The longest text accepted, in characters.</summary>
    public const int MostCharacters = 400;

    public static IReadOnlyList<int> Locate(GameArchives archives) =>
        Enumerable.Range(0, archives.Count).Where(i => TopNames.Read(archives, i).Contains(ArchiveName)).ToList();

    private static ArchiveEntry Entry(byte[] archive, string name) =>
        Tarc.Parse(archive).Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The texts (in the archive's order) and their font, from the first copy on the disc.</summary>
    public static (IReadOnlyList<(string Name, byte[] Bytes)> Texts, TextFont Font)? Load(GameArchives archives)
    {
        foreach (int top in Locate(archives))
        {
            try
            {
                var data = archives.Top(top);
                var archive = Tarc.Extract(data, Entry(data, ArchiveName));
                var entries = Tarc.Parse(archive);
                var font = new TextFont(Tarc.Extract(archive, entries.Single(e => e.Name == "sjistab.dat")));
                var texts = entries.Where(e => !NotText.Contains(e.Name)).Select(e => (e.Name, Tarc.Extract(archive, e))).ToList();
                return (texts, font);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { }
        }
        return null;
    }

    /// <summary>What a text entry is, from its name.</summary>
    public static string Kind(string name)
    {
        string stem = new(name.TakeWhile(c => !char.IsDigit(c)).ToArray());
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

    /// <summary>Edits for every copy of the archive: each changed text replaced (repacking the archive when it grows).</summary>
    public static IReadOnlyList<(int Top, Func<byte[], byte[]> Edit)> Edits(GameArchives archives, IReadOnlyDictionary<string, byte[]> texts) =>
        Locate(archives).Select(top => (top, (Func<byte[], byte[]>)(data =>
        {
            var archive = Tarc.Extract(data, Entry(data, ArchiveName));
            foreach (var (name, bytes) in texts.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (!Tarc.Parse(archive).Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new EditException($"This disc has no text named {name}.");
                archive = ArchiveEditing.ReplaceOrRepack(archive, name, bytes);
            }
            return ArchiveEditing.ReplaceOrRepack(data, ArchiveName, archive);
        }))).ToList();
}

/// <summary>The disc's texts and font, read when first needed.</summary>
public sealed class SystemTextRecords(string discPath)
{
    private readonly object gate = new();
    private Dictionary<string, byte[]>? texts;
    private TextFont? font;

    private void Load()
    {
        if (texts is not null) return;
        using var stream = new FileStream(discPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var loaded = SystemText.Load(new GameArchives(new IsoImage(stream)));
        texts = loaded?.Texts.ToDictionary(t => t.Name, t => t.Bytes, StringComparer.Ordinal) ?? [];
        font = loaded?.Font;
    }

    public byte[]? Bytes(string name) { lock (gate) { Load(); return texts!.TryGetValue(name, out var b) ? b : null; } }
    public TextFont? Font { get { lock (gate) { Load(); return font; } } }
    public IReadOnlyList<string> Names { get { lock (gate) { Load(); return texts!.Keys.ToList(); } } }
}
