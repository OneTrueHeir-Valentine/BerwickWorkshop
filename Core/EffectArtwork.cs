using System.Text.RegularExpressions;
namespace Berwick.Core;

public sealed partial class AnimationSet
{
    public IReadOnlyList<EffectTrack> EffectTracks(string motion)
    {
        if (!Motions.Contains(motion)) throw new EditException("This motion is not in the animation set.");
        var result = new List<EffectTrack>();
        foreach (var file in files.Where(f => f.Key.StartsWith(motion + "_", StringComparison.Ordinal)))
        {
            var match = Regex.Match(file.Key, @"\Am[0-9]+_[0-9]+_([0-9]+)t\.dat\z");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int number)) continue;
            string prefix = $"{motion}_{number}";
            var track = EffectTrack.Decode(motion, number, files.GetValueOrDefault(prefix + "a.dat") ?? [], file.Value, files.GetValueOrDefault(prefix + "k.dat"));
            if (track.Records.Any(r => r.Visible && r.Texture == 1 && r.Picture > 0)) result.Add(track);
        }
        return result.OrderBy(t => t.Number).ToList();
    }
    public string MotionHeaderHex(string motion) => files.TryGetValue(motion + ".anm", out var header) ? Convert.ToHexString(header) : "";
    public (string Name, byte[] Bytes) EffectPalette(int palette)
    {
        string name = files.ContainsKey($"eff{palette}.pal") ? $"eff{palette}.pal" : "eff0.pal";
        if (!files.TryGetValue(name, out var bytes) || bytes.Length != 1024)
            throw new EditException("The effect palette is missing or incomplete.");
        return (name, bytes.ToArray());
    }
    private IEnumerable<(int X, int Y, int U, int V)> EffectTiles(int picture)
    {
        if (!files.TryGetValue("vueffpos.dat", out var positions) || !files.TryGetValue("1.tex", out var texture))
            throw new EditException("The effect artwork is missing from this set.");
        foreach (var tile in Tiles(positions, picture))
        {
            if (tile.U < 0 || tile.V < 0 || (long)tile.U + 8 > TextureWidth || (long)tile.V + 8 > texture.Length / TextureWidth) continue;
            bool used = false;
            for (int i = 0; i < 64 && !used; i++) used = texture[(tile.V + i / 8) * TextureWidth + tile.U + i % 8] != 0;
            if (used) yield return tile;
        }
    }
    public AnimationRect EffectCanvas(EffectTrack track) => AnimationCanvas.Expand(track.Records.Where(r => r.Visible && r.Texture == 1 && r.Picture > 0)
        .Select(r => r.Picture).Distinct().SelectMany(EffectTiles).Select(t => new AnimationRect(t.X, t.Y, 8, 8)));

    public AnimationImage DrawEffect(int picture, int palette, AnimationRect canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0 || canvas.Width > AnimationExport.SheetLimit || canvas.Height > AnimationExport.SheetLimit)
            throw new EditException("The effect canvas is too large.");
        var (_, raw) = EffectPalette(palette); var colors = ReadPalette(raw);
        var pixels = new byte[checked(canvas.Width * canvas.Height * 4)];
        var texture = files.GetValueOrDefault("1.tex") ?? throw new EditException("The effect texture is missing.");
        foreach (var (x, y, u, v) in EffectTiles(picture))
            for (int i = 0; i < 64; i++)
            {
                int index = texture[(v + i / 8) * TextureWidth + u + i % 8];
                int storedIndex = (index & 0x18) == 8 ? index + 8 : (index & 0x18) == 16 ? index - 8 : index;
                if (index == 0 || raw[storedIndex * 4 + 3] == 0) continue;
                long px = (long)x + i % 8 - canvas.X, py = (long)y + i / 8 - canvas.Y;
                if (px < 0 || py < 0 || px >= canvas.Width || py >= canvas.Height) continue;
                int at = (int)(py * canvas.Width + px) * 4; uint color = colors[index];
                pixels[at] = (byte)(color >> 16); pixels[at + 1] = (byte)(color >> 8); pixels[at + 2] = (byte)color; pixels[at + 3] = 255;
            }
        return new(canvas.Width, canvas.Height, pixels) { OriginX = canvas.X, OriginY = canvas.Y };
    }
}
