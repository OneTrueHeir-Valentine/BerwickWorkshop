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
