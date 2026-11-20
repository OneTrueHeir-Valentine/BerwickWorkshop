using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>One row of kirikae.db: who it is for (0 any unit, a unit number, or a side for palettes), what the unit holds
/// (key 0 anything, a negative weapon type, or a positive item number), a two-bit flag, and the set or palette it picks.</summary>
public sealed record AnimationEntry(int Unit, int Key, int Flag, int Value)
{
    /// <summary>Flag value 2 (stored as -2) means the row applies whatever the class bit says.</summary>
    public bool AnyFlag => Flag == -2;
}

/// <summary>kirikae/kirikae.db, which picks each class's battle animation set and palette. The header holds the class count
/// for each table, the size of the weapon change table and some lists; then come the set rows and the palette rows,
/// each class as a count followed by that many rows of three words (unit, key and flag, set or palette).</summary>
public sealed class AnimationTable
{
    public const string Path = "kirikae/kirikae.db";
    private const int HeaderWords = 9;
    private readonly List<AnimationEntry>[] sets, palettes;
    /// <summary>Byte offset of each set row's last word and each palette row's last word, for editing in place.</summary>
    private readonly List<int>[] setOffsets, paletteOffsets;

    private AnimationTable(List<AnimationEntry>[] sets, List<int>[] setOffsets, List<AnimationEntry>[] palettes, List<int>[] paletteOffsets)
    {
        this.sets = sets; this.setOffsets = setOffsets; this.palettes = palettes; this.paletteOffsets = paletteOffsets;
    }

    /// <summary>Number of classes the table covers. Rows are stored by class number minus one.</summary>
    public int ClassCount => sets.Length;

    public IReadOnlyList<AnimationEntry> Sets(int classId) => classId >= 1 && classId <= sets.Length ? sets[classId - 1] : [];
    public IReadOnlyList<AnimationEntry> Palettes(int classId) => classId >= 1 && classId <= palettes.Length ? palettes[classId - 1] : [];
    public int SetOffset(int classId, int row) => setOffsets[classId - 1][row];
    public int PaletteOffset(int classId, int row) => paletteOffsets[classId - 1][row];

    /// <summary>Every set number any row names.</summary>
    public IReadOnlySet<int> NamedSets => sets.SelectMany(s => s).Select(e => e.Value).ToHashSet();

