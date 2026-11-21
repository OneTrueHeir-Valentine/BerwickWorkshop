using Berwick.Core;
using CoreCharacterFields = Berwick.Core.CharacterFields;
using CoreClassFields = Berwick.Core.ClassFields;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Berwick.Workshop;

public partial class MainWindow
{
    // The view owns its controls; the controller owns map discovery and loading.
    private readonly MapEditorController mapController = new();
    private ListBox MapList => MapsPage.MapListControl;
    private CheckBox MapArtBox => MapsPage.MapArtBoxControl;
    private CheckBox MapTerrainBox => MapsPage.MapTerrainBoxControl;
    private CheckBox MapUnitsBox => MapsPage.MapUnitsBoxControl;
    private CheckBox MapArrivalsBox => MapsPage.MapArrivalsBoxControl;
    private CheckBox MapMoveBox => MapsPage.MapMoveBoxControl;
    private CheckBox MapDeployBox => MapsPage.MapDeployBoxControl;
    private ComboBox MapSizeBox => MapsPage.MapSizeBoxControl;
    private Canvas MapCanvas => MapsPage.MapCanvasControl;
    private Border MapUnitBox => MapsPage.MapUnitBoxControl;
    private Button EditMapUnitButton => MapsPage.EditMapUnitButtonControl;
    private TextBlock MapUnitName => MapsPage.MapUnitNameControl;
    private StackPanel MapUnitFields => MapsPage.MapUnitFieldsControl;
    private StackPanel MapUnitEditorPanel => MapsPage.MapUnitEditorPanelControl;
    private StackPanel MapDetails => MapsPage.MapDetailsControl;
    private TextBlock MapCount => MapsPage.MapCountControl;

    private async Task OpenMaps()
    {
        if (primary is not { Supported: true } disc || mapController.IsCurrent(disc.Path)) return;
        await OpenEvents();
        if (events is not { } library || primary != disc) return;
        MapCount.Text = "Reading the battles...";
        try
        {
            var index = await mapController.ReadCatalogueAsync(disc, library);
            if (events != library || primary != disc) return;
            mapController.SetCatalogue(disc.Path, index);
            MapList.ItemsSource = index.Battles.Select(b => new Choice(b.Battle, b.Text)).ToList();
            int unused = index.Battles.Count(b => b.Battle < 0);
            MapCount.Text = $"{index.Battles.Count - unused} battles, {unused} unused maps";
            if (index.Battles.Count > 0) MapList.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MapCount.Text = "The battles could not be read from this disc.";
        }
    }

