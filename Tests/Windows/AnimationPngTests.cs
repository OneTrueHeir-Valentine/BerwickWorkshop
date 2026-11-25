using Berwick.Core;
using Berwick.Workshop;
using System.Text.Json;

internal static class AnimationPngTests
{
    public static void Run(Action<string, Action> test)
    {
        test("PNG encoding preserves BGRA and alpha on a worker thread", () =>
        {
            var image = new AnimationImage(2, 2, [0, 0, 0, 0, 10, 20, 30, 128, 255, 120, 0, 255, 3, 4, 5, 255]);
            using var stream = new MemoryStream();
            Task.Run(() => AnimationPng.Write(stream, image)).GetAwaiter().GetResult();
            stream.Position = 0;
            PngTestImages.Equal(image, PngTestImages.Read(stream));
        });
        test("Effect PNG preserves independently expected CLUT colors and transparency", () =>
        {
            var set = new AnimationFixture().Set();
            var image = set.DrawEffect(1, 5, new(-3, -1, 8, 8));
            var pixels = new byte[8 * 8 * 4];
            new byte[] { 33, 22, 11, 255, 66, 55, 44, 255 }.CopyTo(pixels, 0);
            var expected = new AnimationImage(8, 8, pixels);
            using var stream = new MemoryStream();
            AnimationPng.Write(stream, image);
            stream.Position = 0;
            PngTestImages.Equal(expected, PngTestImages.Read(stream));
        });
        test("Body export PNG frames, sheet cells and origin metadata match rendered source", () =>
        {
            var set = new AnimationFixture().Set();
            foreach (int palette in new[] { 5, 6 })
            {
                using var directory = new ExportTestDirectory();
                var plans = set.Motions.Where(m => set.Frames(m).Count > 0)
                    .Select(m => AnimationExport.Plan(m, set.Frames(m), picture => set.Draw(m, picture, palette), true, 512)).ToArray();
                Check(plans.Length == 2, "Fixture does not cover multiple motions");
                AnimationExport.Write(directory.Target(), new("Synthetic", "fixture", set.Number, palette), plans,
                    (motion, picture) => set.Draw(motion, picture, palette), AnimationPng.Write, true, true);
                foreach (var plan in plans)
                {
                    string folder = Path.Combine(directory.Target(), plan.Motion);
                    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "animation.json")));
                    var doc = json.RootElement;
                    Check(doc.GetProperty("version").GetInt32() == 2, "Wrong metadata version");
                    Check(doc.GetProperty("source").GetProperty("palette").GetInt32() == palette, "Palette metadata changed");
                    var canvas = doc.GetProperty("canvas");
                    Check(canvas.GetProperty("originX").GetInt32() == plan.OriginX && canvas.GetProperty("originY").GetInt32() == plan.OriginY, "Canvas origin lost");
                    var rows = doc.GetProperty("frames");
                    Check(rows.GetArrayLength() == plan.Frames.Count, "Timing rows missing");
                    long ticks = 0;
                    foreach (var frame in plan.Frames)
                    {
                        var expected = AnimationExport.Crop(set.Draw(plan.Motion, frame.Picture, palette), plan.Crop);
                        PngTestImages.Equal(expected, PngTestImages.Load(Path.Combine(folder, frame.File)));
                        var sheet = PngTestImages.Load(Path.Combine(folder, plan.Sheets[frame.Sheet].File));
                        PngTestImages.Equal(expected, AnimationExport.Crop(sheet, new(frame.X, frame.Y, frame.Width, frame.Height)));
                        Check(rows[frame.Index].GetProperty("startTick").GetInt64() == ticks &&
                            rows[frame.Index].GetProperty("durationTicks").GetInt32() == frame.DurationTicks, "Serialized timing changed");
                        ticks += frame.DurationTicks;
                    }
                }
            }
        });
        test("Effect export PNGs preserve untransformed source artwork", () =>
        {
            var set = new AnimationFixture().Set();
            using var directory = new ExportTestDirectory();
            EffectExport.Write(directory.Target(), new("Synthetic", "fixture", set.Number, 5), set, ["m2_1"], AnimationPng.Write);
            foreach (var track in set.EffectTracks("m2_1"))
            {
                var canvas = set.EffectCanvas(track);
                foreach (var record in track.Records.Where(r => r.Visible))
                {
                    var expected = set.DrawEffect(record.Picture, 5, canvas);
                    var png = PngTestImages.Load(Path.Combine(directory.Target(), "m2_1", $"track-{track.Number:D3}", $"record-{record.Index:D4}.png"));
                    PngTestImages.Equal(expected, png);
                    int x = (record.Picture == 1 ? -3 : 290) - canvas.X;
                    int y = (record.Picture == 1 ? -1 : 291) - canvas.Y;
                    Check(png.Pixels.AsSpan((y * png.Width + x) * 4, 8).SequenceEqual(new byte[] { 33, 22, 11, 255, 66, 55, 44, 255 }), "Source tile moved or transformed");
                }
            }
        });
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