    public static AnimationTable Parse(byte[] data)
    {
        int Word(int index)
        {
            if (index < 0 || (index + 1) * 4 > data.Length) throw new InvalidDataException("The animation table is shorter than its header says.");
            return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(index * 4));
        }
        int setClasses = Word(0), paletteClasses = Word(1);
        if (setClasses <= 0 || setClasses > 1000 || paletteClasses <= 0 || paletteClasses > 1000) throw new InvalidDataException("The animation table header is not valid.");
        int w = HeaderWords;
        (List<AnimationEntry>[] Rows, List<int>[] Offsets) Read(int classes)
        {
            var rows = new List<AnimationEntry>[classes];
            var offsets = new List<int>[classes];
            for (int c = 0; c < classes; c++)
            {
                int n = Word(w++);
                if (n < 0 || n > 200) throw new InvalidDataException("The animation table has a class with too many rows.");
                rows[c] = []; offsets[c] = [];
                for (int i = 0; i < n; i++, w += 3)
                {
                    int key = Word(w + 1);
                    rows[c].Add(new AnimationEntry(Word(w), key >> 2, ((key & 3) ^ 2) - 2, Word(w + 2)));
                    offsets[c].Add((w + 2) * 4);
                }
            }
            return (rows, offsets);
        }
        var (s, so) = Read(setClasses);
        var (p, po) = Read(paletteClasses);
        return new AnimationTable(s, so, p, po);
    }

    /// <summary>Every row as an editable record: key from <see cref="RowId"/>, the row's 12 bytes (unit, key word, set or palette)
    /// and where the row starts in the file.</summary>
    public static IReadOnlyList<(int Id, int Offset, byte[] Bytes)> Records(byte[] data)
    {
        var table = Parse(data);
        var result = new List<(int, int, byte[])>();
        for (int c = 1; c <= table.ClassCount; c++)
        {
            for (int r = 0; r < table.Sets(c).Count; r++) Add(false, c, r, table.SetOffset(c, r) - 8);
            for (int r = 0; r < table.Palettes(c).Count; r++) Add(true, c, r, table.PaletteOffset(c, r) - 8);
        }
        return result;
        void Add(bool palette, int c, int r, int at) => result.Add((RowId(palette, c, r), at, data.AsSpan(at, 12).ToArray()));
    }

    /// <summary>A record number for a row: palette rows have bit 24 set, then the class number and the row within the class.</summary>
    public static int RowId(bool palette, int classId, int row) => (palette ? 1 << 24 : 0) | classId << 8 | row;
    public static (bool Palette, int ClassId, int Row) SplitId(int id) => ((id & (1 << 24)) != 0, (id >> 8) & 0xFFFF, id & 0xFF);

    /// <summary>The value an edit changes: word 2 of a row, the set number or the palette number.</summary>
    public static TableField SetField { get; } = new("set", "Animation set", 8, 0, 32, 0, 999999) { Group = "Battle animation" };
    public static TableField PaletteField { get; } = new("palette", "Colours", 8, 0, 32, 0, 99) { Group = "Battle animation" };

    /// <summary>What a key means: anything, a weapon type, or one item.</summary>
    public static string KeyText(int key)
    {
        if (key == 0) return "Any weapon";
        if (key > 0) return GameNames.Item(key) is { } item ? item : $"Item {key}";
        int type = -key - 1;
        // The game maps item types to keys: Fork shares Spear's key, and every type after Lance moves down by one.
        int itemType = type <= 5 ? type : type + 1;
        string name = itemType < ItemDecoder.Types.Length ? ItemDecoder.Types[itemType] : $"Weapon type {itemType}";
        return type == 4 ? "Spear or Fork" : name;
    }

    /// <summary>Who a palette row is for when it does not name a unit.</summary>
    public static string SideText(int unit) => unit switch
    {
        0 => "Everyone else",
        -1 => "Player side",
        -2 => "Enemy side",
        -3 => "Allied side",
        -4 => "Fourth side",
        -5 => "Any side",
        _ => $"Side {-unit}",
    };
}

/// <summary>One frame of a motion: the picture shown and how many 1/60 seconds it stays.</summary>
public sealed record AnimationFrame(int Picture, int Time);

/// <summary>A drawn frame: 32-bit BGRA pixels, transparent where nothing is drawn.</summary>
public sealed record AnimationImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Original coordinate of the image's top-left pixel, before cropping or sheet placement.</summary>
    public int OriginX { get; init; }
    public int OriginY { get; init; }
}

/// <summary>A battle animation set, kirikae/buAAAWWV.ar: class art AAA, weapon WW, variant V (0 on foot, 1 mounted).
/// Each layer is an 8-bit texture 1024 pixels wide with a table of pictures; a picture is a list of 8 by 8 tiles.</summary>
public sealed partial class AnimationSet
{
    public const int Canvas = 288;
    private const int TextureWidth = 1024;
    private readonly Dictionary<string, byte[]> files;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AnimationRect> canvases = new();
    public int Number { get; }

    private AnimationSet(int number, Dictionary<string, byte[]> files) { Number = number; this.files = files; }

    public static string FileName(int number) => $"bu{number:D6}.ar";

    public static AnimationSet? Load(GameArchives archives, int number)
    {
        int top = archives.Find("kirikae/" + FileName(number));
        if (top < 0) return null;
        return FromArchive(number, archives.Top(top));
    }

