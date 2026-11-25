using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Berwick.Core;

/// <summary>The xdelta3 program used for patches, identified by the SHA-256 of the exact executable.</summary>
public sealed record XdeltaTool(string Path, string Sha256, string Version)
{
    /// <summary>Official xdelta3 3.2.2 release for Windows x64 (github.com/jmacd/xdelta, v3.2.2).</summary>
    public const string Release322WindowsX64Sha256 = "732980d52f9220dc81817ccdd0c295eadce8a0048a24c2a771a73f791ac824e9";
}

/// <summary>
/// How a patch is written. Every profile leaves out the application header, so no file names or folders
/// are stored in the patch.
/// </summary>
public enum XdeltaProfile
{
    /// <summary>No secondary compression. Readable by older xdelta3 decoders and patching tools.</summary>
    Compatible,
    /// <summary>LZMA secondary compression. Smaller, but older decoders without LZMA refuse it.</summary>
    Compact,
}

/// <summary>What a patched file is known to be.</summary>
public enum PatchedIdentity
{
    /// <summary>The output matches the target hash the patch's manifest promised.</summary>
    ExpectedTarget,
    /// <summary>No manifest, but the output is a known edition.</summary>
    KnownEdition,
    /// <summary>The patch decoded, but no hash vouches for the output.</summary>
    Unrecognized,
}

/// <summary>One patch and the files it connects. Only file names are recorded, never folders.</summary>
public sealed record PatchManifest(string Format, int Version, string Patch, string PatchSha256, string SourceSha256, long SourceSize,
    string TargetSha256, long TargetSize, string Profile, string Tool, string ToolSha256);

public sealed record PatchResult(string Path, string Sha256, long Size, PatchedIdentity Identity, string? Edition);

/// <summary>Creates and applies xdelta3 patches for whole disc images, through the pinned xdelta3 executable.</summary>
public static class XdeltaPatches
{
    public const string ManifestFormat = "Berwick Workshop patch manifest";
    private const long SpaceMargin = 64L * 1024 * 1024;

