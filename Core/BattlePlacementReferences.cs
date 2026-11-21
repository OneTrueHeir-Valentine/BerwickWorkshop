using System.Text.RegularExpressions;

namespace Berwick.Core;

public sealed record PlacementEventSource(int Number, int Archive, IReadOnlyList<ScriptNode> Nodes);
public sealed record StartingArmyReference(int Battle, int OpeningEvent, int Event, int Offset, int Army, int Team, int Group, bool Conditional);

public sealed class BattlePlacementReferences
{
    private readonly List<StartingArmyReference> assignments = [];
    private readonly Dictionary<int, int> mapArchives = [];
    private readonly Dictionary<int, HashSet<int>> armyBattles = [];
    private readonly Dictionary<int, HashSet<int>> armyEvents = [];
    private readonly HashSet<int> unknownArmyUses = [];
    private readonly HashSet<int> unsafeBattles = [];
    private readonly List<string> problems = [];
    public IReadOnlyList<StartingArmyReference> Assignments => assignments;
    // A stored -1 is a launch naming a proven absent archive; a missing key is an unresolved battle map.
    public IReadOnlyDictionary<int, int> MapArchives => mapArchives;
    public IReadOnlyDictionary<int, HashSet<int>> ArmyBattles => armyBattles;
    public IReadOnlyDictionary<int, HashSet<int>> ArmyEvents => armyEvents;
    public IReadOnlySet<int> UnknownArmyUses => unknownArmyUses;
    public IReadOnlySet<int> UnsafeBattles => unsafeBattles;
    public IReadOnlyList<string> Problems => problems;

    public static BattlePlacementReferences Read(EventLibrary library, EditSession session)
    {
        lock (library)
        {
            var sources = library.Numbers.Select(number => new PlacementEventSource(number, library.TopOf(number),
                ScriptParser.Parse(session.EventScript(number) ?? library.Bytes(number)))).ToList();
            var maps = library.Archives.FolderFiles("bmap");
            return Analyze(sources, library.PlacementEvents(), file =>
            {
                uint hash = GameArchives.NameHash(file) & 0x07FFFFFF;
                var matches = maps.Where(map => map.Hash == hash).ToList();
                if (matches.Count > 1)
                    throw new EditException($"Map {file} has ambiguous archive-index entries.");
                return matches.Count == 1 ? matches[0].Top : -1;
            });
        }
    }

    public static BattlePlacementReferences Analyze(IReadOnlyList<PlacementEventSource> sources,
        IReadOnlyList<MapEventRecord> mapEvents, Func<string, int> findMap) => new ReferenceReader(sources, mapEvents, findMap).Read();

