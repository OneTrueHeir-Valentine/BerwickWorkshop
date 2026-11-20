using Berwick.Core;
using System.Text.Json;

internal static class AnimationExportTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Animation canvas expands safely and preserves original coordinates", () =>
        {
            Check(AnimationCanvas.Expand([new(5, 6, 8, 8)]) == new AnimationRect(0, 0, 288, 288), "Unchanged canvas shifted");
            Check(AnimationCanvas.Expand([new(-7, -3, 8, 8), new(380, 380, 8, 8)]) == new AnimationRect(-7, -3, 395, 391), "Expanded canvas clipped");
            Check(AnimationCanvas.Expand([]) == new AnimationRect(0, 0, 288, 288), "Blank canvas changed");
            Reject(() => AnimationCanvas.Expand([new(int.MaxValue, 0, 8, 8)]));
            Reject(() => AnimationCanvas.Expand([new(int.MinValue, 0, 8, 8)]));
            var image = Draw(1) with { OriginX = -7, OriginY = -3 };
            var plan = AnimationExport.Plan("m0_0", [new(1, 1)], _ => image, true);
            Check(plan.OriginX == -7 && plan.OriginY == -3, "Export lost canvas origin");
            var cropped = AnimationExport.Crop(image, plan.Crop);
            Check(cropped.OriginX == -6 && cropped.OriginY == -2, "Crop lost original coordinates");
            Reject(() => AnimationExport.Plan("m0_0", [new(1, 1), new(2, 1)], p => image with { OriginX = p }, false));
        });
        test("Animation layout preserves frame order, ticks and shared crop", () =>
        {
            var frames = new AnimationFrame[] { new(2, 3), new(1, 5), new(2, 2) };
            var plan = AnimationExport.Plan("m0_0", frames, Draw, true, 4);
            Check(plan.Crop == new AnimationRect(1, 1, 2, 1), "Wrong shared bounds");
            Check(plan.Frames.Select(f => f.Picture).SequenceEqual(new[] { 2, 1, 2 }), "Repeated frame omitted");
            Check(plan.Frames.Select(f => f.StartTick).SequenceEqual(new long[] { 0, 3, 8 }), "Timing changed");
            Check(plan.Frames.All(f => f.Width == 2 && f.Height == 1), "Frame alignment changed");
            Check(plan.Sheets.Count == 1 && plan.Sheets[0].Width == 4 && plan.Sheets[0].Height == 2, "Wrong sheet layout");
            Check(plan.Frames[2].X == 0 && plan.Frames[2].Y == 1, "Wrong sheet rectangle");
            var split = AnimationExport.Plan("m0_0", frames, Draw, false, 4);
            Check(split.Sheets.Count == 3, "Large sheet was not split");
        });
        test("Animation export retains blank canvas and rejects unsafe inputs", () =>
        {
            var blank = AnimationExport.Plan("m0_0", [new(1, 1)], _ => new(4, 4, new byte[64]), true, 4);
            Check(blank.Crop == new AnimationRect(0, 0, 4, 4), "Blank image lost its canvas");
            Reject(() => AnimationExport.Plan("../escape", [new(1, 1)], Draw, false));
            Reject(() => AnimationExport.Plan("m0_0", [new(1, 0)], Draw, false));
            Reject(() => AnimationExport.Plan("m0_0", [], Draw, false));
            Reject(() => AnimationExport.Plan("m0_0", [new(1, 1)], _ => new(4, 4, []), true));
        });
        test("Animation file export protects existing output and cleans canceled output", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "berwick-animation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string target = Path.Combine(root, "export");
                var source = new AnimationExportSource("Test", "hash", 7, 0);
                var motion = AnimationExport.Plan("m0_0", [new(2, 3), new(1, 5), new(2, 2)], Draw, true, 4);
                var escaped = motion with { Frames = motion.Frames.Select(f => f with { File = "../../escape.png" }).ToArray() };
                Reject(() => AnimationExport.Write(target, source, [escaped], (_, picture) => Draw(picture), Encode, true, true));
                AnimationExport.Write(target, source, [motion], (_, picture) => Draw(picture), Encode, true, true);
                Check(File.Exists(Path.Combine(target, "m0_0", "frame-0001.png")), "Frame missing");
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "m0_0", "animation.json")));
                Check(json.RootElement.GetProperty("frames").GetArrayLength() == 3, "Manifest incomplete");
                Reject(() => AnimationExport.Write(target, source, [motion], (_, picture) => Draw(picture), Encode, true, true));
                string failed = Path.Combine(root, "failed");
                try { AnimationExport.Write(failed, source, [motion], (_, picture) => Draw(picture), (_, _) => throw new IOException("Full"), true, true); }
                catch (IOException) { }
                Check(!Directory.Exists(failed) && Directory.GetDirectories(root).Length == 1, "Failed export left output");
                using var cancellation = new CancellationTokenSource();
                string canceled = Path.Combine(root, "cancel");
                try { AnimationExport.Write(canceled, source, [motion], (_, picture) => Draw(picture), (s, i) => { Encode(s, i); cancellation.Cancel(); }, true, true, cancellation: cancellation.Token); }
                catch (OperationCanceledException) { }
                Check(!Directory.Exists(canceled) && Directory.GetDirectories(root).Length == 1, "Canceled export left output");
                Check(File.Exists(Path.Combine(target, "m0_0", "animation.json")), "Existing export damaged");
                string raced = Path.Combine(root, "race");
                bool refused = false;
                try
                {
                    AnimationExport.Write(raced, source, [motion], (_, picture) => Draw(picture), (s, i) =>
                    { Encode(s, i); Directory.CreateDirectory(raced); File.WriteAllText(Path.Combine(raced, "keep.txt"), "Keep"); }, true, false);
                }
                catch (IOException) { refused = true; }
                Check(refused && File.ReadAllText(Path.Combine(raced, "keep.txt")) == "Keep", "Destination collision damaged existing files");
                Check(!Directory.GetDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".berwick-export-")), "Staging remains after collision");
                string sheetsOnly = Path.Combine(root, "sheets");
                AnimationExport.Write(sheetsOnly, source, [motion], (_, picture) => Draw(picture), Encode, false, true);
                Check(!File.Exists(Path.Combine(sheetsOnly, "m0_0", "frame-0001.png")) && File.Exists(Path.Combine(sheetsOnly, "m0_0", "sheet-001.png")), "Sheet-only output wrong");
            }
            finally { Directory.Delete(root, true); }
        });
    }
    private static AnimationImage Draw(int picture)
    {
        var pixels = new byte[64];
        int at = (4 + (picture == 1 ? 1 : 2)) * 4;
        pixels[at] = 57; pixels[at + 3] = 255;
        return new(4, 4, pixels);
    }
    private static void Encode(Stream stream, AnimationImage image) => stream.Write(image.Pixels);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (EditException) { return; } throw new Exception("Unsafe request accepted"); }
}
