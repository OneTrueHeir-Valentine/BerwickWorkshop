using System.Text.Json;
using System.Text.RegularExpressions;

namespace Berwick.Core;

public sealed record AnimationRect(int X, int Y, int Width, int Height);
public sealed record AnimationExportSource(string Edition, string SourceSha256, int Set, int Palette);
public sealed record AnimationExportFrame(int Index, int Picture, int DurationTicks, long StartTick,
    int Sheet, int X, int Y, int Width, int Height, string File);
public sealed record AnimationSheet(int Page, string File, int Width, int Height);
public sealed record AnimationMotionExport(string Motion, int CanvasWidth, int CanvasHeight, AnimationRect Crop,
    IReadOnlyList<AnimationExportFrame> Frames, IReadOnlyList<AnimationSheet> Sheets)
{
    public int OriginX { get; init; }
    public int OriginY { get; init; }
}
public sealed record AnimationExportProgress(string Motion, int Completed, int Total);

public static class AnimationExport
{
    public const int SheetLimit = 4096;
    public const string Limitations = "Exports the preview's body, weapon and shield layers. Effects and source alpha blending are not reproduced. Expanded canvases retain artwork beyond the original frame area. Legacy 288 by 288 output can clip it. The selected set and palette do not resolve every held-weapon variant.";