    private sealed class ReferenceReader(IReadOnlyList<PlacementEventSource> sources,
        IReadOnlyList<MapEventRecord> mapEvents, Func<string, int> findMap)
    {
        private const int LaunchBattleMap = 0x2DE;
        private const int LaunchBattleMapWithMode = 0x2DF;
        private const int AppearArmy = 0x32C;
        private const int OpeningEventType = 13;
        private const int PreloadUnitList = 0x167;
        private const int PreloadUnit = 0x328;
        private const int AppearUnitOld = 0x32A;
        private const int AppearUnit = 0x32B;
        private static readonly Regex ArmyFile = new(@"\Aud([0-9]{3})(\.ar)?\z", RegexOptions.CultureInvariant);
        private readonly BattlePlacementReferences result = new();
        private readonly Dictionary<int, PlacementEventSource> events = sources.ToDictionary(source => source.Number);
        private readonly Dictionary<int, HashSet<int>> eventBattles = sources.ToDictionary(source => source.Number, _ => new HashSet<int>());
        private readonly Dictionary<int, HashSet<int>> archiveBattles = [];

        public BattlePlacementReferences Read()
        {
            ReadMapReferences();
            AssociateEvents();
            FollowEventCalls();
            foreach (var opening in mapEvents.Where(record => record.Type == OpeningEventType && record.Battle > 0).DistinctBy(record => (record.Battle, record.Event)))
                VisitOpeningEvent(opening.Event, opening.Battle, opening.Event, false, []);
            CollectArmyUses();
            return result;
        }

        private void ReadMapReferences()
        {
            foreach (var source in sources)
            {
                foreach (var node in Walk(source.Nodes))
                {
                    if (node.Data is not null)
                        result.problems.Add($"Event {source.Number} contains undecoded commands.");
                    if (node.Command is not (LaunchBattleMap or LaunchBattleMapWithMode))
                        continue;
                    if (!EventLibrary.TryBattleMapLaunch(node, out int battle, out string file) || Path.GetFileName(file) != file)
                    {
                        result.problems.Add($"Event {source.Number} has a computed or unsupported battle-map reference.");
                        continue;
                    }
                    int mapArchive = findMap(file);
                    if (result.mapArchives.TryGetValue(battle, out int previous) && previous != mapArchive)
                        result.problems.Add($"Battle {battle} has conflicting map references.");
                    result.mapArchives[battle] = mapArchive;
                    if (!archiveBattles.TryGetValue(source.Archive, out var battles))
                        archiveBattles[source.Archive] = battles = [];
                    battles.Add(battle);
                }
            }
        }

        private void AssociateEvents()
        {
            if (mapEvents.Count == 0)
                result.problems.Add("The battle event catalogue is missing or unreadable.");
            var allBattles = mapEvents.Where(record => record.Battle > 0).Select(record => record.Battle).Union(result.mapArchives.Keys).ToArray();
            foreach (int battle in allBattles)
                if (!result.mapArchives.ContainsKey(battle))
                    result.problems.Add($"Battle {battle} has no resolved map; shared placement effects cannot be established.");
            foreach (var record in mapEvents)
            {
                if (!eventBattles.TryGetValue(record.Event, out var battles))
                {
                    result.problems.Add($"Battle event {record.Event} is unavailable.");
                    continue;
                }
                if (record.Battle == 0)
                {
                    battles.UnionWith(allBattles);
                    if (record.Type == OpeningEventType)
                        result.problems.Add("Global starting assignments cannot be edited safely.");
                }
                else
                    battles.Add(record.Battle);
            }
            foreach (var source in sources)
                if (eventBattles[source.Number].Count == 0 && archiveBattles.TryGetValue(source.Archive, out var battles))
                    eventBattles[source.Number].UnionWith(battles);
        }

        private void FollowEventCalls()
        {
            bool changed;
            do
            {
                changed = false;
                foreach (var source in sources)
                {
                    foreach (var node in Walk(source.Nodes).Where(node => node.Command == ScriptParser.RunEvent))
                    {
                        if (node.Args.Count == 0 || node.Args[0] is not [{ Value: int target }] || !eventBattles.TryGetValue(target, out var battles))
                        {
                            string problem = $"Event {source.Number} has an unresolved event call.";
                            if (!result.problems.Contains(problem))
                                result.problems.Add(problem);
                            continue;
                        }
                        int before = battles.Count;
                        battles.UnionWith(eventBattles[source.Number]);
                        changed |= before != battles.Count;
                    }
                }
            } while (changed);
        }

        private void VisitOpeningEvent(int number, int battle, int opening, bool conditional, HashSet<int> path)
        {
            if (!events.TryGetValue(number, out var source) || path.Count >= 32 || !path.Add(number))
            {
                result.problems.Add($"Opening event {opening} has a missing, cyclic or unsupported event call.");
                return;
            }
            VisitOpeningNodes(source, source.Nodes, battle, opening, conditional, path);
            path.Remove(number);
        }

        private void VisitOpeningNodes(PlacementEventSource source, IEnumerable<ScriptNode> nodes,
            int battle, int opening, bool conditional, HashSet<int> path)
        {
            foreach (var node in nodes)
            {
                if (node.Command == ScriptParser.RunEvent)
                {
                    if (node.Args.Count > 0 && node.Args[0] is [{ Value: int target }])
                        VisitOpeningEvent(target, battle, opening, conditional, path);
                    else
                        result.problems.Add($"Opening event {opening} has a computed event call.");
                }
                if (node.Command == AppearArmy)
                    AddAssignment(source.Number, node, battle, opening, conditional);
                foreach (var arg in node.Args)
                    VisitOpeningNodes(source, arg, battle, opening, conditional || node.Command != ScriptParser.Event, path);
            }
        }

        private void AddAssignment(int number, ScriptNode node, int battle, int opening, bool conditional)
        {
            if (node.Args.Count < 3 || node.Args[0] is not [{ Text: { } file }] ||
                !ArmyFile.Match(file).Success || node.Args[1] is not [{ Value: int team }] || node.Args[2] is not [{ Value: int group }])
            {
                result.problems.Add($"Opening event {opening} has a computed army, team or group.");
                return;
            }
            int army = int.Parse(ArmyFile.Match(file).Groups[1].Value);
            result.assignments.Add(new(battle, opening, number, node.Offset, army, team, group, conditional));
        }

        private void CollectArmyUses()
        {
            foreach (var source in sources)
            {
                foreach (var node in Walk(source.Nodes))
                {
                    if (node.Command is not (PreloadUnitList or PreloadUnit or AppearUnitOld or AppearUnit or AppearArmy))
                        continue;
                    var files = node.Args.Where(arg => arg is [{ Text: not null }]).Select(arg => ArmyFile.Match(arg[0].Text!)).Where(match => match.Success).ToList();
                    if (files.Count != 1)
                    {
                        result.problems.Add($"Event {source.Number} has an unresolved army reference.");
                        continue;
                    }
                    int army = int.Parse(files[0].Groups[1].Value);
                    if (!result.armyBattles.TryGetValue(army, out var battles))
                        result.armyBattles[army] = battles = [];
                    battles.UnionWith(eventBattles[source.Number]);
                    if (!result.armyEvents.TryGetValue(army, out var eventNumbers))
                        result.armyEvents[army] = eventNumbers = [];
                    eventNumbers.Add(source.Number);
                    if (eventBattles[source.Number].Count == 0)
                        result.unknownArmyUses.Add(army);
                    if (node.Command == AppearArmy)
                        foreach (int battle in eventBattles[source.Number])
                            if (!result.assignments.Any(assignment => assignment.Battle == battle && assignment.Event == source.Number && assignment.Offset == node.Offset))
                                result.unsafeBattles.Add(battle);
                }
            }
        }
    }

    private static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in node.Args.SelectMany(Walk))
                yield return child;
        }
    }
}
