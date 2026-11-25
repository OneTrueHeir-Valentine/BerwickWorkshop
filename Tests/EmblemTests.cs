using System.Text.RegularExpressions;

/// <summary>
/// The sidebar draws the application emblem from the same shapes as App/workshop.svg, the source of the program
/// icon. These checks keep the two from drifting apart.
/// </summary>
internal static class EmblemTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Sidebar emblem matches the icon artwork", () =>
        {
            string svg = File.ReadAllText(AccessKeyTests.SourceFile("App", "workshop.svg"));
            string xaml = File.ReadAllText(AccessKeyTests.SourceFile("App", "MainWindow.xaml"));
            var shapes = Regex.Matches(svg, " d=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Check(shapes.Count == 4, $"Expected 4 emblem shapes in workshop.svg, found {shapes.Count}.");
            foreach (string shape in shapes)
                Check(xaml.Contains($"Data=\"{shape}\""), $"The sidebar emblem is missing the shape {shape}.");
            Check(!xaml.Contains("Text=\"B\""), "The sidebar still draws the old letter badge.");
        });
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
