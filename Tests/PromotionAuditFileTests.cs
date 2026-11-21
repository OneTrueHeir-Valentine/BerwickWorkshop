using Berwick.Core;
using System.Collections;
using System.Diagnostics;

internal static class PromotionAuditFileTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Audit output refuses Git directories and worktree links before creating output", () => InFolder(folder =>
        {
            string repo = Path.Combine(folder, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            string output = Path.Combine(repo, "ignored", "audit");
            Reject(() => PromotionAuditFiles.Write(output, "fixture", Empty()));
            if (Directory.Exists(output)) throw new Exception("Output was created inside a checkout");
            string worktree = Path.Combine(folder, "worktree");
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: synthetic");
            Reject(() => PromotionAuditFiles.ValidateDirectory(Path.Combine(worktree, "audit")));
        }));
        test("Audit reports preserve existing files and directories", () => InFolder(folder =>
        {
            foreach (string suffix in new[] { "commands.csv", "event-calls.csv", "parse-errors.txt" })
            {
                string output = Path.Combine(folder, suffix);
                Directory.CreateDirectory(output);
                string existing = Path.Combine(output, "fixture-" + suffix);
                File.WriteAllText(existing, "keep");
                Reject(() => PromotionAuditFiles.Write(output, "fixture", Empty()));
                if (File.ReadAllText(existing) != "keep" || Directory.GetFileSystemEntries(output).Length != 1)
                    throw new Exception("An existing report was changed");
            }
            Directory.CreateDirectory(Path.Combine(folder, "fixture-commands.csv"));
            Reject(() => PromotionAuditFiles.Write(folder, "fixture", Empty()));
        }));
        test("Concurrent audit writers cannot replace each other's reports", () => InFolder(folder =>
        {
            using var start = new ManualResetEventSlim();
            int succeeded = 0;
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                start.Wait();
                try { PromotionAuditFiles.Write(folder, "fixture", Empty()); Interlocked.Increment(ref succeeded); }
                catch (IOException) { }
            })).ToArray();
            start.Set();
            Task.WaitAll(tasks);
            if (succeeded != 1 || Directory.GetFiles(folder).Length != 3)
                throw new Exception("Concurrent publication replaced or lost reports");
        }));
        test("Audit CSV retains literals and unresolved calls without changing input files", () => InFolder(folder =>
        {
            string source = Path.Combine(folder, "synthetic.iso");
            File.WriteAllBytes(source, [1, 2, 3]);
            var report = new PromotionAuditReport([new(12, 24, 40, "Class,\"candidate\"", "class", 2, "7;?", true)],
                [new(12, 44, null), new(12, 64, 13)], ["Event 14: malformed"]);
            PromotionAuditFiles.Write(folder, "fixture", report);
            string commands = File.ReadAllText(Path.Combine(folder, "fixture-commands.csv"));
            string calls = File.ReadAllText(Path.Combine(folder, "fixture-event-calls.csv"));
            if (!commands.Contains("\"Class,\"\"candidate\"\"\"") || !commands.Contains("7;?") ||
                !calls.Contains("12,44,,computed") || !calls.Contains("12,64,13,literal"))
                throw new Exception("Report fields or unresolved evidence were lost");
            if (!File.ReadAllBytes(source).SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("Input file changed");
        }));
        test("Audit source guard refuses unsupported and modified profiles", () =>
        {
            foreach (var profile in Profiles.All)
            {
                var original = new InspectionResult("synthetic", 0, "", "", "", profile.Sha256, profile.ExecutableSha256, profile, [], [], 0);
                PromotionAuditCommand.RequireOriginal(original);
                Reject(() => PromotionAuditCommand.RequireOriginal(original with { IsModified = true }));
                Reject(() => PromotionAuditCommand.RequireOriginal(original with { Profile = null }));
                Reject(() => PromotionAuditCommand.RequireOriginal(original with { Sha256 = "different" }));
            }
        });
        test("Audit output refuses linked directory ancestors", () => InFolder(folder =>
        {
            string target = Path.Combine(folder, "target");
            string link = Path.Combine(folder, "linked");
            Directory.CreateDirectory(target);
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Could not create junction fixture");
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException(output);
            }
            else Directory.CreateSymbolicLink(link, target);
            try
            {
                Reject(() => PromotionAuditFiles.Write(Path.Combine(link, "audit"), "fixture", Empty()));
                if (Directory.GetFileSystemEntries(target).Length != 0) throw new Exception("Output followed a directory link");
            }
            finally { Directory.Delete(link); }
        }));
        test("Failed audit publication removes only the files it created", () => InFolder(folder =>
        {
            string existing = Path.Combine(folder, "keep.txt");
            File.WriteAllText(existing, "keep");
            Reject(() => PromotionAuditFiles.Write(folder, "fixture", new([], [], new UnreadableErrors())));
            if (Directory.GetFiles(folder).Length != 1 || File.ReadAllText(existing) != "keep")
                throw new Exception("Failure left partial reports or removed an unrelated file");
        }));
    }

    private static PromotionAuditReport Empty() => new([], [], []);
    private sealed class UnreadableErrors : IReadOnlyList<string>
    {
        public int Count => 1;
        public string this[int index] => throw new IOException("Synthetic report read failure");
        public IEnumerator<string> GetEnumerator() => throw new IOException("Synthetic report read failure");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return; }
        throw new Exception("Unsafe audit request was accepted");
    }
    private static void InFolder(Action<string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "berwick-promotion-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { action(folder); }
        finally
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
            string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(parent, temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing cleanup outside the temporary fixture directory.");
            Directory.Delete(folder, recursive: true);
        }
    }
}
