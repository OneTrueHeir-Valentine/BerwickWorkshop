using Berwick.Core;
using Berwick.Workshop;

internal static class MapDocumentTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Map unit projection preserves repeated arrival identity and skips empty slots", () =>
        {
            var first = new ArmyUnit(100, 5, 0, 1, "First", 0, 180);
            var second = new ArmyUnit(101, 5, 1, 2, "Second", 0, 180);
            var disc = new InspectionResult("synthetic", 0, "", "", "", "", "", null, [], [], 0)
            {
                Armies = [new(5, "Army", [], 0, [first, second])],
                ArmyUnitBytes = new Dictionary<int, ReadOnlyMemory<byte>>
                {
                    [100] = new byte[ArmyData.RecordLength], [101] = new byte[ArmyData.RecordLength]
                }
            };
            var arrivals = new Reinforcement[]
            {
                new(5, 0, 1, 3, 4, 1, 1, 2, 12),
                new(5, 0, 1, 3, 4, 1, 1, 3, 12),
                new(5, 0, 1, 6, 7, 1, 1, 4, 12)
            };
            var map = new BattleMap(9, "synthetic", 12, 12, new int[BattleMaps.Side * BattleMaps.Side], [])
            {
                Units = [new(1, 5, 0, 3, 4), new(1, 5, 2, 3, 4), new(1, 99, 0, 3, 4)], Arrivals = arrivals
            };
            // Read-only unit projections do not consult the event library or script-capacity callbacks.
            var document = new MapEditorDocument(disc, null, null!, new Dictionary<int, List<int>>(),
                (_, _) => throw new Exception("Unexpected script access"), (_, _) => throw new Exception("Unexpected script access"));
            var units = document.AllUnits(map);
            Check(document.PlacedUnits(map).Count == 1, "Empty placement slots produced units");
            Check(units.Select(u => u.Key).SequenceEqual(new[] { 100, 100, 101, 100 }), "Repeated script numbers no longer rotate through the army");
            Check(units[0].Arrival is null && units.Skip(1).Select(u => u.Arrival).SequenceEqual(arrivals), "Arrival identity lost on a crowded hex");
            Check(document.ArrivingUnits(map).Select(u => u.Name).SequenceEqual(new[] { "First", "Second", "First" }), "Marker and selection projection disagree");
            Check(map.Units[0].X == 3 && map.Arrivals[0].FromX == 1, "Projection mutated source positions");
        });
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
