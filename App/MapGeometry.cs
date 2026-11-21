using Berwick.Core;

namespace Berwick.Workshop;

internal readonly record struct MapGeometry(double Scale)
{
    public double ColumnWidth => BattleMaps.ColumnWidth * Scale;
    public double RowHeight => BattleMaps.RowHeight * Scale;
    public static MapGeometry ForSize(int index) => new(index switch { 0 => 0.5, 2 => 1.0, _ => 0.75 });
    public (double X, double Y) Center(int x, int y) =>
        (x * ColumnWidth + ColumnWidth / 2, y * RowHeight + RowHeight / 2 + ((x & 1) == 1 ? RowHeight / 2 : 0));
    public (double Width, double Height) Extent(int width, int height) => (width * ColumnWidth, (height + 0.5) * RowHeight);
    public (int X, int Y) HexAt(double px, double py)
    {
        int x = (int)Math.Floor(px / ColumnWidth);
        return (x, (int)Math.Floor((py - ((x & 1) == 1 ? RowHeight / 2 : 0)) / RowHeight));
    }
}
