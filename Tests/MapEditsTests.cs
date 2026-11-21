using Berwick.Core;

internal static class MapEditsTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Map placement validates borders, deployment collisions and slot fields", () =>
        {
            var map = SampleMap();
            var values = MapEdits.PlacementValues(map, 2, 0, 4, 5, []);
            Check(values["g2x1"] == 4 && values["g2y1"] == 5, "Placement field names changed.");
            Reject(() => MapEdits.PlacementValues(map, 2, 0, 0, 5, []));
            Reject(() => MapEdits.PlacementValues(map, 2, 0, 4, 5, [(4, 5)]));
            Reject(() => MapEdits.PlacementValues(map, 0, 0, 4, 5, []));
            Reject(() => MapEdits.PlacementValues(map, 2, 63, 4, 5, []));
            Reject(() => MapEdits.PlacementValues(map, 2, 0, 11, 5, []));
        });

        test("Map arrivals preserve marching origins and change in-place spawn coordinates", () =>
        {
            var map = SampleMap();
            var arrival = new Reinforcement(5, 1, 1, 3, 4, 3, 4, 5, 12)
            {
                Places = (12, 1, 2, 3, 4)
            };
            var edit = MapEdits.PlanArrivalMove(map, arrival, 6, 7);
            Check(edit.Numbers.Count == 4, "An in-place arrival must update both coordinate pairs.");
            Check(edit.Numbers[(12, 1)] == 6 && edit.Numbers[(12, 4)] == 7,
                "The original spawn coordinates were not updated.");
            Check(edit.Updated.FromX == 6 && edit.Updated.FromY == 7, "An in-place arrival no longer stays in place.");

            var marching = arrival with { FromX = 1, FromY = 2 };
            var marched = MapEdits.PlanArrivalMove(map, marching, 6, 7);
            Check(marched.Numbers.Count == 2 && marched.Updated.FromX == 1 && marched.Updated.FromY == 2,
                "Moving an arrival changed its marching origin.");

            var shared = arrival with { Places = (12, 1, 2, 1, 2) };
            Check(MapEdits.PlanArrivalMove(map, shared, 6, 7).Numbers.Count == 2,
                "Shared source coordinate indices were written more than once.");

            Reject(() => MapEdits.PlanArrivalMove(map, arrival with { Places = null }, 6, 7));
            Reject(() => MapEdits.PlanArrivalMove(map, arrival, 0, 7));
            Reject(() => MapEdits.PlanArrivalMove(map, arrival, 6, 11));
        });
    }

    private static BattleMap SampleMap() =>
        new(9, "sample", 12, 12, new int[BattleMaps.Side * BattleMaps.Side], []);

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (EditException) { return; }
        throw new Exception("Invalid map edit was accepted.");
    }
}
