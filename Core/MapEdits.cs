namespace Berwick.Core;

public sealed record MapArrivalEdit(
    Reinforcement Updated,
    IReadOnlyDictionary<(int Event, int Index), int> Numbers);

/// <summary>Project changes initiated from the Maps editor. Rendering and UI state remain in App.</summary>
public static class MapEdits
{
    public static IReadOnlyDictionary<string, int> PlacementValues(
        BattleMap map, int group, int index, int x, int y, IReadOnlyCollection<(int X, int Y)> deployment)
    {
        if (x <= 0 || y <= 0 || x >= map.Width - 1 || y >= map.Height - 1 ||
            map.KindAt(x, y) == BattleMaps.Border)
            throw new EditException("A unit must start inside the map.");
        if (deployment.Contains((x, y)))
            throw new EditException("That hex is one of your deployment hexes. Choose another, or remove it from deployment first.");
        if (group < Placement.FirstGroup || group >= Placement.FirstGroup + Placement.Groups ||
            index < 0 || index >= Placement.Slots - 1)
            throw new EditException("There is no such placement slot.");

        return new Dictionary<string, int>
        {
            [$"g{group}x{index + 1}"] = x,
            [$"g{group}y{index + 1}"] = y
        };
    }

    public static bool MovePlacement(
        EditSession session, BattleMap map, int group, int index, int x, int y,
        IReadOnlyCollection<(int X, int Y)> deployment)
    {
        var values = PlacementValues(map, group, index, x, y, deployment);
        _ = Placement.Move(session.EditedBytes(EditSession.Placements, map.Top), group, index + 1, x, y);
        return session.Apply(EditSession.Placements, map.Top, values);
    }

    public static (bool Changed, int Count) ToggleDeployment(
        EditSession session, BattleMap map, int x, int y, IReadOnlyList<(int X, int Y)> current)
    {
        var hexes = current.ToList();
        if (!hexes.Remove((x, y)))
        {
            if (x <= 0 || y <= 0 || x >= map.Width - 1 || y >= map.Height - 1 ||
                map.KindAt(x, y) == BattleMaps.Border)
                throw new EditException("A deployment hex must be inside the map.");
            hexes.Add((x, y));
        }
        var original = session.Table(EditSession.Deployments).Records[map.Top].Span;
        var updated = Deployment.WithHexes(original, hexes);
        var values = Deployment.Fields.ToDictionary(field => field.Key, field => FieldCodec.Read(updated, field));
        return (session.Apply(EditSession.Deployments, map.Top, values), hexes.Count);
    }

    public static MapArrivalEdit PlanArrivalMove(BattleMap map, Reinforcement arrival, int x, int y)
    {
        if (arrival.Places is not { } at)
            throw new EditException("This arrival's hex is not a plain number in its event, so it cannot be changed here.");
        if (x <= 0 || y <= 0 || x >= map.Width - 1 || y >= map.Height - 1 ||
            map.KindAt(x, y) == BattleMaps.Border)
            throw new EditException("A unit must arrive inside the map.");

        var values = new Dictionary<(int Event, int Index), int>
        {
            [(at.Event, at.X)] = x,
            [(at.Event, at.Y)] = y
        };
        bool inPlace = arrival.FromX == arrival.X && arrival.FromY == arrival.Y;
        if (inPlace && (at.FromX, at.FromY) != (at.X, at.Y))
        {
            values[(at.Event, at.FromX)] = x;
            values[(at.Event, at.FromY)] = y;
        }
        var updated = arrival with
        {
            X = x, Y = y,
            FromX = inPlace ? x : arrival.FromX,
            FromY = inPlace ? y : arrival.FromY
        };
        return new(updated, values);
    }

    /// <summary>Applies event-number edits through the existing project undo history and disc-size checks.</summary>
    public static bool SetEventNumbers(
        EditSession session,
        IReadOnlyDictionary<(int Event, int Index), int> values,
        Action<int, byte[]> checkEventFits,
        Func<int, int, string?> checkValueFits)
    {
        bool changed = false;
        foreach (var group in values.GroupBy(value => value.Key.Event))
        {
            if (session.HasScript(group.Key))
            {
                var updated = ScriptValues.Apply(session.EventScript(group.Key)!,
                    group.ToDictionary(value => value.Key.Index, value => value.Value));
                checkEventFits(group.Key, updated);
                changed |= session.SetScript(group.Key, updated);
                continue;
            }

            foreach (var (position, value) in group)
            {
                int key = ScriptValues.Key(position.Event, position.Index);
                if (checkValueFits(key, value) is string problem)
                    throw new EditException(problem);
                changed |= session.Apply(EditSession.ScriptValues, key,
                    new Dictionary<string, int> { [ScriptValues.Field.Key] = value });
            }
        }
        return changed;
    }

    public static bool SetDeploymentLimit(
        EditSession session, ScriptNumber number, int limit,
        Action<int, byte[]> checkEventFits, Func<int, int, string?> checkValueFits)
    {
        if (limit is < 1 or > 63)
            throw new EditException("Enter a number from 1 to 63.");

        if (session.HasScript(number.Event))
        {
            var updated = ScriptValues.Apply(session.EventScript(number.Event)!,
                new Dictionary<int, int> { [number.Index] = limit });
            checkEventFits(number.Event, updated);
            return session.SetScript(number.Event, updated);
        }

        int key = ScriptValues.Key(number.Event, number.Index);
        if (checkValueFits(key, limit) is string problem)
            throw new EditException(problem);
        return session.Apply(EditSession.ScriptValues, key,
            new Dictionary<string, int> { [ScriptValues.Field.Key] = limit });
    }

    public static (int Event, bool Changed) SetOwnDeploymentLimit(
        EditSession session, EventLibrary library, int battle, int limit,
        Action<int, byte[]> checkEventFits)
    {
        if (limit is < 1 or > 63)
            throw new EditException("Enter a number from 1 to 63.");

        int opening;
        byte[] updated;
        lock (library)
        {
            opening = Deployment.OwnLimitEvent(library, battle)
                ?? throw new EditException("This battle has no opening event to hold its own limit.");
            var current = session.EventScript(opening) ?? library.Bytes(opening);
            updated = Deployment.AddOwnLimit(current, limit,
                library.CommandNames ?? throw new EditException(
                    "This battle's deployment limit cannot be edited because its command list is unavailable."));
        }
        checkEventFits(opening, updated);
        return (opening, session.SetScript(opening, updated));
    }
}
