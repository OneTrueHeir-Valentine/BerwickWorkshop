namespace Berwick.Core;

public sealed record StartingUnitCandidate(ArmyUnit Unit, StartingArmyReference Assignment, int Slot,
    bool Occupied, string? Problem, IReadOnlyList<int> ArmyBattles, IReadOnlyList<int> MapBattles, IReadOnlyList<int> Events)
{
    public string? PlacementProblem { get; init; }
    public bool CanPlace => Problem is null && PlacementProblem is null && !Occupied;
    public bool CanRemove => Problem is null && Occupied;
}

public static class StartingUnitPlacement
{
    public static IReadOnlyList<StartingUnitCandidate> Candidates(BattleMap map, BattlePlacementReferences references,
        IReadOnlyList<Army> armies, ReadOnlySpan<byte> record, ReadOnlySpan<byte> sourceRecord = default)
    {
        if (record.Length != Placement.RecordLength)
            throw new EditException("The placement record has an unsupported length.");
        var result = new List<StartingUnitCandidate>();
        foreach (var assignment in references.Assignments.Where(assignment => assignment.Battle == map.Battle))
        {
            var matchingArmies = armies.Where(army => army.Id == assignment.Army).ToList();
            if (matchingArmies.Count != 1)
                continue;
            var army = matchingArmies[0];
            var mapBattles = references.MapArchives.Where(pair => pair.Value == map.Top).Select(pair => pair.Key).Order().ToArray();
            var armyBattles = references.ArmyBattles.GetValueOrDefault(army.Id)?.Order().ToArray() ?? [];
            foreach (var unit in army.Units)
            {
                int slot = unit.Index + 1;
                string? problem = AssignmentProblem(map, references, assignment, mapBattles);
                bool occupied = false;
                if (unit.Army != army.Id || army.Units.Count(other => other.Index == unit.Index) != 1 ||
                    armies.SelectMany(other => other.Units).Count(other => other.Key == unit.Key) != 1 ||
                    unit.Index < 0 || unit.Index >= army.Units.Count || slot >= Placement.Slots || army.Units[unit.Index] != unit)
                    problem ??= "The unit does not have a unique supported slot (unit indexes 0 to 62).";
                else if (assignment.Group is >= Placement.FirstGroup and < Placement.FirstGroup + Placement.Groups)
                {
                    int offset = (assignment.Group - Placement.FirstGroup) * Placement.Slots + slot;
                    int x = record[offset];
                    int y = record[Placement.Half + offset];
                    occupied = x != Deployment.NoSlot && y != Deployment.NoSlot;
                    if ((x == Deployment.NoSlot) != (y == Deployment.NoSlot))
                        problem ??= "Only one coordinate is unused; this slot is ambiguous.";
                    if (occupied && (x <= 0 || y <= 0 || x >= map.Width - 1 || y >= map.Height - 1 || map.KindAt(x, y) == BattleMaps.Border))
                        problem ??= "The current coordinates use an unsupported or off-map placement.";
                }
                string? placementProblem = references.Assignments
                    .Where(other => mapBattles.Contains(other.Battle) && other.Group == assignment.Group)
                    .Select(other => other.PlacementProblem).FirstOrDefault(message => message is not null);
                if (problem is null && placementProblem is null)
                {
                    var source = sourceRecord.Length == Placement.RecordLength ? Placement.Slot(sourceRecord, assignment.Group, slot) : null;
                    if (source is not { } hex || hex.X <= 0 || hex.Y <= 0 || hex.X >= map.Width - 1 || hex.Y >= map.Height - 1 ||
                        map.KindAt(hex.X, hex.Y) == BattleMaps.Border)
                        placementProblem = "This source slot has no supported starting position. Empty coordinates do not establish that the game will spawn this record; it may be controlled by events. New placement is unavailable.";
                }
                result.Add(new(unit, assignment, slot, occupied, problem, armyBattles, mapBattles,
                    references.ArmyEvents.GetValueOrDefault(army.Id)?.Order().ToArray() ?? []) { PlacementProblem = placementProblem });
            }
        }
        return result;
    }

