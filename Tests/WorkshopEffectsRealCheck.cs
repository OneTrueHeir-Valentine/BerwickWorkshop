using Berwick.Core;

internal static class WorkshopEffectsRealCheck
{
    internal static int Run(string image, string folder)
    {
        string? exported = null;
        try
        {
            var disc = Inspector.Inspect(image);
            if (!disc.Supported || disc.IsModified || disc.DifferentTableRecords != 0)
                throw new InvalidOperationException("Select an original supported image with matching item tables.");
            Directory.CreateDirectory(folder);
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace < disc.Size + 64L * 1024 * 1024)
                throw new IOException("Not enough free space for a temporary exported image.");
            var session = new EditSession(disc);
            // A disposable field-storage test, not a proposed weapon design or a gameplay fixture.
            var fields = new Dictionary<string, int> { ["effectKind"] = 112, ["effectAmount"] = 10, ["effect47"] = 1,
                ["effect18"] = 1, ["effect24"] = 1, ["effect25"] = 1, ["effect83"] = 1, ["chargeMight"] = 3, ["additionalStrikes"] = 2 };
            var edits = fields.Select(f => new FieldEdit(EditSession.Items, 1, f.Key, f.Value)).ToList();
            foreach (var rule in GameRules.All.Where(r => r.ExperimentalEditor))
            {
                if (!session.IsRuleAvailable(rule.Id)) throw new InvalidOperationException($"Missing rule {rule.Key}.");
                int value = rule.Id == 253 ? 2 : rule.Kind == RuleKind.Pair ? GameRules.RangeSetting(4, 3) : rule.Default == rule.Max ? rule.Min : rule.Default + 1;
                edits.Add(new(EditSession.Rules, rule.Id, rule.Key, value));
            }
            session.Apply(edits);
            var reopened = EditSession.FromJson(disc, session.ToJson());
            var patches = Exporter.PatchesFor(session, disc);
            var reopenedPatches = Exporter.PatchesFor(reopened, disc);
            if (patches.Count != reopenedPatches.Count || !patches.Zip(reopenedPatches).All(p => p.First.Offset == p.Second.Offset && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)))
                throw new InvalidOperationException("Project reopen changed the export plan.");
            exported = Path.Combine(folder, $"effects-check-{Guid.NewGuid():N}.iso");
            var result = Exporter.Export(disc.Path, disc.Sha256, exported, patches);
            var back = Inspector.Inspect(exported);
            if (!back.Supported || !back.IsModified || back.DifferentTableRecords != 0 || result.Sha256 != back.Sha256)
                throw new InvalidOperationException("Modified image recognition or item copies failed.");
            var verify = new EditSession(back);
            foreach (var e in edits)
                if (verify.Value(e.Table, e.Id, e.Field) != e.Value)
                    throw new InvalidOperationException($"Readback mismatch: {e.Table} {e.Id} {e.Field}.");
            foreach (var row in disc.ItemBytes)
                if (!back.ItemBytes[row.Key].Span.SequenceEqual(session.EditedBytes(row.Key)))
                    throw new InvalidOperationException($"Unexpected item difference: {row.Key}.");
            if (Inspector.Inspect(image).Sha256 != disc.Sha256) throw new InvalidOperationException("Original image hash changed.");
            Console.WriteLine($"PASS {disc.Edition}: {edits.Count} intended values; {patches.Count} patches; original SHA256 {disc.Sha256}; temporary export SHA256 {back.Sha256}.");
            Console.WriteLine("Edited storage and project/export readback verified. Gameplay not tested.");
            string edition = disc.Edition.Contains("English", StringComparison.OrdinalIgnoreCase) ? "EN" : "JP";
            void Fixture(string name, params (int Id, int Value)[] settings)
            {
                var trial = new EditSession(disc);
                trial.Apply(settings.Select(x => new FieldEdit(EditSession.Rules, x.Id, GameRules.ById(x.Id).Key, x.Value)).ToList());
                var path = Path.Combine(folder, $"{name}-{edition}.bwproj");
                trial.Save(path);
                var loaded = EditSession.FromJson(disc, trial.ToJson());
                foreach (var (id, value) in settings)
                    if (loaded.Value(EditSession.Rules, id, GameRules.ById(id).Key) != value)
                        throw new InvalidOperationException($"Fixture readback mismatch: {name}, rule {id}.");
                Console.WriteLine($"Saved isolated gameplay fixture: {path}");
            }
            Fixture("Maim-Full-Proficiency", (146, 15), (264, 1));
            Fixture("Default-Plain-Horse", (265, 1));
            Fixture("Furniture-Block-Big-Rug", (246, 20), (257, 3));
            Fixture("Furniture-Forest-Hit-Sword-Mount", (248, 20), (266, 0));
            Fixture("Shield-Half-Accuracy", (253, 2));
            // Event 1013 establishes the displayed-furniture flags. Collect the existing formatted
            // event, not a replacement script or a full game archive.
            using (var stream = File.OpenRead(image))
            {
                var library = new EventLibrary(new GameArchives(new IsoImage(stream)));
                if (library.Numbers.Contains(1013))
                    File.WriteAllText(Path.Combine(folder, $"Furniture-Event1013-{edition}.txt"), library.Text(1013));
                else Console.WriteLine("Event 1013 not found; remaining furniture mapping stays unresolved.");
                var index = ScriptDependencyIndex.Build(library.Numbers, library.Bytes, library.CommandNames);
                var references = new List<string> { "Piece\tEvent\tKind\tReason", $"# Indexed {index.IndexedEvents} events; unreadable {index.UnreadableEvents.Count}; literal flag references are candidates only." };
                foreach (int piece in FurniturePrices.Names.Keys.Order())
                {
                    foreach (var link in FurnitureEvents.Known(piece))
                        references.Add($"{piece}\t{link.Event}\t{(library.Numbers.Contains(link.Event) ? "Known" : "Unavailable")}\t{link.Reason}");
                    foreach (var link in FurnitureEvents.Discover(piece, index))
                        references.Add($"{piece}\t{link.Event}\tLiteral reference\t{link.Reason}");
                }
                File.WriteAllLines(Path.Combine(folder, $"Furniture-EventReferences-{edition}.tsv"), references);
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        finally { if (exported != null && File.Exists(exported)) File.Delete(exported); }
    }
}
