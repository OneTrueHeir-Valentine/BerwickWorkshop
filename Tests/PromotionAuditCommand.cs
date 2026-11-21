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

    var disc = Inspector.Inspect(discPath);
    if (disc.Profile is null || disc.IsModified)
        throw new InvalidDataException("Promotion investigation requires a verified original Japanese retail or English v2.3 disc.");
    using var input = File.OpenRead(discPath);
    var library = new EventLibrary(new GameArchives(new IsoImage(input)));
    var audit = PromotionAudit.Inspect(library);
    string edition = disc.Profile.Name.StartsWith("Japanese", StringComparison.Ordinal) ? "jp" : "en";
    string prefix = $"promotion-{edition}-{disc.Sha256[..12]}";
    Directory.CreateDirectory(outputDirectory);
    string commandsPath = Path.Combine(outputDirectory, prefix + "-commands.csv");
    string callsPath = Path.Combine(outputDirectory, prefix + "-event-calls.csv");
    string errorsPath = Path.Combine(outputDirectory, prefix + "-parse-errors.txt");
    if (File.Exists(commandsPath) || File.Exists(callsPath) || File.Exists(errorsPath))
        throw new IOException("An audit for this source already exists. Choose another private output directory.");
    static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    File.WriteAllLines(commandsPath, new[] { "event,offset,command,name,kind,args,literals,in_condition" }
        .Concat(audit.Commands.Select(c => string.Join(",", c.Event, c.Offset, c.Command, Csv(c.Name), Csv(c.Kind),
            c.ArgumentCount, Csv(c.LiteralArguments), c.InCondition ? "true" : "false"))));
    File.WriteAllLines(callsPath, new[] { "event,offset,target,resolution" }
        .Concat(audit.EventCalls.Select(c => string.Join(",", c.Event, c.Offset,
            c.Target?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            c.Target is null ? "computed" : "literal"))));
    File.WriteAllLines(errorsPath, audit.Errors);
    Console.WriteLine($"Read-only audit: {disc.Edition}; {library.Numbers.Count} event IDs; {audit.Commands.Count} candidate uses; {audit.EventCalls.Count} run-event expressions; {audit.Errors.Count} parse errors.");
    Console.WriteLine($"Private reports saved under {Path.GetFullPath(outputDirectory)}");
    Console.WriteLine("Candidate command names are not verified promotion routines; computed event calls and script state require further research.");
    return audit.Errors.Count == 0 ? 0 : 2;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Promotion audit failed: {e.Message}");
            return 2;
        }
    }
}
