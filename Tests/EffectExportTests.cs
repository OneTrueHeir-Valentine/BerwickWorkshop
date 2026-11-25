using Berwick.Core;
using System.Text.Json;

internal static class EffectExportTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Effect export preserves original records, headers, keyframes and palette", () =>
        {
            var fixture = new AnimationFixture();
            var original = fixture.Files.ToDictionary(p => p.Key, p => p.Value.ToArray());
            var set = fixture.Set();
            using var directory = new ExportTestDirectory();
            EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 5), set, ["m2_1"], Encode);
            for (int track = 1; track <= 2; track++)
            {
                string folder = Path.Combine(directory.Target(), "m2_1", $"track-{track:D3}");
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "effect.json")));
                var doc = json.RootElement;
                Check(doc.GetProperty("motionHeaderHex").GetString() == Convert.ToHexString(original["m2_1.anm"]), "Motion header changed");
                Check(doc.GetProperty("trackHeaderHex").GetString() == Convert.ToHexString(original[$"m2_1_{track}a.dat"]), "Track header changed");
                Check(doc.GetProperty("keyframesBase64").GetString() == (track == 1 ? Convert.ToBase64String(original["m2_1_1k.dat"]) : null), "Keyframes changed");
                var palette = doc.GetProperty("palette");
                Check(palette.GetProperty("name").GetString() == "eff5.pal" && palette.GetProperty("requestedPalette").GetInt32() == 5, "Wrong selected palette");
                Check(palette.GetProperty("sourceRgbaBase64").GetBytesFromBase64().SequenceEqual(original["eff5.pal"]), "Source palette changed");
                var records = doc.GetProperty("records");
                Check(records.GetArrayLength() == 3, "Hidden records omitted");
                for (int i = 0; i < 3; i++)
                    Check(records[i].GetProperty("source").GetProperty("sourceHex").GetString() == Convert.ToHexString(original[$"m2_1_{track}t.dat"].AsSpan(i * 60, 60)), "Source record bytes changed");
                Check(records[0].GetProperty("file").ValueKind == JsonValueKind.Null && !File.Exists(Path.Combine(folder, "record-0000.png")), "Hidden record produced artwork");
                Check(records[0].GetProperty("source").GetProperty("durationCounter").GetInt32() == 30, "Hidden delay changed");
                Check(records[2].GetProperty("source").GetProperty("nextIndex").GetInt32() == 1, "Loop edge changed");
                Check(records[1].GetProperty("effectiveDepth").GetInt32() == 321, "Header depth was truncated");
                Check(records[1].GetProperty("blend").GetProperty("fixedAlpha").GetInt32() == 32, "Blend metadata changed");
                Check(doc.GetProperty("canvas").GetProperty("originX").GetInt32() == -3, "Effect origin lost");
                Check(Directory.GetFiles(folder, "*.png").Length == 2, "Visible records missing");
            }
            Check(fixture.Files.All(p => p.Value.SequenceEqual(original[p.Key])), "Fixture source bytes mutated");
            Check(set.EffectTracks("m2_1")[0].Records[1].SourceHex == Convert.ToHexString(original["m2_1_1t.dat"].AsSpan(60, 60)), "Set source bytes mutated");
        });
        test("Effect export identifies fallback palette without changing its source bytes", () =>
        {
            var fixture = new AnimationFixture();
            using var directory = new ExportTestDirectory();
            EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 99), fixture.Set(), ["m2_1"], Encode);
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Target(), "m2_1", "track-001", "effect.json")));
            var palette = json.RootElement.GetProperty("palette");
            Check(palette.GetProperty("name").GetString() == "eff0.pal" && palette.GetProperty("requestedPalette").GetInt32() == 99, "Fallback metadata wrong");
            Check(palette.GetProperty("sourceRgbaBase64").GetBytesFromBase64().SequenceEqual(fixture.Files["eff0.pal"]), "Fallback source palette changed");
        });
        test("Effect export refuses existing folders and files", () =>
        {
            using var directory = new ExportTestDirectory();
            var set = new AnimationFixture().Set();
            Directory.CreateDirectory(directory.Target());
            File.WriteAllText(Path.Combine(directory.Target(), "keep.txt"), "Keep");
            Throws<EditException>(() => EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 5), set, ["m2_1"], Encode));
            File.WriteAllText(directory.Target("existing-file"), "Keep");
            Throws<EditException>(() => EffectExport.Write(directory.Target("existing-file"), new("Synthetic", "fixture", 7, 5), set, ["m2_1"], Encode));
            Check(File.ReadAllText(Path.Combine(directory.Target(), "keep.txt")) == "Keep" && File.ReadAllText(directory.Target("existing-file")) == "Keep", "Existing output changed");
        });
        test("Effect export cancellation removes only staging output", () =>
        {
            using var directory = new ExportTestDirectory();
            using var cancellation = new CancellationTokenSource();
            Throws<OperationCanceledException>(() => EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 5), new AnimationFixture().Set(), ["m2_1"],
                (s, image) => { Encode(s, image); cancellation.Cancel(); }, cancellation: cancellation.Token));
            Check(!Directory.EnumerateFileSystemEntries(directory.Root).Any(), "Canceled output remains");
        });
        test("Effect export encoder failure removes staging output", () =>
        {
            using var directory = new ExportTestDirectory();
            Throws<IOException>(() => EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 5), new AnimationFixture().Set(), ["m2_1"],
                (s, _) => { s.WriteByte(1); throw new IOException("Synthetic encoder failure"); }));
            Check(!Directory.EnumerateFileSystemEntries(directory.Root).Any(), "Failed output remains");
        });
        test("Effect export destination race preserves the other writer's output", () =>
        {
            using var directory = new ExportTestDirectory();
            Throws<IOException>(() => EffectExport.Write(directory.Target(), new("Synthetic", "fixture", 7, 5), new AnimationFixture().Set(), ["m2_1"],
                (s, image) => { Encode(s, image); Directory.CreateDirectory(directory.Target()); File.WriteAllText(Path.Combine(directory.Target(), "keep.txt"), "Keep"); }));
            Check(File.ReadAllText(Path.Combine(directory.Target(), "keep.txt")) == "Keep", "Other writer's output changed");
            Check(Directory.GetDirectories(directory.Root).SequenceEqual(new[] { directory.Target() }), "Collision left staging output");
        });
    }

    private static void Encode(Stream stream, AnimationImage image) => stream.Write(image.Pixels);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