    /// <summary>Hashes of known original and translated editions, for patches that come without a manifest.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownEditions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320"] = "Japanese retail",
        ["d7355857377114af3c7734f43250eea2a01cef755531aff04df0a4ccff215111"] = "English v2.3",
    };

    internal static string[] EncodeOptions(XdeltaProfile profile) => profile switch
    {
        XdeltaProfile.Compatible => ["-S", "none", "-a", "-G", "-A="],
        XdeltaProfile.Compact => ["-A="],
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>
    /// Writes a patch that turns <paramref name="sourcePath"/> into <paramref name="targetPath"/>, and a manifest next to it.
    /// The source must still match <paramref name="sourceSha256"/>.
    /// </summary>
    public static PatchManifest Create(XdeltaTool tool, string sourcePath, string sourceSha256, string targetPath, string patchPath,
        XdeltaProfile profile = XdeltaProfile.Compatible, CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath), target = Path.GetFullPath(targetPath), patch = Path.GetFullPath(patchPath);
        string manifestPath = ManifestPathFor(patch);
        RefuseDestination(patch, source, target);
        RefuseDestination(manifestPath, source, target);
        CheckTool(tool);
        RequireHash(source, sourceSha256, "The source disc image has changed since it was checked.", cancellation);
        string targetSha = Hash(target, cancellation);
        long targetSize = new FileInfo(target).Length;
        RequireSpace(patch, targetSize / 2);

        string finished = RunToPartial(tool, patch, partial => ["-e", .. EncodeOptions(profile), "-s", source, target, partial], cancellation,
            "The patch could not be created");
        var manifest = new PatchManifest(ManifestFormat, 1, Path.GetFileName(patch), Hash(finished, cancellation), sourceSha256.ToLowerInvariant(),
            new FileInfo(source).Length, targetSha, targetSize, profile.ToString(), "xdelta3 " + tool.Version, tool.Sha256);
        try
        {
            WriteNew(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(finished, patch, overwrite: false);
        }
        catch
        {
            TryDelete(finished);
            throw;
        }
        return manifest;
    }

    /// <summary>Exports a project's changes to a temporary disc image, then writes the patch from the original to it.</summary>
    public static PatchManifest CreateFromProject(XdeltaTool tool, string originalPath, string originalSha256, IReadOnlyList<DiscPatch> changes,
        string patchPath, XdeltaProfile profile = XdeltaProfile.Compatible, CancellationToken cancellation = default)
    {
        string patch = Path.GetFullPath(patchPath);
        RefuseDestination(patch, Path.GetFullPath(originalPath));
        string folder = Path.GetDirectoryName(patch)!;
        string exported = Path.Combine(folder, $".berwick-export-{Guid.NewGuid():N}.iso");
        try
        {
            Exporter.Export(originalPath, originalSha256, exported, changes, cancellation: cancellation);
            return Create(tool, originalPath, originalSha256, exported, patch, profile, cancellation);
        }
        finally
        {
            TryDelete(exported);
        }
    }

    /// <summary>
    /// Applies one patch. With a manifest, the source must be the one it names and the output must be its target;
    /// without one, the source must be a known edition and the output is reported as known or unrecognized.
    /// </summary>
    public static PatchResult Apply(XdeltaTool tool, string sourcePath, string patchPath, string outputPath, PatchManifest? manifest = null,
        CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath), patch = Path.GetFullPath(patchPath), output = Path.GetFullPath(outputPath);
        RefuseDestination(output, source, patch);
        CheckTool(tool);
        if (!File.Exists(patch)) throw new EditException("The patch file was not found.");
        string sourceSha = Hash(source, cancellation);
        if (manifest is not null)
        {
            if (!string.Equals(Hash(patch, cancellation), manifest.PatchSha256, StringComparison.OrdinalIgnoreCase))
                throw new EditException("The patch file does not match its manifest.");
            if (!string.Equals(sourceSha, manifest.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new EditException("This disc image is not the one the patch was made for.");
        }
        else if (!KnownEditions.ContainsKey(sourceSha))
            throw new EditException("The disc image is not a known edition, and the patch has no manifest that names its source.");
        RequireSpace(output, manifest?.TargetSize ?? new FileInfo(source).Length);

        string finished = RunToPartial(tool, output, partial => ["-d", "-s", source, patch, partial], cancellation,
            "The patch could not be applied");
        try
        {
            string sha = Hash(finished, cancellation);
            PatchedIdentity identity;
            if (manifest is not null)
            {
                if (!string.Equals(sha, manifest.TargetSha256, StringComparison.OrdinalIgnoreCase))
                    throw new EditException("The patched disc image is not the one the manifest describes. Nothing was saved.");
                identity = PatchedIdentity.ExpectedTarget;
            }
            else identity = KnownEditions.ContainsKey(sha) ? PatchedIdentity.KnownEdition : PatchedIdentity.Unrecognized;
            long size = new FileInfo(finished).Length;
            File.Move(finished, output, overwrite: false);
            return new(output, sha, size, identity, KnownEditions.GetValueOrDefault(sha));
        }
        catch
        {
            TryDelete(finished);
            throw;
        }
    }

    /// <summary>
    /// Applies patches in order. Each step's source must be the previous step's target, as its manifest says,
    /// and only the final image is kept.
    /// </summary>
    public static PatchResult ApplyChain(XdeltaTool tool, string sourcePath, IReadOnlyList<(string Patch, PatchManifest Manifest)> steps,
        string outputPath, CancellationToken cancellation = default)
    {
        if (steps.Count == 0) throw new EditException("The patch chain is empty.");
        string output = Path.GetFullPath(outputPath);
        RefuseDestination(output, [Path.GetFullPath(sourcePath), .. steps.Select(s => Path.GetFullPath(s.Patch))]);
        for (int i = 1; i < steps.Count; i++)
            if (!string.Equals(steps[i].Manifest.SourceSha256, steps[i - 1].Manifest.TargetSha256, StringComparison.OrdinalIgnoreCase))
                throw new EditException($"Patch {i + 1} does not start from the result of patch {i}. Check the order of the patches.");
        string folder = Path.GetDirectoryName(output)!;
        string current = sourcePath;
        var intermediates = new List<string>();
        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                bool last = i == steps.Count - 1;
                string next = last ? output : Path.Combine(folder, $".berwick-chain-{Guid.NewGuid():N}-{i + 1}.iso");
                if (!last) intermediates.Add(next);
                Apply(tool, current, steps[i].Patch, next, steps[i].Manifest, cancellation);
                current = next;
            }
            var info = new FileInfo(output);
            return new(output, steps[^1].Manifest.TargetSha256, info.Length, PatchedIdentity.ExpectedTarget,
                KnownEditions.GetValueOrDefault(steps[^1].Manifest.TargetSha256));
        }
        finally
        {
            foreach (string file in intermediates) TryDelete(file);
        }
    }

    public static PatchManifest ReadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<PatchManifest>(File.ReadAllBytes(path))
            ?? throw new EditException("The patch manifest is empty.");
        if (manifest.Format != ManifestFormat || manifest.Version != 1)
            throw new EditException("This is not a patch manifest this version can read.");
        if (manifest.Patch.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new EditException("The patch manifest names a folder, which it never should.");
        return manifest;
    }

    public static string ManifestPathFor(string patchPath) => patchPath + ".json";

    /// <summary>Runs xdelta3 into a new partial file next to <paramref name="destination"/> and returns it once xdelta3 succeeds.</summary>
    private static string RunToPartial(XdeltaTool tool, string destination, Func<string, string[]> arguments, CancellationToken cancellation, string failure)
    {
        string partial = destination + $".{Guid.NewGuid():N}.partial";
        var start = new ProcessStartInfo(tool.Path)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(destination)!,
        };
        foreach (string argument in arguments(partial)) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new EditException("xdelta3 could not be started.");
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            using (cancellation.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
                process.WaitForExit();
            string message = (errors.Result + output.Result).Trim();
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(partial))
                throw new EditException($"{failure}. {Explain(message, process.ExitCode)}");
            return partial;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Turns xdelta3's own messages into a reason the user can act on.</summary>
    public static string Explain(string message, int exitCode)
    {
        if (message.Contains("unavailable secondary compressor", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unknown secondary compressor", StringComparison.OrdinalIgnoreCase))
            return "The patch uses a compression method this xdelta3 does not support.";
        if (message.Contains("target window checksum mismatch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("source file too short", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("does not match", StringComparison.OrdinalIgnoreCase))
            return "The patch does not fit this disc image, or the patch is damaged.";
        if (message.Contains("not a VCDIFF input", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return "The patch file is damaged or incomplete.";
        if (message.Contains("No space", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("disk full", StringComparison.OrdinalIgnoreCase))
            return "The destination drive ran out of space.";
        return exitCode == 0 ? "xdelta3 did not write a file." : $"xdelta3 stopped with exit code {exitCode}.";
    }

    private static void CheckTool(XdeltaTool tool)
    {
        if (!File.Exists(tool.Path)) throw new EditException("xdelta3 was not found.");
        if (!string.Equals(Hash(tool.Path, default), tool.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new EditException("The xdelta3 program is not the verified version, so it was not run.");
    }

    private static void RequireHash(string path, string expected, string message, CancellationToken cancellation)
    {
        if (!string.Equals(Hash(path, cancellation), expected, StringComparison.OrdinalIgnoreCase)) throw new EditException(message);
    }

    /// <summary>Refuses a destination that exists, or that is one of the inputs under another name.</summary>
    private static void RefuseDestination(string destination, params string[] inputs)
    {
        string? folder = Path.GetDirectoryName(destination);
        if (folder is null || !Directory.Exists(folder)) throw new EditException("The destination folder does not exist.");
        foreach (string input in inputs)
            if (string.Equals(Path.GetFullPath(input), destination, StringComparison.OrdinalIgnoreCase))
                throw new EditException("Choose a new file name. An input file is never overwritten.");
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            foreach (string input in inputs)
                if (File.Exists(input) && SameFile(input, destination))
                    throw new EditException("Choose a new file name. That name refers to one of the input files.");
            throw new EditException("Choose a new file name. Existing files are never overwritten.");
        }
    }

    private static void RequireSpace(string destination, long needed)
    {
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        if (drive.IsReady && drive.AvailableFreeSpace < needed + SpaceMargin)
            throw new EditException($"There is not enough free space on {drive.Name.TrimEnd('\\')}. This needs about {(needed + SpaceMargin) / (1024.0 * 1024 * 1024):0.0} GB.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(true);
    }

    public static string Hash(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 * 1024 * 1024];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether two paths name the same file, including hard links and links to it.</summary>
    public static bool SameFile(string a, string b)
    {
        if (OperatingSystem.IsWindows())
        {
            using var first = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var second = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(first, out var x) && GetFileInformationByHandle(second, out var y))
                return x.VolumeSerialNumber == y.VolumeSerialNumber && x.FileIndexHigh == y.FileIndexHigh && x.FileIndexLow == y.FileIndexLow;
        }
        string Resolve(string p) => File.ResolveLinkTarget(p, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(p);
        return string.Equals(Resolve(a), Resolve(b), StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);
}
