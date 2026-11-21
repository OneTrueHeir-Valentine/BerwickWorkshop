using Berwick.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Berwick.Workshop;

internal sealed record MapRenderOptions(int Size, bool Art, bool Terrain, bool Units, bool Arrivals, bool Move, bool Deployment);
internal sealed record MapRenderActions(Action<int, int> ToggleDeployment, Action<MapDisplayedUnit> SelectUnit, Action<BattleMap, int> DeploymentControls);

internal sealed class MapRenderer(Canvas canvas, StackPanel details)
{
    private static readonly Dictionary<string, Color> TerrainColors = new()
    {
        ["Plains or Grass"] = Color.FromRgb(185,216,138), ["Wilds"] = Color.FromRgb(205,187,142), ["Sand"] = Color.FromRgb(233,217,160),
        ["Forest"] = Color.FromRgb(79,138,60), ["Flying only"] = Color.FromRgb(124,150,178), ["Mountains"] = Color.FromRgb(168,139,94),
        ["Shoal"] = Color.FromRgb(156,201,227), ["Unidentified"] = Color.FromRgb(208,200,216), ["Cave"] = Color.FromRgb(154,143,128),
        ["Cave Interior"] = Color.FromRgb(125,115,104), ["Blocked"] = Color.FromRgb(94,90,85), ["Snow, temple or castle"] = Color.FromRgb(232,230,224),
        ["Gate"] = Color.FromRgb(224,123,57), ["Road or bridge"] = Color.FromRgb(216,196,154), ["Inner Floor"] = Color.FromRgb(207,194,168),
        ["Throne"] = Color.FromRgb(217,180,74), ["Cliff"] = Color.FromRgb(142,122,98), ["Building"] = Color.FromRgb(181,82,59),
        ["Snowy Woods"] = Color.FromRgb(159,191,160), ["Swamp"] = Color.FromRgb(141,111,168)
    };

