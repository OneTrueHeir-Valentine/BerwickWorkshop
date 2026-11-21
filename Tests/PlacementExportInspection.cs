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
}