    private async void MapSelected(object sender, SelectionChangedEventArgs e)
    {
        if (events is not { } library || MapList.SelectedValue is not int battle) return;
        if (!mapController.TryGetBattle(battle, out var entry)) return;
        BattleMap? map;
        BitmapSource? picture = null;
        try
        {
            var selection = await mapController.ReadMapAsync(library, entry);
            map = selection.Map;
            if (selection.Picture is { } art)
            {
                picture = BitmapSource.Create(art.Width, art.Height, 96, 96, PixelFormats.Bgra32,
                    null, art.Pixels, art.Width * 4);
                picture.Freeze();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            map = null;
        }
        if (events != library || MapList.SelectedValue is not int selected || selected != battle) return;
        if (editing?.Page == View.Maps) EndEdit();
        selectedMapUnit = null;
        selectedArrival = null;
        pickingArrival = false;
        MapUnitBox.Visibility = Visibility.Collapsed;
        UpdateEditButtons();
        shownMap = map;
        shownPicture = picture;
        shownTitle = entry.Text;
        DrawMap(map, picture, entry.Text);
    }

    // Deployment: hexes are the map file's placement group 0 (project table Deployments, keyed by the map's archive);
    // the limit is a SetMapUnitMax number in the battle's opening events or the shared default in event 70.
    private IReadOnlyList<(int X,int Y)> DeploymentOf(BattleMap map)=>session is not null && session.IsEdited(EditSession.Deployments,map.Top)
        ?Deployment.Hexes(session.EditedBytes(EditSession.Deployments,map.Top)):map.Deployment;
    // Army placements: where each placed unit starts, from the project's placement record when the map is edited.
    private (int Group,int Index)? movingUnit;
    private double mapHexWidth=30,mapHexHeight=26.25;
    private IEnumerable<MapUnit> UnitsOf(BattleMap map)
    {
        if(session is null || !session.IsEdited(EditSession.Placements,map.Top))return map.Units;
        var record=session.EditedBytes(EditSession.Placements,map.Top);
        return map.Units.Select(u=>Placement.Slot(record,u.Group,u.Index+1) is { } at?u with {X=at.X,Y=at.Y}:u)
            .Where(u=>u.X<map.Width && u.Y<map.Height);
    }
    private (int X,int Y) HexAt(Point p)
    {
        int x=(int)Math.Floor(p.X/mapHexWidth);
        int y=(int)Math.Floor((p.Y-((x&1)==1?mapHexHeight/2:0))/mapHexHeight);
        return (x,y);
    }
    private void MapCanvasClick(object sender,MouseButtonEventArgs e)
    {
        if(shownMap is not { } map || map.Battle<=0)return;
        if(pickingArrival && selectedArrival is { } picked)
        {
            e.Handled=true;pickingArrival=false;
            var (px,py)=HexAt(e.GetPosition(MapCanvas));
            SetArrivalHex(map,picked,px,py);
            return;
        }
        if(MapMoveBox.IsChecked!=true)
        {
            var (sx,sy)=HexAt(e.GetPosition(MapCanvas));
            if(MapDeployBox.IsChecked==true && session is not null)
            {
                if(MapUnitsAt(map,sx,sy).Count==0)return;
                e.Handled=true;
                Status.Text="A unit starts or arrives on that hex, so it is shown instead of being made a deployment hex.";
            }
            SelectMapUnit(map,sx,sy);
            return;
        }
        if(session is null)return;
        e.Handled=true;
        var (x,y)=HexAt(e.GetPosition(MapCanvas));
        if(x<0 || y<0 || x>=map.Width || y>=map.Height)return;
        var here=PlacedUnits(map).Where(p=>p.Place.X==x && p.Place.Y==y).ToList();
        if(movingUnit is not { } moving || here.Count>0 && !here.Any(p=>p.Place.Group==moving.Group && p.Place.Index==moving.Index))
        {
            if(here.Count==0){Status.Text="Click a unit first, then the hex where it should start.";return;}
            var pick=here[0].Place;movingUnit=(pick.Group,pick.Index);
            if(primary!.Armies.FirstOrDefault(a=>a.Id==pick.Army) is { } pickArmy && pick.Index<pickArmy.Units.Count)ShowMapUnitKey(pickArmy.Units[pick.Index].Key,$"Starts at {pick.X}, {pick.Y}: {here[0].Text}");
            Status.Text=$"Moving {here[0].Name}. Click the hex where it should start, or another unit.";
            DrawMap(shownMap,shownPicture,shownTitle);return;
        }
        if(here.Count>0){movingUnit=null;Status.Text="Move cancelled.";DrawMap(shownMap,shownPicture,shownTitle);return;}
        if(map.KindAt(x,y)==BattleMaps.Border){Status.Text="A unit must start inside the map.";return;}
        if(DeploymentOf(map).Contains((x,y))){Status.Text="That hex is one of your deployment hexes. Choose another, or remove it from deployment first.";return;}
        try
        {
            var record=Placement.Move(session.EditedBytes(EditSession.Placements,map.Top),moving.Group,moving.Index+1,x,y);
            var values=new Dictionary<string,int>{[$"g{moving.Group}x{moving.Index+1}"]=x,[$"g{moving.Group}y{moving.Index+1}"]=y};
            if(session.Apply(EditSession.Placements,map.Top,values))
            {
                Status.Text=$"Unit moved to {x}, {y}. Save the project to keep this change.";
                if(selectedMapUnit is not null)mapUnitWhere=$"Starts at {x}, {y}";
                if(map.Units.Count(u=>u.Group==moving.Group && u.Index==moving.Index)>1)Status.Text+=" Every unit placed in that slot moved with it.";
                movingUnit=null;RefreshDocument();
            }
        }
        catch(EditException ex){Status.Text=ex.Message;}
        DrawMap(shownMap,shownPicture,shownTitle);
    }
    // Selecting a unit on the map: units that start on a hex and units that arrive there, with their army unit keys.
    private int? selectedMapUnit;
    private string mapUnitWhere="";
    private (int X,int Y,int Turn)? lastMapClick;
    private readonly Dictionary<(int Key,int X,int Y),Reinforcement> arrivalOf=[];
    private Reinforcement? selectedArrival;
    private bool pickingArrival;
    private List<(int Key,string Name,string Text,int X,int Y,bool Arrives)> AllMapUnits(BattleMap map)
    {
        var result=new List<(int,string,string,int,int,bool)>();
        if(primary is null)return result;
        var armies=primary.Armies.ToDictionary(a=>a.Id);
        foreach(var p in PlacedUnits(map))
            if(armies.TryGetValue(p.Place.Army,out var army) && p.Place.Index<army.Units.Count)
                result.Add((army.Units[p.Place.Index].Key,p.Name,$"Starts at {p.Place.X}, {p.Place.Y}: {p.Text}",p.Place.X,p.Place.Y,false));
        var taken=new Dictionary<(int,int),int>();
        foreach(var r in ArrivalsOf(map))
        {
            if(!armies.TryGetValue(r.Army,out var army))continue;
            var matches=army.Units.Where(u=>ArmyData.ScriptNumber(ArmyBytes(u))==r.Script).ToList();
            if(matches.Count==0)continue;
            int n=taken.TryGetValue((r.Army,r.Script),out var c)?c:0;taken[(r.Army,r.Script)]=n+1;
            var unit=matches[n%matches.Count];
            result.Add((unit.Key,unit.Name,$"Arrives at {r.X}, {r.Y}{(r.Turn is int t?$" on turn {t}":"")}: {unit.Name} (army {r.Army:000})",r.X,r.Y,true));
            arrivalOf[(unit.Key,r.X,r.Y)]=r;
        }
        return result;
    }
    private List<(int Key,string Name,string Text)> MapUnitsAt(BattleMap map,int x,int y)=>
        AllMapUnits(map).Where(u=>u.X==x && u.Y==y).Select(u=>(u.Key,u.Name,u.Text)).ToList();
    private void ShowMapUnitKey(int key,string where)
    {
        if(editing?.Page==View.Maps)EndEdit();
        selectedMapUnit=key;mapUnitWhere=where;
        selectedArrival=shownMap is null?null:AllMapUnits(shownMap).Where(u=>u.Key==key && u.Arrives && u.Text==where).Select(u=>arrivalOf.TryGetValue((u.Key,u.X,u.Y),out var r)?r:null).FirstOrDefault();
        ShowMapUnit(where);
    }
    private void MapCanvasRightClick(object sender,MouseButtonEventArgs e)
    {
        if(shownMap is not { } map || map.Battle<=0)return;
        e.Handled=true;
        var (x,y)=HexAt(e.GetPosition(MapCanvas));
        SelectMapUnit(map,x,y);
    }
    private void SelectMapUnit(BattleMap map,int x,int y)
    {
        var here=MapUnitsAt(map,x,y);
        if(here.Count==0)return;
        int turn=lastMapClick is { } last && last.X==x && last.Y==y?(last.Turn+1)%here.Count:0;
        lastMapClick=(x,y,turn);
        if(editing?.Page==View.Maps)EndEdit();
        selectedMapUnit=here[turn].Key;
        selectedArrival=here[turn].Text.StartsWith("Arrives",StringComparison.Ordinal) && arrivalOf.TryGetValue((here[turn].Key,x,y),out var arrival)?arrival:null;
        mapUnitWhere=here[turn].Text+(here.Count>1?$" ({turn+1} of {here.Count}; click again for the next)":"");
        ShowMapUnit(mapUnitWhere);
    }
    private void ShowMapUnit(string where)
    {
        if(selectedMapUnit is not int key || primary is null){MapUnitBox.Visibility=Visibility.Collapsed;UpdateEditButtons();return;}
        var unit=primary.Armies.SelectMany(a=>a.Units).FirstOrDefault(u=>u.Key==key);
        if(unit is null){MapUnitBox.Visibility=Visibility.Collapsed;UpdateEditButtons();return;}
        var record=ArmyRecord(unit,new Dictionary<int,string>());
        MapUnitName.Text=unit.Name;
        MapUnitFields.Children.Clear();
        AddText(MapUnitFields,where);
        string className=classLookup.TryGetValue(record.ClassId,out var k)?k.Name:$"Class 0x{record.ClassId:X2}";
        AddPair(MapUnitFields,"Class",$"{className}, level {record.Level}");
        AddPair(MapUnitFields,"Items",record.Items.Count==0?"None":string.Join(", ",record.Items.Select(i=>i.Name)));
        AddPair(MapUnitFields,"Personal skills",record.Skills.Count==0?"None":string.Join(", ",record.Skills));
        AddText(MapUnitFields,"Changes apply wherever this army is used.");
        if(selectedArrival is { } arrival)AddArrivalControls(arrival);
        AddChangeList(MapUnitFields,ch=>ch.Table==EditSession.ArmyUnits && ch.Id==key,()=>session!.Revert(EditSession.ArmyUnits,key));
        MapUnitBox.Visibility=Visibility.Visible;
        UpdateEditButtons();
    }
    private void BeginMapUnitEdit(object sender,RoutedEventArgs e)
    {
        if(selectedMapUnit is not int key)return;
        var fields=ArmyData.Fields;
        OpenEditor(new(View.Maps,key,"Unit",MapUnitEditorPanel,MapUnitFields,EditMapUnitButton,
        [
            new("Unit record",EditSession.ArmyUnits,fields.Where(f=>f.Group.Length==0).ToList()),
            new("Weapon levels",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Weapon levels").ToList()),
            new("Personal skills",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Personal skills").ToList()),
            new("Items",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Items").ToList()),
            new("Dropped when defeated",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Drops").ToList()),
        ],"This unit's changes apply wherever this army is used. A class change affects only this unit; its stats become the new class's base plus its own bonus."));
    }
    // Reinforcements: their hexes and first turn are numbers in the battle's events (Reinforcement.Places and TurnPlace).
    private int EventNumberNow(int eventNumber,int index,int original)
    {
        if(session is null)return original;
        if(session.HasScript(eventNumber) && events is { } library)
        {
            var numbers=ScriptValues.Numbers(eventNumber,ScriptParser.Parse(session.EventScript(eventNumber)!),library.CommandNames);
            return index<numbers.Count?numbers[index].Value:original;
        }
        return session.IsEdited(EditSession.ScriptValues,ScriptValues.Key(eventNumber,index))?session.Value(EditSession.ScriptValues,ScriptValues.Key(eventNumber,index),ScriptValues.Field.Key):original;
    }
    private IEnumerable<Reinforcement> ArrivalsOf(BattleMap map)
    {
        foreach(var r in map.Arrivals)
        {
            var now=r;
            if(r.Places is { } at)now=now with {X=EventNumberNow(at.Event,at.X,r.X),Y=EventNumberNow(at.Event,at.Y,r.Y),FromX=EventNumberNow(at.Event,at.FromX,r.FromX),FromY=EventNumberNow(at.Event,at.FromY,r.FromY)};
            if(r.TurnPlace is { } turn && r.Turn is int first)now=now with {Turn=EventNumberNow(turn.Event,turn.Index,first-turn.TurnOffset)+turn.TurnOffset};
            yield return now;
        }
    }
    /// <summary>Changes event numbers as one step each: through the project's copy when it replaces the event, else as script numbers.</summary>
    private bool SetEventNumbers(IReadOnlyDictionary<(int Event,int Index),int> values)
    {
        if(session is null || events is not { } library)return false;
        bool changed=false;
        foreach(var group in values.GroupBy(v=>v.Key.Event))
        {
            if(session.HasScript(group.Key))
            {
                var updated=ScriptValues.Apply(session.EventScript(group.Key)!,group.ToDictionary(v=>v.Key.Index,v=>v.Value));
                CheckEventFits(library,group.Key,updated);
                changed|=session.SetScript(group.Key,updated);
                continue;
            }
            foreach(var (place,value) in group)
            {
                int key=ScriptValues.Key(place.Event,place.Index);
                if(ScriptFitProblem(key,value) is string problem)throw new EditException(problem);
                changed|=session.Apply(EditSession.ScriptValues,key,new Dictionary<string,int>{[ScriptValues.Field.Key]=value});
            }
        }
        return changed;
    }
    private void SetArrivalHex(BattleMap map,Reinforcement arrival,int x,int y)
    {
        if(arrival.Places is not { } at){Status.Text="This arrival's hex is not a plain number in its event, so it cannot be changed here.";return;}
        if(x<=0 || y<=0 || x>=map.Width-1 || y>=map.Height-1 || map.KindAt(x,y)==BattleMaps.Border){Status.Text="A unit must arrive inside the map.";return;}
        var values=new Dictionary<(int,int),int>{[(at.Event,at.X)]=x,[(at.Event,at.Y)]=y};
        // A unit that appears in place (no march in from elsewhere) keeps appearing in place.
        bool inPlace=arrival.FromX==arrival.X && arrival.FromY==arrival.Y;
        if(inPlace && (at.FromX,at.FromY)!=(at.X,at.Y)){values[(at.Event,at.FromX)]=x;values[(at.Event,at.FromY)]=y;}
        try
        {
            if(SetEventNumbers(values))
            {
                Status.Text=$"Arrives at {x}, {y} now. Save the project to keep this change.";
                mapUnitWhere=$"Arrives at {x}, {y}";
                selectedArrival=arrival with {X=x,Y=y,FromX=inPlace?x:arrival.FromX,FromY=inPlace?y:arrival.FromY};
                RefreshDocument();
            }
        }
        catch(EditException ex){Status.Text=ex.Message;}
        DrawMap(shownMap,shownPicture,shownTitle);
        if(selectedMapUnit is not null)ShowMapUnit(mapUnitWhere);
    }
    private void AddArrivalControls(Reinforcement arrival)
    {
        AddSection(MapUnitFields,"Arrival");
        string when=arrival.Turn is int t?(arrival.LastTurn is int l && l!=t?$"turns {t} to {l}":$"turn {t}"):"when an event sets it off";
        if(arrival.Spot is { } spot)when=$"after a unit uses {spot.Name} at {spot.X}, {spot.Y}"+(arrival.Turn is not null?$" ({when} at most)":"");
        else if(arrival.Talk is not null)when=$"after a conversation"+(arrival.Turn is not null?$" ({when} at most)":"");
        AddText(MapUnitFields,$"Arrives at {arrival.X}, {arrival.Y}{(arrival.FromX!=arrival.X || arrival.FromY!=arrival.Y?$", marching in from {arrival.FromX}, {arrival.FromY}":"")}, {when}{(arrival.Conditional?", if its other conditions are met":"")}.");
        if(session is null || shownMap is not { } map)return;
        if(arrival.Places is null){AddText(MapUnitFields,"This arrival's hex comes from a calculation in its event, so it is changed on the Events page.");return;}
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,4)};
        row.Children.Add(new TextBlock{Text="Column",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)});
        var bx=new TextBox{Width=40,Text=arrival.X.ToString(),Margin=new Thickness(0,0,8,0)};
        row.Children.Add(bx);
        row.Children.Add(new TextBlock{Text="Row",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)});
        var by=new TextBox{Width=40,Text=arrival.Y.ToString(),Margin=new Thickness(0,0,8,0)};
        row.Children.Add(by);
        var set=new Button{Content="Set",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(0,0,6,0)};
        set.Click+=(_,_)=>{if(int.TryParse(bx.Text.Trim(),out int x) && int.TryParse(by.Text.Trim(),out int y))SetArrivalHex(map,arrival,x,y);else Status.Text="Enter a column and a row.";};
        row.Children.Add(set);
        var pick=new Button{Content="Pick on map",Style=(Style)FindResource("SmallButton")};
        pick.Click+=(_,_)=>{pickingArrival=true;Status.Text="Click the hex where this unit should arrive.";};
        row.Children.Add(pick);
        System.Windows.Automation.AutomationProperties.SetName(bx,"Arrival column");System.Windows.Automation.AutomationProperties.SetName(by,"Arrival row");
        MapUnitFields.Children.Add(row);
        if(arrival.TurnPlace is { } turn && arrival.Turn is int first)
        {
            var turnRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,4)};
            turnRow.Children.Add(new TextBlock{Text="First turn",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)});
            var bt=new TextBox{Width=40,Text=first.ToString(),Margin=new Thickness(0,0,8,0)};
            System.Windows.Automation.AutomationProperties.SetName(bt,"Arrival turn");
            turnRow.Children.Add(bt);
            var setTurn=new Button{Content="Set turn",Style=(Style)FindResource("SmallButton")};
            setTurn.Click+=(_,_)=>
            {
                if(!int.TryParse(bt.Text.Trim(),out int t) || t<1 || t>99){Status.Text="Enter a turn from 1 to 99.";return;}
                try
                {
                    if(SetEventNumbers(new Dictionary<(int,int),int>{[(turn.Event,turn.Index)]=t-turn.TurnOffset}))
                    {
                        Status.Text=$"Arrives from turn {t} now. Save the project to keep this change.";
                        selectedArrival=arrival with {Turn=t};RefreshDocument();
                    }
                }
                catch(EditException ex){Status.Text=ex.Message;}
                DrawMap(shownMap,shownPicture,shownTitle);
                if(selectedMapUnit is not null)ShowMapUnit(mapUnitWhere);
            };
            turnRow.Children.Add(setTurn);
            MapUnitFields.Children.Add(turnRow);
            AddText(MapUnitFields,"Units brought in by the same turn check change turn together.");
        }
    }
    private int? DeployLimitValue(BattleMap map)
    {
        if(events is not { } library || map.Battle<=0)return null;
        (ScriptNumber Number,bool Own)? limit;
        lock(library)limit=Deployment.Limit(library,map.Battle,n=>session?.HasScript(n)==true?session.EventScript(n):null);
        if(limit is not { } found)return null;
        if(session is null || session.HasScript(found.Number.Event))return found.Number.Value;
        return session.Value(EditSession.ScriptValues,ScriptValues.Key(found.Number.Event,found.Number.Index),ScriptValues.Field.Key);
    }
    private void ToggleDeployment(BattleMap map,int x,int y)
    {
        if(session is null)return;
        var hexes=DeploymentOf(map).ToList();
        if(!hexes.Remove((x,y)))
        {
            if(map.KindAt(x,y)==BattleMaps.Border){Status.Text="A deployment hex must be inside the map.";return;}
            hexes.Add((x,y));
        }
        try
        {
            var record=Deployment.WithHexes(session.Table(EditSession.Deployments).Records[map.Top].Span,hexes);
            var values=Deployment.Fields.ToDictionary(f=>f.Key,f=>FieldCodec.Read(record,f));
            if(session.Apply(EditSession.Deployments,map.Top,values)){Status.Text=$"{hexes.Count} deployment hexes. Save the project to keep this change.";RefreshDocument();}
        }
        catch(EditException ex){Status.Text=ex.Message;}
        DrawMap(shownMap,shownPicture,shownTitle);
    }
    private void AddDeploymentControls(BattleMap map,int hexCount)
    {
        if(events is not { } library)return;
        (ScriptNumber Number,bool Own)? limit;
        lock(library)limit=Deployment.Limit(library,map.Battle,n=>session?.HasScript(n)==true?session.EventScript(n):null);
        if(limit is not { } found){AddText(MapDetails,"The most units you can deploy is not set by this battle's events.");return;}
        int key=ScriptValues.Key(found.Number.Event,found.Number.Index);
        bool replaced=session?.HasScript(found.Number.Event)==true;
        int value=replaced?found.Number.Value:session?.Value(EditSession.ScriptValues,key,ScriptValues.Field.Key)??found.Number.Value;
        AddText(MapDetails,found.Own?$"Most units you can deploy: {value} (set by this battle, event {found.Number.Event})"
            :$"Most units you can deploy: {value} (the game's default for every battle that does not set its own, event {Deployment.DefaultEvent})");
        if(hexCount<value)AddText(MapDetails,$"Only {hexCount} hexes are marked, so no more than {hexCount} units can be placed.");
        if(hexCount>value)AddText(MapDetails,$"The game marks only the first {value} hexes, in the order they were added; the other {hexCount-value} (dashed) are not used.");
        if(session is null)return;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,8)};
        var box=new TextBox{Width=50,Text=value.ToString(),Margin=new Thickness(0,0,6,0)};
        System.Windows.Automation.AutomationProperties.SetName(box,"Most units you can deploy");
        row.Children.Add(box);
        var set=new Button{Content=found.Own?"Set":"Set for every battle",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(0,0,6,0)};
        set.Click+=(_,_)=>
        {
            if(!int.TryParse(box.Text.Trim(),out int n) || n<1 || n>63){Status.Text="Enter a number from 1 to 63.";return;}
            if(replaced)
            {
                try
                {
                    var updated=ScriptValues.Apply(session.EventScript(found.Number.Event)!,new Dictionary<int,int>{[found.Number.Index]=n});
                    CheckEventFits(library,found.Number.Event,updated);
                    if(session.SetScript(found.Number.Event,updated)){Status.Text="Deployment limit changed. Save the project to keep this change.";RefreshDocument();DrawMap(shownMap,shownPicture,shownTitle);}
                }
                catch(EditException ex){Status.Text=ex.Message;}
                return;
            }
            if(ScriptFitProblem(key,n) is string problem){Status.Text=problem;return;}
            if(session.Apply(EditSession.ScriptValues,key,new Dictionary<string,int>{[ScriptValues.Field.Key]=n})){Status.Text="Deployment limit changed. Save the project to keep this change.";RefreshDocument();DrawMap(shownMap,shownPicture,shownTitle);}
        };
        row.Children.Add(set);
        if(!found.Own)
        {
            var own=new Button{Content="Set for this battle only",Style=(Style)FindResource("SmallButton")};
            own.Click+=(_,_)=>
            {
                if(!int.TryParse(box.Text.Trim(),out int n) || n<1 || n>63){Status.Text="Enter a number from 1 to 63.";return;}
                try
                {
                    byte[] updated;
                    int opening;
                    lock(library)
                    {
                        var target=Deployment.OwnLimitEvent(library,map.Battle) ?? throw new EditException("This battle has no opening event to hold its own limit.");
                        opening=target;
                        var current=session.EventScript(target) ?? library.Bytes(target);
                        updated=Deployment.AddOwnLimit(current,n,library.CommandNames ?? throw new EditException("This battle's deployment limit cannot be edited because its command list is unavailable."));
                    }
                    CheckEventFits(library,opening,updated);
                    if(session.SetScript(opening,updated)){Status.Text=$"This battle now sets its own limit in event {opening}. Save the project to keep this change.";RefreshDocument();DrawMap(shownMap,shownPicture,shownTitle);}
                }
                catch(EditException ex){Status.Text=ex.Message;}
            };
            row.Children.Add(own);
        }
        MapDetails.Children.Add(row);
    }
    private BattleMap? shownMap;
    private BitmapSource? shownPicture;
    private string shownTitle="";
    private void MapViewChanged(object sender,RoutedEventArgs e){if(shownTitle.Length>0)DrawMap(shownMap,shownPicture,shownTitle);}
    private static readonly Dictionary<string,Color> TerrainColors=new()
    {
        ["Plains or Grass"]=Color.FromRgb(185,216,138),["Wilds"]=Color.FromRgb(205,187,142),["Sand"]=Color.FromRgb(233,217,160),
        ["Forest"]=Color.FromRgb(79,138,60),["Flying only"]=Color.FromRgb(124,150,178),["Mountains"]=Color.FromRgb(168,139,94),
        ["Shoal"]=Color.FromRgb(156,201,227),["Unidentified"]=Color.FromRgb(208,200,216),["Cave"]=Color.FromRgb(154,143,128),
        ["Cave Interior"]=Color.FromRgb(125,115,104),["Blocked"]=Color.FromRgb(94,90,85),["Snow, temple or castle"]=Color.FromRgb(232,230,224),
        ["Gate"]=Color.FromRgb(224,123,57),["Road or bridge"]=Color.FromRgb(216,196,154),["Inner Floor"]=Color.FromRgb(207,194,168),
        ["Throne"]=Color.FromRgb(217,180,74),["Cliff"]=Color.FromRgb(142,122,98),["Building"]=Color.FromRgb(181,82,59),
        ["Snowy Woods"]=Color.FromRgb(159,191,160),["Swamp"]=Color.FromRgb(141,111,168),
    };
    private void DrawMap(BattleMap? map,BitmapSource? picture,string title)
    {
        MapCanvas.Children.Clear();MapDetails.Children.Clear();
        MapDetails.Children.Add(new TextBlock {Text=title,FontFamily=new FontFamily("Georgia"),FontSize=18,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
        if(map is null){MapDetails.Children.Add(new TextBlock {Text="This battle's map could not be read.",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted")});return;}
        // Hexes follow the painted picture: 40 pixels per column, 35 per row, odd columns half a row lower.
        double scale=MapSizeBox.SelectedIndex switch {0=>0.5,2=>1.0,_=>0.75};
        double w=BattleMaps.ColumnWidth*scale,h=BattleMaps.RowHeight*scale,side=w/3;
        bool art=MapArtBox.IsChecked==true && picture is not null;
        bool colors=!art || MapTerrainBox.IsChecked==true;
        MapTerrainBox.IsEnabled=picture is not null && MapArtBox.IsChecked==true;MapArtBox.IsEnabled=picture is not null;
        MapCanvas.Width=map.Width*w;MapCanvas.Height=(map.Height+0.5)*h;
        if(art)
        {
            var image=new Image {Source=picture,Width=picture!.PixelWidth*scale,Height=picture.PixelHeight*scale,IsHitTestVisible=false};
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            MapCanvas.Children.Add(image);
        }
        var spotsAt=map.Spots.GroupBy(s=>(s.X,s.Y)).ToDictionary(g=>g.Key,g=>g.ToList());
        var placed=PlacedUnits(map);
        bool showUnits=MapUnitsBox.IsChecked==true;
        var unitsAt=showUnits?placed.GroupBy(p=>(p.Place.X,p.Place.Y)).ToDictionary(g=>g.Key,g=>g.ToList()):[];
        bool moveUnits=MapMoveBox.IsChecked==true && session is not null && map.Battle>0;
        bool editDeploy=!moveUnits && MapDeployBox.IsChecked==true && session is not null && map.Battle>0;
        MapDeployBox.IsEnabled=session is not null && map.Battle>0;MapMoveBox.IsEnabled=session is not null && map.Battle>0;
        if(!moveUnits)movingUnit=null;
        mapHexWidth=w;mapHexHeight=h;
        var deployList=DeploymentOf(map);
        var deployAt=showUnits || editDeploy?deployList.ToHashSet():[];
        int deployLimit=DeployLimitValue(map)??deployList.Count;
        var unusedDeploy=deployList.Skip(deployLimit).ToHashSet();
        var arrivals=ArrivingUnits(map);
        var talkNames=TalkNames(map);
        var arrivalsAt=MapArrivalsBox.IsChecked==true?arrivals.GroupBy(a=>(a.Place.X,a.Place.Y)).ToDictionary(g=>g.Key,g=>g.OrderBy(a=>a.Place.Turn??int.MaxValue).ToList()):[];
        var used=new HashSet<string>();
        var gridLine=new SolidColorBrush(art?Color.FromArgb(70,255,255,255):Color.FromArgb(70,0,0,0));gridLine.Freeze();
        double marker=Math.Max(15,h*0.6);
        for(int y=0;y<map.Height;y++)
            for(int x=0;x<map.Width;x++)
            {
                int kind=map.KindAt(x,y);
                if(kind==BattleMaps.Border)continue;
                string name=TerrainData.TerrainGroups[kind];used.Add(name);
                double cx=x*w+w/2,cy=y*h+h/2+((x&1)==1?h/2:0);
                Color fill=colors?(TerrainColors.TryGetValue(name,out var c)?c:Colors.LightGray):Colors.Transparent;
                if(colors&&art)fill.A=120;
                var hex=new System.Windows.Shapes.Polygon {Fill=new SolidColorBrush(fill),Stroke=gridLine,StrokeThickness=0.6};
                hex.Points.Add(new Point(cx-w/2-side/2,cy));hex.Points.Add(new Point(cx-side,cy-h/2));hex.Points.Add(new Point(cx+side,cy-h/2));
                hex.Points.Add(new Point(cx+w/2+side/2,cy));hex.Points.Add(new Point(cx+side,cy+h/2));hex.Points.Add(new Point(cx-side,cy+h/2));
                string tip=$"{x}, {y}: {name}";
                if(spotsAt.TryGetValue((x,y),out var here))tip+="\n"+string.Join("\n",here.Select(s=>s.Unit==0?s.Name:$"{s.Name} (one character only)"));
                unitsAt.TryGetValue((x,y),out var standing);
                if(standing is not null)tip+="\n"+string.Join("\n",standing.Select(p=>$"{TeamName(p.Place.Team)}: {p.Text}"));
                if(deployAt.Contains((x,y)))tip+=unusedDeploy.Contains((x,y))?$"\nDeployment hex not used: the game marks only the first {deployLimit}":"\nYour units can start here";
                arrivalsAt.TryGetValue((x,y),out var arriving);
                if(arriving is not null)tip+="\n"+string.Join("\n",arriving.Select(a=>$"Arrives {When(a.Place,talkNames)}: {a.Text}{(a.Place.FromX!=a.Place.X || a.Place.FromY!=a.Place.Y?", from the map edge":"")}"));
                hex.ToolTip=tip;MapCanvas.Children.Add(hex);
                if(editDeploy){int hx=x,hy=y;hex.Cursor=Cursors.Hand;hex.MouseLeftButtonDown+=(_,args)=>{args.Handled=true;ToggleDeployment(map,hx,hy);};}
                if(moveUnits){hex.Cursor=Cursors.Hand;if(movingUnit is { } mu && standing?.Any(p=>p.Place.Group==mu.Group && p.Place.Index==mu.Index)==true){hex.Stroke=Brushes.Gold;hex.StrokeThickness=3;Panel.SetZIndex(hex,4);}}
                if(deployAt.Contains((x,y)))
                {
                    bool unused=unusedDeploy.Contains((x,y));
                    var start=new System.Windows.Shapes.Polygon {Fill=new SolidColorBrush(Color.FromArgb(unused?(byte)25:(byte)70,46,111,216)),Stroke=new SolidColorBrush(TeamColor(0)),StrokeThickness=unused?1:2,IsHitTestVisible=false};
                    if(unused)start.StrokeDashArray=new DoubleCollection{2,2};
                    foreach(var point in hex.Points)start.Points.Add(new Point(cx+(point.X-cx)*0.82,cy+(point.Y-cy)*0.82));
                    MapCanvas.Children.Add(start);
                }
                double shift=here is not null && standing is not null?w*0.24:0;
                if(arriving is not null)
                {
                    double size=Math.Max(13,marker*0.72);var first=arriving[0];
                    var tag=new Border {Width=size,Height=size,CornerRadius=new CornerRadius(3),Background=Brushes.White,BorderBrush=new SolidColorBrush(TeamColor(first.Place.Team)),BorderThickness=new Thickness(2),ToolTip=tip,
                        Child=new TextBlock {Text=TurnLabel(first.Place),FontSize=size>16?10:8,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
                    bool crowded=standing is not null || here is not null;
                    Canvas.SetLeft(tag,cx-size/2+(crowded?w*0.3:0));Canvas.SetTop(tag,cy-size/2-(crowded?h*0.28:0));Panel.SetZIndex(tag,3);MapCanvas.Children.Add(tag);
                }
                if(standing is not null)
                {
                    var first=standing[0];double size=marker*0.8;
                    var box=new System.Windows.Shapes.Rectangle {Width=size,Height=size,RadiusX=3,RadiusY=3,Fill=new SolidColorBrush(TeamColor(first.Place.Team)),
                        Stroke=first.Named?new SolidColorBrush(Color.FromRgb(241,196,15)):Brushes.White,StrokeThickness=first.Named?2.4:1.4,ToolTip=tip};
                    Canvas.SetLeft(box,cx-shift-size/2);Canvas.SetTop(box,cy-size/2);Panel.SetZIndex(box,1);MapCanvas.Children.Add(box);
                }
                if(here is not null)
                {
                    var mark=new System.Windows.Shapes.Ellipse {Width=marker,Height=marker,Fill=Brushes.White,Stroke=Brushes.Black,StrokeThickness=1.4,ToolTip=tip};
                    Canvas.SetLeft(mark,cx+shift-marker/2);Canvas.SetTop(mark,cy-marker/2);Panel.SetZIndex(mark,1);MapCanvas.Children.Add(mark);
                    var label=new TextBlock {Text=(map.Spots.ToList().IndexOf(here[0])+1).ToString(),FontSize=marker>18?11:9,FontWeight=FontWeights.SemiBold,IsHitTestVisible=false};
                    label.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                    Canvas.SetLeft(label,cx+shift-label.DesiredSize.Width/2);Canvas.SetTop(label,cy-label.DesiredSize.Height/2);Panel.SetZIndex(label,2);MapCanvas.Children.Add(label);
                }
            }
        MapDetails.Children.Add(new TextBlock {Text=$"{map.Width} by {map.Height} hexes, file {map.File}",Foreground=(Brush)FindResource("Muted"),FontSize=12,Margin=new Thickness(0,0,0,10)});
        if(map.Battle==0)
            AddText(MapDetails,"No event in the game starts a battle on this map. Some belong to quests that are listed in the game's data but never offered.");
        else
        {
        AddSection(MapDetails,"Spots");
        if(map.Spots.Count==0)AddText(MapDetails,"None");
        for(int i=0;i<map.Spots.Count;i++)
        {
            var s=map.Spots[i];
            AddText(MapDetails,$"{i+1}. {s.Name} at {s.X}, {s.Y}{(s.Unit!=0?" (one character only)":"")}");
        }
        AddSection(MapDetails,"Units at the start");
        if(deployList.Count>0)AddText(MapDetails,$"Your army: {deployList.Count} hexes to place units{(session?.IsEdited(EditSession.Deployments,map.Top)==true?" (edited)":"")}");
        if(map.Battle>0)AddDeploymentControls(map,deployList.Count);
        var everyUnit=AllMapUnits(map);
        if(everyUnit.Count>0)
        {
            var list=new StackPanel();
            foreach(var u in everyUnit.OrderBy(u=>u.Arrives).ThenBy(u=>u.Name))
            {
                var pick=new Button{Content=new TextBlock{Text=$"{u.Name}{(u.Arrives?" (arrives)":"")}, {u.X}, {u.Y}",TextWrapping=TextWrapping.Wrap},HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(2,1,2,1),Cursor=Cursors.Hand,FontSize=12};
                var chosen=u;pick.Click+=(_,_)=>ShowMapUnitKey(chosen.Key,chosen.Text);
                list.Children.Add(pick);
            }
            MapDetails.Children.Add(new Expander{Header=$"Every unit ({everyUnit.Count}), click one to select it",Content=list,Margin=new Thickness(0,4,0,8),FontSize=12});
        }
        foreach(var team in placed.GroupBy(p=>p.Place.Team).OrderBy(g=>g.Key==1?0:1).ThenBy(g=>g.Key))
        {
            var named=team.Where(p=>p.Named).Select(p=>p.Name).Distinct().ToList();
            AddText(MapDetails,$"{TeamName(team.Key)}: {team.Count()} {(team.Count()==1?"unit":"units")}{(named.Count>0?", led by "+string.Join(", ",named):"")}");
        }
        if(placed.Count==0)AddText(MapDetails,"No army is placed when this battle opens; its units arrive through the battle's events.");
        if(placed.Count>0 || deployList.Count>0)
        {
            foreach(var (label,color,boxed) in new[]{("Where your units can start",TeamColor(0),false),("Enemy unit",TeamColor(1),true),("Unit of another army",TeamColor(2),true),("Leader or boss (gold edge)",TeamColor(1),true)})
            {
                if(boxed && label.StartsWith("Unit of") && !placed.Any(p=>p.Place.Team>=2))continue;
                var line=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,4)};
                line.Children.Add(new Border {Width=14,Height=14,CornerRadius=new CornerRadius(boxed?3:0),Background=boxed?new SolidColorBrush(color):new SolidColorBrush(Color.FromArgb(70,46,111,216)),
                    BorderBrush=label.StartsWith("Leader")?new SolidColorBrush(Color.FromRgb(241,196,15)):new SolidColorBrush(boxed?Colors.White:color),BorderThickness=new Thickness(label.StartsWith("Leader")?2:boxed?1:2),Margin=new Thickness(0,0,8,0)});
                line.Children.Add(new TextBlock {Text=label,FontSize=12});
                MapDetails.Children.Add(line);
            }
        }
        if(arrivals.Count>0)
        {
            AddSection(MapDetails,"Later arrivals");
            foreach(var when in arrivals.GroupBy(a=>When(a.Place,talkNames)).OrderBy(g=>g.Min(a=>a.Place.Turn??int.MaxValue)).ThenBy(g=>g.Key))
            {
                var names=when.GroupBy(a=>a.Name).Select(g=>g.Count()>1?$"{g.Key} ({g.Count()})":g.Key);
                AddText(MapDetails,$"{char.ToUpperInvariant(when.Key[0])}{when.Key[1..]}: {string.Join(", ",names)}");
            }
            var key=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,4)};
            key.Children.Add(new Border {Width=14,Height=14,CornerRadius=new CornerRadius(3),Background=Brushes.White,BorderBrush=new SolidColorBrush(TeamColor(1)),BorderThickness=new Thickness(2),Margin=new Thickness(0,0,8,0)});
            key.Children.Add(new TextBlock {Text="Later arrival and its turn",FontSize=12});
            MapDetails.Children.Add(key);
        }
        AddSection(MapDetails,"Armies");
        var armyIds=mapController.BattleArmies.TryGetValue(map.Battle,out var ids)?ids:[];
        var armyLabels=primary?.Armies.Where(a=>armyIds.Contains(a.Id)).Select(a=>$"Army {a.Id:000}: {string.Join(", ",a.Units.Where(u=>u.Named).Select(u=>u.Name).Distinct().Take(4).DefaultIfEmpty(a.Units.Count==0?"":a.Units[0].Name))}").ToList()??[];
        if(armyLabels.Count==0)AddText(MapDetails,"Not known");else foreach(var l in armyLabels)AddText(MapDetails,l);
        }
        AddSection(MapDetails,"Terrain");
        foreach(var name in TerrainData.TerrainGroups.Distinct().Where(used.Contains))
        {
            var line=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,4)};
            line.Children.Add(new Border {Width=14,Height=14,Background=new SolidColorBrush(TerrainColors.TryGetValue(name,out var c)?c:Colors.LightGray),BorderBrush=new SolidColorBrush(Color.FromArgb(90,0,0,0)),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,8,0)});
            line.Children.Add(new TextBlock {Text=name,FontSize=12});
            MapDetails.Children.Add(line);
        }
    }

    // Units a battle places when it opens, with the unit's name, class and level as the army file has them.
    private List<(MapUnit Place,string Name,string Text,bool Named)> PlacedUnits(BattleMap map)
    {
        var result=new List<(MapUnit,string,string,bool)>();
        if(primary is null)return result;
        var armies=primary.Armies.ToDictionary(a=>a.Id);
        var noItems=new Dictionary<int,string>();
        foreach(var place in UnitsOf(map))
        {
            // A group can have more slots than the army file has units; the extra slots stay empty in the game.
            if(!armies.TryGetValue(place.Army,out var army) || place.Index>=army.Units.Count)continue;
            var unit=army.Units[place.Index];var record=ArmyRecord(unit,noItems);
            string className=classLookup.TryGetValue(record.ClassId,out var k)?k.Name:$"Class 0x{record.ClassId:X2}";
            result.Add((place,unit.Name,$"{unit.Name}, {className} level {record.Level} (army {place.Army:000})",unit.Named));
        }
        return result;
    }
    // Units the battle's events bring in later. Several calls can name the same script number; each takes the next unit with it.
    private List<(Reinforcement Place,string Name,string Text)> ArrivingUnits(BattleMap map)
    {
        var result=new List<(Reinforcement,string,string)>();
        if(primary is null)return result;
        var armies=primary.Armies.ToDictionary(a=>a.Id);
        var noItems=new Dictionary<int,string>();var taken=new Dictionary<(int,int),int>();
        foreach(var r in ArrivalsOf(map))
        {
            if(!armies.TryGetValue(r.Army,out var army))
            {
                // Army file 001 holds the recruitable characters (edited on the Characters page), who join some battles this way.
                var character=r.Army==StartingCharacters?primary.Characters.FirstOrDefault(c=>primary.UnitBytes.TryGetValue(c.Id,out var bytes) && ArmyData.ScriptNumber(bytes.Span)==r.Script):null;
                if(character is not null)result.Add((r,character.Name,$"{character.Name}, {(classLookup.TryGetValue(character.ClassId,out var ck)?ck.Name:$"Class 0x{character.ClassId:X2}")} level {character.Level} (recruitable character)"));
                continue;
            }
            var matches=army.Units.Where(u=>ArmyData.ScriptNumber(ArmyBytes(u))==r.Script).ToList();
            if(matches.Count==0)continue;
            int n=taken.TryGetValue((r.Army,r.Script),out var c)?c:0;taken[(r.Army,r.Script)]=n+1;
            var unit=matches[n%matches.Count];var record=ArmyRecord(unit,noItems);
            string className=classLookup.TryGetValue(record.ClassId,out var k)?k.Name:$"Class 0x{record.ClassId:X2}";
            result.Add((r,unit.Name,$"{unit.Name}, {className} level {record.Level} (army {r.Army:000})"));
        }
        return result;
    }
    private const int StartingCharacters=1;
    // A unit's name from its script number, among the battle's armies and the recruitable characters (script numbers repeat
    // between armies, so only the armies this battle uses are searched).
    private Func<int,string> TalkNames(BattleMap map)
    {
        var names=new Dictionary<int,string>();
        if(primary is not null)
        {
            var used=map.Units.Select(u=>u.Army).Concat(map.Arrivals.Select(a=>a.Army)).ToHashSet();
            foreach(var army in primary.Armies.Where(a=>used.Contains(a.Id)))
                foreach(var unit in army.Units)names.TryAdd(ArmyData.ScriptNumber(ArmyBytes(unit)),unit.Name);
            foreach(var c in primary.Characters)
                if(primary.UnitBytes.TryGetValue(c.Id,out var bytes))names.TryAdd(ArmyData.ScriptNumber(bytes.Span),c.Name);
        }
        return number=>names.TryGetValue(number,out var n)?n:"another unit";
    }
    // When an arrival happens, as the script decides it: the turns it compares, the spot whose command sets it off, and whether
    // it also depends on something else (an event flag, a unit's state).
    private static string When(Reinforcement r,Func<int,string> name)
    {
        string text=r.Turn is not int first?r.Talk is { } talk?$"when {(talk.Speaker==0?"one of your units":name(talk.Speaker))} talks to {name(talk.Listener)}":(r.Spot is { } spot?(spot.Name=="Event"?$"when a unit sets off the spot at {spot.X}, {spot.Y}":$"when a unit uses {spot.Name} at {spot.X}, {spot.Y}"):r.EveryTurn?"at the start of a turn, when the battle's conditions are met":"when an event in the battle happens")
            :r.LastTurn==first?$"on turn {first}":r.LastTurn is int last?(first<=1?$"each turn up to turn {last}":$"each turn from turn {first} to {last}"):$"each turn from turn {first}";
        if(r.Turn is not null && r.Spot is { } at)text+=$", when a unit uses {(at.Name=="Event"?"the spot":at.Name)} at {at.X}, {at.Y}";
        return r.Conditional && r.Spot is null && r.Turn is not null?text+", under conditions the battle checks":text;
    }
    // The number on an arrival's marker: its turn, the first of several turns with a plus, or nothing when an event decides.
    private static string TurnLabel(Reinforcement r)=>r.Turn is not int first?"":r.LastTurn==first?first.ToString():$"{first}+";
    private static string TeamName(int team)=>team switch {0=>"Your army",1=>"Enemy",_=>"Other army"};
    private static Color TeamColor(int team)=>team switch {0=>Color.FromRgb(46,111,216),1=>Color.FromRgb(192,57,43),_=>Color.FromRgb(46,139,87)};

}
