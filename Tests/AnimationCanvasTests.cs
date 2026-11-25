using Berwick.Core;

internal static class AnimationCanvasTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Rendered motion frames share a canvas and preserve the legacy region", () =>
        {
            var set = new AnimationFixture().Set();
            var area = set.CanvasFor("m0_0");
            Check(area == new AnimationRect(-4, -2, 304, 300), "Wrong shared canvas");
            foreach (var frame in set.Frames("m0_0"))
            {
                var expanded = set.Draw("m0_0", frame.Picture, 5);
                Check(expanded.Width == area.Width && expanded.Height == area.Height &&
                    expanded.OriginX == area.X && expanded.OriginY == area.Y, "Canvas shifted between pictures");
                var legacy = set.Draw("m0_0", frame.Picture, 5, legacyCanvas: true);
                Check(AnimationExport.Crop(expanded, new(4, 2, 288, 288)).Pixels.SequenceEqual(legacy.Pixels), "Legacy region changed");
                Check(expanded.Pixels.Where((_, i) => i % 4 == 3).Count(a => a != 0) == 2, "Expanded artwork was clipped");
                Check(legacy.Pixels.Where((_, i) => i % 4 == 3).Count(a => a != 0) == (frame.Picture == 3 ? 2 : 0), "Wrong legacy artwork coverage");
            }
            foreach (int palette in new[] { 5, 6 })
            {
                var image = set.Draw("m0_0", 3, palette);
                byte[] expected = palette == 5 ? [66, 55, 44, 255] : [99, 88, 77, 255];
                Check(image.Pixels.AsSpan((16 * image.Width + 16) * 4, 4).SequenceEqual(expected), "Body palette selection changed");
            }
        });
        test("Sets sharing a motion name have independent preview bounds", () =>
        {
            var first = new AnimationFixture().Set();
            var second = new AnimationFixture().WithPositions((40, 30), (50, 35), (60, 40)).Set();
            var originalBounds = first.Bounds("m0_0");
            var otherBounds = second.Bounds("m0_0");
            Check(originalBounds != otherBounds, "Different animation sets unexpectedly share crop bounds");

            foreach (var set in new[] { first, second, first })
            {
                var bounds = set.Bounds("m0_0");
                foreach (var frame in set.Frames("m0_0"))
                {
                    var image = set.Draw("m0_0", frame.Picture, 0);
                    var cropped = AnimationExport.Crop(image, new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                    int Visible(AnimationImage img) => img.Pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha != 0);
                    Check(Visible(image) == Visible(cropped), "A set-specific preview crop lost visible artwork");
                    Check(cropped.OriginX == image.OriginX + bounds.X && cropped.OriginY == image.OriginY + bounds.Y,
                        "Preview crop origin did not follow the selected set");
                }
            }
        });
        test("Concurrent canvas lookups keep the same bounds", () =>
        {
            var set = new AnimationFixture().Set();
            var bounds = new AnimationRect[32];
            Parallel.For(0, bounds.Length, i => bounds[i] = set.CanvasFor("m0_0"));
            Check(bounds.All(b => b == new AnimationRect(-4, -2, 304, 300)), "Concurrent canvas bounds differ");
        });
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
