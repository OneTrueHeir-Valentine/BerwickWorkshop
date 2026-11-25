using Berwick.Core;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Patch creation and application through a real xdelta3 executable, on small generated files.
/// Run with --xdelta &lt;path to xdelta3.exe&gt;; the executable must be the pinned release.
/// </summary>
public static class XdeltaPatchTests
{
    public static void Run(Action<string, Action> test, string exe)
    {
        var tool = new XdeltaTool(exe, XdeltaTool.Release322WindowsX64Sha256, "3.2.2");
        var data = new byte[6 * 1024 * 1024];
        new Random(1125).NextBytes(data);
        byte[] changed = (byte[])data.Clone();
        changed.AsSpan(1000, 100).Clear();
        Encoding.ASCII.GetBytes("BERWICK!").CopyTo(changed, 3_000_000);
        byte[] third = (byte[])changed.Clone();
        third[5_000_000] ^= 0x5A;

        test("A compatible patch stores no file names and reproduces the target", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("source disc.iso", data), b = dir.Write("target disc.iso", changed), patch = dir.Path("a to b.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            byte[] bytes = File.ReadAllBytes(patch);
            Equal(0, (int)bytes[4]);
            Equal(false, Contains(bytes, "source disc") || Contains(bytes, "target disc") || Contains(bytes, dir.Root));
            string json = File.ReadAllText(XdeltaPatches.ManifestPathFor(patch));
            Equal(false, json.Contains(dir.Root.Replace("\\", "\\\\")) || json.Contains("source disc"));
            Equal("a to b.xdelta", manifest.Patch);
            var result = XdeltaPatches.Apply(tool, a, patch, dir.Path("b again.iso"), XdeltaPatches.ReadManifest(XdeltaPatches.ManifestPathFor(patch)));
            Equal(PatchedIdentity.ExpectedTarget, result.Identity);
            Equal(Sha(changed), Sha(File.ReadAllBytes(result.Path)));
            Equal(0, dir.Partials());
        });

        test("A compact patch declares LZMA and still reproduces the target", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("compact.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch, XdeltaProfile.Compact);
            Equal(false, Contains(File.ReadAllBytes(patch), "a.iso"));
            var result = XdeltaPatches.Apply(tool, a, patch, dir.Path("out.iso"), manifest);
            Equal(Sha(changed), result.Sha256);
        });

        test("A patch without a manifest needs a known edition as its source", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, dir.Path("out.iso")));
            Equal(false, File.Exists(dir.Path("out.iso")));
        });

        test("The wrong source, a changed patch and a changed source are refused without output", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), c = dir.Write("c.iso", third), patch = dir.Path("p.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            Throws<EditException>(() => XdeltaPatches.Apply(tool, c, patch, dir.Path("o1.iso"), manifest));
            Throws<EditException>(() => XdeltaPatches.Create(tool, c, Sha(data), b, dir.Path("p2.xdelta")));
            byte[] damaged = File.ReadAllBytes(patch);
            for (int i = 6; i < damaged.Length; i += 3) damaged[i] ^= 0xFF;
            string bad = dir.Write("damaged.xdelta", damaged);
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, bad, dir.Path("o2.iso"), manifest));
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, bad, dir.Path("o3.iso"), manifest with { PatchSha256 = Sha(damaged) }));
            Equal(false, File.Exists(dir.Path("o1.iso")) || File.Exists(dir.Path("o2.iso")) || File.Exists(dir.Path("o3.iso")));
            Equal(0, dir.Partials());
        });

        test("Existing files, inputs and hard links to an input are never written", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            string existing = dir.Write("existing.iso", [1, 2, 3]);
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, existing, manifest));
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, a, manifest));
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, patch, manifest));
            Throws<EditException>(() => XdeltaPatches.Create(tool, a, Sha(data), b, patch));
            string link = dir.Path("link to source.iso");
            if (OperatingSystem.IsWindows() && CreateHardLink(link, a, IntPtr.Zero))
            {
                Equal(true, XdeltaPatches.SameFile(a, link));
                Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, link, manifest));
            }
            Equal(Sha(data), Sha(File.ReadAllBytes(a)));
            Equal(3, File.ReadAllBytes(existing).Length);
            Equal(0, dir.Partials());
        });

        test("A program that is not the pinned xdelta3 is never run", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            var other = tool with { Sha256 = new string('0', 64) };
            Throws<EditException>(() => XdeltaPatches.Create(other, a, Sha(data), b, dir.Path("p.xdelta")));
            Equal(false, File.Exists(dir.Path("p.xdelta")));
        });

        test("A program that exits with an error leaves nothing behind", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            string findstr = Path.Combine(Environment.SystemDirectory, "findstr.exe");
            var failing = new XdeltaTool(findstr, Sha(File.ReadAllBytes(findstr)), "test");
            var error = Catch(() => XdeltaPatches.Create(failing, a, Sha(data), b, dir.Path("p.xdelta")));
            Equal(true, error.Message.StartsWith("The patch could not be created."));
            Equal(false, File.Exists(dir.Path("p.xdelta")) || File.Exists(XdeltaPatches.ManifestPathFor(dir.Path("p.xdelta"))));
            Equal(0, dir.Partials());
        });

        test("Cancelling stops xdelta3 and removes its partial output", () =>
        {
            using var dir = new Folder();
            var big = new byte[256 * 1024 * 1024];
            new Random(7).NextBytes(big);
            string a = dir.Write("big a.iso", big);
            big[100_000_000] ^= 1; big[200_000_000] ^= 1;
            for (int i = 0; i < big.Length; i += 4096) big[i] ^= 0x33;
            string b = dir.Write("big b.iso", big);
            string sha = Sha(File.ReadAllBytes(a));
            using var cancel = new CancellationTokenSource();
            var run = Task.Run(() => XdeltaPatches.Create(tool, a, sha, b, dir.Path("p.xdelta"), XdeltaProfile.Compatible, cancel.Token));
            while (dir.Partials() == 0 && !run.IsCompleted) Thread.Sleep(20);
            cancel.Cancel();
            try { run.Wait(); throw new Exception("Expected cancellation"); }
            catch (AggregateException e) when (e.InnerException is OperationCanceledException) { }
            Equal(false, File.Exists(dir.Path("p.xdelta")));
            Equal(0, dir.Partials());
        });

        test("An ordered chain keeps only the final image and refuses the wrong order", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), c = dir.Write("c.iso", third);
            var first = XdeltaPatches.Create(tool, a, Sha(data), b, dir.Path("1.xdelta"));
            var second = XdeltaPatches.Create(tool, b, Sha(changed), c, dir.Path("2.xdelta"));
            var steps = new[] { (dir.Path("1.xdelta"), first), (dir.Path("2.xdelta"), second) };
            var result = XdeltaPatches.ApplyChain(tool, a, steps, dir.Path("final.iso"));
            Equal(Sha(third), Sha(File.ReadAllBytes(result.Path)));
            Equal(0, Directory.GetFiles(dir.Root, ".berwick-chain-*").Length);
            Throws<EditException>(() => XdeltaPatches.ApplyChain(tool, a, steps.Reverse().ToArray(), dir.Path("wrong.iso")));
            Throws<EditException>(() => XdeltaPatches.ApplyChain(tool, c, steps, dir.Path("wrong2.iso")));
            Equal(false, File.Exists(dir.Path("wrong.iso")) || File.Exists(dir.Path("wrong2.iso")));
            Equal(0, Directory.GetFiles(dir.Root, ".berwick-chain-*").Length);
            Equal(0, dir.Partials());
        });

        test("A project's changes become a patch without keeping the exported image", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data);
            var changes = new[] { new DiscPatch(4096, [1, 2, 3, 4], data.AsSpan(4096, 4).ToArray()) };
            var manifest = XdeltaPatches.CreateFromProject(tool, a, Sha(data), changes, dir.Path("project.xdelta"));
            Equal(0, Directory.GetFiles(dir.Root, ".berwick-export-*").Length);
            var result = XdeltaPatches.Apply(tool, a, dir.Path("project.xdelta"), dir.Path("out.iso"), manifest);
            byte[] output = File.ReadAllBytes(result.Path);
            Equal(true, output.AsSpan(4096, 4).SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            Equal(data.Length, output.Length);
        });

        test("xdelta3 messages are explained in plain words", () =>
        {
            Equal(true, XdeltaPatches.Explain("xdelta3: unavailable secondary compressor: LZMA: XD3_INTERNAL", 1).Contains("compression method"));
            Equal(true, XdeltaPatches.Explain("xdelta3: target window checksum mismatch: XD3_INVALID_INPUT", 1).Contains("does not fit"));
            Equal(true, XdeltaPatches.Explain("xdelta3: write failed: No space left on device", 1).Contains("ran out of space"));
            Equal("xdelta3 stopped with exit code 3.", XdeltaPatches.Explain("", 3));
        });
    }

    /// <summary>Full-size checks with the real discs: a patch above 4 GiB, and an existing patch with no manifest.</summary>
    public static int RunReal(string exe, string japanese, string english, string legacyPatch, string folder)
    {
        var tool = new XdeltaTool(exe, XdeltaTool.Release322WindowsX64Sha256, "3.2.2");
        string jp = XdeltaPatches.Hash(japanese, default);
        string patch = Path.Combine(folder, "jp-to-en.xdelta");
        var manifest = XdeltaPatches.Create(tool, japanese, jp, english, patch);
        Console.WriteLine($"created {new FileInfo(patch).Length} bytes, target {manifest.TargetSha256}");
        var result = XdeltaPatches.Apply(tool, japanese, patch, Path.Combine(folder, "en-from-patch.iso"), manifest);
        Console.WriteLine($"applied with manifest: {result.Identity} {result.Edition} {result.Sha256} {result.Size}");
        File.Delete(result.Path);
        var legacy = XdeltaPatches.Apply(tool, japanese, legacyPatch, Path.Combine(folder, "en-from-legacy.iso"));
        Console.WriteLine($"applied legacy patch: {legacy.Identity} {legacy.Edition} {legacy.Sha256}");
        File.Delete(legacy.Path);
        bool ok = result.Identity == PatchedIdentity.ExpectedTarget && result.Edition == "English v2.3" &&
                  legacy.Identity == PatchedIdentity.KnownEdition && legacy.Edition == "English v2.3";
        Console.WriteLine(ok ? "real disc patches passed" : "real disc patches FAILED");
        return ok ? 0 : 1;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Contains(byte[] bytes, string text) =>
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual} (line {line})");
    }
    private static void Throws<T>(Action action, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name} (line {line})");
    }
    private static Exception Catch(Action action)
    {
        try { action(); } catch (Exception e) { return e; }
        throw new Exception("Expected an error");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    private sealed class Folder : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"berwick xdelta ü {Guid.NewGuid():N}");
        public Folder() => Directory.CreateDirectory(Root);
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public string Write(string name, byte[] bytes) { string p = Path(name); File.WriteAllBytes(p, bytes); return p; }
        public int Partials() => Directory.GetFiles(Root, "*.partial").Length;
        public void Dispose() => Directory.Delete(Root, true);
    }
}
