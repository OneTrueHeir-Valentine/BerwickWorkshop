using System.Buffers.Binary;

namespace Berwick.Core;

/// <summary>A spot on a battle map that adds a command to the menu of a unit on it (or, for doors and gates, next to it).</summary>
public sealed record MapSpot(int Kind, string Name, int X, int Y, int Unit, int Event);

/// <summary>
/// A battle's map: the terrain kind of each hex (64 by 64, the outer rows and columns are the border) and its spots.
/// Columns are offset: odd columns sit half a hex lower than even ones.
/// </summary>
public sealed record BattleMap(int Battle, string File, int Width, int Height, IReadOnlyList<int> Terrain, IReadOnlyList<MapSpot> Spots)
{
    /// <summary>The top-level archive the map was read from (it also holds the map's picture).</summary>
    public int Top { get; init; } = -1;

    /// <summary>The hexes where you place your units before the battle.</summary>
    public IReadOnlyList<(int X, int Y)> Deployment { get; init; } = [];

    /// <summary>
    /// Units the battle's opening events place from army files, one per placement slot. A group can have more slots than its
    /// army file has units; slots past the last unit stay empty in the game.
    /// </summary>
    public IReadOnlyList<MapUnit> Units { get; init; } = [];

    /// <summary>Units the battle's events bring in after it opens (see <see cref="EventLibrary.Reinforcements"/>).</summary>
    public IReadOnlyList<Reinforcement> Arrivals { get; init; } = [];

    public int KindAt(int x, int y) => x < 0 || y < 0 || x >= BattleMaps.Side || y >= BattleMaps.Side ? BattleMaps.Border : Terrain[y * BattleMaps.Side + x];
}

/// <summary>
/// A unit placed when a battle starts: unit Index of army file udNNN (Army), on the given team (0 your army, 1 the enemy,
/// 2 and 3 other armies), at hex X, Y.
/// </summary>
public sealed record MapUnit(int Team, int Army, int Index, int X, int Y);

/// <summary>A map's painted picture as 32-bit BGRA pixels, top row first.</summary>
public sealed record MapPicture(int Width, int Height, byte[] Pixels);

public static class BattleMaps
{
    // The picture is painted to the grid: each column is 40 pixels wide and each row 35 high, odd columns half a row lower,
    // starting at the picture's top left corner (matched against the hex lines the game draws). The border hexes are part
    // of the picture.
    public const int ColumnWidth = 40, RowHeight = 35;

    // The map file starts with 16 layers: a header (flag, then width and height of the layer's texture) and a 64 by 64 grid
    // of 16-bit cells. A cell with the top bit set takes tile (cell & 0x7FFF) of texture lNN.ttx (NN the layer number), tiles
    // being 40 by 35 pixels in rows across the texture, and covers the picture at column times 40, row times 35 (the tiles
    // are not staggered like the hexes). Layers hold moving water and changes such as a broken bridge; the game switches
    // them on and off from event scripts.
    private const int LayerGrid = 0x18, LayerStride = 0x2014, LayerCount = 16;

    // After the terrain grid come the placement groups: 16 groups of 64 slots, one byte of x per slot from 0x22144 and one of y
    // 0x400 later, 0xFF for no slot. Group 0 holds the hexes where you place your units; a battle's opening event places army
    // file udNNN with AppearUnitEx(file, team, group, ...), unit n of the file at slot n + 1 of the group. Slot 0 of every group
    // lies on the border and holds no unit. Checked against a running game on quest 6 (every unit of armies 371 and the
    // player's at its slot).
    private const int PlacementX = 0x22144, PlacementY = PlacementX + 0x400, Groups = 16, Slots = 64, NoSlot = 0xFF;

    private static (int X, int Y)? Slot(byte[] map, int group, int slot)
    {
        int at = group * Slots + slot;
        if (PlacementY + at >= map.Length || map[PlacementX + at] == NoSlot || map[PlacementY + at] == NoSlot) return null;
        return (map[PlacementX + at], map[PlacementY + at]);
    }

    // The map file ("map" in the battle's archive in folder bmap) holds the terrain grid at 0x20144: 64 rows of 64 16-bit values,
    // the terrain kind in the low 6 bits. Kind 63 is the border.
    public const int Side = 64, Border = 63;
    private const int GridOffset = 0x20144;

    /// <summary>
    /// The command a spot kind adds, as the game's menu names it (menu texts 565 to 615, chosen by the action menu at 0x0038CB54).
    /// Kinds 7 and 8 are opened from the next hex (Open, with Locktouch or a Door Key); a Chest needs Locktouch.
    /// </summary>
    public static string SpotName(int kind) => kind switch
    {
        2 => "Armory", 3 => "Shop", 4 => "Arena", 5 => "Search", 6 => "Seize", 7 => "Door or gate", 8 => "Gate", 9 => "Chest",
        13 => "Drawbridge", 14 => "Withdraw", 15 => "Inn", 18 => "Descend", 19 => "Ascend", 20 => "Synthesis guild",
        25 => "Sword and knife shop", 26 => "Spear and axe shop", 27 => "Bow and arrow shop", 28 => "Shield and item shop",
        31 => "Sundries", 32 => "Magic shop", 33 => "Investigate", 34 => "Material shop", 36 => "Weapon shop", 37 => "Item shop",
        38 => "Arrive", _ => "Event",
    };

