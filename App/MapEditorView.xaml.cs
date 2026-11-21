using Berwick.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Berwick.Workshop;

internal sealed record MapUnitEditRequest(int Key, StackPanel Host, StackPanel Fields, Button Button);

public partial class MapEditorView : UserControl
{
    private readonly MapEditorController controller = new();
    private readonly MapSelectionState selection = new();
    private MapRenderer? renderer;
    private MapEditorDocument? document;
    private EditSession? currentSession;
    private BattleMap? shownMap;
    private BitmapSource? shownPicture;
    private string shownTitle = "";
    private sealed record BattleChoice(int Value, string Text);

    public MapEditorView()
    {
        InitializeComponent();
        renderer = new(MapCanvas, MapDetails);
    }
    internal event Action<string>? StatusChanged;
    internal event Action? ProjectChanged;
    internal event Action? SelectionChanging;
    internal event Action? SelectionChanged;
    internal event Action<MapUnitEditRequest>? EditUnitRequested;
    internal event Action<Panel, int>? UnitChangesRequested;

    internal async Task OpenAsync(InspectionResult disc, EditSession? session, EventLibrary library,
        Action<int, byte[]> checkFits, Func<int, int, string?> fitProblem)
    {
        if (controller.IsCurrent(disc.Path)) return;
        currentSession = session;
        int token = selection.BeginLoad();
        MapCount.Text = "Reading the battles...";
        try
        {
            var catalogue = await controller.ReadCatalogueAsync(disc, library);
            if (!selection.IsCurrent(token)) return;
            controller.SetCatalogue(disc.Path, catalogue);
            document = new(disc, currentSession, library, controller.BattleArmies, checkFits, fitProblem);
            MapList.ItemsSource = catalogue.Battles.Select(b => new BattleChoice(b.Battle, b.Text)).ToList();
            int unused = catalogue.Battles.Count(b => b.Battle < 0);
            MapCount.Text = $"{catalogue.Battles.Count - unused} battles, {unused} unused maps";
            if (catalogue.Battles.Count > 0) MapList.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (selection.IsCurrent(token)) MapCount.Text = "The battles could not be read from this disc.";
        }
    }
    private async void OnBattleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (document is not { } current || MapList.SelectedValue is not int battle || !controller.TryGetBattle(battle, out var entry)) return;
        StartingPlacementsButton.IsEnabled = false;
        int token = selection.BeginLoad();
        MapSelection loaded;
        try { loaded = await controller.ReadMapAsync(current.Library, entry); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { loaded = new(null, null); }
        if (!selection.IsCurrent(token) || document != current) return;
        SelectionChanging?.Invoke();
        selection.Reset();
        MapUnitBox.Visibility = Visibility.Collapsed;
        shownMap = loaded.Map; shownPicture = null;
        if (loaded.Picture is { } art)
        {
            shownPicture = BitmapSource.Create(art.Width, art.Height, 96, 96, PixelFormats.Bgra32, null, art.Pixels, art.Width * 4);
            shownPicture.Freeze();
        }
        shownTitle = entry.Text;
        SelectionChanged?.Invoke(); Redraw();
    }
    internal void Reset()
    {
        SelectionChanging?.Invoke();
        selection.Reset(); controller.Reset(); document = null; currentSession = null;
        shownMap = null; shownPicture = null; shownTitle = "";
        MapList.ItemsSource = null;
        MapCanvas.Children.Clear(); MapDetails.Children.Clear();
        MapCanvas.Width = 0; MapCanvas.Height = 0;
        MapUnitFields.Children.Clear(); MapUnitBox.Visibility = Visibility.Collapsed; MapCount.Text = "";
        MapUnitEditorPanel.Children.Clear(); MapUnitEditorPanel.Visibility = Visibility.Collapsed;
        MapUnitFields.Visibility = Visibility.Visible;
        StartingPlacementsButton.IsEnabled = false;
        SelectionChanged?.Invoke();
    }
    internal void RefreshProject(EditSession? session, bool unitEditing)
    {
        currentSession = session;
        if (document is null) return;
        document.RefreshProject(session);
        if (shownMap is { } map) selection.RefreshSelection(document.AllUnits(map));
        if (!unitEditing) RefreshSelectedUnit();
        Redraw();
    }
    internal void SetEditAvailability(bool canEdit) => EditMapUnitButton.Visibility = canEdit && selection.UnitKey is not null ? Visibility.Visible : Visibility.Collapsed;
    internal void BeginUnitEdit()
    {
        if (selection.UnitKey is int key) EditUnitRequested?.Invoke(new(key, MapUnitEditorPanel, MapUnitFields, EditMapUnitButton));
    }
    private void OnEditUnitRequested(object sender, RoutedEventArgs e) => BeginUnitEdit();
    private void OnOptionsChanged(object sender, RoutedEventArgs e) => Redraw();
    private void Redraw()
    {
        if (document is null || renderer is null || shownTitle.Length == 0) return;
        bool editable = document.Session is not null && shownMap?.Battle > 0;
        StartingPlacementsButton.IsEnabled = editable && MapList.SelectedValue is int selectedBattle && selectedBattle == shownMap?.Battle;
        bool move = MapMoveBox.IsChecked == true && editable;
        if (!move) selection.MovingUnit = null;
        MapMoveBox.IsEnabled = editable; MapDeployBox.IsEnabled = editable;
        MapTerrainBox.IsEnabled = shownPicture is not null && MapArtBox.IsChecked == true; MapArtBox.IsEnabled = shownPicture is not null;
        var options = new MapRenderOptions(MapSizeBox.SelectedIndex, MapArtBox.IsChecked == true, MapTerrainBox.IsChecked == true,
            MapUnitsBox.IsChecked == true, MapArrivalsBox.IsChecked == true, move, !move && editable && MapDeployBox.IsChecked == true);
        renderer.Draw(shownMap, shownPicture, shownTitle, document, selection, options,
            new(ToggleDeployment, unit => SelectUnit(unit, unit.Text), AddDeploymentControls));
    }
    private (int X, int Y) HexAt(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(MapCanvas);
        return MapGeometry.ForSize(MapSizeBox.SelectedIndex).HexAt(p.X, p.Y);
    }
    private void OnCanvasRightClicked(object sender, MouseButtonEventArgs e)
    {
        if (shownMap is not { Battle: > 0 }) return;
        e.Handled = true;
        var (x,y) = HexAt(e); SelectHex(x,y);
    }
    private void OnCanvasLeftClicked(object sender, MouseButtonEventArgs e)
    {
        if (shownMap is not { Battle: > 0 } map || document is null) return;
        var (x,y) = HexAt(e);
        if (selection.PickingArrival && selection.Arrival is { } arrival)
        {
            e.Handled = true; selection.PickingArrival = false; SetArrivalHex(map, arrival, x,y); return;
        }
        if (MapMoveBox.IsChecked != true)
        {
            if (MapDeployBox.IsChecked == true && document.Session is not null)
            {
                if (UnitsAt(x,y).Count == 0) return;
                e.Handled = true; StatusChanged?.Invoke("A unit starts or arrives on that hex, so it is shown instead of being made a deployment hex.");
            }
            SelectHex(x,y); return;
        }
        if (document.Session is null) return;
        e.Handled = true;
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) return;
        var here = document.PlacedUnits(map).Where(p => p.Place.X == x && p.Place.Y == y).ToList();
        if (selection.MovingUnit is not { } moving || here.Count > 0 && !here.Any(p => p.Place.Group == moving.Group && p.Place.Index == moving.Index))
        {
            if (here.Count == 0) { StatusChanged?.Invoke("Click a unit first, then the hex where it should start."); return; }
            var pick = here[0].Place; selection.MovingUnit = (pick.Group,pick.Index);
            var unit = UnitsAt(x,y).FirstOrDefault(u => document.Unit(u.Key)?.Army == pick.Army && document.Unit(u.Key)?.Index == pick.Index);
            if (unit is not null) SelectUnit(unit,unit.Text);
            StatusChanged?.Invoke($"Moving {here[0].Name}. Click the hex where it should start, or another unit."); Redraw(); return;
        }
        if (here.Count > 0) { selection.MovingUnit = null; StatusChanged?.Invoke("Move cancelled."); Redraw(); return; }
        try
        {
            if (MapEdits.MovePlacement(document.Session,map,moving.Group,moving.Index,x,y,document.DeploymentOf(map)))
            {
                if (selection.UnitKey is not null)
                {
                    selection.Where = $"Starts at {x}, {y}";
                    selection.MoveSelectedUnit(x, y);
                }
                selection.MovingUnit = null;
                Changed($"Unit moved to {x}, {y}. Save the project to keep this change." + (map.Units.Count(u => u.Group == moving.Group && u.Index == moving.Index) > 1 ? " Every unit placed in that slot moved with it." : ""));
            }
        }
        catch (EditException ex) { StatusChanged?.Invoke(ex.Message); }
        Redraw();
    }
    private List<MapDisplayedUnit> UnitsAt(int x, int y) => shownMap is { } map && document is not null ? document.AllUnits(map).Where(u => u.X == x && u.Y == y).ToList() : [];
    private void SelectHex(int x, int y)
    {
        var here = UnitsAt(x,y);
        int turn = selection.Cycle(x,y,here.Count);
        if (turn >= 0) SelectUnit(here[turn],here[turn].Text + (here.Count > 1 ? $" ({turn+1} of {here.Count}; click again for the next)" : ""));
    }
    private void SelectUnit(MapDisplayedUnit unit, string where)
    {
        SelectionChanging?.Invoke();
        selection.Select(unit);
        selection.Where = where;
        RefreshSelectedUnit(); SelectionChanged?.Invoke(); Redraw();
    }
    internal void RefreshSelectedUnit()
    {
        if (selection.UnitKey is not int key || document?.Unit(key) is not { } unit) { MapUnitBox.Visibility = Visibility.Collapsed; return; }
        var record = document.Record(unit);
        MapUnitName.Text = unit.Name; MapUnitFields.Children.Clear();
        MapText.Text(MapUnitFields,selection.Where);
        MapText.Pair(MapUnitFields,"Class",$"{document.ClassName(record.ClassId)}, level {record.Level}");
        MapText.Pair(MapUnitFields,"Items",record.Items.Count == 0 ? "None" : string.Join(", ",record.Items.Select(i => i.Name)));
        MapText.Pair(MapUnitFields,"Personal skills",record.Skills.Count == 0 ? "None" : string.Join(", ",record.Skills));
        MapText.Text(MapUnitFields,"Changes apply wherever this army is used.");
        if (selection.Arrival is { } arrival && shownMap is { } map)
            MapEditorControls.Arrival(MapUnitFields,arrival,document.Session is not null, (x,y) => SetArrivalHex(map,arrival,x,y), turn => SetArrivalTurn(arrival,turn),
                () => { selection.PickingArrival = true; StatusChanged?.Invoke("Click the hex where this unit should arrive."); }, message => StatusChanged?.Invoke(message));
        UnitChangesRequested?.Invoke(MapUnitFields,key); MapUnitBox.Visibility = Visibility.Visible;
    }
    private void Changed(string message) { StatusChanged?.Invoke(message); ProjectChanged?.Invoke(); }
    private void OnStartingPlacementsRequested(object sender, RoutedEventArgs e)
    {
        if (document is not { Session: not null } current || shownMap is not { Battle: > 0 } map || MapList.SelectedValue is not int selectedBattle || selectedBattle != map.Battle)
            return;
        SelectionChanging?.Invoke();
        try
        {
            var candidates = current.StartingUnitCandidates(map);
            if (candidates.Count == 0)
            {
                StatusChanged?.Invoke("No existing units have an identified starting army/group assignment in this battle.");
                return;
            }
            var window = new StartingPlacementWindow(map.Battle, candidates, selection.UnitKey, (candidate, remove, x, y, acknowledged) =>
            {
                if (document != current || shownMap != map || MapList.SelectedValue is not int activeBattle || activeBattle != map.Battle)
                    throw new EditException("The selected battle changed. Open starting placements again.");
                if (!current.ApplyStartingPlacement(map, candidate, remove, x, y, acknowledged))
                    return;
                selection.Reset();
                string message = remove ? "Starting placement removed. The army record was kept." : $"Unit placed at {x}, {y}.";
                Changed(message + " Save the project to keep this change.");
                if (!remove)
                {
                    var placed = current.AllUnits(map).FirstOrDefault(unit => unit.Key == candidate.Unit.Key && unit.Arrival is null && unit.X == x && unit.Y == y);
                    if (placed is not null)
                        SelectUnit(placed, placed.Text);
                }
                RefreshSelectedUnit();
                SelectionChanged?.Invoke();
                Redraw();
            }) { Owner = Window.GetWindow(this) };
            window.ShowDialog();
        }
        catch (Exception exception) when (exception is EditException or IOException or InvalidDataException or InvalidOperationException)
        {
            StatusChanged?.Invoke("Starting placements are unavailable: " + exception.Message);
        }
    }
    private void SetArrivalHex(BattleMap map, Reinforcement arrival, int x, int y)
    {
        if (document is null) return;
        try
        {
            var edit = MapEdits.PlanArrivalMove(map,arrival,x,y);
            if (document.SetNumbers(edit.Numbers))
            {
                selection.Where = $"Arrives at {x}, {y}"; selection.ChangeSelectedArrival(edit.Updated);
                Changed($"Arrives at {x}, {y} now. Save the project to keep this change.");
            }
        }
        catch (EditException ex) { StatusChanged?.Invoke(ex.Message); }
        Redraw(); RefreshSelectedUnit();
    }
    private void SetArrivalTurn(Reinforcement arrival, int value)
    {
        if (document is null || arrival.TurnPlace is not { } turn) return;
        try
        {
            if (document.SetNumbers(new Dictionary<(int,int),int> { [(turn.Event,turn.Index)] = value-turn.TurnOffset }))
            {
                selection.ChangeSelectedArrival(arrival with { Turn = value }); Changed($"Arrives from turn {value} now. Save the project to keep this change.");
            }
        }
        catch (EditException ex) { StatusChanged?.Invoke(ex.Message); }
        Redraw(); RefreshSelectedUnit();
    }
    private void ToggleDeployment(int x, int y)
    {
        if (shownMap is not { } map || document?.Session is not { } session) return;
        try
        {
            var edit = MapEdits.ToggleDeployment(session,map,x,y,document.DeploymentOf(map));
            if (edit.Changed) Changed($"{edit.Count} deployment hexes. Save the project to keep this change.");
        }
        catch (EditException ex) { StatusChanged?.Invoke(ex.Message); }
        Redraw();
    }
    private void AddDeploymentControls(BattleMap map, int hexCount)
    {
        if (document is not null) MapEditorControls.Deployment(MapDetails,document.DeploymentLimit(map),hexCount,document.Session is not null,
            (value,own) => SetDeploymentLimit(map,value,own), message => StatusChanged?.Invoke(message));
    }
    private void SetDeploymentLimit(BattleMap map, int value, bool own)
    {
        if (document?.Session is not { } session || document.DeploymentLimit(map) is not { } found) return;
        try
        {
            if (own)
            {
                var edit = MapEdits.SetOwnDeploymentLimit(session,document.Library,map.Battle,value,document.CheckFits);
                if (edit.Changed) Changed($"This battle now sets its own limit in event {edit.Event}. Save the project to keep this change.");
            }
            else if (MapEdits.SetDeploymentLimit(session,found.Number,value,document.CheckFits,document.FitProblem)) Changed("Deployment limit changed. Save the project to keep this change.");
        }
        catch (EditException ex) { StatusChanged?.Invoke(ex.Message); }
        Redraw();
    }
}
