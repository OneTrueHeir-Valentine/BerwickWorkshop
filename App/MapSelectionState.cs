using Berwick.Core;

namespace Berwick.Workshop;

internal sealed class MapSelectionState
{
    private (int X, int Y, int Turn)? lastClick;
    private int generation;
    public int? UnitKey { get; set; }
    public MapDisplayedUnit? SelectedUnit { get; private set; }
    public string Where { get; set; } = "";
    public Reinforcement? Arrival { get; set; }
    public bool PickingArrival { get; set; }
    public (int Group, int Index)? MovingUnit { get; set; }
    public void Select(MapDisplayedUnit unit)
    {
        SelectedUnit = unit;
        UnitKey = unit.Key;
        Arrival = unit.Arrival;
    }

    public bool IsSelected(MapDisplayedUnit unit) =>
        SelectedUnit is { } current &&
        current.Key == unit.Key && current.X == unit.X && current.Y == unit.Y &&
        Equals(current.Arrival, unit.Arrival);

    public void MoveSelectedUnit(int x, int y)
    {
        if (SelectedUnit is { Arrival: null } unit)
            SelectedUnit = unit with { X = x, Y = y };
    }

    public void ChangeSelectedArrival(Reinforcement arrival)
    {
        Arrival = arrival;
        if (SelectedUnit is { Arrival: not null } unit)
            SelectedUnit = unit with { X = arrival.X, Y = arrival.Y, Arrival = arrival };
    }

    public void RefreshSelection(IReadOnlyList<MapDisplayedUnit> units)
    {
        if (SelectedUnit is not { } old) return;
        var candidates = units.Where(unit => unit.Key == old.Key &&
            (unit.Arrival is null) == (old.Arrival is null)).ToList();

        var current = candidates.FirstOrDefault(unit => IsSelected(unit));
        if (current is null && old.Arrival is { } previous)
            current = candidates.FirstOrDefault(unit => unit.Arrival is { } arrival &&
                arrival.Places == previous.Places && arrival.TurnPlace == previous.TurnPlace &&
                arrival.Army == previous.Army && arrival.Script == previous.Script);
        current ??= candidates.FirstOrDefault();
        if (current is null)
        {
            SelectedUnit = null;
            UnitKey = null;
            Arrival = null;
            Where = "";
            return;
        }

        if ((current.X, current.Y) != (old.X, old.Y)) Where = current.Text;
        Select(current);
    }

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
        SelectedUnit = null;
        Where = "";
        Arrival = null;
        PickingArrival = false;
        MovingUnit = null;
        lastClick = null;
    }
}
