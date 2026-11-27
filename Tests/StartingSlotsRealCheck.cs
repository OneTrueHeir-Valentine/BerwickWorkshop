using Berwick.Core;

internal static class StartingSlotsRealCheck
{
    internal static int Run(string image, string folder)
    {
        string? exported = null;
        try
        {
            var disc = Inspector.Inspect(image);
            if (!disc.Supported || disc.IsModified || !disc.CharacterCopiesMatch || disc.DifferentTableRecords != 0)
                throw new InvalidOperationException("Select an original supported image with matching tables.");
            Directory.CreateDirectory(folder);
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace < disc.Size + 64L * 1024 * 1024)
                throw new IOException("Not enough space for a temporary exported image.");
            const int character = 83; // Reese, available at the beginning of the campaign.
            int[] items = [0x1CD, 0x1CE, 0x1CF]; // Vulnerary, Concoction, Elixir.
            var session = new EditSession(disc);
            var edits = new List<FieldEdit>();
            for (int slot = 1; slot <= CharacterFields.ItemSlots; slot++)
            {
                if (session.Value(EditSession.Characters, character, $"item{slot}") != 0)
                {
                    if (slot > CharacterFields.VerifiedItemSlots) throw new InvalidOperationException($"Starting slot {slot} was not empty on the original.");
                    continue;
                }
                int id = items[slot % items.Length];
                if (!disc.ItemBytes.ContainsKey(id) || disc.Items.Single(i => i.Id == id).IsUnused)
                    throw new InvalidOperationException($"Test item {id:X3} is unavailable.");
                edits.Add(new(EditSession.Characters, character, $"item{slot}", id));
                edits.Add(new(EditSession.Characters, character, $"item{slot}Uses", disc.Items.Single(i => i.Id == id).Uses));
            }
            session.Apply(edits);
            string edition = disc.Edition.Contains("English", StringComparison.OrdinalIgnoreCase) ? "EN" : "JP";
            string project = Path.Combine(folder, $"Starting-Slots-{edition}.bwproj");
            session.Save(project);
            var reopened = EditSession.FromJson(disc, session.ToJson());
            var patches = Exporter.PatchesFor(session, disc);
            var other = Exporter.PatchesFor(reopened, disc);
            if (patches.Count != other.Count || !patches.Zip(other).All(p => p.First.Offset == p.Second.Offset && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)))
                throw new InvalidOperationException("Project reopen changed the export plan.");
            exported = Path.Combine(folder, $"starting-slots-{Guid.NewGuid():N}.iso");
            Exporter.Export(disc.Path, disc.Sha256, exported, patches);
            var back = Inspector.Inspect(exported);
            if (!back.Supported || !back.IsModified || !back.CharacterCopiesMatch || back.DifferentTableRecords != 0)
                throw new InvalidOperationException("Edited character table copies or modified disc recognition failed.");
            foreach (var row in disc.UnitBytes)
                if (!back.UnitBytes[row.Key].Span.SequenceEqual(session.EditedBytes(EditSession.Characters, row.Key)))
                    throw new InvalidOperationException($"Character readback mismatch: {row.Key:X3}.");
            foreach (var row in disc.BagBytes)
                if (!back.BagBytes.TryGetValue(row.Key, out var bag) || !bag.Span.SequenceEqual(row.Value.Span))
                    throw new InvalidOperationException($"Unrelated bag changed: {row.Key:X3}.");
            foreach (var row in disc.ItemBytes)
                if (!back.ItemBytes[row.Key].Span.SequenceEqual(row.Value.Span))
                    throw new InvalidOperationException($"Unrelated item record changed: {row.Key:X3}.");
            if (Inspector.Inspect(image).Sha256 != disc.Sha256) throw new InvalidOperationException("Original image hash changed.");
            Console.WriteLine($"PASS {disc.Edition}: {edits.Count} field requests, {patches.Count} patches; character copies and bags verified.");
            Console.WriteLine($"Original SHA256 {disc.Sha256}; temporary export SHA256 {back.Sha256}.");
            Console.WriteLine($"Saved isolated gameplay fixture: {project}. Test a fresh game, deployment, inventory and normal save/reload; existing saves may not reinitialize starting records.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (exported != null && File.Exists(exported)) File.Delete(exported); }
    }
}
