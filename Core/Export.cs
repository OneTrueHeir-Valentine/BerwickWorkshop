using System.Security.Cryptography;

namespace Berwick.Core;

public sealed record DiscPatch(long Offset, byte[] Bytes);
public sealed record ExportResult(string Path, long Size, string Sha256, int PatchCount, int ChangedBytes);

public static class Exporter
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const long SpaceMargin = 64L * 1024 * 1024;

    /// <summary>Positions of a record inside DATA3.DAT, one for each copy of its table.</summary>
    public static long[] RecordLocations(InspectionResult disc, string table, int id)
    {
        if (disc.Profile is not { } p) throw new EditException("Only a verified disc can be exported.");
        return table switch
        {
            EditSession.Items => [p.ItemTable + (id - 1) * 56L, p.SecondItemTable + (id - 1) * 56L],
            EditSession.Characters => disc.CharacterOffsets.TryGetValue(id, out int offset)
                ? [p.UnitTable + offset, p.SecondUnitTable + offset] : throw new EditException($"Character 0x{id:X3} is not on this disc."),
            EditSession.Growths => [p.GrowthTable + (id - 1) * (long)CharacterDecoder.GrowthLength, p.SecondGrowthTable + (id - 1) * (long)CharacterDecoder.GrowthLength],
            EditSession.Classes => [p.ClassTable + (id - 1) * (long)ClassDecoder.RecordLength, p.SecondClassTable + (id - 1) * (long)ClassDecoder.RecordLength],
            _ => throw new EditException("This project contains changes this version cannot export."),
        };
    }

    /// <summary>Byte patches for every edited record: only the bytes that differ, written to both copies of each table.</summary>
    public static IReadOnlyList<DiscPatch> PatchesFor(EditSession session, InspectionResult disc)
    {
        var data = disc.Files.Single(e => e.Name.Equals("DATA3.DAT", StringComparison.OrdinalIgnoreCase));
        var patches = new List<DiscPatch>();
        foreach (var (table, id) in session.EditedRecords)
        {
            var original = session.Table(table).Records[id].Span;
            var edited = session.EditedBytes(table, id);
            var runs = new List<(int Start, int Length)>();
            for (int i = 0; i < edited.Length; i++)
            {
                if (edited[i] == original[i]) continue;
                int start = i;
                while (i < edited.Length && edited[i] != original[i]) i++;
                runs.Add((start, i - start));
            }
            foreach (long location in RecordLocations(disc, table, id))
            {
                if (location < 0 || location + edited.Length > data.Length) throw new InvalidDataException("The game tables are incomplete.");
                foreach (var (start, length) in runs) patches.Add(new(data.Offset + location + start, edited.AsSpan(start, length).ToArray()));
            }
        }
        return patches.AsReadOnly();
    }

    public static ExportResult Export(string sourcePath, string expectedSha256, string destinationPath, IReadOnlyList<DiscPatch> patches,
        IProgress<InspectionProgress>? progress = null, CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath), destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new EditException("Choose a new file name. The original disc image is never overwritten.");
        if (patches.Count == 0) throw new EditException("The project has no changes to export.");

        var ordered = patches.OrderBy(p => p.Offset).ToList();
        long sourceLength = new FileInfo(source).Length;
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            if (p.Offset < 0 || p.Bytes.Length == 0 || p.Offset + p.Bytes.Length > sourceLength) throw new EditException("A change falls outside the disc image.");
            if (i > 0 && ordered[i - 1].Offset + ordered[i - 1].Bytes.Length > p.Offset) throw new EditException("Two changes overlap.");
        }
        string? folder = Path.GetDirectoryName(destination);
        if (folder is null || !Directory.Exists(folder)) throw new EditException("The destination folder does not exist.");
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.IsReady && drive.AvailableFreeSpace < sourceLength + SpaceMargin)
            throw new EditException($"There is not enough free space on {drive.Name.TrimEnd('\\')}. Exporting needs about {(sourceLength + SpaceMargin) / (1024.0 * 1024 * 1024):0.0} GB.");

        string partial = destination + ".partial";
        int changed = 0;
        string outputHash;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            using (var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var exportHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
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
                    var back = new byte[p.Bytes.Length];
                    check.Position = p.Offset;
                    check.ReadExactly(back);
                    if (!back.AsSpan().SequenceEqual(p.Bytes)) throw new IOException("The exported disc could not be verified.");
                }
            }
            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return new(destination, sourceLength, outputHash, ordered.Count, changed);
    }
}