    public void Draw(BattleMap? map, BitmapSource? picture, string title, MapEditorDocument document,
        MapSelectionState selection, MapRenderOptions options, MapRenderActions actions)
    {
        canvas.Children.Clear();
        details.Children.Clear();
        details.Children.Add(new TextBlock { Text = title, FontFamily = new("Georgia"), FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,8) });
        if (map is null)
        {
            details.Children.Add(new TextBlock { Text = "This battle's map could not be read.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)details.FindResource("Muted") });
            return;
        }
        var geometry = MapGeometry.ForSize(options.Size);
        double w = geometry.ColumnWidth, h = geometry.RowHeight, side = w / 3;
        (canvas.Width, canvas.Height) = geometry.Extent(map.Width, map.Height);
        bool art = options.Art && picture is not null, colors = !art || options.Terrain;
        if (art)
        {
            var image = new Image { Source = picture, Width = picture!.PixelWidth * geometry.Scale, Height = picture.PixelHeight * geometry.Scale, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            canvas.Children.Add(image);
        }
        var spotsAt = map.Spots.GroupBy(s => (s.X, s.Y)).ToDictionary(g => g.Key, g => g.ToList());
        var placed = document.PlacedUnits(map);
        var unitsAt = options.Units ? placed.GroupBy(p => (p.Place.X, p.Place.Y)).ToDictionary(g => g.Key, g => g.ToList()) : [];
        var deployList = document.DeploymentOf(map);
        var deployAt = options.Units || options.Deployment ? deployList.ToHashSet() : [];
        int deployLimit = document.DeploymentLimit(map)?.Number.Value ?? deployList.Count;
        var unusedDeploy = deployList.Skip(deployLimit).ToHashSet();
        var arrivals = document.ArrivingUnits(map);
        var talkNames = document.TalkNames(map);
        var arrivalsAt = options.Arrivals ? arrivals.GroupBy(a => (a.Place.X, a.Place.Y)).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Place.Turn ?? int.MaxValue).ToList()) : [];
        var used = new HashSet<string>();
        var gridLine = new SolidColorBrush(art ? Color.FromArgb(70,255,255,255) : Color.FromArgb(70,0,0,0));
        gridLine.Freeze();
        double marker = Math.Max(15, h * 0.6);
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                int kind = map.KindAt(x, y);
                if (kind == BattleMaps.Border) continue;
                string name = TerrainData.TerrainGroups[kind]; used.Add(name);
                var (cx, cy) = geometry.Center(x, y);
                Color fill = colors ? TerrainColors.GetValueOrDefault(name, Colors.LightGray) : Colors.Transparent;
                if (colors && art) fill.A = 120;
                var hex = new Polygon { Fill = new SolidColorBrush(fill), Stroke = gridLine, StrokeThickness = 0.6 };
                hex.Points = new([new(cx-w/2-side/2,cy), new(cx-side,cy-h/2), new(cx+side,cy-h/2), new(cx+w/2+side/2,cy), new(cx+side,cy+h/2), new(cx-side,cy+h/2)]);
                string tip = $"{x}, {y}: {name}";
                if (spotsAt.TryGetValue((x,y), out var here)) tip += "\n" + string.Join("\n", here.Select(s => s.Unit == 0 ? s.Name : $"{s.Name} (one character only)"));
                unitsAt.TryGetValue((x,y), out var standing);
                if (standing is not null) tip += "\n" + string.Join("\n", standing.Select(p => $"{TeamName(p.Place.Team)}: {p.Text}"));
                if (deployAt.Contains((x,y))) tip += unusedDeploy.Contains((x,y)) ? $"\nDeployment hex not used: the game marks only the first {deployLimit}" : "\nYour units can start here";
                arrivalsAt.TryGetValue((x,y), out var arriving);
                if (arriving is not null) tip += "\n" + string.Join("\n", arriving.Select(a => $"Arrives {When(a.Place,talkNames)}: {a.Text}{(a.Place.FromX != a.Place.X || a.Place.FromY != a.Place.Y ? ", from the map edge" : "")}"));
                hex.ToolTip = tip; canvas.Children.Add(hex);
                if (options.Deployment) { int hx = x, hy = y; hex.Cursor = Cursors.Hand; hex.MouseLeftButtonDown += (_, e) => { e.Handled = true; actions.ToggleDeployment(hx,hy); }; }
                if (options.Move)
                {
                    hex.Cursor = Cursors.Hand;
                    if (selection.MovingUnit is { } moving && standing?.Any(p => p.Place.Group == moving.Group && p.Place.Index == moving.Index) == true)
                    { hex.Stroke = Brushes.Gold; hex.StrokeThickness = 3; Panel.SetZIndex(hex,4); }
                }
                if (deployAt.Contains((x,y)))
                {
                    bool unused = unusedDeploy.Contains((x,y));
                    var start = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(unused ? (byte)25 : (byte)70,46,111,216)), Stroke = new SolidColorBrush(TeamColor(0)), StrokeThickness = unused ? 1 : 2, IsHitTestVisible = false };
                    if (unused) start.StrokeDashArray = new() { 2,2 };
                    foreach (var point in hex.Points) start.Points.Add(new(cx+(point.X-cx)*0.82, cy+(point.Y-cy)*0.82));
                    canvas.Children.Add(start);
                }
                double shift = here is not null && standing is not null ? w * 0.24 : 0;
                if (arriving is not null)
                {
                    double size = Math.Max(13,marker*0.72); var first = arriving[0];
                    var tag = new Border { Width = size, Height = size, CornerRadius = new(3), Background = Brushes.White, BorderBrush = new SolidColorBrush(TeamColor(first.Place.Team)), BorderThickness = new(2), ToolTip = tip,
                        Child = new TextBlock { Text = TurnLabel(first.Place), FontSize = size > 16 ? 10 : 8, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    bool crowded = standing is not null || here is not null;
                    Canvas.SetLeft(tag,cx-size/2+(crowded?w*0.3:0)); Canvas.SetTop(tag,cy-size/2-(crowded?h*0.28:0)); Panel.SetZIndex(tag,3); canvas.Children.Add(tag);
                }
                if (standing is not null)
                {
                    var first = standing[0]; double size = marker * 0.8;
                    var box = new Rectangle { Width = size, Height = size, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(TeamColor(first.Place.Team)), Stroke = first.Named ? new SolidColorBrush(Color.FromRgb(241,196,15)) : Brushes.White, StrokeThickness = first.Named ? 2.4 : 1.4, ToolTip = tip };
                    Canvas.SetLeft(box,cx-shift-size/2); Canvas.SetTop(box,cy-size/2); Panel.SetZIndex(box,1); canvas.Children.Add(box);
                }
                if (here is not null)
                {
                    var mark = new Ellipse { Width = marker, Height = marker, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.4, ToolTip = tip };
                    Canvas.SetLeft(mark,cx+shift-marker/2); Canvas.SetTop(mark,cy-marker/2); Panel.SetZIndex(mark,1); canvas.Children.Add(mark);
                    var label = new TextBlock { Text = (map.Spots.ToList().IndexOf(here[0])+1).ToString(), FontSize = marker > 18 ? 11 : 9, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
                    label.Measure(new(double.PositiveInfinity,double.PositiveInfinity));
                    Canvas.SetLeft(label,cx+shift-label.DesiredSize.Width/2); Canvas.SetTop(label,cy-label.DesiredSize.Height/2); Panel.SetZIndex(label,2); canvas.Children.Add(label);
                }
            }
        details.Children.Add(new TextBlock { Text = $"{map.Width} by {map.Height} hexes, file {map.File}", Foreground = (Brush)details.FindResource("Muted"), FontSize = 12, Margin = new(0,0,0,10) });
        if (map.Battle == 0) MapText.Text(details, "No event in the game starts a battle on this map. Some belong to quests that are listed in the game's data but never offered.");
        else DrawBattleDetails(map,document,placed,arrivals,deployList,talkNames,actions);
        MapText.Section(details,"Terrain");
        foreach (string name in TerrainData.TerrainGroups.Distinct().Where(used.Contains))
            Legend(name, new SolidColorBrush(TerrainColors.GetValueOrDefault(name,Colors.LightGray)), new SolidColorBrush(Color.FromArgb(90,0,0,0)),0,1);
    }

    private void DrawBattleDetails(BattleMap map, MapEditorDocument document, List<MapPlacedUnit> placed,
        List<MapArrivalUnit> arrivals, IReadOnlyList<(int X,int Y)> deployList, Func<int,string> talkNames, MapRenderActions actions)
    {
        MapText.Section(details,"Spots");
        if (map.Spots.Count == 0) MapText.Text(details,"None");
        for (int i = 0; i < map.Spots.Count; i++) { var s = map.Spots[i]; MapText.Text(details,$"{i+1}. {s.Name} at {s.X}, {s.Y}{(s.Unit != 0 ? " (one character only)" : "")}"); }
        MapText.Section(details,"Units at the start");
        if (deployList.Count > 0) MapText.Text(details,$"Your army: {deployList.Count} hexes to place units{(document.Session?.IsEdited(EditSession.Deployments,map.Top) == true ? " (edited)" : "")}");
        if (map.Battle > 0) actions.DeploymentControls(map,deployList.Count);
        var everyUnit = document.AllUnits(map);
        if (everyUnit.Count > 0)
        {
            var list = new StackPanel();
            foreach (var unit in everyUnit.OrderBy(u => u.Arrival is not null).ThenBy(u => u.Name))
            {
                var pick = new Button { Content = new TextBlock { Text = $"{unit.Name}{(unit.Arrival is not null ? " (arrives)" : "")}, {unit.X}, {unit.Y}", TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(2,1,2,1), Cursor = Cursors.Hand, FontSize = 12 };
                pick.Click += (_,_) => actions.SelectUnit(unit); list.Children.Add(pick);
            }
            details.Children.Add(new Expander { Header = $"Every unit ({everyUnit.Count}), click one to select it", Content = list, Margin = new(0,4,0,8), FontSize = 12 });
        }
        foreach (var team in placed.GroupBy(p => p.Place.Team).OrderBy(g => g.Key == 1 ? 0 : 1).ThenBy(g => g.Key))
        {
            var named = team.Where(p => p.Named).Select(p => p.Name).Distinct().ToList();
            MapText.Text(details,$"{TeamName(team.Key)}: {team.Count()} {(team.Count() == 1 ? "unit" : "units")}{(named.Count > 0 ? ", led by " + string.Join(", ",named) : "")}");
        }
        if (placed.Count == 0) MapText.Text(details,"No army is placed when this battle opens; its units arrive through the battle's events.");
        if (placed.Count > 0 || deployList.Count > 0)
            foreach (var (label,team,boxed) in new[] { ("Where your units can start",0,false), ("Enemy unit",1,true), ("Unit of another army",2,true), ("Leader or boss (gold edge)",1,true) })
            {
                if (boxed && label.StartsWith("Unit of") && !placed.Any(p => p.Place.Team >= 2)) continue;
                Legend(label, new SolidColorBrush(boxed ? TeamColor(team) : Color.FromArgb(70,46,111,216)), new SolidColorBrush(label.StartsWith("Leader") ? Color.FromRgb(241,196,15) : boxed ? Colors.White : TeamColor(team)), boxed ? 3 : 0, label.StartsWith("Leader") ? 2 : boxed ? 1 : 2);
            }
        if (arrivals.Count > 0)
        {
            MapText.Section(details,"Later arrivals");
            foreach (var when in arrivals.GroupBy(a => When(a.Place,talkNames)).OrderBy(g => g.Min(a => a.Place.Turn ?? int.MaxValue)).ThenBy(g => g.Key))
            {
                var names = when.GroupBy(a => a.Name).Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key);
                MapText.Text(details,$"{char.ToUpperInvariant(when.Key[0])}{when.Key[1..]}: {string.Join(", ",names)}");
            }
            Legend("Later arrival and its turn",Brushes.White,new SolidColorBrush(TeamColor(1)),3,2);
        }
        MapText.Section(details,"Armies");
        var ids = document.BattleArmies.GetValueOrDefault(map.Battle) ?? [];
        var labels = document.Disc.Armies.Where(a => ids.Contains(a.Id)).Select(a => $"Army {a.Id:000}: {string.Join(", ",a.Units.Where(u => u.Named).Select(u => u.Name).Distinct().Take(4).DefaultIfEmpty(a.Units.Count == 0 ? "" : a.Units[0].Name))}").ToList();
        if (labels.Count == 0) MapText.Text(details,"Not known"); else foreach (string label in labels) MapText.Text(details,label);
    }

    private void Legend(string label, Brush fill, Brush stroke, double corner, double thickness)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0,0,0,4) };
        row.Children.Add(new Border { Width = 14, Height = 14, Background = fill, BorderBrush = stroke, CornerRadius = new(corner), BorderThickness = new(thickness), Margin = new(0,0,8,0) });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12 }); details.Children.Add(row);
    }

    private static string When(Reinforcement r, Func<int,string> name)
    {
        string text = r.Turn is not int first ? r.Talk is { } talk ? $"when {(talk.Speaker == 0 ? "one of your units" : name(talk.Speaker))} talks to {name(talk.Listener)}" : r.Spot is { } spot ? spot.Name == "Event" ? $"when a unit sets off the spot at {spot.X}, {spot.Y}" : $"when a unit uses {spot.Name} at {spot.X}, {spot.Y}" : r.EveryTurn ? "at the start of a turn, when the battle's conditions are met" : "when an event in the battle happens"
            : r.LastTurn == first ? $"on turn {first}" : r.LastTurn is int last ? first <= 1 ? $"each turn up to turn {last}" : $"each turn from turn {first} to {last}" : $"each turn from turn {first}";
        if (r.Turn is not null && r.Spot is { } at) text += $", when a unit uses {(at.Name == "Event" ? "the spot" : at.Name)} at {at.X}, {at.Y}";
        return r.Conditional && r.Spot is null && r.Turn is not null ? text + ", under conditions the battle checks" : text;
    }
    private static string TurnLabel(Reinforcement r) => r.Turn is not int first ? "" : r.LastTurn == first ? first.ToString() : $"{first}+";
    private static string TeamName(int team) => team switch { 0 => "Your army", 1 => "Enemy", _ => "Other army" };
    private static Color TeamColor(int team) => team switch { 0 => Color.FromRgb(46,111,216), 1 => Color.FromRgb(192,57,43), _ => Color.FromRgb(46,139,87) };
}

internal static class MapText
{
    public static void Pair(Panel panel, string label, string value)
    {
        var grid = new Grid { Margin = new(0,0,0,7) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Foreground = (Brush)panel.FindResource("Muted"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
        var text = new TextBlock { Text = value, FontWeight = FontWeights.SemiBold, Margin = new(12,0,0,0) };
        Grid.SetColumn(text,1); grid.Children.Add(text); panel.Children.Add(grid);
    }
    public static void Text(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,4) });
    public static void Section(Panel panel, string title) => panel.Children.Add(new TextBlock { Text = title, Style = (Style)panel.FindResource("Caption"), FontWeight = FontWeights.SemiBold, Margin = new(0,14,0,8) });
}
