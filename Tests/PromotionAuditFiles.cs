using Berwick.Core;
using System.Globalization;

internal static class PromotionAuditFiles
{
    public static string ValidateDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Private audit output cannot use a linked directory.");
            string git = Path.Combine(directory.FullName, ".git");
            bool bare = File.Exists(Path.Combine(directory.FullName, "HEAD")) &&
                Directory.Exists(Path.Combine(directory.FullName, "objects")) && Directory.Exists(Path.Combine(directory.FullName, "refs"));
            if (File.Exists(git) || Directory.Exists(git) || bare)
                throw new IOException("Private audit output must be outside Git repositories, including ignored folders.");
        }
        return full;
    }

    public static void Write(string directory, string prefix, PromotionAuditReport audit)
    {
        if (prefix.Length == 0 || prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid audit report prefix.");
        directory = ValidateDirectory(directory);
        var reports = new Dictionary<string, IEnumerable<string>>
        {
            ["commands.csv"] = new[] { "event,offset,command,name,kind,args,literals,in_condition" }
                .Concat(audit.Commands.Select(c => string.Join(",", c.Event, c.Offset, c.Command, Csv(c.Name), Csv(c.Kind),
                    c.ArgumentCount, Csv(c.LiteralArguments), c.InCondition ? "true" : "false"))),
            ["event-calls.csv"] = new[] { "event,offset,target,resolution" }
                .Concat(audit.EventCalls.Select(c => string.Join(",", c.Event, c.Offset,
                    c.Target?.ToString(CultureInfo.InvariantCulture) ?? "", c.Target is null ? "computed" : "literal"))),
            ["parse-errors.txt"] = audit.Errors
        };
        var paths = reports.Keys.Select(name => Path.Combine(directory, prefix + "-" + name)).ToArray();
        if (paths.Any(path => File.Exists(path) || Directory.Exists(path)))
            throw new IOException("An audit for this source already exists. Choose another private output directory.");
        Directory.CreateDirectory(directory);
        ValidateDirectory(directory);
        var created = new List<string>();
        try
        {
            foreach (var (name, lines) in reports)
            {
                string path = Path.Combine(directory, prefix + "-" + name);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(path);
                using var writer = new StreamWriter(file);
                foreach (string line in lines) writer.WriteLine(line);
            }
        }
        catch
        {
            foreach (string path in created) File.Delete(path);
            throw;
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