    public static AnimationSet FromArchive(int number, byte[] archive)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Tarc.Parse(archive)) result[entry.Name] = Tarc.Extract(archive, entry);
        return new AnimationSet(number, result);
    }

    /// <summary>Every set file in the kirikae folder, by number. Names are found by hashing each possible number.</summary>
    public static IReadOnlyDictionary<int, int> OnDisc(GameArchives archives)
    {
        var hashes = archives.FolderFiles("kirikae").ToDictionary(f => f.Hash, f => f.Top);
        var result = new SortedDictionary<int, int>();
        for (int art = 0; art < 1000; art++)
            for (int weapon = 0; weapon <= 15; weapon++)
                for (int variant = 0; variant <= 9; variant++)
                {
                    int number = art * 1000 + weapon * 10 + variant;
                    if (hashes.TryGetValue(GameArchives.NameHash(FileName(number)) & 0x07FFFFFF, out int top)) result[number] = top;
                }
        return result;
    }

    /// <summary>The motions of every set, read from the archive's file names only (the set files are not read whole).</summary>
    public static IReadOnlyDictionary<int, IReadOnlySet<string>> MotionLists(GameArchives archives, IReadOnlyDictionary<int, int> onDisc)
    {
        var result = new Dictionary<int, IReadOnlySet<string>>();
        foreach (var (number, top) in onDisc)
        {
            var head = archives.TopRange(top, 0, 16);
            if (!Tarc.IsArchive(head)) { result[number] = new HashSet<string>(); continue; }
            int count = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4)), names = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(8));
            var list = archives.TopRange(top, names, Math.Clamp(count * 40, 64, 1 << 20));
            var motions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int p = 0;
            for (int i = 0; i < count && p < list.Length; i++)
            {
                int end = Array.IndexOf(list, (byte)0, p);
                if (end < 0) break;
                string name = System.Text.Encoding.ASCII.GetString(list, p, end - p);
                if (name.EndsWith("_0t.dat", StringComparison.OrdinalIgnoreCase) && name.Length > 7) motions.Add(name[..^7]);
                p = end + 1;
            }
            result[number] = motions;
        }
        return result;
    }

    /// <summary>Whether a set can stand in for another: it has every motion the other has. A set that lacks a motion the
    /// class plays stops the battle when that motion is called (checked in a running game).</summary>
    public static bool Covers(IReadOnlySet<string> candidate, IReadOnlySet<string> needed) => needed.All(candidate.Contains);

    /// <summary>Motions that have frames, in the game's order (group, then number).</summary>
    public IReadOnlyList<string> Motions => files.Keys.Where(n => n.EndsWith("_0t.dat", StringComparison.OrdinalIgnoreCase) && n.Length > 7)
        .Select(n => n[..^7]).OrderBy(MotionGroup).ThenBy(MotionNumber).ToList();

    /// <summary>The numbered palettes the set has (n.pal), besides its own colours.</summary>
    public IReadOnlyList<int> Palettes => files.Keys.Select(n => n.EndsWith(".pal", StringComparison.OrdinalIgnoreCase) && int.TryParse(n[..^4], out int v) ? v : -1)
        .Where(v => v >= 0).Order().ToList();

    public bool HasFrames => Motions.Count > 0;

    private static int MotionGroup(string m) => int.TryParse(m.AsSpan(1, m.IndexOf('_') - 1), out int a) ? a : 99;
    private static int MotionNumber(string m) => int.TryParse(m.AsSpan(m.IndexOf('_') + 1), out int b) ? b : 99;

    /// <summary>The frames of a motion in playing order. Each 60-byte record names the next record (counting from 1, 0 ends),
    /// the time it shows and the picture.</summary>
    public IReadOnlyList<AnimationFrame> Frames(string motion)
    {
        if (!files.TryGetValue(motion + "_0t.dat", out var t)) return [];
        var result = new List<AnimationFrame>();
        var seen = new HashSet<int>();
        int i = 0;
        while (i >= 0 && (i + 1) * 60 <= t.Length && seen.Add(i))
        {
            int at = i * 60;
            int picture = BinaryPrimitives.ReadUInt16LittleEndian(t.AsSpan(at + 14));
            if (t[at] == 0 || picture == 0) break;
            result.Add(new AnimationFrame(picture, Math.Max(1, (int)t[at + 3])));
            int next = t[at + 2];
            if (next == 0) break;
            i = next - 1;
        }
        return result;
    }

    /// <summary>Draws one picture of a motion. Body and shield use the chosen palette (0 for the set's own colours);
    /// the weapon keeps the set's colours. Layers are drawn in the order of the depth byte in each layer's header.</summary>
    public AnimationRect CanvasFor(string motion) => canvases.GetOrAdd(motion, key => AnimationCanvas.Expand(MotionTiles(key)));

    private IEnumerable<AnimationRect> MotionTiles(string motion)
    {
        foreach (int picture in Frames(motion).Select(f => f.Picture).Distinct())
            foreach (var (positions, texture) in new[] { ("vupos.dat", "0.tex"), ("vuwppos.dat", "2.tex"), ("vusdpos.dat", "3.tex") })
                if (files.TryGetValue(positions, out var p) && files.TryGetValue(texture, out var t))
                    foreach (var tile in RenderTiles(p, t, picture)) yield return new(tile.X, tile.Y, 8, 8);
    }

    private static IEnumerable<(int X, int Y, int U, int V)> RenderTiles(byte[] positions, byte[] texture, int picture)
    {
        int rows = texture.Length / TextureWidth;
        foreach (var tile in Tiles(positions, picture))
        {
            var (x, y, u, v) = tile;
            if (u < 0 || v < 0 || (long)u + 8 > TextureWidth || (long)v + 8 > rows) continue;
            // The first sixteen colours are frame markers, as in the original preview renderer.
            bool art = false;
            for (int i = 0; i < 64 && !art; i++) art = texture[(v + i / 8) * TextureWidth + u + i % 8] >= 0x10;
            if (art) yield return tile;
        }
    }

    public AnimationImage Draw(string motion, int picture, int palette, bool legacyCanvas = false)
    {
        var canvas = legacyCanvas ? new AnimationRect(0, 0, Canvas, Canvas) : CanvasFor(motion);
        var pixels = new byte[checked(canvas.Width * canvas.Height * 4)];
        var own = ReadPalette(files.GetValueOrDefault("common.pal"));
        var chosen = palette > 0 && files.TryGetValue($"{palette}.pal", out var p) ? ReadPalette(p) : own;
        var layers = new List<(int Depth, int Order, string Positions, string Texture, uint[] Colors)>();
        void Add(string header, string positions, string texture, uint[] colors, int order)
        {
            if (!files.ContainsKey(positions) || !files.ContainsKey(texture)) return;
            int depth = files.TryGetValue($"{motion}_0{header}.dat", out var h) && h.Length > 14 ? h[14] : order;
            layers.Add((depth, order, positions, texture, colors));
        }
        Add("a", "vupos.dat", "0.tex", chosen, 0);
        Add("wa", "vuwppos.dat", "2.tex", own, 1);
        Add("sa", "vusdpos.dat", "3.tex", chosen, 2);
        foreach (var layer in layers.OrderBy(l => l.Depth).ThenBy(l => l.Order))
            DrawPicture(files[layer.Positions], files[layer.Texture], picture, layer.Colors, pixels, canvas);
        return new AnimationImage(canvas.Width, canvas.Height, pixels) { OriginX = canvas.X, OriginY = canvas.Y };
    }

    /// <summary>The area every frame of a motion draws in, with a small margin, so a viewer can show the motion without empty canvas.</summary>
    public (int X, int Y, int Width, int Height) Bounds(string motion)
    {
        var canvas = CanvasFor(motion);
        int left = canvas.Width, top = canvas.Height, right = -1, bottom = -1;
        foreach (var frame in Frames(motion).DistinctBy(f => f.Picture))
        {
            var image = Draw(motion, frame.Picture, 0);
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (image.Pixels[(y * image.Width + x) * 4 + 3] != 0)
                    {
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
        }
        if (right < 0) return (0, 0, canvas.Width, canvas.Height);
        const int margin = 6;
        left = Math.Max(0, left - margin); top = Math.Max(0, top - margin);
        right = Math.Min(canvas.Width - 1, right + margin); bottom = Math.Min(canvas.Height - 1, bottom + margin);
        return (left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The tiles of one picture: position and the tile's top-left texel.</summary>
    public static IReadOnlyList<(int X, int Y, int U, int V)> Tiles(ReadOnlySpan<byte> positions, int picture)
    {
        var result = new List<(int, int, int, int)>();
        if (positions.Length < 4) return result;
        int count = BinaryPrimitives.ReadInt32LittleEndian(positions);
        if (picture < 0 || picture >= count || 8 + picture * 8 + 8 > positions.Length) return result;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(positions[(4 + picture * 8)..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(positions[(8 + picture * 8)..]);
        if (offset < 0 || size < 0 || offset + (long)size > positions.Length) return result;
        var chunk = positions.Slice(offset, size);
        // After a 16-byte count, each packet is an unpack header (top byte 0x6C), a tile count, a drawing tag and
        // 32 bytes per tile: X and Y as floats, a packed word, then u0, v0, u1, v1 in 1/16 texel. Other quadwords are skipped.
        int o = 16;
        while (o + 48 <= chunk.Length)
        {
            if (chunk[o + 15] != 0x6C) { o += 16; continue; }
            int n = BinaryPrimitives.ReadInt32LittleEndian(chunk[(o + 16)..]);
            if (n < 0 || o + 48 + n * 32L > chunk.Length) break;
            for (int k = 0; k < n; k++)
            {
                int r = o + 48 + k * 32;
                float x = BinaryPrimitives.ReadSingleLittleEndian(chunk[r..]), y = BinaryPrimitives.ReadSingleLittleEndian(chunk[(r + 4)..]);
                int u = BinaryPrimitives.ReadInt32LittleEndian(chunk[(r + 16)..]) / 16, v = BinaryPrimitives.ReadInt32LittleEndian(chunk[(r + 20)..]) / 16;
                result.Add(((int)x, (int)y, u, v));
            }
            o += 48 + n * 32;
        }
        return result;
    }

    private static void DrawPicture(byte[] positions, byte[] texture, int picture, uint[] colors, byte[] pixels, AnimationRect canvas)
    {
        foreach (var (x, y, u, v) in RenderTiles(positions, texture, picture))
            for (int i = 0; i < 64; i++)
            {
                byte index = texture[(v + i / 8) * TextureWidth + u + i % 8];
                if (index == 0) continue;
                long px = (long)x + i % 8 - canvas.X, py = (long)y + i / 8 - canvas.Y;
                if (px < 0 || py < 0 || px >= canvas.Width || py >= canvas.Height) continue;
                uint c = colors[index];
                int at = (int)(py * canvas.Width + px) * 4;
                pixels[at] = (byte)(c >> 16); pixels[at + 1] = (byte)(c >> 8); pixels[at + 2] = (byte)c; pixels[at + 3] = 255;
            }
    }

    /// <summary>A 256-colour palette as stored for the PS2: RGBA bytes, with entries 8 to 15 and 16 to 23 of each block of 32 swapped.</summary>
    public static uint[] ReadPalette(byte[]? data)
    {
        var raw = new uint[256];
        if (data is not null)
            for (int i = 0; i < 256 && i * 4 + 3 < data.Length; i++)
                raw[i] = (uint)data[i * 4] | (uint)data[i * 4 + 1] << 8 | (uint)data[i * 4 + 2] << 16;
        var result = (uint[])raw.Clone();
        for (int b = 0; b < 256; b += 32)
            for (int i = 0; i < 8; i++) { result[b + 8 + i] = raw[b + 16 + i]; result[b + 16 + i] = raw[b + 8 + i]; }
        return result;
    }
}