    // Map files are named bmap (story), qmap (quests) and zmap (the title screen demo) with three digits.
    private static readonly string[] Prefixes = ["bmap", "qmap", "zmap"];

    /// <summary>
    /// The map files in folder bmap whose names follow the game's pattern, as (archive, name). Not every one is used:
    /// some belong to quests that have no event starting them.
    /// </summary>
    public static IReadOnlyList<(int Top, string File)> Files(GameArchives archives)
    {
        var names = new Dictionary<uint, string>();
        foreach (var prefix in Prefixes)
            for (int n = 0; n < 1000; n++)
            {
                string name = $"{prefix}{n:000}.ar";
                names.TryAdd(GameArchives.NameHash(name) & 0x07FFFFFF, name);
            }
        return archives.FolderFiles("bmap").Where(f => names.ContainsKey(f.Hash)).Select(f => (f.Top, names[f.Hash])).OrderBy(f => f.Item2).ToList();
    }

    /// <summary>A battle's map, or null when its map file is not on the disc or has no terrain grid.</summary>
    public static BattleMap? Load(GameArchives archives, EventLibrary events, int battle, string file)
    {
        int top = archives.Find("bmap/" + file);
        return top < 0 ? null : Load(archives, events, top, battle, file);
    }

    /// <summary>The map in a top-level archive. With battle 0 (a map no battle uses) it has no spots.</summary>
    public static BattleMap? Load(GameArchives archives, EventLibrary? events, int top, int battle, string file)
    {
        byte[] grid;
        try
        {
            var archive = archives.Top(top);
            var entry = Tarc.Parse(archive).FirstOrDefault(e => e.Name == "map");
            if (entry is null) return null;
            grid = Tarc.Extract(archive, entry);
        }
        catch (InvalidDataException) { return null; }
        if (grid.Length < GridOffset + Side * Side * 2) return null;
        var terrain = new int[Side * Side];
        int width = 0, height = 0;
        for (int i = 0; i < terrain.Length; i++)
        {
            terrain[i] = grid[GridOffset + i * 2] & 0x3F;
            int raw = grid[GridOffset + i * 2] | grid[GridOffset + i * 2 + 1] << 8;
            if (raw != 0) { width = Math.Max(width, i % Side + 1); height = Math.Max(height, i / Side + 1); }
        }
        if (width == 0) return null;
        var deployment = new List<(int, int)>();
        for (int slot = 1; slot < Slots; slot++) if (Slot(grid, 0, slot) is { } hex) deployment.Add(hex);
        if (battle == 0 || events is null) return new BattleMap(battle, file, width, height, terrain, []) { Top = top, Deployment = deployment };
        var units = new List<MapUnit>();
        foreach (var placement in events.StartingPlacements(battle))
            if (placement.Group is > 0 and < Groups)
                for (int slot = 1; slot < Slots; slot++)
                    if (Slot(grid, placement.Group, slot) is { } hex && hex.X < width && hex.Y < height)
                        units.Add(new MapUnit(placement.Team, placement.Army, slot - 1, hex.X, hex.Y));
        var spots = events.MapEvents()
            .Where(r => r.Battle == battle && r.Type == 1 && r.Args.Length >= 4 && r.Args[2] >= 0 && r.Args[2] < width && r.Args[3] >= 0 && r.Args[3] < height)
            .Select(r => new MapSpot(r.Args[0], SpotName(r.Args[0]), r.Args[2], r.Args[3], r.Args[1], r.Event))
            .DistinctBy(s => (s.Kind, s.X, s.Y, s.Unit)).OrderBy(s => s.Y).ThenBy(s => s.X).ToList();
        var arrivals = events.Reinforcements(battle).Where(r => r.X >= 0 && r.Y >= 0 && r.X < width && r.Y < height).ToList();
        return new BattleMap(battle, file, width, height, terrain, spots) { Top = top, Deployment = deployment, Units = units, Arrivals = arrivals };
    }

