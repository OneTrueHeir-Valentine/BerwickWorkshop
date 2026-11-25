using Berwick.Core;
using System.Diagnostics;

/// <summary>
/// Real-disc check of patches made from Workshop projects. For one original disc: load a saved project, add ordinary edits,
/// export it the normal way as the reference, then make a patch straight from the project in each format, apply it and
/// compare hashes. Then a two-step chain from the reference to a second project, including wrong intermediates.
/// Run with --xdelta-projects &lt;xdelta3.exe&gt; &lt;original iso&gt; &lt;project.bwproj&gt; &lt;empty work folder on D:&gt;.
/// </summary>
public static class XdeltaProjectCheck
{
    public static int Run(string exe, string iso, string projectPath, string folder)
    {
        var tool = new XdeltaTool(exe, XdeltaTool.Release322WindowsX64Sha256, "3.2.2");
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        if (Directory.EnumerateFileSystemEntries(folder).Any()) { Console.WriteLine("The work folder must be empty."); return 2; }
        string label = disc.Edition.Contains("English") ? "EN" : "JP";
        bool ok = true;
        void Check(bool condition, string what) { Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} {what}"); ok &= condition; }

        // Project 1: the saved project plus ordinary item and growth edits.
        var session = EditSession.Load(disc, projectPath);
        var item = disc.Items.First(i => !i.IsUnused && i.Might is > 0 and < 50);
        var character = disc.Characters.First();
        session.Apply([
            new FieldEdit(EditSession.Items, item.Id, "might", item.Might + 2),
            new FieldEdit(EditSession.Items, item.Id, "weight", Math.Min(31, item.Weight + 1)),
            new FieldEdit(EditSession.Growths, character.Id, "strengthGrowth", Math.Min(127, character.StrengthGrowth + 5)),
        ]);
        var first = Exporter.PatchesFor(session, disc);
        Console.WriteLine($"{label} {disc.Edition}: project {Path.GetFileName(projectPath)} + {item.Name} might/weight + {character.Name} strength growth, {first.Count} disc patches");

        // Project 2: one more ordinary edit on top.
        var second = item.Id;
        session.Apply([new FieldEdit(EditSession.Items, second, "accuracy", Math.Max(0, session.Value(EditSession.Items, second, "accuracy") - 5))]);
        var secondPatches = Exporter.PatchesFor(session, disc);

        string Work(string name) => Path.Combine(folder, name);
        var monitor = new Monitor(folder);
        var time = Stopwatch.StartNew();
        string originalBefore = XdeltaPatches.Hash(iso, default);

        var reference = Exporter.Export(iso, disc.Sha256, Work("reference 1.iso"), first);
        var reference2 = Exporter.Export(iso, disc.Sha256, Work("reference 2.iso"), secondPatches);
        Console.WriteLine($"  references exported: {reference.Sha256} and {reference2.Sha256} ({time.Elapsed.TotalSeconds:0} s)");

        var manifests = new Dictionary<XdeltaProfile, PatchManifest>();
        foreach (var profile in new[] { XdeltaProfile.Compatible, XdeltaProfile.Compact })
        {
            monitor.Reset();
            var t = Stopwatch.StartNew();
            string patch = Work($"project {profile}.xdelta");
            var manifest = XdeltaPatches.CreateFromProject(tool, iso, disc.Sha256, first, patch, profile);
            double create = t.Elapsed.TotalSeconds;
            t.Restart();
            var applied = XdeltaPatches.Apply(tool, iso, patch, Work($"applied {profile}.iso"), XdeltaPatches.ReadManifest(XdeltaPatches.ManifestPathFor(patch)));
            double apply = t.Elapsed.TotalSeconds;
            Console.WriteLine($"  {profile}: patch {new FileInfo(patch).Length:N0} bytes, create {create:0} s, apply {apply:0} s, " +
                              $"xdelta3 peak {monitor.PeakChildMemory / 1048576.0:0} MiB, scratch peak {monitor.PeakScratch / 1073741824.0:0.00} GiB beyond the references");
            Check(manifest.TargetSha256 == reference.Sha256, $"{profile} manifest target equals the normal export");
            Check(applied.Identity == PatchedIdentity.ExpectedTarget && applied.Sha256 == reference.Sha256, $"{profile} applied output equals the normal export");
            File.Delete(applied.Path);
            manifests[profile] = manifest;
        }

        // Chain: original -> reference 1 (project patch), reference 1 -> reference 2.
        string step2 = Work("step 2.xdelta");
        var m2 = XdeltaPatches.Create(tool, reference.Path, reference.Sha256, reference2.Path, step2);
        var steps = new[] { (Work("project Compatible.xdelta"), manifests[XdeltaProfile.Compatible]), (step2, m2) };
        monitor.Reset();
        var tc = Stopwatch.StartNew();
        var chained = XdeltaPatches.ApplyChain(tool, iso, steps, Work("chain result.iso"));
        Console.WriteLine($"  chain: {tc.Elapsed.TotalSeconds:0} s, scratch peak {monitor.PeakScratch / 1073741824.0:0.00} GiB");
        Check(XdeltaPatches.Hash(chained.Path, default) == reference2.Sha256, "two-step chain ends at the second normal export");
        File.Delete(chained.Path);
        Check(Refused(() => XdeltaPatches.ApplyChain(tool, iso, [steps[1], steps[0]], Work("wrong order.iso"))), "steps in the wrong order are refused");
        Check(Refused(() => XdeltaPatches.ApplyChain(tool, iso, [steps[0], (step2, m2 with { SourceSha256 = new string('0', 64) })], Work("wrong intermediate.iso"))),
            "a step that names a different intermediate is refused before anything runs");
        Check(Refused(() => XdeltaPatches.ApplyChain(tool, reference2.Path, steps, Work("wrong start.iso"))), "starting from the wrong image is refused");

        File.Delete(reference.Path); File.Delete(reference2.Path);
        monitor.Dispose();
        var left = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Console.WriteLine($"  left in the work folder: {string.Join(", ", left)}");
        Check(left.All(n => n!.EndsWith(".xdelta") || n.EndsWith(".xdelta.json")) && left.Count == 6, "only the three patches and their manifests remain");
        Check(XdeltaPatches.Hash(iso, default) == originalBefore && originalBefore == disc.Sha256, "the original disc is unchanged");
        Console.WriteLine($"  total {time.Elapsed.TotalMinutes:0.0} min");
        Console.WriteLine(ok ? $"{label} project patches passed" : $"{label} project patches FAILED");
        return ok ? 0 : 1;
    }

    private static bool Refused(Action action)
    {
        try { action(); return false; } catch (EditException) { return true; }
    }

    /// <summary>Samples the work folder size and the xdelta3 child's peak working set while patches run.</summary>
    private sealed class Monitor : IDisposable
    {
        private readonly Timer timer;
        private readonly string folder;
        private long baseline;
        public long PeakScratch { get; private set; }
        public long PeakChildMemory { get; private set; }
        public Monitor(string folder) { this.folder = folder; timer = new Timer(_ => Sample(), null, 0, 250); }
        public void Reset() { lock (this) { baseline = Size(); PeakScratch = 0; PeakChildMemory = 0; } }
        private long Size() => new DirectoryInfo(folder).EnumerateFiles().Sum(f => { try { return f.Length; } catch (IOException) { return 0; } });
        private void Sample()
        {
            lock (this)
            {
                try { PeakScratch = Math.Max(PeakScratch, Size() - baseline); } catch (IOException) { }
                foreach (var p in Process.GetProcessesByName("xdelta3"))
                    using (p) try { PeakChildMemory = Math.Max(PeakChildMemory, p.PeakWorkingSet64); } catch (InvalidOperationException) { }
            }
        }
        public void Dispose() => timer.Dispose();
    }
}
