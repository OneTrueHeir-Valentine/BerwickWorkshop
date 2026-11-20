namespace Berwick.Core;

public static class AnimationCanvas
{
    public static AnimationRect Expand(IEnumerable<AnimationRect> tiles)
    {
        long left = 0, top = 0, right = AnimationSet.Canvas, bottom = AnimationSet.Canvas;
        foreach (var tile in tiles)
        {
            if (tile.Width <= 0 || tile.Height <= 0) throw new EditException("An animation tile has invalid dimensions.");
            left = Math.Min(left, tile.X); top = Math.Min(top, tile.Y);
            right = Math.Max(right, (long)tile.X + tile.Width); bottom = Math.Max(bottom, (long)tile.Y + tile.Height);
            if (right - left > AnimationExport.SheetLimit || bottom - top > AnimationExport.SheetLimit)
                throw new EditException("This motion exceeds the supported animation canvas size.");
        }
        return new((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }
}