    /// <summary>
    /// The map's picture: textures named by page number (row times 8 plus column) of 256 by 256 pixels, put side by side,
    /// with the given layers drawn over it (see <see cref="EventLibrary.StartingLayers"/>). Null when the archive has no
    /// pages the reader understands.
    /// </summary>
    public static MapPicture? LoadPicture(GameArchives archives, int top, IEnumerable<int>? layers = null)
    {
        byte[] archive;
        IReadOnlyList<ArchiveEntry> entries;
        try { archive = archives.Top(top); entries = Tarc.Parse(archive); }
        catch (InvalidDataException) { return null; }
        var pages = new List<(int Number, Texture Texture)>();
        foreach (var e in entries)
        {
            if (e.Name.Length != 6 || !e.Name.EndsWith(".ttx", StringComparison.OrdinalIgnoreCase) || !int.TryParse(e.Name[..2], out int number)) continue;
            Texture? texture;
            try { texture = Texture.Decode(Tarc.Extract(archive, e)); } catch (InvalidDataException) { continue; }
            if (texture is { Width: Page, Height: Page }) pages.Add((number, texture));
        }
        if (pages.Count == 0) return null;
        int columns = pages.Max(p => p.Number % 8) + 1, rows = pages.Max(p => p.Number / 8) + 1;
        int width = columns * Page, height = rows * Page;
        var pixels = new byte[width * height * 4];
        foreach (var (number, texture) in pages)
        {
            int x0 = number % 8 * Page, y0 = number / 8 * Page;
            for (int y = 0; y < Page; y++)
                Buffer.BlockCopy(texture.Pixels, y * Page * 4, pixels, ((y0 + y) * width + x0) * 4, Page * 4);
        }
        var map = entries.FirstOrDefault(e => e.Name == "map");
        if (layers is not null && map is not null)
        {
            byte[] data;
            try { data = Tarc.Extract(archive, map); } catch (InvalidDataException) { data = []; }
            foreach (int layer in layers)
            {
                var entry = entries.FirstOrDefault(e => e.Name == $"l{layer:00}.ttx");
                if (entry is null || layer <= 0 || layer >= LayerCount) continue;
                Texture texture;
                try { texture = Texture.Decode(Tarc.Extract(archive, entry)); } catch (InvalidDataException) { continue; }
                DrawLayer(data, layer, texture, pixels, width, height);
            }
        }
        return new MapPicture(width, height, pixels);
    }

    private static void DrawLayer(byte[] map, int layer, Texture texture, byte[] pixels, int width, int height)
    {
        int grid = LayerGrid + layer * LayerStride;
        if (grid + BattleMaps.Side * BattleMaps.Side * 2 > map.Length) return;
        int across = BinaryPrimitives.ReadInt32LittleEndian(map.AsSpan(grid - 8)) / ColumnWidth;
        if (across <= 0) return;
        for (int cell = 0; cell < Side * Side; cell++)
        {
            int value = BinaryPrimitives.ReadUInt16LittleEndian(map.AsSpan(grid + cell * 2));
            if ((value & 0x8000) == 0) continue;
            int tile = value & 0x7FFF, sx = tile % across * ColumnWidth, sy = tile / across * RowHeight;
            int dx = cell % Side * ColumnWidth, dy = cell / Side * RowHeight;
            if (sx + ColumnWidth > texture.Width || sy + RowHeight > texture.Height) continue;
            for (int y = 0; y < RowHeight && dy + y < height; y++)
            {
                int count = Math.Min(ColumnWidth, width - dx);
                if (count <= 0) break;
                Buffer.BlockCopy(texture.Pixels, ((sy + y) * texture.Width + sx) * 4, pixels, ((dy + y) * width + dx) * 4, count * 4);
            }
        }
    }

    private const int Page = 256;
}

/// <summary>
/// A TTX0 texture: a 32-byte header (format, pixel byte count, width, height, palette byte count), the palette as
/// RGBA and one byte per pixel. Format 0x13 is 8 bits per pixel; its palette is stored in the console's order, where in
/// every 32 colors the second and third groups of 8 are swapped.
/// </summary>
public sealed record Texture(int Width, int Height, byte[] Pixels)
{
    public static Texture Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20 || !data[..4].SequenceEqual("TTX0"u8)) throw new InvalidDataException("Not a texture.");
        int format = BinaryPrimitives.ReadInt32LittleEndian(data[8..]), size = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[16..]), height = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        int palette = BinaryPrimitives.ReadInt32LittleEndian(data[28..]);
        if (format != 0x13 || palette != 1024 || width <= 0 || height <= 0 || width > 4096 || height > 4096 || size != width * height || data.Length < 0x20 + palette + size)
            throw new InvalidDataException("This texture format is not supported.");
        var colors = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            int stored = (i & 0x18) switch { 0x08 => i + 8, 0x10 => i - 8, _ => i };
            var c = data.Slice(0x20 + stored * 4, 4);
            colors[i] = 0xFF000000u | (uint)c[0] << 16 | (uint)c[1] << 8 | c[2];
        }
        var pixels = new byte[width * height * 4];
        var source = data.Slice(0x20 + palette, size);
        for (int i = 0; i < size; i++) BinaryPrimitives.WriteUInt32LittleEndian(pixels.AsSpan(i * 4), colors[source[i]]);
        return new Texture(width, height, pixels);
    }
}
