using System.Text.Json;
namespace Berwick.Core;

public static class EffectExport
{
    public const string Description = "Separate, untransformed effect artwork. Hidden records, source duration counters, links and blend settings are retained in JSON. PNGs do not apply offsets, scales, flips, keyframes or game blending. Linked motions are exported separately with All motions. Initial and terminal tick phase and runtime branches have not been simulated.";
    private sealed record BlendState(bool Enabled, int Mode, int A, int B, int C, int D, int FixedAlpha);
    private static BlendState Blend(EffectRecord record) => record.BlendMode switch
    {
        1 => new(record.BlendEnabled, 1, 0, 2, 0, 1, 128),
        2 => new(record.BlendEnabled, 2, 2, 0, 0, 1, 128),
        3 => new(record.BlendEnabled, 3, 0, 2, 2, 1, 32),
        _ => new(record.BlendEnabled, 0, 0, 1, 0, 1, 0)
    };

    public static void Write(string destination, AnimationExportSource source, AnimationSet set, IReadOnlyList<string> motions,
        Action<Stream, AnimationImage> encodePng, IProgress<AnimationExportProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (source.Set != set.Number || motions.Count == 0) throw new EditException("Choose an animation set and motion to export.");
        var tracks = new List<EffectTrack>();
        foreach (string motion in motions.Distinct(StringComparer.Ordinal))
        { cancellation.ThrowIfCancellationRequested(); tracks.AddRange(set.EffectTracks(motion)); }
        if (tracks.Count == 0) throw new EditException("These motions have no effect tracks. Choose All motions to include separately linked effects.");
        var palette = set.EffectPalette(source.Palette);
        destination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(destination) ?? throw new EditException("Choose an export location.");
        if (!Directory.Exists(parent) || File.Exists(destination) || Directory.Exists(destination)) throw new EditException("Choose a new export folder in an existing directory.");
        cancellation.ThrowIfCancellationRequested();
        string staging = Path.Combine(parent, ".berwick-effect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var ownedFiles = new List<string>(); var ownedFolders = new List<string> { staging };
        try
        {
            int completed = 0, total = tracks.Sum(t => t.Records.Count(r => r.Visible && r.Texture == 1 && r.Picture > 0));
            foreach (var group in tracks.GroupBy(t => t.Motion))
            {
                string motionFolder = Path.Combine(staging, group.Key); Directory.CreateDirectory(motionFolder); ownedFolders.Add(motionFolder);
                foreach (var track in group)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string folder = Path.Combine(motionFolder, $"track-{track.Number:D3}"); Directory.CreateDirectory(folder); ownedFolders.Add(folder);
                    var canvas = set.EffectCanvas(track);
                    foreach (var record in track.Records.Where(r => r.Visible && r.Texture == 1 && r.Picture > 0))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string path = Path.Combine(folder, $"record-{record.Index:D4}.png");
                        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { ownedFiles.Add(path); encodePng(file, set.DrawEffect(record.Picture, source.Palette, canvas)); }
                        progress?.Report(new(track.Motion, ++completed, total));
                    }
                    cancellation.ThrowIfCancellationRequested();
                    byte[] motionHeader = Convert.FromHexString(set.MotionHeaderHex(track.Motion));
                    var document = new
                    {
                        format = "Berwick Workshop effect layers", version = 1, source, motion = track.Motion, track = track.Number,
                        ticksPerSecond = 60, timing = "DurationCounter is the original source counter. Record order is storage order; NextIndex is the normal next-record edge, zero-based. A null edge marks no next record. Hidden records occupy time. Conditional branches and initial/terminal tick phase are not simulated.",
                        canvas = new { width = canvas.Width, height = canvas.Height, originX = canvas.X, originY = canvas.Y },
                        coordinates = "Original coordinate = canvas origin + PNG pixel position. Track offsets and other transforms are stored, not applied.",
                        palette = new { name = palette.Name, requestedPalette = source.Palette, sourceRgbaBase64 = Convert.ToBase64String(palette.Bytes),
                            layout = "256 RGBA entries; swap entries 8 to 15 with 16 to 23 in each group of 32. PNG index zero and source alpha zero are transparent; other pixels are opaque. Original alpha is retained in the source palette." },
                        motionHeaderHex = Convert.ToHexString(motionHeader), trackHeaderHex = track.HeaderHex, keyframesBase64 = track.KeyframesBase64,
                        linkedSlots = motionHeader.Skip(7).Take(4).Select((value, index) => new { slot = index, value }),
                        blendEquation = "(A - B) * C / 128 + D; A/B/D: 0 source, 1 destination, 2 zero. C: 0 source alpha, 1 destination alpha, 2 fixed alpha.",
                        records = track.Records.Select(r => new { source = r, file = r.Visible && r.Texture == 1 && r.Picture > 0 ? $"record-{r.Index:D4}.png" : null,
                            effectiveDepth = r.Depth == 0 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(Convert.FromHexString(track.HeaderHex).AsSpan(14)) : r.Depth, blend = Blend(r) }), limitations = Description
                    };
                    string json = Path.Combine(folder, "effect.json");
                    using var output = new FileStream(json, FileMode.CreateNew, FileAccess.Write, FileShare.None); ownedFiles.Add(json);
                    JsonSerializer.Serialize(output, document, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                }
            }
            cancellation.ThrowIfCancellationRequested(); Directory.Move(staging, destination);
        }
        catch (Exception original)
        {
            try
            {
                foreach (string path in ownedFiles.AsEnumerable().Reverse()) File.Delete(path);
                foreach (string path in ownedFolders.AsEnumerable().Reverse()) Directory.Delete(path, false);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException($"Export stopped, but some partial files remain in {staging}.", original); }
            throw;
        }
    }
}