    private static string? AssignmentProblem(BattleMap map, BattlePlacementReferences references, StartingArmyReference assignment, IReadOnlyList<int> mapBattles)
    {
        if (references.Problems.Count > 0)
            return "References are incomplete: " + references.Problems[0];
        if (map.Battle <= 0 || !references.MapArchives.TryGetValue(map.Battle, out int top) || top != map.Top)
            return "The selected battle's map cannot be identified unambiguously.";
        if (assignment.Conditional || assignment.Team is < 0 or > 3 || assignment.Group is < Placement.FirstGroup or >= Placement.FirstGroup + Placement.Groups)
            return "Conditional assignments, deployment group 0 and unsupported teams or groups cannot be edited.";
        var selected = references.Assignments.Where(other => other.Battle == map.Battle).ToList();
        if (selected.Count(other => other.Group == assignment.Group) != 1 || selected.Count(other => other.Army == assignment.Army) != 1)
            return "This army or placement group is assigned more than once in the battle.";
        if (references.UnknownArmyUses.Contains(assignment.Army) || mapBattles.Any(references.UnsafeBattles.Contains))
            return "Some army or group uses cannot be resolved to starting battle positions.";
        foreach (int battle in mapBattles)
        {
            var groupAssignments = references.Assignments.Where(other => other.Battle == battle && other.Group == assignment.Group).ToList();
            if (groupAssignments.Count != 1 || groupAssignments.Any(other => other.Army != assignment.Army || other.Team != assignment.Team || other.Conditional) ||
                references.Assignments.Count(other => other.Battle == battle && other.Army == assignment.Army) != 1)
                return $"Battle {battle} shares this map with an incomplete or conflicting group assignment.";
        }
        return null;
    }

    public static IReadOnlyDictionary<string, int> PlaceValues(BattleMap map, StartingUnitCandidate candidate,
        ReadOnlySpan<byte> record, IReadOnlyList<(int X, int Y)> deployment, int x, int y, bool sharedMapAcknowledged)
    {
        if (!candidate.CanPlace)
            throw new EditException(candidate.Problem ?? candidate.PlacementProblem ?? "This unit already has a starting placement.");
        if (candidate.Slot != candidate.Unit.Index + 1)
            throw new EditException("The selected record does not match its placement slot.");
        RequireAcknowledgement(candidate, sharedMapAcknowledged);
        var values = MapEdits.PlacementValues(map, candidate.Assignment.Group, candidate.Unit.Index, x, y, deployment);
        if (record.Length != Placement.RecordLength)
            throw new EditException("The placement record has an unsupported length.");
        int offset = (candidate.Assignment.Group - Placement.FirstGroup) * Placement.Slots + candidate.Slot;
        if (record[offset] != Deployment.NoSlot || record[Placement.Half + offset] != Deployment.NoSlot)
            throw new EditException("This placement slot is no longer unused.");
        for (int group = Placement.FirstGroup; group < Placement.FirstGroup + Placement.Groups; group++)
            for (int slot = 1; slot < Placement.Slots; slot++)
                if (Placement.Slot(record, group, slot) == (x, y))
                    throw new EditException($"Group {group}, slot {slot} already uses that hex.");
        return values;
    }

    public static bool Apply(EditSession session, EventLibrary library, BattleMap map, int unitKey, int group,
        bool remove, int x, int y, bool sharedMapAcknowledged)
    {
        if (!session.Disc.ArmyUnitBytes.ContainsKey(unitKey) || session.EditedBytes(EditSession.ArmyUnits, unitKey).Length != ArmyData.RecordLength)
            throw new EditException("The selected unit has no complete existing army record.");
        var references = BattlePlacementReferences.Read(library, session);
        var record = session.EditedBytes(EditSession.Placements, map.Top);
        var sourceRecord = session.Table(EditSession.Placements).Records.GetValueOrDefault(map.Top);
        var matches = Candidates(map, references, session.Disc.Armies, record, sourceRecord.Span)
            .Where(candidate => candidate.Unit.Key == unitKey && candidate.Assignment.Group == group).ToList();
        if (matches.Count != 1)
            throw new EditException("The selected unit's army/group assignment is ambiguous or no longer available.");
        var candidate = matches[0];
        IReadOnlyDictionary<string, int> values;
        if (remove)
        {
            if (!candidate.CanRemove)
                throw new EditException(candidate.Problem ?? "This unit has no starting placement to remove.");
            RequireAcknowledgement(candidate, sharedMapAcknowledged);
            values = new Dictionary<string, int>
            {
                [$"g{group}x{candidate.Slot}"] = Deployment.NoSlot,
                [$"g{group}y{candidate.Slot}"] = Deployment.NoSlot
            };
        }
        else
        {
            var deployment = Deployment.Hexes(session.EditedBytes(EditSession.Deployments, map.Top));
            values = PlaceValues(map, candidate, record, deployment, x, y, sharedMapAcknowledged);
        }
        return session.Apply(EditSession.Placements, map.Top, values);
    }

    private static void RequireAcknowledgement(StartingUnitCandidate candidate, bool acknowledged)
    {
        if (candidate.MapBattles.Count > 1 && !acknowledged)
            throw new EditException("This map is shared by other battles. Review and acknowledge the affected battles first.");
    }
}
