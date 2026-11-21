using Berwick.Workshop;

internal static class MapPresentationTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Map geometry preserves scale, odd-column offset and canvas extent", () =>
        {
            foreach (var (size, width, height) in new[] { (0, 20.0, 17.5), (1, 30.0, 26.25), (2, 40.0, 35.0) })
            {
                var geometry = MapGeometry.ForSize(size);
                Check(geometry.ColumnWidth == width && geometry.RowHeight == height, "Map scale changed");
                Check(geometry.Center(0, 0) == (width / 2, height / 2), "Even-column center changed");
                Check(geometry.Center(1, 0) == (width * 1.5, height), "Odd-column offset changed");
                Check(geometry.Extent(12, 9) == (12 * width, 9.5 * height), "Canvas extent changed");
            }
        });
        test("Map hit testing retains floor boundaries at all sizes", () =>
        {
            for (int size = 0; size < 3; size++)
            {
                var g = MapGeometry.ForSize(size);
                for (int x = 0; x < 8; x++)
                    for (int y = 0; y < 8; y++)
                    {
                        var center = g.Center(x, y);
                        Check(g.HexAt(center.X, center.Y) == (x, y), "Center hit changed");
                    }
                Check(g.HexAt(-0.01, 0) == (-1, -1), "Negative coordinates were truncated");
                Check(g.HexAt(g.ColumnWidth, g.RowHeight / 2 - 0.01) == (1, -1), "Odd-column top boundary changed");
                Check(g.HexAt(g.ColumnWidth, g.RowHeight / 2) == (1, 0), "Odd-column boundary changed");
            }
        });
        test("Map selection cycles only on repeated populated hexes", () =>
        {
            var state = new MapSelectionState();
            Check(state.Cycle(2, 3, 3) == 0 && state.Cycle(2, 3, 3) == 1 && state.Cycle(2, 3, 3) == 2 && state.Cycle(2, 3, 3) == 0, "Selection cycle changed");
            Check(state.Cycle(7, 8, 2) == 0, "New hex inherited the previous cycle");
            Check(state.Cycle(0, 0, 0) == -1 && state.Cycle(7, 8, 2) == 1, "Empty hex changed selection history");
            Check(state.Cycle(7, 8, 1) == 0, "Changed stack size produced an invalid index");
        });
        test("Map selection identifies an exact starting unit and its hex", () =>
        {
            var state = new MapSelectionState();
            var start = new MapDisplayedUnit(41, "Unit", "Starts at 2, 3", 2, 3, null);
            var elsewhere = start with { X = 6 };
            state.Select(start);
            Check(state.UnitKey == 41 && state.IsSelected(start), "Selected starting unit was not retained");
            Check(!state.IsSelected(elsewhere), "Another position was marked as selected");

            state.MoveSelectedUnit(6, 3);
            Check(state.IsSelected(elsewhere) && !state.IsSelected(start), "Moved unit kept the old highlight");

            state.RefreshSelection([elsewhere with { Text = "Updated class" }]);
            Check(state.IsSelected(elsewhere), "Edited unit lost its selection identity");
            state.Reset();
            Check(state.SelectedUnit is null, "Reset retained the selected highlight");
        });
        test("Map selection distinguishes reinforcements from starting units", () =>
        {
            var arrival = new Berwick.Core.Reinforcement(5, 1, 1, 3, 4, 3, 4, 5, 12)
            {
                Places = (12, 1, 2, 3, 4)
            };
            var state = new MapSelectionState();
            var later = new MapDisplayedUnit(51, "Unit", "Arrives at 3, 4", 3, 4, arrival);
            state.Select(later);
            Check(!state.IsSelected(later with { Arrival = null }), "Starting unit matched an arrival selection");
            var changed = arrival with { X = 7, Y = 6 };
            state.ChangeSelectedArrival(changed);
            var moved = later with { X = 7, Y = 6, Arrival = changed };
            Check(state.IsSelected(moved), "Updated arrival lost its map highlight");
            state.RefreshSelection([moved]);
            Check(state.IsSelected(moved), "Refreshed arrival no longer matches the selected source");
        });
        test("Map reset clears unit, movement, arrival picking and selection history", () =>
        {
            var state = new MapSelectionState { UnitKey = 12, Where = "Starts", MovingUnit = (2, 0), PickingArrival = true,
                Arrival = new(5, 1, 1, 3, 4, 3, 4, 5, 12) };
            state.Cycle(2, 3, 2);
            state.Reset();
            Check(state.UnitKey is null && state.Where == "" && state.MovingUnit is null && state.Arrival is null && !state.PickingArrival, "Old map state survived reset");
            Check(state.Cycle(2, 3, 2) == 0, "New map retained click history");
        });
        test("Map load generation refuses stale selections and reset completions", () =>
        {
            var state = new MapSelectionState();
            int first = state.BeginLoad(), second = state.BeginLoad();
            Check(!state.IsCurrent(first) && state.IsCurrent(second), "Old selection can replace the new one");
            state.Reset();
            Check(!state.IsCurrent(second), "Reset did not invalidate pending loads");
        });
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
