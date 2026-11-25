using System.Text.RegularExpressions;

/// <summary>
/// Checks the main window's keyboard access keys. Two buttons sharing a letter make Alt plus that letter move
/// focus between them instead of activating either one.
/// </summary>
internal static class AccessKeyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Main window buttons have unique access keys", () =>
        {
            string xaml = File.ReadAllText(SourceFile("App", "MainWindow.xaml"));
            var keys = Regex.Matches(xaml, "Content=\"([^\"]*)_([^\"])")
                .Select(m => (Label: m.Groups[1].Value + m.Groups[2].Value, Key: char.ToUpperInvariant(m.Groups[2].Value[0])))
                .ToList();
            Check(keys.Count >= 20, $"Only {keys.Count} access keys were found in the main window.");
            var repeated = keys.GroupBy(k => k.Key).Where(g => g.Count() > 1)
                .Select(g => $"{g.Key}: {string.Join(", ", g.Select(k => k.Label))}").ToList();
            Check(repeated.Count == 0, "Repeated access keys: " + string.Join("; ", repeated));
        });
        test("Help opens with F1 instead of a shared access key", () =>
        {
            string xaml = File.ReadAllText(SourceFile("App", "MainWindow.xaml"));
            string code = File.ReadAllText(SourceFile("App", "MainWindow.xaml.cs"));
            Check(xaml.Contains("Content=\"Help\"") && xaml.Contains("Show help (F1)"), "The Help button lost its F1 hint.");
            Check(code.Contains("e.Key==Key.F1"), "F1 no longer opens Help.");
        });
        test("Help covers the license, the Randomizer, Promotions and keyboard access", () =>
        {
            string code = File.ReadAllText(SourceFile("App", "MainWindow.xaml.cs"));
            foreach (string title in new[] { "(\"License\",", "(\"Randomizer\",", "(\"Promotions\",", "F1 opens this help" })
                Check(code.Contains(title), $"Help is missing {title}.");
            string project = File.ReadAllText(SourceFile("App", "Berwick.Workshop.csproj"));
            Check(project.Contains("<Copyright>"), "The program has no copyright notice in its file properties.");
        });
    }

    internal static string SourceFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Could not find the application source from the test folder.", Path.Combine(parts));
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
