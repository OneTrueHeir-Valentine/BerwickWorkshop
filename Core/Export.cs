using System.Security.Cryptography;

namespace Berwick.Core;

public sealed record DiscPatch(long Offset, byte[] Bytes, byte[]? Expected = null);
public sealed record ExportResult(string Path, long Size, string Sha256, int PatchCount, int ChangedBytes);

public static class Exporter
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const long SpaceMargin = 64L * 1024 * 1024;

    /// <summary>Where a record lives on the disc: the file that holds it and its offset inside that file, once for each copy.</summary>
    public static IReadOnlyList<(IsoEntry File, long Offset)> RecordLocations(InspectionResult disc, string table, int id)
    {
        if (disc.Profile is not { } p) throw new EditException("Only a verified disc can be exported.");
        var data = disc.Files.Single(e => e.Name.Equals("DATA3.DAT", StringComparison.OrdinalIgnoreCase));
        long[] inData = table switch
        {
            EditSession.Items => [p.ItemTable + (id - 1) * 56L, p.SecondItemTable + (id - 1) * 56L],
            EditSession.Characters => disc.CharacterOffsets.TryGetValue(id, out int offset)
                ? [p.UnitTable + offset, p.SecondUnitTable + offset] : throw new EditException($"Character 0x{id:X3} is not on this disc."),
            EditSession.Growths => [p.GrowthTable + (id - 1) * (long)CharacterDecoder.GrowthLength, p.SecondGrowthTable + (id - 1) * (long)CharacterDecoder.GrowthLength],
            EditSession.Classes => [p.ClassTable + (id - 1) * (long)ClassDecoder.RecordLength, p.SecondClassTable + (id - 1) * (long)ClassDecoder.RecordLength],
            EditSession.Terrain => [p.TerrainTable + TerrainData.HeaderLength + id * (long)TerrainData.RowLength, p.SecondTerrainTable + TerrainData.HeaderLength + id * (long)TerrainData.RowLength],
            EditSession.TerrainKinds => [p.TerrainTable + TerrainKindData.OffsetFromCosts + TerrainKindData.HeaderLength + id * (long)TerrainKindData.RowLength,
                p.SecondTerrainTable + TerrainKindData.OffsetFromCosts + TerrainKindData.HeaderLength + id * (long)TerrainKindData.RowLength],
            EditSession.Partners => disc.PartnerRows.TryGetValue(id, out int row)
                ? [p.PartnerTable + row * (long)PartnerData.RowLength, p.SecondPartnerTable + row * (long)PartnerData.RowLength] : throw new EditException($"Character 0x{id:X3} has no partner row on this disc."),
            EditSession.Bags => disc.CharacterOffsets.TryGetValue(id, out int bagAt) && disc.BagBytes.ContainsKey(id)
                ? [p.UnitTable + bagAt + BagData.OffsetFromRecord, p.SecondUnitTable + bagAt + BagData.OffsetFromRecord] : throw new EditException($"Character 0x{id:X3} has no starting bag on this disc."),
            EditSession.AnimationSets or EditSession.AnimationPalettes => disc.AnimationRowOffsets.TryGetValue(id, out long row)
                ? [row] : throw new EditException("This project changes a battle animation row that is not on this disc."),
            EditSession.Rules or EditSession.Mercenaries or EditSession.Recipes or EditSession.Dishes or EditSession.Tastes or EditSession.Deployments or EditSession.Placements or EditSession.ArmyUnits or EditSession.MapSprites => [],
            _ => throw new EditException("This project contains changes this version cannot export."),
        };
        if (table == EditSession.Rules) throw new EditException("Rules are written instruction by instruction; use PatchesFor.");
        if (table == EditSession.Recipes)
        {
            var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
            return disc.RecipeRows.TryGetValue(id, out int recipeRow)
                ? [(exe, RecipeData.FileOffset + recipeRow * (long)RecipeData.RowLength)] : throw new EditException($"Item 0x{id:X3} has no Atelier recipe on this disc.");
        }
        if (table == EditSession.Dishes)
        {
            var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
            return disc.DishRows.TryGetValue(id, out int dishRow)
                ? [(exe, DishData.FileOffset + dishRow * (long)DishData.RowLength)] : throw new EditException($"Dish {id} is not on this disc.");
        }
        if (table == EditSession.Tastes)
        {
            var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
            return disc.TasteRows.TryGetValue(id, out int tasteRow)
                ? [(exe, TasteData.FileOffset + tasteRow * (long)TasteData.RowLength)] : throw new EditException($"Character 0x{id:X3} has no tastes on this disc.");
        }
        if (table == EditSession.MapSprites)
        {
            var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
            return id >= 1 && id <= MapSpriteData.Classes && disc.MapSpriteBytes.ContainsKey(id)
                ? [(exe, MapSpriteData.FileOffset + (id - 1) * (long)MapSpriteData.RowLength)] : throw new EditException($"Class {id} has no map sprite row on this disc.");
        }
        if (table == EditSession.Mercenaries)
        {
            var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
            return disc.MercenaryRows.TryGetValue(id, out int row)
                ? [(exe, MercenaryData.FileOffset + row * (long)MercenaryData.RowLength)] : throw new EditException($"Character 0x{id:X3} is not a mercenary on this disc.");
        }
        return inData.Select(o => (data, o)).ToList();
    }

    /// <summary>Byte patches for every edited record: only the bytes that differ, written to both copies of each table.</summary>
    public static IReadOnlyList<DiscPatch> PatchesFor(EditSession session, InspectionResult disc)
    {
        if (!string.Equals(session.Disc.Sha256, disc.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException("This project belongs to a different disc image. Open its original disc before exporting.");
        var patches = new List<DiscPatch>();
        var stable = new Dictionary<int, int>();
        var furniture = new Dictionary<int, int>();
        var scripts = new Dictionary<int, Dictionary<int, int>>();
        var replaced = new Dictionary<int, byte[]>();
        var deployments = new Dictionary<int, byte[]>();
        var placements = new Dictionary<int, byte[]>();
        var changedTexts = session.ChangedTexts;
        foreach (var (table, id) in session.EditedRecords)
        {
            if (table == EditSession.Scripts)
            {
                replaced[id] = session.EventScript(id)!;
                continue;
            }
            if (table == EditSession.ScriptValues)
            {
                var (eventNumber, index) = ScriptValues.Split(id);
                (scripts.TryGetValue(eventNumber, out var numbers) ? numbers : scripts[eventNumber] = [])[index] = session.Value(table, id, ScriptValues.Field.Key);
                continue;
            }
            if (table == EditSession.Deployments)
            {
                deployments[id] = session.EditedBytes(table, id);
                continue;
            }
            if (table == EditSession.Texts) continue;
            if (table == EditSession.Placements)
            {
                placements[id] = session.EditedBytes(table, id);
                continue;
            }
            if (table == EditSession.StablePrices)
            {
                stable[id] = session.Value(table, id, StablePrices.Field.Key);
                continue;
            }
            if (table == EditSession.FurniturePrices)
            {
                furniture[id] = session.Value(table, id, FurniturePrices.Table.Field.Key);
                continue;
            }
            var original = session.Table(table).Records[id].Span;
            var edited = session.EditedBytes(table, id);
            if (table == EditSession.Rules)
            {
                // Each instruction of a rule lives at its own address in the game program.
                var exe = disc.ExecutableFile ?? throw new EditException("The game program was not found on this disc.");
                var rule = GameRules.ById(id);
                for (int i = 0; i < rule.Sites.Count; i++)
                {
                    if (edited.AsSpan(i * 4, 4).SequenceEqual(original.Slice(i * 4, 4))) continue;
                    long at = rule.Sites[i].FileOffset;
                    if (at < 0 || at + 4 > exe.Length) throw new InvalidDataException("The game program is incomplete.");
                    patches.Add(new(exe.Offset + at, edited.AsSpan(i * 4, 4).ToArray(), original.Slice(i * 4, 4).ToArray()));
                }
                continue;
            }
            var runs = new List<(int Start, int Length)>();
            if (table == EditSession.ArmyUnits)
            {
                // Army units are presented in the starting record layout; each changed byte goes back to its place in every copy of the army file.
                var unit = disc.Armies.SelectMany(a => a.Units).FirstOrDefault(u => u.Key == id) ?? throw new EditException("This project changes a unit that is not on this disc.");
                var army = disc.Armies.First(a => a.Id == unit.Army);
                for (int i = 0; i < edited.Length; i++)
                {
                    if (edited[i] == original[i]) continue;
                    if (i >= ArmyData.StatsLength && i < ArmyData.ItemListAt || i >= ArmyData.ItemListAt + ArmyData.ItemListLength)
                        throw new InvalidDataException("An army unit change falls outside the unit's data.");
                    int at = ArmyData.RealOffset(unit, i);
                    foreach (long location in army.Locations)
                        patches.Add(new(location + at, [edited[i]], [original[i]]));
                }
                continue;
            }
            for (int i = 0; i < edited.Length; i++)
            {
                if (edited[i] == original[i]) continue;
                int start = i;
                while (i < edited.Length && edited[i] != original[i]) i++;
                runs.Add((start, i - start));
            }
            foreach (var (file, location) in RecordLocations(disc, table, id))
            {
                if (location < 0 || location + edited.Length > file.Length) throw new InvalidDataException("The game data is incomplete.");
                foreach (var (start, length) in runs)
                    patches.Add(new(file.Offset + location + start, edited.AsSpan(start, length).ToArray(), original.Slice(start, length).ToArray()));
            }
        }
        if (stable.Count > 0 || furniture.Count > 0 || scripts.Count > 0 || replaced.Count > 0 || deployments.Count > 0 || placements.Count > 0 || changedTexts.Count > 0)
        {
            // Stable and furniture prices live in compressed event scripts, each stored several times and partly in the same
            // archives; every copy is rewritten in place, one archive at a time.
            using var stream = new FileStream(disc.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archives = new GameArchives(new IsoImage(stream));
            var events = new List<(ScriptPrices, IReadOnlyList<StableCopy>, byte[])>();
            var other = new List<(int Top, Func<byte[], byte[]> Edit)>();
            foreach (var (table, prices) in new[] { (StablePrices.Table, stable), (FurniturePrices.Table, furniture) })
            {
                if (prices.Count == 0 && !scripts.ContainsKey(table.EventNumber)) continue;
                var (original, decoded, copies) = table.Load(archives);
                var written = prices.Count > 0 ? table.Write(decoded, prices) : decoded;
                // Numbers changed in the same event on the Events page are written on top of the prices.
                if (scripts.Remove(table.EventNumber, out var numbers)) written = ScriptValues.Apply(written, numbers);
                events.Add((table, copies, written));
                // The furniture shop's list writes each price into its own text, so the list is changed too.
                if (table == FurniturePrices.Table) other.AddRange(FurnitureList.Edits(archives, original, prices));
            }
            // Other events edited on the Events page: every copy rewritten (moving files after it when it grew).
            foreach (var (eventNumber, numbers) in scripts.OrderBy(s => s.Key))
            {
                var (decoded, copies) = EventCopies.Load(archives, eventNumber);
                var edited = ScriptValues.Apply(decoded, numbers);
                foreach (var copy in copies) other.Add((copy.Top, top => EventCopies.Replace(top, eventNumber, copy, edited)));
            }
            foreach (var (eventNumber, edited) in replaced.OrderBy(s => s.Key))
            {
                var (_, copies) = EventCopies.Load(archives, eventNumber);
                foreach (var copy in copies) other.Add((copy.Top, top => EventCopies.Replace(top, eventNumber, copy, edited)));
            }
            // Deployment hexes and army placements: the map file in the battle's archive is rewritten with its new slots.
            foreach (int top in deployments.Keys.Union(placements.Keys).Order())
                other.Add((top, archive =>
                {
                    var entry = Tarc.Parse(archive).FirstOrDefault(e => e.Name == "map") ?? throw new EditException($"Map archive {top} has no map file.");
                    var map = Tarc.Extract(archive, entry);
                    if (deployments.TryGetValue(top, out var hexes)) map = Deployment.Write(map, hexes);
                    if (placements.TryGetValue(top, out var slots)) map = Placement.Write(map, slots);
                    return ArchiveEditing.ReplaceOrRepack(archive, "map", map);
                }));
            // Game texts: every copy of sysfont.ar gets the changed entries.
            if (changedTexts.Count > 0) other.AddRange(GameTexts.Edits(archives, changedTexts));
            patches.AddRange(ScriptPrices.Combined(archives, events, other, ArchiveEditing.FixedTops(archives, disc.Profile)));
        }
        return patches.AsReadOnly();
    }

    public static ExportResult Export(string sourcePath, string expectedSha256, string destinationPath, IReadOnlyList<DiscPatch> patches,
        IProgress<InspectionProgress>? progress = null, CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath), destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new EditException("Choose a new file name. The original disc image is never overwritten.");
        if (File.Exists(destination))
            throw new EditException("Choose a new file name. Existing disc images are never overwritten.");
        if (patches.Count == 0) throw new EditException("The project has no changes to export.");

        var ordered = patches.OrderBy(p => p.Offset).ToList();
        long sourceLength = new FileInfo(source).Length;
        if (PatchChecks.Analyze(ordered, sourceLength).FirstOrDefault() is { } problem)
            throw new EditException(problem.Message);
        string? folder = Path.GetDirectoryName(destination);
        if (folder is null || !Directory.Exists(folder)) throw new EditException("The destination folder does not exist.");
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.IsReady && drive.AvailableFreeSpace < sourceLength + SpaceMargin)
            throw new EditException($"There is not enough free space on {drive.Name.TrimEnd('\\')}. Exporting needs about {(sourceLength + SpaceMargin) / (1024.0 * 1024 * 1024):0.0} GB.");

        string partial = destination + $".{Guid.NewGuid():N}.partial";
        bool ownsPartial = false;
        int changed = 0;
        string outputHash;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                ownsPartial = true;
                using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var exportHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[BufferSize];
                long position = 0;
                int next = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    sourceHash.AppendData(buffer, 0, read);
                    long end = position + read;
                    while (next < ordered.Count && ordered[next].Offset + ordered[next].Bytes.Length <= position) next++;
                    for (int i = next; i < ordered.Count && ordered[i].Offset < end; i++)
                    {
                        var p = ordered[i];
                        long from = Math.Max(p.Offset, position), to = Math.Min(p.Offset + p.Bytes.Length, end);
                        for (long at = from; at < to; at++)
                        {
                            if (p.Expected is { } expected && buffer[at - position] != expected[at - p.Offset])
                                throw new EditException("The disc's data copies disagree at a value being changed. Export was stopped to preserve those differences.");
                            byte value = p.Bytes[at - p.Offset];
                            if (buffer[at - position] != value) changed++;
                            buffer[at - position] = value;
                        }
                    }
                    exportHash.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                    position = end;
                    progress?.Report(new((double)position / sourceLength, "Exporting disc"));
                }
                if (position != sourceLength) throw new IOException("The disc image could not be read completely.");
                string hash = Convert.ToHexString(sourceHash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new EditException("The original disc image has changed since it was opened. Open it again before exporting.");
                outputHash = Convert.ToHexString(exportHash.GetHashAndReset()).ToLowerInvariant();
                output.Flush(true);
            }

            progress?.Report(new(1, "Checking exported disc"));
            using (var check = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (check.Length != sourceLength) throw new IOException("The exported disc has the wrong size.");
                foreach (var p in ordered)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var back = new byte[p.Bytes.Length];
                    check.Position = p.Offset;
                    check.ReadExactly(back);
                    if (!back.AsSpan().SequenceEqual(p.Bytes)) throw new IOException("The exported disc could not be verified.");
                }
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
        }
        catch
        {
            if (ownsPartial)
                try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return new(destination, sourceLength, outputHash, ordered.Count, changed);
    }
}
