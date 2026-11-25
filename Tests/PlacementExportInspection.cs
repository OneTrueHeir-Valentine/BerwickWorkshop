using Berwick.Core;

internal static class PlacementExportInspection
{
    public static int Run(string imagePath, string archiveIndex, string placementGroup, string placementSlot)
    {
        try
        {
            if (!int.TryParse(archiveIndex, out int top) || top < 0 ||
                !int.TryParse(placementGroup, out int group) || group < Placement.FirstGroup ||
                group >= Placement.FirstGroup + Placement.Groups ||
                !int.TryParse(placementSlot, out int slot) || slot < 1 || slot >= Placement.Slots)
                throw new ArgumentException("Specify a nonnegative map archive, group 1 to 15, and slot 1 to 63.");

            using var stream = File.OpenRead(imagePath);
            using var iso = new IsoImage(stream);
            var archives = new GameArchives(iso);
            var archive = archives.Top(top);
            var entry = Tarc.Parse(archive).SingleOrDefault(e => e.Name == "map")
                ?? throw new InvalidDataException("The archive does not contain a map record.");
            var record = Placement.Read(Tarc.Extract(archive, entry))
                ?? throw new InvalidDataException("The map has no complete placement table.");
            var location = Placement.Slot(record, group, slot);
            Console.WriteLine(location is { } hex
                ? $"Map archive {top}, group {group}, slot {slot}: {hex.X}, {hex.Y}"
                : $"Map archive {top}, group {group}, slot {slot}: unused");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or EditException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
    public static int RunArmy(string imagePath, string archiveIndex, string placementGroup, string armyNumber)
    {
        try
        {
            if (!int.TryParse(archiveIndex, out int top) || top < 0 ||
                !int.TryParse(placementGroup, out int group) || group < Placement.FirstGroup ||
                group >= Placement.FirstGroup + Placement.Groups ||
                !int.TryParse(armyNumber, out int armyId) || armyId < 0)
                throw new ArgumentException("Specify a nonnegative map archive, group 1 to 15 and army number.");

            using var stream = File.OpenRead(imagePath);
            using var iso = new IsoImage(stream);
            var archives = new GameArchives(iso);
            var archive = archives.Top(top);
            var entry = Tarc.Parse(archive).SingleOrDefault(e => e.Name == "map")
                ?? throw new InvalidDataException("The archive does not contain a map record.");
            var slots = Placement.Read(Tarc.Extract(archive, entry))
                ?? throw new InvalidDataException("The map has no complete placement table.");
            var matching = ArmyData.Load(archives, ArmyData.Names)
                .Where(a => a.Army.Id == armyId).ToList();
            if (matching.Count != 1)
                throw new InvalidDataException("The chosen army does not have one unambiguous record set.");
            var (army, team) = matching[0];

            Console.WriteLine($"Archive {top}; group {group}; army ud{armyId:000}; {army.Units.Count} records.");
            Console.WriteLine("Slot | Position | Unit | Class | Level | Script | Header (first 32 bytes)");
            int? firstUnused = null;
            foreach (var unit in army.Units.Where(u => u.Index < Placement.Slots - 1))
            {
                int slot = unit.Index + 1;
                var position = Placement.Slot(slots, group, slot);
                if (position is null) firstUnused ??= slot;
                var record = ArmyData.Present(team, unit);
                int cls = record[19], level = record[20] & 0x3F, script = ArmyData.ScriptNumber(record);
                string hex = position is { } at ? $"{at.X}, {at.Y}" : "unused";
                Console.WriteLine($"{slot,4} | {hex,-8} | {unit.Name,-18} | {cls,5} | {level,5} | {script,6} | {Convert.ToHexString(record.AsSpan(0, 32))}");
            }
            Console.WriteLine(firstUnused is int missing
                ? $"First unused slot: {missing}. Its effect on unit loading has not been established."
                : "No unused slot among these army records.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or EditException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

}
