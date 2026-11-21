using Berwick.Core;

namespace Berwick.Workshop;

internal sealed record MapDisplayedUnit(int Key, string Name, string Text, int X, int Y, Reinforcement? Arrival);
internal sealed record MapPlacedUnit(MapUnit Place, string Name, string Text, bool Named);
internal sealed record MapArrivalUnit(Reinforcement Place, string Name, string Text);

internal sealed class MapEditorDocument(
    InspectionResult disc, EditSession? session, EventLibrary library,
    IReadOnlyDictionary<int, List<int>> battleArmies,
    Action<int, byte[]> checkFits, Func<int, int, string?> fitProblem)
{
    private static readonly byte[] NoGrowth = new byte[CharacterDecoder.GrowthLength];
    private Dictionary<int, string> classNames = (session?.EditedClasses() ?? disc.Classes).ToDictionary(c => c.Id, c => c.Name);
    public InspectionResult Disc { get; } = disc;
    public EditSession? Session { get; private set; } = session;
    public EventLibrary Library { get; } = library;
    public IReadOnlyDictionary<int, List<int>> BattleArmies { get; } = battleArmies;
    public Action<int, byte[]> CheckFits { get; } = checkFits;
    public Func<int, int, string?> FitProblem { get; } = fitProblem;
    public string ClassName(int id) => classNames.GetValueOrDefault(id, $"Class 0x{id:X2}");
    public void RefreshProject(EditSession? current)
    {
        Session = current;
        classNames = (current?.EditedClasses() ?? Disc.Classes).ToDictionary(c => c.Id, c => c.Name);
    }
    public ArmyUnit? Unit(int key) => Disc.Armies.SelectMany(a => a.Units).FirstOrDefault(u => u.Key == key);
    private byte[] ArmyBytes(ArmyUnit unit) => Session?.EditedBytes(EditSession.ArmyUnits, unit.Key) ?? Disc.ArmyUnitBytes[unit.Key].ToArray();
    public CharacterRecord Record(ArmyUnit unit) => CharacterDecoder.Decode(new(unit.NameId, unit.Name, 0), ArmyBytes(unit), NoGrowth, new Dictionary<int, string>());

    public IReadOnlyList<(int X, int Y)> DeploymentOf(BattleMap map) => Session?.IsEdited(EditSession.Deployments, map.Top) == true
        ? Deployment.Hexes(Session.EditedBytes(EditSession.Deployments, map.Top)) : map.Deployment;

    private IEnumerable<MapUnit> UnitsOf(BattleMap map)
    {
        if (Session?.IsEdited(EditSession.Placements, map.Top) != true) return map.Units;
        var record = Session.EditedBytes(EditSession.Placements, map.Top);
        return map.Units.Select(u => Placement.Slot(record, u.Group, u.Index + 1) is { } at ? u with { X = at.X, Y = at.Y } : u)
            .Where(u => u.X < map.Width && u.Y < map.Height);
    }

    private int EventNumberNow(int number, int index, int original)
    {
        if (Session is null) return original;
        if (Session.HasScript(number))
        {
            var numbers = ScriptValues.Numbers(number, ScriptParser.Parse(Session.EventScript(number)!), Library.CommandNames);
            return index < numbers.Count ? numbers[index].Value : original;
        }
        int key = ScriptValues.Key(number, index);
        return Session.IsEdited(EditSession.ScriptValues, key) ? Session.Value(EditSession.ScriptValues, key, ScriptValues.Field.Key) : original;
    }

    public IEnumerable<Reinforcement> ArrivalsOf(BattleMap map)
    {
        foreach (var r in map.Arrivals)
        {
            var now = r;
            if (r.Places is { } at) now = now with { X = EventNumberNow(at.Event, at.X, r.X), Y = EventNumberNow(at.Event, at.Y, r.Y), FromX = EventNumberNow(at.Event, at.FromX, r.FromX), FromY = EventNumberNow(at.Event, at.FromY, r.FromY) };
            if (r.TurnPlace is { } turn && r.Turn is int first) now = now with { Turn = EventNumberNow(turn.Event, turn.Index, first - turn.TurnOffset) + turn.TurnOffset };
            yield return now;
        }
    }

    public (ScriptNumber Number, bool Own)? DeploymentLimit(BattleMap map)
    {
        if (map.Battle <= 0) return null;
        (ScriptNumber Number, bool Own)? limit;
        lock (Library) limit = Deployment.Limit(Library, map.Battle, n => Session?.HasScript(n) == true ? Session.EventScript(n) : null);
        if (limit is not { } found) return null;
        if (Session is not null && !Session.HasScript(found.Number.Event))
            found.Number = found.Number with { Value = Session.Value(EditSession.ScriptValues, ScriptValues.Key(found.Number.Event, found.Number.Index), ScriptValues.Field.Key) };
        return found;
    }

    public bool SetNumbers(IReadOnlyDictionary<(int Event, int Index), int> values) =>
        Session is not null && MapEdits.SetEventNumbers(Session, values, CheckFits, FitProblem);

    public List<MapPlacedUnit> PlacedUnits(BattleMap map)
    {
        var result = new List<MapPlacedUnit>();
        var armies = Disc.Armies.ToDictionary(a => a.Id);
        foreach (var place in UnitsOf(map))
        {
            if (!armies.TryGetValue(place.Army, out var army) || place.Index >= army.Units.Count) continue;
            var unit = army.Units[place.Index];
            var record = Record(unit);
            result.Add(new(place, unit.Name, $"{unit.Name}, {ClassName(record.ClassId)} level {record.Level} (army {place.Army:000})", unit.Named));
        }
        return result;
    }

    public List<MapArrivalUnit> ArrivingUnits(BattleMap map)
    {
        var result = new List<MapArrivalUnit>();
        var armies = Disc.Armies.ToDictionary(a => a.Id);
        var taken = new Dictionary<(int, int), int>();
        foreach (var r in ArrivalsOf(map))
        {
            if (!armies.TryGetValue(r.Army, out var army))
            {
                var character = r.Army == 1 ? Disc.Characters.FirstOrDefault(c => Disc.UnitBytes.TryGetValue(c.Id, out var bytes) && ArmyData.ScriptNumber(bytes.Span) == r.Script) : null;
                if (character is not null) result.Add(new(r, character.Name, $"{character.Name}, {ClassName(character.ClassId)} level {character.Level} (recruitable character)"));
                continue;
            }
            var matches = army.Units.Where(u => ArmyData.ScriptNumber(ArmyBytes(u)) == r.Script).ToList();
            if (matches.Count == 0) continue;
            int n = taken.GetValueOrDefault((r.Army, r.Script));
            taken[(r.Army, r.Script)] = n + 1;
            var unit = matches[n % matches.Count];
            var record = Record(unit);
            result.Add(new(r, unit.Name, $"{unit.Name}, {ClassName(record.ClassId)} level {record.Level} (army {r.Army:000})"));
        }
        return result;
    }

    public List<MapDisplayedUnit> AllUnits(BattleMap map)
    {
        var result = PlacedUnits(map).Select(p => new MapDisplayedUnit(Disc.Armies.First(a => a.Id == p.Place.Army).Units[p.Place.Index].Key,
            p.Name, $"Starts at {p.Place.X}, {p.Place.Y}: {p.Text}", p.Place.X, p.Place.Y, null)).ToList();
        var taken = new Dictionary<(int, int), int>();
        foreach (var r in ArrivalsOf(map))
        {
            var army = Disc.Armies.FirstOrDefault(a => a.Id == r.Army);
            if (army is null) continue;
            var matches = army.Units.Where(u => ArmyData.ScriptNumber(ArmyBytes(u)) == r.Script).ToList();
            if (matches.Count == 0) continue;
            int n = taken.GetValueOrDefault((r.Army, r.Script));
            taken[(r.Army, r.Script)] = n + 1;
            var unit = matches[n % matches.Count];
            result.Add(new(unit.Key, unit.Name, $"Arrives at {r.X}, {r.Y}{(r.Turn is int t ? $" on turn {t}" : "")}: {unit.Name} (army {r.Army:000})", r.X, r.Y, r));
        }
        return result;
    }

    public Func<int, string> TalkNames(BattleMap map)
    {
        var names = new Dictionary<int, string>();
        var used = map.Units.Select(u => u.Army).Concat(map.Arrivals.Select(a => a.Army)).ToHashSet();
        foreach (var army in Disc.Armies.Where(a => used.Contains(a.Id)))
            foreach (var unit in army.Units) names.TryAdd(ArmyData.ScriptNumber(ArmyBytes(unit)), unit.Name);
        foreach (var c in Disc.Characters)
            if (Disc.UnitBytes.TryGetValue(c.Id, out var bytes)) names.TryAdd(ArmyData.ScriptNumber(bytes.Span), c.Name);
        return n => names.GetValueOrDefault(n, "another unit");
    }
}
