using Berwick.Core;

public static class PromotionAuditCommand
{
    public const string Usage = "Usage: --promotion-audit \"C:\\path\\original.iso\" \"C:\\path\\private-output-folder\"";

    public static bool TryParse(string[] args, out string discPath, out string outputDirectory, out string error)
    {
        discPath = "";
        outputDirectory = "";
        error = "";
        if (args is not ["--promotion-audit", var source, var directory])
        {
            error = "The promotion audit needs exactly one original ISO path and one private output folder.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(directory)
            || source.Contains('<') || source.Contains('>') || directory.Contains('<') || directory.Contains('>'))
        {
            error = "Enter real paths. Text in angle brackets is an example, not a filename.";
            return false;
        }
        if (source.StartsWith("--", StringComparison.Ordinal) || directory.StartsWith("--", StringComparison.Ordinal))
        {
            error = "An option cannot be used as an audit source or output path.";
            return false;
        }
        discPath = source;
        outputDirectory = directory;
        return true;
    }

    public static int Run(string[] args)
    {
        if (!TryParse(args, out var discPath, out var outputDirectory, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (!File.Exists(discPath))
        {
            Console.Error.WriteLine($"Original game ISO was not found: {discPath}");
            Console.Error.WriteLine(Usage);
            return 2;
        }
        try
        {
            outputDirectory = PromotionAuditFiles.ValidateDirectory(outputDirectory);
            using var input = File.OpenRead(discPath);
            var disc = Inspector.Inspect(discPath);
            RequireOriginal(disc);
            using var iso = new IsoImage(input);
            var library = new EventLibrary(new GameArchives(iso));
            var audit = PromotionAudit.Inspect(library);
            string edition = disc.Profile!.Name.StartsWith("Japanese", StringComparison.Ordinal) ? "jp" : "en";
            string prefix = $"promotion-{edition}-{disc.Sha256[..12]}";
            PromotionAuditFiles.Write(outputDirectory, prefix, audit);
            Console.WriteLine($"Read-only audit: {disc.Edition}; {library.Numbers.Count} event IDs; {audit.Commands.Count} candidate uses; {audit.EventCalls.Count} run-event expressions; {audit.Errors.Count} parse errors.");
            Console.WriteLine($"Private reports saved under {outputDirectory}");
            Console.WriteLine("Candidate command names are not verified promotion routines; computed event calls and script state require further research.");
            return audit.Errors.Count == 0 ? 0 : 2;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Promotion audit failed: {e.Message}");
            return 2;
        }
    }

    internal static void RequireOriginal(InspectionResult disc)
    {
        if (disc.Profile is null || disc.IsModified || !string.Equals(disc.Sha256, disc.Profile.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Promotion investigation requires a verified original Japanese retail or English v2.3 disc.");
    }
}
