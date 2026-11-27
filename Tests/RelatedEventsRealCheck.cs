using Berwick.Core;

internal static class RelatedEventsRealCheck
{
    internal static int Run(string image, string folder)
    {
        try
        {
            var disc = Inspector.Inspect(image);
            if (!disc.Supported || disc.IsModified)
                throw new InvalidOperationException("Select a supported original image.");
            Directory.CreateDirectory(folder);
            using var stream = File.OpenRead(image);
            var library = new EventLibrary(new GameArchives(new IsoImage(stream)));
            var index = ScriptDependencyIndex.Build(library.Numbers, library.Bytes, library.CommandNames);
            var ironLance = RelatedEvents.Discover(RelatedRecord.Item, 106, index);
            if (!ironLance.Any(x => x.Event == 168) || !ironLance.Any(x => x.Event == 170))
                throw new InvalidOperationException("Known Iron Lance item references were not found.");
            var links = new List<string> { "Record\tID\tEvent\tReason", $"# Indexed {index.IndexedEvents}; unreadable {index.UnreadableEvents.Count}; literal references are not gameplay proof." };
            void Add(string record, int id, IEnumerable<RelatedEvent> found)
            {
                foreach (var link in found)
                    links.Add($"{record}\t{id}\t{link.Event}\t{link.Reason}");
            }
            Add("Item", 106, ironLance);
            Add("Character", 87, RelatedEvents.Discover(RelatedRecord.Character, 87, index));
            Add("Class", 28, RelatedEvents.Discover(RelatedRecord.Class, 28, index));
            var promotions = PromotionCatalogue.Read(disc);
            if (!promotions.OriginalSourceVerified) throw new InvalidOperationException("Original promotion source was not verified.");
            foreach (var path in promotions.Paths)
                Add("Original promotion", path.UnitId, RelatedEvents.Promotion(path));
            string edition = disc.Edition.Contains("English", StringComparison.OrdinalIgnoreCase) ? "EN" : "JP";
            File.WriteAllLines(Path.Combine(folder, $"Related-Events-{edition}.tsv"), links);
            Console.WriteLine($"PASS {disc.Edition}: {index.IndexedEvents} events indexed; {index.UnreadableEvents.Count} unreadable; known item106 references 168/170 present.");
            Console.WriteLine("Read-only source references; no original image writes or export.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }
}