    public static AnimationMotionExport Plan(string motion, IReadOnlyList<AnimationFrame> frames,
        Func<int, AnimationImage> draw, bool crop, int sheetLimit = SheetLimit, CancellationToken cancellation = default)
    {
        if (!Regex.IsMatch(motion, @"\Am[0-9]+_[0-9]+\z", RegexOptions.CultureInvariant) || motion.Length > 40)
            throw new EditException("The motion name is not valid for export.");
        if (frames.Count == 0 || frames.Count > 65536 || sheetLimit < 1 || sheetLimit > SheetLimit)
            throw new EditException("The motion has no frames or is too large to export.");
        int originX = 0, originY = 0;
        int width = 0, height = 0, left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        foreach (var frame in frames.DistinctBy(f => f.Picture))
        {
            cancellation.ThrowIfCancellationRequested();
            var image = draw(frame.Picture); Validate(image);
            if (width != 0 && (width != image.Width || height != image.Height || originX != image.OriginX || originY != image.OriginY)) throw new EditException("Animation frame sizes do not agree.");
            width = image.Width; height = image.Height; originX = image.OriginX; originY = image.OriginY;
            if (crop)
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (image.Pixels[(y * width + x) * 4 + 3] != 0)
                        { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        }
        var rect = crop && right >= 0 ? new AnimationRect(left, top, right - left + 1, bottom - top + 1) : new(0, 0, width, height);
        if (rect.Width > sheetLimit || rect.Height > sheetLimit) throw new EditException("A frame exceeds the sprite sheet size limit.");
        int columns = Math.Min(frames.Count, sheetLimit / rect.Width), rows = sheetLimit / rect.Height;
        int capacity = checked(columns * rows);
        var pages = new List<AnimationSheet>();
        for (int first = 0; first < frames.Count; first += capacity)
        {
            int count = Math.Min(capacity, frames.Count - first), page = pages.Count;
            pages.Add(new(page, $"sheet-{page + 1:D3}.png", Math.Min(columns, count) * rect.Width, ((count + columns - 1) / columns) * rect.Height));
        }
        var output = new List<AnimationExportFrame>(); long tick = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Time <= 0) throw new EditException("Animation frame durations must be positive.");
            int cell = i % capacity;
            output.Add(new(i, frame.Picture, frame.Time, tick, i / capacity, cell % columns * rect.Width, cell / columns * rect.Height,
                rect.Width, rect.Height, $"frame-{i + 1:D4}.png"));
            tick = checked(tick + frame.Time);
        }
        return new(motion, width, height, rect, output.AsReadOnly(), pages.AsReadOnly()) { OriginX = originX, OriginY = originY };
    }

    public static AnimationImage Crop(AnimationImage image, AnimationRect crop)
    {
        Validate(image);
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || crop.X > image.Width - crop.Width || crop.Y > image.Height - crop.Height)
            throw new EditException("The animation crop is outside the canvas.");
        var bytes = new byte[checked(crop.Width * crop.Height * 4)];
        for (int y = 0; y < crop.Height; y++)
            Buffer.BlockCopy(image.Pixels, ((y + crop.Y) * image.Width + crop.X) * 4, bytes, y * crop.Width * 4, crop.Width * 4);
        return new(crop.Width, crop.Height, bytes) { OriginX = checked(image.OriginX + crop.X), OriginY = checked(image.OriginY + crop.Y) };
    }

    private static void Validate(AnimationImage image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Width > SheetLimit || image.Height > SheetLimit || image.Pixels.LongLength != (long)image.Width * image.Height * 4)
            throw new EditException("The animation image dimensions are not valid.");
    }

    public static void Write(string destination, AnimationExportSource source, IReadOnlyList<AnimationMotionExport> motions,
        Func<string, int, AnimationImage> draw, Action<Stream, AnimationImage> encodePng, bool frames, bool sheets,
        IProgress<AnimationExportProgress>? progress = null, CancellationToken cancellation = default)
    {
        if (!frames && !sheets || motions.Count == 0 || motions.Select(m => m.Motion).Distinct(StringComparer.OrdinalIgnoreCase).Count() != motions.Count)
            throw new EditException("Choose at least one motion and output type.");
        foreach (var motion in motions)
        {
            if (!Regex.IsMatch(motion.Motion, @"\Am[0-9]+_[0-9]+\z", RegexOptions.CultureInvariant) || motion.Motion.Length > 40)
                throw new EditException("The motion name is not valid for export.");
            bool SafeFile(string file) => Regex.IsMatch(file, @"\A(?:frame|sheet)-[0-9]+\.png\z", RegexOptions.CultureInvariant);
            if (motion.Frames.Count == 0 || motion.Sheets.Count == 0 ||
                motion.Frames.Any(f => !SafeFile(f.File)) || motion.Sheets.Any(s => !SafeFile(s.File)) ||
                motion.Sheets.Select(s => s.Page).Distinct().Count() != motion.Sheets.Count ||
                motion.Sheets.Any(s => s.Width <= 0 || s.Height <= 0 || s.Width > SheetLimit || s.Height > SheetLimit) ||
                motion.Frames.Any(f => f.DurationTicks <= 0 || f.Width != motion.Crop.Width || f.Height != motion.Crop.Height ||
                    f.Width <= 0 || f.Height <= 0 || !motion.Sheets.Any(s => s.Page == f.Sheet && f.X >= 0 && f.Y >= 0 && f.X <= s.Width - f.Width && f.Y <= s.Height - f.Height)))
                throw new EditException("The animation export layout is not valid.");
        }
        destination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(destination) ?? throw new EditException("Choose an export folder.");
        if (!Directory.Exists(parent) || File.Exists(destination) || Directory.Exists(destination)) throw new EditException("Choose a new export folder in an existing directory.");
        cancellation.ThrowIfCancellationRequested();
        // The final move refuses a destination created by another process while export was running.
        string staging = Path.Combine(parent, ".berwick-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var ownedFiles = new List<string>(); var ownedFolders = new List<string> { staging };
        try
        {
            int done = 0, total = motions.Sum(m => m.Frames.Count);
            foreach (var motion in motions)
            {
                cancellation.ThrowIfCancellationRequested();
                string folder = Path.Combine(staging, motion.Motion); Directory.CreateDirectory(folder); ownedFolders.Add(folder);
                void ImageFile(string name, AnimationImage image)
                {
                    string path = Path.Combine(folder, name);
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    ownedFiles.Add(path); encodePng(stream, image);
                }
                foreach (var page in motion.Sheets)
                {
                    cancellation.ThrowIfCancellationRequested();
                    AnimationImage? sheet = sheets ? new(page.Width, page.Height, new byte[checked(page.Width * page.Height * 4)]) : null;
                    if (sheet is not null) Validate(sheet);
                    foreach (var frame in motion.Frames.Where(f => f.Sheet == page.Page))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var image = draw(motion.Motion, frame.Picture);
                        if (image.Width != motion.CanvasWidth || image.Height != motion.CanvasHeight || image.OriginX != motion.OriginX || image.OriginY != motion.OriginY) throw new EditException("The animation canvas changed during export.");
                        var cropped = Crop(image, motion.Crop);
                        if (frames) ImageFile(frame.File, cropped);
                        if (sheet is not null)
                            for (int y = 0; y < cropped.Height; y++)
                                Buffer.BlockCopy(cropped.Pixels, y * cropped.Width * 4, sheet.Pixels, ((frame.Y + y) * sheet.Width + frame.X) * 4, cropped.Width * 4);
                        progress?.Report(new(motion.Motion, ++done, total));
                    }
                    if (sheet is not null) ImageFile(page.File, sheet);
                }
                cancellation.ThrowIfCancellationRequested();
                var metadata = new { format = "Berwick Workshop animation", version = 2, source, motion = motion.Motion, ticksPerSecond = 60,
                    canvas = new { width = motion.CanvasWidth, height = motion.CanvasHeight, originX = motion.OriginX, originY = motion.OriginY },
                    coordinates = "Original coordinate = canvas origin + crop offset + pixel position within a frame",
                    crop = motion.Crop, frameImages = frames, sheetImages = sheets, frames = motion.Frames, sheets = sheets ? motion.Sheets : [], limitations = Limitations };
                string json = Path.Combine(folder, "animation.json");
                using var output = new FileStream(json, FileMode.CreateNew, FileAccess.Write, FileShare.None); ownedFiles.Add(json);
                JsonSerializer.Serialize(output, metadata, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        catch (Exception original)
        {
            try
            {
                foreach (string file in ownedFiles.AsEnumerable().Reverse()) File.Delete(file);
                foreach (string folder in ownedFolders.AsEnumerable().Reverse()) Directory.Delete(folder, false);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException($"Export stopped, but some partial files remain in {staging}.", original); }
            throw;
        }
    }
}
