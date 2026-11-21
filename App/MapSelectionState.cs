using Berwick.Core;

namespace Berwick.Workshop;

internal sealed class MapSelectionState
{
    private (int X, int Y, int Turn)? lastClick;
    private int generation;
    public int? UnitKey { get; set; }
    public string Where { get; set; } = "";
    public Reinforcement? Arrival { get; set; }
    public bool PickingArrival { get; set; }
    public (int Group, int Index)? MovingUnit { get; set; }
    public int BeginLoad() => ++generation;
    public bool IsCurrent(int token) => token == generation;

    public int Cycle(int x, int y, int count)
    {
        if (count == 0) return -1;
        int turn = lastClick is { } last && last.X == x && last.Y == y ? (last.Turn + 1) % count : 0;
        lastClick = (x, y, turn);
        return turn;
    }

    public void Reset()
    {
        generation++;
        UnitKey = null;
        Where = "";
        Arrival = null;
        PickingArrival = false;
        MovingUnit = null;
        lastClick = null;
    }
}
