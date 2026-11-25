using System.Xml.Linq;

/// <summary>
/// Checks which pages use the responsive list and details layout, and that each has the shape it needs.
/// </summary>
internal static class ResponsiveLayoutXamlTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] Pages =
    [
        "ItemPage", "ArmyPage", "CharacterPage", "ClassPage", "MovementPage", "TerrainPage",
        "ShopsPage", "FurniturePage", "TextsPage", "DishesPage", "LivePage"
    ];

    public static void Run(Action<string, Action> test)
    {
        test("Responsive layout is enabled on exactly the eleven list and details pages", () =>
        {
            var root = XDocument.Load(AccessKeyTests.SourceFile("App", "MainWindow.xaml")).Root!;
            var grids = root.Descendants(Wpf + "Grid")
                .Where(grid => grid.Attributes().Any(a => a.Name.LocalName == "ResponsiveSplit.Enabled" && a.Value == "True"))
                .ToList();
            var pages = grids.Select(PageOf).ToList();
            Check(pages.Order().SequenceEqual(Pages.Order()), "Responsive pages: " + string.Join(", ", pages));
            foreach (var grid in grids)
            {
                string page = PageOf(grid);
                var columns = grid.Element(Wpf + "Grid.ColumnDefinitions")?.Elements(Wpf + "ColumnDefinition").ToList() ?? [];
                Check(columns.Count == 3, $"{page} needs three columns.");
                Check(grid.Element(Wpf + "Grid.RowDefinitions") is null, $"{page} must not define rows.");
                Check(columns[2].Attribute("Width")?.Value is { } width && double.TryParse(width, out _),
                    $"{page} needs a fixed details width for the default split.");
                var children = grid.Elements().Where(e => !e.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal)).ToList();
                Check(children.Count == 3, $"{page} needs one list, one splitter and one details panel.");
                Check(children.Count(c => c.Name == Wpf + "GridSplitter" && Column(c) == 1) == 1, $"{page} needs its splitter in column 1.");
                Check(children.Count(c => c.Name != Wpf + "GridSplitter" && Column(c) == 0) == 1, $"{page} needs its list in column 0.");
                Check(children.Count(c => c.Name != Wpf + "GridSplitter" && Column(c) == 2) == 1, $"{page} needs its details in column 2.");
            }
        });
    }

    private static int Column(XElement element) => int.Parse(element.Attribute("Grid.Column")?.Value ?? "0");

    private static string PageOf(XElement element) =>
        element.Ancestors().Select(a => a.Attribute(Xaml + "Name")?.Value).FirstOrDefault(name => name?.EndsWith("Page") == true) ?? "(no page)";

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
