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
    /// <summary>Checks that need no xdelta3 program: the pinned hash and notices the release package depends on.</summary>
    public static void RunOffline(Action<string, Action> test)
    {
        test("The release package pin for xdelta3 matches the program Workshop accepts, and its notices are present", () =>
        {
            string pin = File.ReadAllText(AccessKeyTests.SourceFile("third-party", "xdelta3", "xdelta3.exe.sha256")).Trim();
            Equal(XdeltaTool.Release322WindowsX64Sha256, pin);
            Equal(true, File.ReadAllText(AccessKeyTests.SourceFile("third-party", "xdelta3", "xdelta3-LICENSE.txt")).Contains("Apache License"));
            Equal(true, File.ReadAllText(AccessKeyTests.SourceFile("third-party", "xdelta3", "liblzma-COPYING.0BSD.txt")).Contains("Permission to use, copy, modify"));
            Equal(true, File.ReadAllText(AccessKeyTests.SourceFile("third-party", "xdelta3", "BLAKE3-LICENSE_A2.txt")).Contains("Apache License"));
            Equal(@"tools\xdelta3.exe", XdeltaTool.BundledRelativePath);
        });
    }

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

        test("Damaged or misleading manifests are refused before anything runs", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var good = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            string Json(PatchManifest m) => System.Text.Json.JsonSerializer.Serialize(m);
            var bad = new[]
            {
                Json(good with { Patch = @"..\elsewhere.xdelta" }), Json(good with { Patch = @"C:\p.xdelta" }), Json(good with { Patch = "" }),
                Json(good with { TargetSha256 = "1234" }), Json(good with { SourceSha256 = new string('g', 64) }), Json(good with { TargetSize = -1 }),
                Json(good with { Profile = "Shell" }), Json(good with { Format = "Something else" }), Json(good with { Version = 2 }),
                "{ not json", "null", new string(' ', 70 * 1024),
            };
            for (int i = 0; i < bad.Length; i++)
            {
                string path = dir.Write($"bad{i}.json", Encoding.UTF8.GetBytes(bad[i]));
                Throws<EditException>(() => XdeltaPatches.ReadManifest(path));
            }
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, dir.Path("o.iso"), good with { PatchSha256 = "x" }));
            Equal(false, File.Exists(dir.Path("o.iso")));
            Equal(good, XdeltaPatches.ReadManifest(XdeltaPatches.ManifestPathFor(patch)));
        });

        test("A full drive, a crash and a timeout leave nothing behind and keep other partial files", () =>
        {
            using var dir = new Folder(ascii: true);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            string unrelated = dir.Write("out.iso.partial", [7]), other = dir.Write("other download.partial", [8]);
            var full = Catch(() => XdeltaPatches.Apply(FakeTool(dir, "xdelta3: write failed: No space left on device", 1), a, patch, dir.Path("out.iso"), manifest));
            Equal(true, full.Message.Contains("ran out of space"));
            File.Delete(dir.Path("fake xdelta.cmd"));
            var crash = Catch(() => XdeltaPatches.Apply(FakeTool(dir, "", -1073741819), a, patch, dir.Path("out.iso"), manifest));
            Equal(true, crash.Message.Contains("exit code -1073741819"));
            File.Delete(dir.Path("fake xdelta.cmd"));
            var slow = Catch(() => XdeltaPatches.Apply(FakeTool(dir, "", 0, waitSeconds: 8, timeout: TimeSpan.FromSeconds(1)), a, patch, dir.Path("out.iso"), manifest));
            Equal(true, slow.Message.Contains("took longer"));
            Equal(false, File.Exists(dir.Path("out.iso")));
            Equal(2, dir.Partials());
            Equal(true, File.Exists(unrelated) && File.Exists(other));
        });

        test("A file that appears at the destination while running is kept, and the inputs cannot change meanwhile", () =>
        {
            using var dir = new Folder(ascii: true);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var slow = FakeTool(dir, "", 0, waitSeconds: 3);
            var run = Task.Run(() => Catch(() => XdeltaPatches.Create(slow, a, Sha(data), b, patch)));
            while (dir.Partials() == 0 && !run.IsCompleted) Thread.Sleep(20);
            Throws<IOException>(() => { using var writer = new FileStream(a, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            File.WriteAllBytes(patch, [1, 2, 3]);
            var error = run.Result;
            Equal(true, error.Message.Contains("appeared while this was running"));
            Equal(3, File.ReadAllBytes(patch).Length);
            Equal(false, File.Exists(XdeltaPatches.ManifestPathFor(patch)));
            Equal(0, dir.Partials());
        });

        test("A symbolic link to an input is never used as the output", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            string link = dir.Path("link.iso");
            try { File.CreateSymbolicLink(link, a); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // creating links needs Developer Mode
            Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, link, manifest));
            Equal(Sha(data), Sha(File.ReadAllBytes(a)));
        });

        test("An existing manifest blocks a new patch and is left as it is", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            string sidecar = dir.Write("p.xdelta.json", Encoding.UTF8.GetBytes("someone else's notes"));
            Throws<EditException>(() => XdeltaPatches.Create(tool, a, Sha(data), b, patch));
            Equal(false, File.Exists(patch));
            Equal("someone else's notes", File.ReadAllText(sidecar));
            Equal(0, dir.Partials());
        });

        test("A path through a directory junction that names an input is refused", () =>
        {
            using var dir = new Folder(ascii: true);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), patch = dir.Path("p.xdelta");
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, patch);
            string junction = dir.Path("junction");
            var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, dir.Root }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            mk!.WaitForExit();
            if (!Directory.Exists(junction)) throw new Exception("Could not create the junction for this test.");
            try
            {
                string alias = Path.Combine(junction, "a.iso");
                Equal(true, XdeltaPatches.SameFile(a, alias));
                Throws<EditException>(() => XdeltaPatches.Apply(tool, a, patch, alias, manifest));
                Throws<EditException>(() => XdeltaPatches.Apply(tool, alias, patch, Path.Combine(junction, "p.xdelta"), manifest));
                Equal(Sha(data), Sha(File.ReadAllBytes(a)));
                var through = XdeltaPatches.Apply(tool, alias, patch, Path.Combine(junction, "new.iso"), manifest);
                Equal(true, File.Exists(dir.Path("new.iso")));
                Equal(Sha(changed), through.Sha256);
            }
            finally { Directory.Delete(junction); }
            Equal(0, dir.Partials());
        });

        test("Cancelling while hashing or exporting leaves nothing behind", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Throws<OperationCanceledException>(() => XdeltaPatches.Create(tool, a, Sha(data), b, dir.Path("p.xdelta"), XdeltaProfile.Compatible, cancelled.Token));
            var changes = new[] { new DiscPatch(4096, [1, 2, 3, 4], data.AsSpan(4096, 4).ToArray()) };
            Throws<OperationCanceledException>(() => XdeltaPatches.CreateFromProject(tool, a, Sha(data), changes, dir.Path("q.xdelta"), XdeltaProfile.Compatible, cancelled.Token));
            var manifest = XdeltaPatches.Create(tool, a, Sha(data), b, dir.Path("r.xdelta"));
            Throws<OperationCanceledException>(() => XdeltaPatches.Apply(tool, a, dir.Path("r.xdelta"), dir.Path("out.iso"), manifest, cancelled.Token));
            var left = Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order().ToArray();
            Equal("a.iso,b.iso,r.xdelta,r.xdelta.json", string.Join(",", left));
        });

        test("The checked xdelta3 cannot be changed, renamed or deleted until it has finished, and a missing one is refused", () =>
        {
            using var dir = new Folder(ascii: true);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            var slow = FakeTool(dir, "", 0, waitSeconds: 3);
            var run = Task.Run(() => XdeltaPatches.Create(slow, a, Sha(data), b, dir.Path("p.xdelta")));
            while (dir.Partials() == 0 && !run.IsCompleted) Thread.Sleep(20);
            Throws<IOException>(() => { using var w = new FileStream(slow.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            Throws<IOException>(() => File.Move(slow.Path, dir.Path("moved.cmd")));
            Throws<IOException>(() => File.Delete(slow.Path));
            run.Wait();
            Equal(true, File.Exists(slow.Path) && File.Exists(dir.Path("p.xdelta")));
            var missing = tool with { Path = dir.Path("no xdelta3 here.exe") };
            Equal(true, Catch(() => XdeltaPatches.Create(missing, a, Sha(data), b, dir.Path("q.xdelta"))).Message.Contains("not found"));
            Equal(0, dir.Partials());
        });

        test("The packaged xdelta3 next to the application is used only when it is the verified program", () =>
        {
            using var dir = new Folder();
            Equal(BundledToolState.Missing, XdeltaPatches.FindBundled(dir.Root).State);
            Equal(null, XdeltaPatches.FindBundled(dir.Root).Tool);
            Directory.CreateDirectory(dir.Path("tools"));
            string copy = System.IO.Path.Combine(dir.Root, XdeltaTool.BundledRelativePath);
            File.Copy(exe, copy);
            var found = XdeltaPatches.FindBundled(dir.Root);
            Equal(BundledToolState.Verified, found.State);
            Equal(copy, found.Tool!.Path);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            var manifest = XdeltaPatches.Create(found.Tool, a, Sha(data), b, dir.Path("p.xdelta"));
            Equal(Sha(changed), manifest.TargetSha256);
            using (var append = new FileStream(copy, FileMode.Append, FileAccess.Write)) append.WriteByte(0);
            var tampered = XdeltaPatches.FindBundled(dir.Root);
            Equal(BundledToolState.NotVerified, tampered.State);
            Equal(null, tampered.Tool);
            Equal(true, Catch(() => XdeltaPatches.Create(found.Tool, a, Sha(data), b, dir.Path("q.xdelta"))).Message.Contains("not the verified version"));
            Equal(false, File.Exists(dir.Path("q.xdelta")));
        });

        test("xdelta3 reached through a junction is started from its real folder, so the junction cannot redirect it", () =>
        {
            using var dir = new Folder();
            string real = dir.Path("real tools"), app = dir.Path("app");
            Directory.CreateDirectory(real); Directory.CreateDirectory(app);
            File.Copy(exe, System.IO.Path.Combine(real, "xdelta3.exe"));
            string link = System.IO.Path.Combine(app, "tools");
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, real },
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Equal(0, mklink.ExitCode);
            string viaLink = System.IO.Path.Combine(link, "xdelta3.exe");
            using (var handle = new FileStream(viaLink, FileMode.Open, FileAccess.Read, FileShare.Read))
                Equal(System.IO.Path.GetFullPath(System.IO.Path.Combine(real, "xdelta3.exe")), XdeltaPatches.FinalPath(handle));
            var found = XdeltaPatches.FindBundled(app);
            Equal(BundledToolState.Verified, found.State);
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            var manifest = XdeltaPatches.Create(found.Tool!, a, Sha(data), b, dir.Path("p.xdelta"));
            Equal(Sha(changed), manifest.TargetSha256);
            Directory.Delete(link);
        });

        test("xdelta3 runs in its own folder, not in the folder the new file goes to", () =>
        {
            using var dir = new Folder(ascii: true); // the batch stand-in writes its folder in the console code page
            string tools = dir.Path("tools"), output = dir.Path("downloads");
            Directory.CreateDirectory(tools); Directory.CreateDirectory(output);
            // A stand-in that writes its working directory as the "patch".
            string script = "@echo off\r\nset last=\r\nfor %%a in (%*) do set \"last=%%~a\"\r\n>\"%last%\" echo %CD%\r\nexit /b 0\r\n";
            string path = System.IO.Path.Combine(tools, "fake xdelta.cmd");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes(script));
            var fake = new XdeltaTool(path, Sha(File.ReadAllBytes(path)), "fake");
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed);
            XdeltaPatches.Create(fake, a, Sha(data), b, System.IO.Path.Combine(output, "p.xdelta"));
            Equal(System.IO.Path.GetFullPath(tools).TrimEnd('\\'), File.ReadAllText(System.IO.Path.Combine(output, "p.xdelta")).Trim().TrimEnd('\\'));
        });

        test("Each step reports what it is doing, and a chain names the patch it is on", () =>
        {
            using var dir = new Folder();
            string a = dir.Write("a.iso", data), b = dir.Write("b.iso", changed), c = dir.Write("c.iso", third);
            var phases = new List<string>();
            var report = new Recorder(phases);
            var first = XdeltaPatches.Create(tool, a, Sha(data), b, dir.Path("1.xdelta"), XdeltaProfile.Compatible, default, report);
            Equal("Checking the original and changed disc images|Running xdelta3 to write the patch", string.Join("|", phases));
            var second = XdeltaPatches.Create(tool, b, Sha(changed), c, dir.Path("2.xdelta"));
            phases.Clear();
            XdeltaPatches.ApplyChain(tool, a, [(dir.Path("1.xdelta"), first), (dir.Path("2.xdelta"), second)], dir.Path("out.iso"), default, report);
            Equal(6, phases.Count);
            Equal("Patch 1 of 2: Checking the disc image and the patch", phases[0]);
            Equal("Patch 2 of 2: Checking the new disc image", phases[^1]);
            phases.Clear();
            var changes = new[] { new DiscPatch(4096, [1, 2, 3, 4], data.AsSpan(4096, 4).ToArray()) };
            XdeltaPatches.CreateFromProject(tool, a, Sha(data), changes, dir.Path("p.xdelta"), XdeltaProfile.Compatible, default, report);
            Equal("Writing the project's changes to a temporary disc image", phases[0]);
        });

        test("xdelta3 messages are explained in plain words", () =>
        {
            Equal(true, XdeltaPatches.Explain("xdelta3: unavailable secondary compressor: LZMA: XD3_INTERNAL", 1).Contains("compression method"));
            Equal(true, XdeltaPatches.Explain("xdelta3: target window checksum mismatch: XD3_INVALID_INPUT", 1).Contains("does not fit"));
            Equal(true, XdeltaPatches.Explain("xdelta3: write failed: No space left on device", 1).Contains("ran out of space"));
            Equal("xdelta3 stopped with exit code 3.", XdeltaPatches.Explain("", 3));
            Equal(true, XdeltaPatches.Explain("", unchecked((int)0xC0000135)).Contains("Visual C++ Redistributable"));
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

    /// <summary>
    /// A stand-in for xdelta3 for failure tests: a batch file that writes some bytes to the output it is given (its last
    /// argument), optionally waits, prints a message and exits with a chosen code. It is pinned by its own hash.
    /// </summary>
    private static XdeltaTool FakeTool(Folder dir, string message, int exitCode, int waitSeconds = 0, TimeSpan? timeout = null)
    {
        string script = "@echo off\r\nset last=\r\nfor %%a in (%*) do set \"last=%%~a\"\r\n>\"%last%\" echo partial output\r\n" +
            (waitSeconds > 0 ? $"ping -n {waitSeconds + 1} 127.0.0.1 >nul\r\n" : "") +
            (message.Length > 0 ? $"echo {message} 1>&2\r\n" : "") + $"exit /b {exitCode}\r\n";
        string path = dir.Write("fake xdelta.cmd", Encoding.ASCII.GetBytes(script));
        return new XdeltaTool(path, Sha(File.ReadAllBytes(path)), "fake") { Timeout = timeout ?? TimeSpan.FromMinutes(1) };
    }

    /// <summary>Records reports on the calling thread, in order.</summary>
    private sealed class Recorder(List<string> into) : IProgress<string>
    {
        public void Report(string value) => into.Add(value);
    }

    private sealed class Folder : IDisposable
    {
        public string Root { get; }
        public Folder(bool ascii = false)
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), ascii ? $"berwick xdelta {Guid.NewGuid():N}" : $"berwick xdelta ü {Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }
        public string Path(string name) => System.IO.Path.Combine(Root, name);
        public string Write(string name, byte[] bytes) { string p = Path(name); File.WriteAllBytes(p, bytes); return p; }
        public int Partials() => Directory.GetFiles(Root, "*.partial").Length;
        public void Dispose() => Directory.Delete(Root, true);
    }
}
