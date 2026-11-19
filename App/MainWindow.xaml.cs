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

public partial class MainWindow : Window
{
    private InspectionResult? primary, comparison;
    private List<ItemRow> rows=[];
    private List<CharacterRow> characterRows=[];
    private List<ClassRow> classRows=[];
    private List<MovementRow> movementRows=[];
    private List<TerrainKindRow> terrainRows=[];
    private enum View { Items, Characters, Armies, Classes, Movement, Terrain, Furniture, Dishes, Shops, Live, Skills, Rules, Maps, Events, Mechanics, Disc, Animations }
    private View view=View.Items;
    private CancellationTokenSource? loading;
    private bool ready, closing;
    private EditSession? session;
    private EditTarget? editing;
    private readonly List<(string Table,TableField Field,Control Input,TextBlock Error)> editors=[];
    private Dictionary<int,ClassRecord> classLookup=[];
    private TextBlock? editorError;

    public MainWindow()
    {
        InitializeComponent();
        Category.Items.Add("All types");
        foreach(string type in ItemDecoder.Types) Category.Items.Add(type);
        Category.Items.Add("Other"); Category.SelectedIndex=0;
        DiscNav.IsEnabled=false; ready=true;
        BuildMechanics();
    }

    private string? PickIso(string title)
    {
        var picker=new OpenFileDialog { Title=title,Filter="ISO disc images (*.iso)|*.iso",CheckFileExists=true,Multiselect=false };
        if(primary is not null) picker.InitialDirectory=Path.GetDirectoryName(primary.Path);
        else
        {
            string parent=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
            if(Directory.Exists(parent)) picker.InitialDirectory=parent;
        }
        return picker.ShowDialog(this)==true ? picker.FileName : null;
    }
    private async void OpenPrimary(object sender,RoutedEventArgs e)
    {
        if(loading is not null)return;
        if(!ConfirmDiscard())return;
        if(PickIso("Open a Berwick Saga ISO") is { } path) await LoadDisc(path,false);
    }
    private async void OpenComparison(object sender,RoutedEventArgs e)
    {
        if(loading is not null || primary?.Supported!=true)return;
        if(PickIso("Choose a disc to compare") is { } path) await LoadDisc(path,true);
    }
    private async Task<bool> LoadDisc(string path,bool asComparison)
    {
        if(loading is not null)return false;
        using var cancellation=new CancellationTokenSource();loading=cancellation;
        SetBusy(true);MessageBar.Visibility=Visibility.Collapsed;
        Status.Text="Opening disc";Progress.Value=0;
        var progress=new Progress<InspectionProgress>(p=>
        {
            if(closing || loading!=cancellation)return;
            Progress.Value=p.Fraction*100;Status.Text=$"{p.Message} · {p.Fraction:P0}";
        });
        try
        {
            var result=await Task.Run(()=>Inspector.Inspect(path,progress,cancellation.Token));
            if(closing)return false;
            if(asComparison)
            {
                if(!result.Supported) { Notify("This disc is not a supported edition. The current comparison has been kept.");Status.Text="Comparison not changed";return false; }
                comparison=result;
            }
            else
            {
                EndEdit();CloseEvents();EndLive();primary=result;comparison=null;
                session=result.Supported?new EditSession(result):null;
                Search.Clear();Category.SelectedIndex=0;OnlyDifferences.IsChecked=false;
                CharacterSearch.Clear();CharacterDifferencesOnly.IsChecked=false;
                ClassSearch.Clear();ClassDifferencesOnly.IsChecked=false;StartingClassesOnly.IsChecked=true;
                view=result.Supported?(view is View.Characters or View.Classes?view:View.Items):View.Disc;
                if(!result.Supported) Notify("This image does not match a supported edition. Disc details are available, but item data has not been loaded.");
            }
            RefreshDocument();Status.Text="Disc checked. No files changed.";return true;
        }
        catch(OperationCanceledException) { if(!closing)Status.Text="Canceled. No files changed.";return false; }
        catch(InvalidDataException ex) { if(!closing){Notify(ex.Message);Status.Text="Disc could not be opened";}return false; }
        catch(UnauthorizedAccessException) { if(!closing){Notify("This file could not be opened. Choose a disc image you have permission to read.");Status.Text="Disc could not be opened";}return false; }
        catch(IOException) { if(!closing){Notify("The disc could not be read. Check that the file is available and is not being changed by another program.");Status.Text="Disc could not be opened";}return false; }
        catch(Exception) { if(!closing){Notify("The disc could not be opened. Try another ISO image.");Status.Text="Disc could not be opened";}return false; }
        finally { loading=null;if(!closing)SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled=!busy;EmptyOpenButton.IsEnabled=!busy;
        CompareButton.IsEnabled=!busy && primary?.Supported==true;
        OpenProjectButton.IsEnabled=!busy;SaveProjectButton.IsEnabled=!busy;
        ExportButton.IsEnabled=!busy && session?.ChangeCount>0;
        foreach(var b in new[]{EditItemButton,EditCharacterButton,EditArmyUnitButton,EditFurnitureButton,EditDishButton,EditClassButton,EditMovementButton,EditTerrainButton}) b.IsEnabled=!busy;
        if(busy){UndoButton.IsEnabled=false;RedoButton.IsEnabled=false;}
        else{UndoButton.IsEnabled=session?.CanUndo==true;RedoButton.IsEnabled=session?.CanRedo==true;}
        CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        Progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
    }
    private void RefreshDocument()
    {
        if(primary is null)return;
        DiscStrip.Visibility=Visibility.Visible;EmptyState.Visibility=Visibility.Collapsed;DiscNav.IsEnabled=true;
        EditionText.Text=primary.Edition;FileText.Text=Path.GetFileName(primary.Path);FileText.ToolTip=primary.Path;
        VerifiedText.Text=!primary.Supported?"Unknown disc":primary.IsModified?"Modified disc":"Verified disc";
        VerifiedText.ToolTip=primary.IsModified?$"Not an original release. Recognized from its game program and data layout as a modified copy of {primary.Profile!.Name}.":null;
        VerifiedText.Foreground=!primary.Supported?Brushes.DarkGoldenrod:primary.IsModified?new SolidColorBrush(Color.FromRgb(138,106,31)):(Brush)FindResource("Accent");
        var items=session?.EditedItems()??primary.Items;
        var differences=comparison is null?[]:ItemComparison.Compare(items,comparison.Items);
        var groups=differences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        rows=items.Select(i=>new ItemRow(i,groups.TryGetValue(i.Id,out var list)?list:[],comparison is not null) { Edited=session?.IsEdited(i.Id)==true }).ToList();
        UpdateProjectBar();
        ComparisonBar.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        ChangeColumn.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        if(comparison is not null)
        {
            ComparisonText.Text=$"{primary.Edition} / {comparison.Edition}\n"+(differences.Count==0?$"All {rows.Count} items match in the compared fields.":$"{groups.Count} items have differences in the compared fields.");
        }
        var characters=session?.EditedCharacters()??primary.Characters;
        var classes=session?.EditedClasses()??primary.Classes;
        classLookup=classes.ToDictionary(c=>c.Id);
        var characterDifferences=comparison is null?[]:RecordComparison.Compare(characters,comparison.Characters);
        var characterGroups=characterDifferences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        characterRows=characters.Select(c=>new CharacterRow(c,characterGroups.TryGetValue(c.Id,out var list)?list:[],comparison is not null)
            { Edited=session is not null && CharacterTables.Any(t=>session.IsEdited(t,c.Id)) }).ToList();
        CharactersNav.IsEnabled=primary.Supported;
        armyRows=BuildArmyRows();ArmiesNav.IsEnabled=primary.Supported && armyRows.Count>0;
        CharacterComparisonBar.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        CharacterChangeColumn.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        if(comparison is not null)
            CharacterComparisonText.Text=$"{primary.Edition} / {comparison.Edition}\n"+(characterDifferences.Count==0?$"All {characterRows.Count} characters match in the compared fields.":$"{characterGroups.Count} characters have differences in the compared fields.");
        var classDifferences=comparison is null?[]:ClassComparison.Compare(classes,comparison.Classes);
        var classGroups=classDifferences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        var starters=primary.Characters.GroupBy(c=>c.ClassId).ToDictionary(g=>g.Key,g=>(IReadOnlyList<string>)g.Select(c=>c.Name).ToList());
        classRows=classes.Select(k=>new ClassRow(k,classGroups.TryGetValue(k.Id,out var list)?list:[],comparison is not null,starters.TryGetValue(k.Id,out var s)?s:[])
            { Edited=session?.IsEdited(EditSession.Classes,k.Id)==true }).ToList();
        ClassesNav.IsEnabled=primary.Supported;
        ClassComparisonBar.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        ClassChangeColumn.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        if(comparison is not null)
            ClassComparisonText.Text=$"{primary.Edition} / {comparison.Edition}\n"+(classDifferences.Count==0?$"All {classRows.Count} classes match in the compared fields.":$"{classGroups.Count} classes have differences in the compared fields.");
        var byType=classes.GroupBy(k=>k.MovementType).ToDictionary(g=>g.Key,g=>(IReadOnlyList<string>)g.Select(k=>k.Name).ToList());
        movementRows=(session?.EditedMovementTypes()??primary.MovementTypes).Select(m=>new MovementRow(m,byType.TryGetValue(m.Id,out var names)?names:[])
            { Edited=session?.IsEdited(EditSession.Terrain,m.Id)==true }).ToList();
        MovementNav.IsEnabled=primary.Supported && movementRows.Count>0;
        terrainRows=(session?.EditedTerrainKinds()??primary.TerrainKinds).Select(k=>new TerrainKindRow(k){ Edited=session?.IsEdited(EditSession.TerrainKinds,k.Id)==true }).ToList();
        TerrainNav.IsEnabled=primary.Supported && terrainRows.Count>0;
        FurnitureNav.IsEnabled=primary.Supported && primary.FurniturePriceBytes.Count>0;
        DishesNav.IsEnabled=primary.Supported && primary.DishBytes.Count>0;
        ShopsNav.IsEnabled=primary.Supported;
        LiveNav.IsEnabled=primary.Supported;
        RulesNav.IsEnabled=primary.Supported;SkillsNav.IsEnabled=primary.Supported;EventsNav.IsEnabled=primary.Supported;MapsNav.IsEnabled=primary.Supported;AnimationsNav.IsEnabled=primary.Supported;
        PopulateDiscDetails();ApplyFilter();ApplyCharacterFilter();ApplyArmyFilter();ApplyClassFilter();ApplyMovementList();ApplyTerrainList();ApplyFurnitureList();ApplyDishList();BuildRules();UpdatePage();
        if(primary.DifferentTableRecords>0) Notify($"The disc contains {primary.DifferentTableRecords} items with differing table copies. Values shown are from the first copy.");
        if(view==View.Events && events is { } library && EventList.SelectedValue is int shown)_=ShowEvent(library,shown,scrollHome:false);
        if(shops is not null)ApplyShopList();
        if(animationTable is not null && editing?.Page!=View.Animations)RefreshAnimationClass();
    }
    private void PopulateDiscDetails()
    {
        DiscFields.Children.Clear();if(primary is null)return;
        AddInfo(DiscFields,"Edition",primary.Edition);
        if(primary.Supported && primary.RuleWords.Count>0) AddInfo(DiscFields,"Game rules",string.Join(", ",GameRules.All.Where(r=>r.Confirmed && r.Group.Length==0 && primary.RuleWords.ContainsKey(r.Id)).Select(r=>$"{r.Name}: {r.Describe(r.Decode(primary.RuleWords[r.Id].Span)??r.Default)}")));
        if(primary.Supported) AddInfo(DiscFields,"Recognized by",primary.IsModified?"Game program and data layout. This disc differs from the original release.":"Exact match with the original release.");AddInfo(DiscFields,"Serial",primary.Serial);
        AddInfo(DiscFields,"Disc version",primary.DiscVersion);AddInfo(DiscFields,"Size",$"{primary.Size:N0} bytes");
        AddInfo(DiscFields,"Executable CRC",primary.Crc);AddInfo(DiscFields,"Image SHA-256",primary.Sha256);
        AddInfo(DiscFields,"Executable SHA-256",primary.ExecutableSha256);AddInfo(DiscFields,"File",primary.Path);
        if(primary.Supported) AddInfo(DiscFields,"Character tables",primary.CharacterCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
        if(primary.Supported) AddInfo(DiscFields,"Class tables",primary.ClassCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
        if(primary.Supported && primary.TerrainKinds.Count>0) AddInfo(DiscFields,"Terrain effect tables",primary.TerrainKindCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
        if(primary.Supported && primary.MovementTypes.Count>0) AddInfo(DiscFields,"Terrain tables",primary.TerrainCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
        if(primary.Supported) AddInfo(DiscFields,"Item tables",primary.DifferentTableRecords==0?"Both copies match in the compared fields.":$"{primary.DifferentTableRecords} records differ between copies.");
        FilesGrid.ItemsSource=primary.Files;
    }
    private static void AddInfo(Panel panel,string label,string text)
    {
        var grid=new Grid { Margin=new Thickness(0,0,0,13) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(155) });grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock {Text=label,Foreground=new SolidColorBrush(Color.FromRgb(104,115,107))});
        var value=new TextBlock { Text=text,TextWrapping=TextWrapping.Wrap };Grid.SetColumn(value,1);grid.Children.Add(value);panel.Children.Add(grid);
    }
    private void ApplyFilter()
    {
        if(!ready)return;
        string query=Search.Text.Trim();SearchHint.Visibility=query.Length==0?Visibility.Visible:Visibility.Collapsed;
        int? selected=(ItemGrid.SelectedItem as ItemRow)?.Item.Id;
        string category=Category.SelectedItem as string??"All types";
        var filtered=rows.Where(r=>(Unused.IsChecked==true || !r.Item.IsUnused)
            && (category=="All types" || r.Item.Category==category)
            && (OnlyDifferences.IsChecked!=true || comparison is null || r.Differences.Count>0)
            && (query.Length==0 || r.Item.DisplayName.Contains(query,StringComparison.OrdinalIgnoreCase)
                || r.Item.Name.Contains(query,StringComparison.OrdinalIgnoreCase)
                || r.Item.IdText.Equals(query,StringComparison.OrdinalIgnoreCase)
                || r.Item.Id.ToString()==query)).ToList();
        ItemGrid.ItemsSource=filtered;
        NoResults.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
        ItemCount.Text=$"{filtered.Count:N0} of {rows.Count:N0} items";
        if(filtered.Count>0) ItemGrid.SelectedItem=filtered.FirstOrDefault(r=>r.Item.Id==selected)??filtered[0];
        else ClearItem();
    }
    private void FilterChanged(object sender,TextChangedEventArgs e)=>ApplyFilter();
    private void CategoryChanged(object sender,SelectionChangedEventArgs e)=>ApplyFilter();
    private void UnusedChanged(object sender,RoutedEventArgs e)=>ApplyFilter();
    private void ItemSelected(object sender,SelectionChangedEventArgs e)
    {
        if(ItemGrid.SelectedItem is not ItemRow row){ClearItem();return;}
        var item=row.Item;ItemName.Text=item.DisplayName;ItemIdentity.Text=$"{item.IdText} · {item.Category}";
        int? createdFrom=session?.CopiedFrom(EditSession.Items,item.Id);
        if(createdFrom is int fromId) ItemIdentity.Text+=$" · New item based on {primary?.Items.FirstOrDefault(i=>i.Id==fromId)?.DisplayName??$"0x{fromId:X3}"}";
        if(editing?.Page==View.Items && editing.Id!=item.Id) EndEdit();
        UpdateEditButtons();
        DetailFields.Children.Clear();
        string Was(params string[] keys)
        {
            if(session is null || createdFrom is not null || !keys.Any(k=>session.Value(item.Id,k)!=session.OriginalValue(item.Id,k)))return "";
            int O(string k)=>session.OriginalValue(item.Id,k);
            return keys switch
            {
                ["minRange","maxRange"]=>O("minRange")==O("maxRange")?$"was {O("minRange")}":$"was {O("minRange")} to {O("maxRange")}",
                ["durability"]=>$"was {"SABCDEF"[Math.Clamp(O("durability"),0,6)]}",
                ["price"]=>$"was {O("price"):N0}",
                _=>$"was {O(keys[0])}"
            };
        }
        foreach(var (label,value,was) in new (string,string,string)[]{("Might",item.Might.ToString(),Was("might")),("Accuracy",item.Accuracy.ToString(),Was("accuracy")),("Weight",item.Weight.ToString(),Was("weight")),("Range",item.Range,Was("minRange","maxRange")),("Critical",item.Critical.ToString(),Was("critical")),("Level",item.Level.ToString(),Was("level")),("Base price",item.Price.ToString("N0"),Was("price")),("Durability",item.DurabilityText,Was("durability")),("Durability pool",item.Uses.ToString(),Was("uses"))})
        {
            var grid=new Grid {Margin=new Thickness(0,0,0,10)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            grid.Children.Add(new TextBlock {Text=label,Foreground=(Brush)FindResource("Muted"),FontSize=13});
            var text=new TextBlock {HorizontalAlignment=HorizontalAlignment.Right};
            if(was.Length>0) text.Inlines.Add(new System.Windows.Documents.Run(was+"   ") {Foreground=(Brush)FindResource("Muted"),FontSize=11});
            text.Inlines.Add(new System.Windows.Documents.Run(value) {FontWeight=FontWeights.SemiBold,Foreground=was.Length>0?(Brush)FindResource("Accent"):(Brush)FindResource("Ink")});
            Grid.SetColumn(text,1);grid.Children.Add(text);DetailFields.Children.Add(grid);
        }
        if(primary?.StablePriceBytes.ContainsKey(item.Id)==true)
        {
            int stable=session?.Value(EditSession.StablePrices,item.Id,StablePrices.Field.Key)??System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(primary.StablePriceBytes[item.Id].Span);
            bool stableEdited=session?.IsEdited(EditSession.StablePrices,item.Id)==true;
            var grid=new Grid {Margin=new Thickness(0,0,0,10)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            grid.Children.Add(new TextBlock {Text="Stable price",Foreground=(Brush)FindResource("Muted"),FontSize=13,ToolTip="What the stable charges for this horse. It is separate from the base price."});
            var text=new TextBlock {HorizontalAlignment=HorizontalAlignment.Right};
            if(stableEdited) text.Inlines.Add(new System.Windows.Documents.Run($"was {session!.OriginalValue(EditSession.StablePrices,item.Id,StablePrices.Field.Key):N0}   ") {Foreground=(Brush)FindResource("Muted"),FontSize=11});
            text.Inlines.Add(new System.Windows.Documents.Run(stable.ToString("N0")) {FontWeight=FontWeights.SemiBold,Foreground=stableEdited?(Brush)FindResource("Accent"):(Brush)FindResource("Ink")});
            Grid.SetColumn(text,1);grid.Children.Add(text);DetailFields.Children.Add(grid);
        }
        AddRecipeDetails(item.Id);
        if(session?.IsEdited(item.Id)==true || session?.IsEdited(EditSession.StablePrices,item.Id)==true || session?.IsEdited(EditSession.Recipes,item.Id)==true)
        {
            var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};
            if(createdFrom is not null)
            {
                var rename=new Button {Content="Rename",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(0,0,6,0)};
                rename.Click+=(_,_)=>RenameItem(item.Id,item.DisplayName);
                actions.Children.Add(rename);
            }
            var revert=new Button {Content=createdFrom is null?"Restore original values":"Remove new item",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(0)};
            revert.Click+=(_,_)=>{if(loading is not null)return;if(session.IsEdited(item.Id))session.Revert(item.Id);if(session.IsEdited(EditSession.StablePrices,item.Id))session.Revert(EditSession.StablePrices,item.Id);if(session.IsEdited(EditSession.Recipes,item.Id))session.Revert(EditSession.Recipes,item.Id);RefreshDocument();Status.Text=createdFrom is null?"Item restored. Save the project to keep this change.":"New item removed. Its slot is free again.";};
            actions.Children.Add(revert);
            DetailFields.Children.Add(actions);
        }
        ItemEffect.Text=item.EffectDescription;
        ItemComparisonText.Text=!row.HasComparison?"":row.Differences.Count==0?"This item's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    // Atelier recipes: the recipe that makes this item, and the recipes that use it.
    private int RecipeValue(int id,string key)=>session?.Value(EditSession.Recipes,id,key)
        ??System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(primary!.RecipeBytes[id].Span[(8+(int.Parse(key["ingredient".Length..])-1)*4)..]);
    private List<int> RecipeIngredients(int id)=>Enumerable.Range(1,RecipeData.Ingredients).Select(i=>RecipeValue(id,$"ingredient{i}")).Where(v=>v!=0).ToList();
    private string ItemLabel(int id)=>(session?.EditedItems()??primary!.Items).FirstOrDefault(i=>i.Id==id)?.DisplayName??$"Item 0x{id:X3}";
    private void AddRecipeDetails(int itemId)
    {
        if(primary is null || primary.RecipeBytes.Count==0)return;
        void Heading(string t)=>DetailFields.Children.Add(new TextBlock {Text=t,Style=(Style)FindResource("Caption"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,6)});
        void Line(string t)=>DetailFields.Children.Add(new TextBlock {Text=t,TextWrapping=TextWrapping.Wrap,FontSize=13,Margin=new Thickness(0,0,0,6)});
        if(primary.RecipeBytes.TryGetValue(itemId,out var recipe))
        {
            Heading("ATELIER RECIPE");
            Line($"{RecipeData.Craftsman(RecipeData.Type(recipe.Span))} makes this from {string.Join(", ",RecipeIngredients(itemId).Select(ItemLabel))}."+(session?.IsEdited(EditSession.Recipes,itemId)==true?" (edited)":""));
        }
        var uses=primary.RecipeBytes.Keys.Where(r=>RecipeIngredients(r).Contains(itemId)).Select(ItemLabel).ToList();
        if(uses.Count>0){Heading("USED AT THE ATELIER FOR");Line(string.Join(", ",uses));}
    }
    private void ClearItem()
    {
        if(ItemName is null)return;if(editing?.Page==View.Items)EndEdit();UpdateEditButtons();ItemName.Text="Select an item";ItemIdentity.Text="";DetailFields.Children.Clear();ItemEffect.Text="";ItemComparisonText.Text="";
    }
    private void ShowItems(object sender,RoutedEventArgs e) { view=View.Items;UpdatePage(); }
    private void ShowCharacters(object sender,RoutedEventArgs e) { view=View.Characters;UpdatePage(); }
    private void ShowArmies(object sender,RoutedEventArgs e) { view=View.Armies;UpdatePage(); }
    private void ShowFurniture(object sender,RoutedEventArgs e) { view=View.Furniture;UpdatePage(); }
    private void ShowDishes(object sender,RoutedEventArgs e) { view=View.Dishes;UpdatePage(); }
    private void ShowShops(object sender,RoutedEventArgs e) { view=View.Shops;UpdatePage();_=OpenShops(); }
    private void ShowClasses(object sender,RoutedEventArgs e) { view=View.Classes;UpdatePage(); }
    private void ShowMovement(object sender,RoutedEventArgs e) { view=View.Movement;UpdatePage(); }
    private void ShowTerrain(object sender,RoutedEventArgs e) { view=View.Terrain;UpdatePage(); }
    private void ShowRules(object sender,RoutedEventArgs e) { view=View.Rules;UpdatePage(); }
    private void ShowSkills(object sender,RoutedEventArgs e) { view=View.Skills;UpdatePage(); }
    private void ShowMechanics(object sender,RoutedEventArgs e) { view=View.Mechanics;UpdatePage(); }
    private void ShowEvents(object sender,RoutedEventArgs e) { view=View.Events;UpdatePage();_=OpenEvents(); }
    private void ShowMaps(object sender,RoutedEventArgs e) { view=View.Maps;UpdatePage();_=OpenMaps(); }
    private void ShowAnimations(object sender,RoutedEventArgs e) { view=View.Animations;UpdatePage();_=OpenAnimations(); }

    // Shops: read from the event scripts through the event library; each item change is a script change in the project.
    private List<Shop>? shops;
    private string? shopsPath;
    private Dictionary<int,string> shopBattleNames=[];
    private sealed record ShopRow(int Event,string Name,string Where,int Count,bool Edited){public string EditedText=>Edited?"Edited":"";}
    private async Task OpenShops()
    {
        if(primary is null || !primary.Supported)return;
        if(shopsPath==primary.Path && shops is not null)return;
        await OpenEvents();
        if(events is not { } library || primary is null)return;
        string path=primary.Path;var exeEntry=primary.ExecutableFile!;
        ShopCount.Text="Reading the shops...";
        try
        {
            var (list,names)=await Task.Run(()=>
            {
                byte[] exe;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var iso=new IsoImage(stream)) exe=iso.ReadRange(exeEntry.Offset,(int)exeEntry.Length,(int)exeEntry.Length);
                lock(library)
                {
                    var titles=LiveGame.Battles(exe,ArmyData.LoadBattleTitles(library.Archives)).GroupBy(b=>b.Battle).ToDictionary(g=>g.Key,g=>g.First().Text);
                    return (Shops.Read(library).ToList(),titles);
                }
            });
            if(events!=library)return;
            shops=list;shopBattleNames=names;shopsPath=path;
            ApplyShopList();
        }
        catch(Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException){ShopCount.Text="The shops could not be read from this disc.";}
    }
    // What a shop sells, in words, for the shops at the base (named by their goods).
    private static readonly Dictionary<string,string> GoodsNames=new()
    {
        ["Knife"]="knives",["Sword"]="swords",["Blade"]="blades",["Mace"]="maces",["Spear"]="spears",["Lance"]="lances",["Fork"]="forks",["Axe"]="axes",
        ["Bow"]="bows",["Crossbow"]="crossbows",["Ballista"]="ballistae",["Fire"]="fire tomes",["Thunder"]="thunder tomes",["Wind"]="wind tomes",
        ["Holy"]="holy tomes",["Dark"]="dark tomes",["Healing"]="healing staves",["Dark healing"]="dark staves",["Small shield"]="small shields",
        ["Medium shield"]="medium shields",["Large shield"]="large shields",["Arrow"]="arrows",["Bolt"]="bolts",["Accessory"]="accessories",["Consumable"]="items",
    };
    private (string Name,string Where) ShopTitle(Shop shop,IReadOnlyList<ShopItem> items)
    {
        if(shop.Battle>0)return (shop.Spot,shopBattleNames.TryGetValue(shop.Battle,out var b)?b:$"Battle {shop.Battle}");
        var catalog=(session?.EditedItems()??primary!.Items).ToDictionary(i=>i.Id);
        var goods=items.Where(i=>catalog.ContainsKey(i.Item)).GroupBy(i=>catalog[i.Item].Category).OrderByDescending(g=>g.Count()).Select(g=>GoodsNames.TryGetValue(g.Key,out var n)?n:g.Key.ToLowerInvariant()).Take(3).ToList();
        string list=goods.Count switch {0=>"Goods",1=>goods[0],2=>$"{goods[0]} and {goods[1]}",_=>$"{goods[0]}, {goods[1]} and {goods[2]}"};
        return (char.ToUpperInvariant(list[0])+list[1..],"At the base");
    }
    private IReadOnlyList<ShopItem> ShopItems(Shop shop)=>session is not null && (session.HasScript(shop.Event) || ScriptChanges(shop.Event).Count>0)
        ?Shops.Items(ScriptParser.Parse(session.EventScript(shop.Event)!)):shop.Items;
    private void ApplyShopList()
    {
        if(shops is null || primary is null)return;
        int? selected=(ShopGrid.SelectedItem as ShopRow)?.Event;
        var rows=shops.Select(s=>{var items=ShopItems(s);var (name,where)=ShopTitle(s,items);return new ShopRow(s.Event,name,where,items.Count,session is not null && (session.HasScript(s.Event) || ScriptChanges(s.Event).Count>0));})
            .OrderBy(r=>r.Where=="At the base"?0:1).ThenBy(r=>r.Where).ThenBy(r=>r.Name).ToList();
        if(primary.StablePriceBytes.Count>0)
            rows.Insert(0,new ShopRow(StableRow,"Stable","At the base",primary.StablePriceBytes.Count,primary.StablePriceBytes.Keys.Any(id=>session?.IsEdited(EditSession.StablePrices,id)==true) || ScriptChanges(StableStock.Event).Count>0));
        foreach(var (rowId,type) in new[]{(AtelierWeapons,0),(AtelierMagic,5)})
        {
            var made=AtelierRecipes(type);
            if(made.Count>0)rows.Insert(rows.Count(r=>r.Event<0),new ShopRow(rowId,$"Atelier, {RecipeData.Craftsman(type)}","At the base",made.Count,made.Any(id=>session?.IsEdited(EditSession.Recipes,id)==true)));
        }
        if(events is not null)rows.Insert(rows.Count(r=>r.Event<0),new ShopRow(PavilionRow,"Kingfisher Pavilion","At the base",0,session?.HasScript(Pavilion.ServerEvent)==true));
        ShopGrid.ItemsSource=rows;
        ShopCount.Text=$"{rows.Count} shops, {rows.Count(r=>r.Where=="At the base")} at the base";
        if(rows.Count>0)ShopGrid.SelectedItem=rows.FirstOrDefault(r=>r.Event==selected)??rows[0];
    }
    private Shop? SelectedShop=>ShopGrid.SelectedItem is ShopRow row?shops?.FirstOrDefault(s=>s.Event==row.Event):null;
    private List<ShopItem> shownShopItems=[];
    private void ShopSelected(object sender,SelectionChangedEventArgs e)
    {
        ShopItemPicker.Visibility=Visibility.Collapsed;
        if(ShopGrid.SelectedItem is ShopRow{Event:StableRow}){ShowStable();return;}
        if(ShopGrid.SelectedItem is ShopRow{Event:AtelierWeapons or AtelierMagic} atelier){ShowAtelier(atelier.Event==AtelierWeapons?0:5);return;}
        if(ShopGrid.SelectedItem is ShopRow{Event:PavilionRow}){ShowPavilion();return;}
        if(SelectedShop is not { } shop || ShopGrid.SelectedItem is not ShopRow row){ShopName.Text="Select a shop";ShopWhere.Text="";ShopItemList.ItemsSource=null;UpdateShopButtons();return;}
        int? keep=ShopItemList.SelectedIndex>=0?ShopItemList.SelectedIndex:null;
        var catalog=(session?.EditedItems()??primary!.Items).ToDictionary(i=>i.Id);
        shownShopItems=ShopItems(shop).ToList();
        ShopName.Text=row.Name;ShopWhere.Text=$"{row.Where} · event {shop.Event}";
        ShopItemList.ItemsSource=shownShopItems.Select((it,i)=>new Choice(i,$"{(catalog.TryGetValue(it.Item,out var item)?item.DisplayName:$"Item 0x{it.Item:X3}")}{it.Sale switch {ShopSale.Stock=>"  (stock)",ShopSale.Special=>"  (special)",_=>""}}")).ToList();
        if(keep is int k && k<shownShopItems.Count)ShopItemList.SelectedIndex=k;
        ShopNotes.Children.Clear();
        if(session?.HasScript(shop.Event)==true || ScriptChanges(shop.Event).Count>0)
        {
            AddText(ShopNotes,"This shop is changed in this project.");
            var restore=new Button {Content="Restore this shop",Style=(Style)FindResource("SmallButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,0)};
            restore.Click+=(_,_)=>
            {
                var records=session!.EditedRecords.Where(r=>r.Table==EditSession.Scripts && r.Id==shop.Event || r.Table==EditSession.ScriptValues && ScriptValues.Split(r.Id).Event==shop.Event).ToArray();
                session.Revert(records);RefreshDocument();Status.Text="Shop restored. Save the project to keep this change.";
            };
            ShopNotes.Children.Add(restore);
        }
        UpdateShopButtons();ShopItemSelected(this,null!);
    }
    private void ShopItemSelected(object sender,SelectionChangedEventArgs e)
    {
        UpdateShopButtons();
        if(ShopGrid.SelectedItem is ShopRow{Event:StableRow}){StableHorseSelected();return;}
        if(ShopGrid.SelectedItem is ShopRow{Event:AtelierWeapons or AtelierMagic}){AtelierRecipeSelected();return;}
        if(ShopGrid.SelectedItem is ShopRow{Event:PavilionRow}){ShopItemNote.Text="";return;}
        if(ShopItemList.SelectedIndex<0 || ShopItemList.SelectedIndex>=shownShopItems.Count){ShopItemNote.Text="";return;}
        var it=shownShopItems[ShopItemList.SelectedIndex];
        var catalog=(session?.EditedItems()??primary!.Items).ToDictionary(i=>i.Id);
        string price=catalog.TryGetValue(it.Item,out var item)?$"Base price {item.Price:N0}. ":"";
        ShopItemNote.Text=price+it.Sale switch
        {
            ShopSale.Stock=>$"Sold from a stock that refills over time (stock {it.Extra}). A changed item keeps the stock of the one it replaces.",
            ShopSale.Special=>$"A special purchase: buying it sets off event {it.Extra}.",
            _=>"Always for sale.",
        };
    }
    private void UpdateShopButtons()
    {
        bool can=session is not null && editing is null && SelectedShop is not null;
        bool item=can && ShopItemList.SelectedIndex>=0 && ShopItemList.SelectedIndex<shownShopItems.Count;
        ChangeShopItemButton.IsEnabled=item;RemoveShopItemButton.IsEnabled=item && shownShopItems.Count>1;AddShopItemButton.IsEnabled=can && shownShopItems.Count>0;
    }
    // The stable is not a script shop: its horses and prices come from event 602, edited through the stable price table.
    private const int StableRow=-602;
    private List<int> stableHorses=[];
    private int StablePrice(int id)=>session?.Value(EditSession.StablePrices,id,StablePrices.Field.Key)
        ??System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(primary!.StablePriceBytes[id].Span);
    private void ShowStable()
    {
        int? keep=ShopItemList.SelectedIndex>=0?ShopItemList.SelectedIndex:null;
        var catalog=(session?.EditedItems()??primary!.Items).ToDictionary(i=>i.Id);
        stableHorses=StablePrices.Slots.Select(s=>s.Item).Where(id=>primary!.StablePriceBytes.ContainsKey(id)).ToList();
        shownShopItems=[];
        ShopName.Text="Stable";ShopWhere.Text="At the base · event 602";
        ShopItemList.ItemsSource=stableHorses.Select((id,i)=>new Choice(i,$"{(catalog.TryGetValue(id,out var item)?item.DisplayName:$"Item 0x{id:X3}")}  {StablePrice(id):N0}{(session?.IsEdited(EditSession.StablePrices,id)==true?"  (edited)":"")}")).ToList();
        if(keep is int k && k<stableHorses.Count)ShopItemList.SelectedIndex=k;
        ShopNotes.Children.Clear();
        AddText(ShopNotes,"These are all the horses the stable can sell. Select a horse to change what the stable charges. Its item price does not affect the stable.");
        AddStableStock();
        UpdateShopButtons();StableHorseSelected();
    }
    // Which horses are for sale each chapter: numbers in event 138, edited as script values.
    private IReadOnlyList<ScriptNumber>? StableStockNumbers()
    {
        if(events is not { } library || session?.HasScript(StableStock.Event)==true)return null;
        byte[] bytes;
        lock(library)bytes=library.Bytes(StableStock.Event);
        var changes=ScriptChanges(StableStock.Event);
        if(changes.Count>0)bytes=ScriptValues.Apply(bytes,changes);
        return ScriptValues.Numbers(StableStock.Event,ScriptParser.Parse(bytes));
    }
    private void SetStableStock(Dictionary<int,int> values,string done)
    {
        if(session is null)return;
        var fresh=values.Select(v=>(Key:ScriptValues.Key(StableStock.Event,v.Key),v.Value)).ToList();
        foreach(var (key,value) in fresh)
            if(ScriptFitProblem(key,value) is string problem){Status.Text=problem;return;}
        bool any=false;
        foreach(var (key,value) in fresh)any|=session.Apply(EditSession.ScriptValues,key,new Dictionary<string,int>{[ScriptValues.Field.Key]=value});
        if(any){RefreshDocument();Status.Text=done+" Save the project to keep this change.";}
    }
    private void AddStableStock()
    {
        var numbers=StableStockNumbers();
        AddText(ShopNotes,"Horses for sale each chapter (event 138)");
        AddText(ShopNotes,"Each chapter the stable picks its horses: the Plain Horse always, the Spry and Tough Horses on the chosen chapters, the Strong Horse at its chance, and at another chance one rare horse picked at random from six. Thin Horses fill the rest up to the number in all. Each kind counts once toward that number, whatever its stock. The numbers below are how many of each horse are for sale; 0 leaves it out.");
        if(numbers is null){AddText(ShopNotes,"This project replaces event 138's script, so these numbers are edited on the Events page.");return;}
        if(numbers.Count<=StableStock.Numbers.Max(n=>n.Index)){AddText(ShopNotes,"Event 138 does not have the layout this editor expects. Edit it on the Events page.");return;}
        bool edit=session is not null;
        var chapter=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,0)};
        chapter.Children.Add(new TextBlock{Text="Spry and Tough Horses",Width=260,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
        int? current=StableStock.ChapterChoice(numbers[StableStock.DivisorIndex].Value,numbers[StableStock.RemainderIndex].Value);
        var pick=new ComboBox{Width=150,IsEnabled=edit && current is not null,ItemsSource=StableStock.ChapterChoices,SelectedIndex=current??-1};
        System.Windows.Automation.AutomationProperties.SetName(pick,"Chapters with the Spry and Tough Horses");
        pick.SelectionChanged+=(_,_)=>
        {
            if(pick.SelectedIndex<0 || pick.SelectedIndex==current)return;
            var (d,r)=StableStock.ChapterWords(pick.SelectedIndex);
            SetStableStock(new(){[StableStock.DivisorIndex]=d,[StableStock.RemainderIndex]=r},"Stable chapters changed.");
        };
        chapter.Children.Add(pick);
        if(current is null)chapter.Children.Add(new TextBlock{Text="  (changed on the Events page)",VerticalAlignment=VerticalAlignment.Center});
        ShopNotes.Children.Add(chapter);
        foreach(var n in StableStock.Numbers)
        {
            var line=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,4,0,0)};
            int value=numbers[n.Index].Value;
            bool changed=value!=n.Original;
            line.Children.Add(new TextBlock{Text=n.Label+(changed?$" (game: {n.Original})":""),Width=260,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
            var box=new TextBox{Width=60,Text=value.ToString(),IsEnabled=edit,Margin=new Thickness(0,0,6,0)};
            System.Windows.Automation.AutomationProperties.SetName(box,n.Label);
            line.Children.Add(box);
            if(edit)
            {
                var set=new Button{Content="Set",Style=(Style)FindResource("SmallButton")};
                set.Click+=(_,_)=>
                {
                    if(!int.TryParse(box.Text.Trim(),out int v) || v<n.Min || v>n.Max){Status.Text=$"{n.Label}: enter a whole number from {n.Min} to {n.Max}.";return;}
                    SetStableStock(new(){[n.Index]=v},"Stable stock changed.");
                };
                line.Children.Add(set);
            }
            ShopNotes.Children.Add(line);
        }
    }
    private void StableHorseSelected()
    {
        int index=ShopItemList.SelectedIndex;
        ShopItemNote.Text="";
        var old=ShopNotes.Children.OfType<FrameworkElement>().Where(x=>x.Tag as string=="stablePrice").ToList();foreach(var x in old)ShopNotes.Children.Remove(x);
        if(index<0 || index>=stableHorses.Count)return;
        int id=stableHorses[index];
        ShopItemNote.Text=session?.IsEdited(EditSession.StablePrices,id)==true?$"Stable price {StablePrice(id):N0}, was {session.OriginalValue(EditSession.StablePrices,id,StablePrices.Field.Key):N0}.":$"Stable price {StablePrice(id):N0}.";
        if(session is null)return;
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,0),Tag="stablePrice"};
        var box=new TextBox{Width=110,Text=StablePrice(id).ToString(),Margin=new Thickness(0,0,6,0)};
        System.Windows.Automation.AutomationProperties.SetName(box,"Stable price");
        var apply=new Button{Content="Set price",Style=(Style)FindResource("SmallButton")};
        apply.Click+=(_,_)=>
        {
            if(!int.TryParse(box.Text.Replace(",","").Trim(),out int value) || value<ScriptPrices.Minimum || value>ScriptPrices.Maximum)
            {Status.Text=$"A stable price must be a whole number from {ScriptPrices.Minimum:N0} to {ScriptPrices.Maximum:N0}.";return;}
            if(session.Apply(EditSession.StablePrices,id,new Dictionary<string,int>{[StablePrices.Field.Key]=value})){RefreshDocument();Status.Text="Stable price changed. Save the project to keep this change.";}
        };
        row.Children.Add(box);row.Children.Add(apply);ShopNotes.Children.Add(row);
    }
    // The Atelier makes items from recipes in the game program; each recipe is edited on the Items page of the item it makes.
    // The Kingfisher Pavilion serves meals; its options rewrite the server's event (659).
    private const int PavilionRow=-659;
    private void ShowPavilion()
    {
        shownShopItems=[];
        ShopName.Text="Kingfisher Pavilion";ShopWhere.Text="At the base · meals · event 659";
        ShopItemList.ItemsSource=null;
        ShopNotes.Children.Clear();
        AddText(ShopNotes,"The Pavilion serves meals while a mission is chosen. Units from your army sit at its eight seats, and talking to one lets it order from the menu. Each unit eats once a chapter, and the meal's effects last into the next battle. The seats stay the same until the game clears them.");
        if(events is not { } library){UpdateShopButtons();return;}
        byte[]? current=session?.EventScript(Pavilion.ServerEvent);
        if(current is null){lock(library)current=library.Bytes(Pavilion.ServerEvent);}
        bool anyUnit=Pavilion.IsAnyUnit(current,library.CommandNames);
        bool other=session?.HasScript(Pavilion.ServerEvent)==true && !anyUnit;
        var box=new CheckBox{Content="Any unit can order: the server opens the unit list, so every unit in your army can eat, one after another",IsChecked=anyUnit,IsEnabled=session is not null && !other,Margin=new Thickness(0,8,0,0)};
        box.Click+=(_,_)=>
        {
            if(session is null)return;
            if(box.IsChecked==true)
            {
                try
                {
                    var bytes=Pavilion.AnyUnitServer(library.CommandNames);
                    CheckEventFits(library,Pavilion.ServerEvent,bytes);
                    if(session.SetScript(Pavilion.ServerEvent,bytes)){RefreshDocument();Status.Text="The Pavilion's server now takes orders for any unit. Save the project to keep this change.";}
                }
                catch(EditException ex){Status.Text=ex.Message;box.IsChecked=false;}
            }
            else{session.Revert((EditSession.Scripts,Pavilion.ServerEvent));RefreshDocument();Status.Text="The Pavilion's server is back to the game's own event.";}
        };
        ShopNotes.Children.Add(box);
        if(other)AddText(ShopNotes,"Event 659 is changed on the Events page in this project, so this option is off. Restore the event there to use it.");
        AddText(ShopNotes,"With this option, talk to the server (not the owner) while the Pavilion is serving, choose a unit, and order. A unit that has eaten this chapter says so. Cancel the list to hear the server's usual line.");
        UpdateShopButtons();
    }
    private const int AtelierWeapons=-723,AtelierMagic=-732;
    private List<int> atelierShown=[];
    private List<int> AtelierRecipes(int type)=>primary is null?[]:primary.RecipeRows.OrderBy(p=>p.Value).Select(p=>p.Key).Where(id=>RecipeData.Type(primary.RecipeBytes[id].Span)==type).ToList();
    private void ShowAtelier(int type)
    {
        int? keep=ShopItemList.SelectedIndex>=0?ShopItemList.SelectedIndex:null;
        atelierShown=AtelierRecipes(type);shownShopItems=[];
        ShopName.Text=$"Atelier, {RecipeData.Craftsman(type)}";ShopWhere.Text=type==0?"At the base · weapons and shields":"At the base · magic and potions";
        ShopItemList.ItemsSource=atelierShown.Select((id,i)=>new Choice(i,$"{ItemLabel(id)}{(session?.IsEdited(EditSession.Recipes,id)==true?"  (edited)":"")}")).ToList();
        if(keep is int k && k<atelierShown.Count)ShopItemList.SelectedIndex=k;
        ShopNotes.Children.Clear();
        AddText(ShopNotes,$"{RecipeData.Craftsman(type)} makes these items once you bring every ingredient. To change a recipe, open the item it makes on the Items page and edit its Atelier recipe.");
        UpdateShopButtons();AtelierRecipeSelected();
    }
    private void AtelierRecipeSelected()
    {
        int index=ShopItemList.SelectedIndex;
        ShopItemNote.Text=index<0 || index>=atelierShown.Count?"":$"Made from {string.Join(", ",RecipeIngredients(atelierShown[index]).Select(ItemLabel))}.";
    }
    private bool shopAdding;
    private void BeginChangeShopItem(object sender,RoutedEventArgs e)=>OpenShopPicker(adding:false);
    private void BeginAddShopItem(object sender,RoutedEventArgs e)=>OpenShopPicker(adding:true);
    private void OpenShopPicker(bool adding)
    {
        if(SelectedShop is null)return;
        shopAdding=adding;
        ShopPickerLabel.Text=adding?"Item to add (always for sale, after the last item)":"Sell this item instead";
        ShopItemChoice.ItemsSource=ItemChoices().Where(c=>c.Value!=0).ToList();
        if(!adding && ShopItemList.SelectedIndex>=0 && ShopItemList.SelectedIndex<shownShopItems.Count)ShopItemChoice.SelectedValue=shownShopItems[ShopItemList.SelectedIndex].Item;
        ShopPickerError.Visibility=Visibility.Collapsed;ShopItemPicker.Visibility=Visibility.Visible;ShopItemChoice.Focus();
    }
    private void CancelShopPicker(object sender,RoutedEventArgs e){ShopItemPicker.Visibility=Visibility.Collapsed;}
    // Changes a shop's script as one undoable step, after checking it still fits the disc.
    private bool ChangeShop(Shop shop,Func<byte[],byte[]> change,string done,Action<string>? fail=null)
    {
        if(session is null || events is not { } library)return false;
        try
        {
            var updated=change(session.EventScript(shop.Event)!);
            CheckEventFits(library,shop.Event,updated);
            if(!session.SetScript(shop.Event,updated)){Status.Text="Nothing was changed.";return false;}
        }
        catch(EditException ex){if(fail is not null)fail(ex.Message);else Status.Text=ex.Message;return false;}
        RefreshDocument();Status.Text=done+" Save the project to keep your changes.";
        return true;
    }
    private void ApplyShopPicker(object sender,RoutedEventArgs e)
    {
        if(SelectedShop is not { } shop || session is null)return;
        void Fail(string message){ShopPickerError.Text=message;ShopPickerError.Visibility=Visibility.Visible;}
        if(ShopItemChoice.SelectedValue is not int item){Fail("Choose an item from the list.");return;}
        if(shopAdding)
        {
            var last=shownShopItems[^1];
            if(ChangeShop(shop,b=>ScriptLines.InsertAfter(b,last.Path,ScriptLines.Command(Shops.AddItem,item.ToString())),"Item added.",Fail))ShopItemPicker.Visibility=Visibility.Collapsed;
            return;
        }
        if(ShopItemList.SelectedIndex<0 || ShopItemList.SelectedIndex>=shownShopItems.Count)return;
        var it=shownShopItems[ShopItemList.SelectedIndex];
        if(it.Item==item){ShopItemPicker.Visibility=Visibility.Collapsed;return;}
        if(ChangeShop(shop,b=>ScriptValues.Apply(b,new Dictionary<int,int>{[it.NumberIndex]=item}),"Item changed.",Fail))ShopItemPicker.Visibility=Visibility.Collapsed;
    }
    private void RemoveShopItem(object sender,RoutedEventArgs e)
    {
        if(SelectedShop is not { } shop || ShopItemList.SelectedIndex<0 || ShopItemList.SelectedIndex>=shownShopItems.Count)return;
        var it=shownShopItems[ShopItemList.SelectedIndex];
        ChangeShop(shop,b=>ScriptLines.Remove(b,it.Path),"Item removed.");
    }

    // Battle animations: kirikae.db and the buAAAWWV.ar sets, read through the event library (which keeps the disc open).
    private AnimationTable? animationTable;
    private string? animationsPath;
    private IReadOnlyDictionary<int,int> animationSetsOnDisc=new Dictionary<int,int>();
    private IReadOnlyDictionary<int,IReadOnlySet<string>> animationMotions=new Dictionary<int,IReadOnlySet<string>>();
    private Dictionary<uint,string> spriteNames=[];
    private readonly Dictionary<int,AnimationSet?> animationSets=[];
    private AnimationSet? shownAnimationSet;
    private IReadOnlyList<AnimationFrame> animationFrames=[];
    private string animationMotion="";
    private int animationFrame,animationTicksLeft,animationPalette;
    private (int X,int Y,int Width,int Height) animationBounds=(0,0,AnimationSet.Canvas,AnimationSet.Canvas);
    private bool animationPlaying=true;
    private System.Windows.Threading.DispatcherTimer? animationTimerField;
    private System.Windows.Threading.DispatcherTimer animationTimer
    {
        get
        {
            if(animationTimerField is null){animationTimerField=new(){Interval=TimeSpan.FromMilliseconds(1000.0/60)};animationTimerField.Tick+=AnimationTick;}
            return animationTimerField;
        }
    }
    private Dictionary<int,string>? unitNames;
    private static readonly Dictionary<int,string> SetWeapons=new(){[0]="none",[1]="knife",[2]="sword",[4]="mace",[5]="spear",[6]="lance",[7]="axe",[8]="bow",[9]="crossbow",[10]="ballista",[11]="magic",[14]="staff"};

    private void CloseAnimations()
    {
        animationTimer.Stop();animationTable=null;animationsPath=null;animationSetsOnDisc=new Dictionary<int,int>();animationMotions=new Dictionary<int,IReadOnlySet<string>>();animationSets.Clear();
        shownAnimationSet=null;animationFrames=[];AnimationList.ItemsSource=null;AnimationSetBox.ItemsSource=null;AnimationMotionBox.ItemsSource=null;
        AnimationPaletteBox.ItemsSource=null;AnimationImage.Source=null;AnimationDetails.Children.Clear();AnimationCount.Text="";AnimationFrameText.Text="";
    }

    private string UnitName(int unit)
    {
        unitNames??=CharacterDecoder.LoadRoster().GroupBy(c=>c.Id).ToDictionary(g=>g.Key,g=>g.First().Name);
        if(unitNames.TryGetValue(unit,out var name))return name;
        return ArmyData.Names.TryGetValue(unit,out var other)?other:$"Unit {unit}";
    }

    private string ClassTitle(int id)
    {
        // The Classes page's names, which mark mounted classes; game names for classes outside the class table.
        string? name=primary?.Classes.FirstOrDefault(k=>k.Id==id)?.Name;
        if(string.IsNullOrWhiteSpace(name) || name=="--")name=GameNames.Class(id);
        return name is { } n && n!="--"?$"{n} (class {id})":$"Class {id}";
    }
    private static string SetLabel(int number)=>AnimationSet.FileName(number)[..^3];

    private async Task OpenAnimations()
    {
        if(primary is null || !primary.Supported)return;
        if(animationsPath==primary.Path && animationTable is not null)return;
        await OpenEvents();
        if(events is not { } library || primary is null)return;
        string path=primary.Path;
        AnimationCount.Text="Reading the battle animations...";
        try
        {
            var exeEntry=primary.ExecutableFile;
            var (table,onDisc,motions,names)=await Task.Run(()=>
            {
                lock(library)
                {
                    int top=library.Archives.Find(AnimationTable.Path);
                    if(top<0)throw new InvalidDataException("The disc has no animation table.");
                    var sets=AnimationSet.OnDisc(library.Archives);
                    var spriteNames=new Dictionary<uint,string>();
                    if(exeEntry is not null)
                    {
                        byte[] exe;
                        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                        using(var iso=new IsoImage(stream)) exe=iso.ReadRange(exeEntry.Offset,(int)exeEntry.Length,(int)exeEntry.Length);
                        foreach(var (_,row) in MapSpriteData.Read(exe))
                            for(int w=0;w<2;w++){uint pointer=System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(w*4));if(MapSpriteData.Name(exe,pointer) is { } n)spriteNames[pointer]=n;}
                    }
                    return (AnimationTable.Parse(library.Archives.Top(top)),sets,AnimationSet.MotionLists(library.Archives,sets),spriteNames);
                }
            });
            if(events!=library)return;
            animationTable=table;animationSetsOnDisc=onDisc;animationMotions=motions;spriteNames=names;animationsPath=path;
            var named=table.NamedSets;
            var choices=new List<Choice>();
            for(int c=1;c<=table.ClassCount;c++)
                if(table.Sets(c).Count>0)choices.Add(new Choice(c,ClassTitle(c)));
            var unused=onDisc.Keys.Where(n=>!named.Contains(n)).ToList();
            choices.AddRange(unused.Select(n=>new Choice(-n,$"Unused set {SetLabel(n)}")));
            AnimationList.ItemsSource=choices;
            AnimationCount.Text=$"{choices.Count-unused.Count} classes, {onDisc.Count} animation sets on the disc, {unused.Count} not used by any class";
            if(choices.Count>0)AnimationList.SelectedIndex=0;
        }
        catch(Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException){AnimationCount.Text="The battle animations could not be read from this disc.";}
    }

    private async Task<AnimationSet?> AnimationSetFor(int number)
    {
        if(animationSets.TryGetValue(number,out var cached))return cached;
        if(events is not { } library)return null;
        AnimationSet? set;
        try{set=await Task.Run(()=>{lock(library)return AnimationSet.Load(library.Archives,number);});}
        catch(Exception ex) when (ex is IOException or InvalidDataException){set=null;}
        animationSets[number]=set;
        return set;
    }

    private void AnimationClassSelected(object sender,SelectionChangedEventArgs e)
    {
        if(editing?.Page==View.Animations)EndEdit();
        CloseMapSpriteEditor();
        RefreshAnimationClass();
    }

    /// <summary>A row's set or palette number as the project has it.</summary>
    private int AnimationValue(bool palette,int classId,int row,int disc)
    {
        int id=AnimationTable.RowId(palette,classId,row);
        string table=palette?EditSession.AnimationPalettes:EditSession.AnimationSets;
        var field=palette?AnimationTable.PaletteField:AnimationTable.SetField;
        return session is not null && session.Table(table).Records.ContainsKey(id)?session.Value(table,id,field.Key):disc;
    }

    private bool AnimationEdited(bool palette,int classId,int row)=>session?.IsEdited(palette?EditSession.AnimationPalettes:EditSession.AnimationSets,AnimationTable.RowId(palette,classId,row))==true;

    private void RefreshAnimationClass()
    {
        if(animationTable is not { } table || AnimationList.SelectedValue is not int value)return;
        int? keepSet=AnimationSetBox.SelectedValue as int?;
        AnimationDetails.Children.Clear();
        var muted=(Brush)FindResource("Muted");
        void Heading(string text)=>AnimationDetails.Children.Add(new TextBlock{Text=text,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,4)});
        void Line(string text,bool dim=false)=>AnimationDetails.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,3),Foreground=dim?muted:Brushes.Black});
        void Row(string text,bool edited,Action change)
        {
            var line=new DockPanel{Margin=new Thickness(0,0,0,4)};
            if(session is not null)
            {
                var button=new Button{Content="Change",Style=(Style)FindResource("SmallButton"),Padding=new Thickness(8,1,8,1),Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Top};
                button.Click+=(_,_)=>change();
                System.Windows.Automation.AutomationProperties.SetName(button,$"Change {text}");
                DockPanel.SetDock(button,Dock.Right);line.Children.Add(button);
            }
            line.Children.Add(new TextBlock{Text=edited?$"{text} (changed)":text,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center,FontWeight=edited?FontWeights.SemiBold:FontWeights.Normal});
            AnimationDetails.Children.Add(line);
        }
        var sets=new List<Choice>();
        if(value>0)
        {
            AnimationDetails.Children.Add(new TextBlock{Text=ClassTitle(value),FontFamily=new FontFamily("Georgia"),FontSize=18,TextWrapping=TextWrapping.Wrap});
            Heading("Animation sets");
            var setRows=table.Sets(value);
            for(int r=0;r<setRows.Count;r++)
            {
                var row=setRows[r];int number=AnimationValue(false,value,r,row.Value);int at=r;
                string who=row.Unit==0?"Any unit":UnitName(row.Unit);
                string text=$"{who}, {AnimationTable.KeyText(row.Key).ToLowerInvariant()}: {SetLabel(number)}{(animationSetsOnDisc.ContainsKey(number)?"":" (not on the disc)")}";
                Row(text,AnimationEdited(false,value,r),()=>BeginAnimationEdit(false,value,at,text));
                if(!sets.Any(s=>s.Value==number))sets.Add(new Choice(number,SetLabel(number)));
            }
            Heading("Colours");
            var palettes=table.Palettes(value);
            if(palettes.Count==0)Line("The set's own colours for everyone.",true);
            for(int r=0;r<palettes.Count;r++)
            {
                var row=palettes[r];int number=AnimationValue(true,value,r,row.Value);int at=r;
                string who=row.Unit>0?UnitName(row.Unit):AnimationTable.SideText(row.Unit);
                string when=row.Key==0?"":$" holding {AnimationTable.KeyText(row.Key)}";
                string text=$"{who}{when}: {(number==0?"the set's own colours":$"palette {number}")}";
                Row(text,AnimationEdited(true,value,r),()=>BeginAnimationEdit(true,value,at,text));
            }
            if(primary?.MapSpriteBytes.ContainsKey(value)==true)
            {
                Heading("Map sprite");
                var (sprite,shield)=MapSpriteNames(value);
                Row(shield is null?sprite:$"{sprite}, with a shield {shield}",session?.IsEdited(EditSession.MapSprites,value)==true,()=>BeginMapSpriteEdit(value));
            }
        }
        else
        {
            AnimationDetails.Children.Add(new TextBlock{Text=$"Unused set {SetLabel(-value)}",FontFamily=new FontFamily("Georgia"),FontSize=18,TextWrapping=TextWrapping.Wrap});
            Line("No class names this set on the disc. Choose Change beside a class's row to give the class this set.",true);
            if(animationMotions.TryGetValue(-value,out var have) && have.Count>0)
            {
                var fits=new List<string>();
                for(int c=1;c<=table.ClassCount;c++)
                    if(table.Sets(c).Any(r=>animationMotions.TryGetValue(r.Value,out var need) && need.Count>0 && AnimationSet.Covers(have,need)))fits.Add(ClassTitle(c));
                Heading("Has every motion of a set used by");
                Line(fits.Count==0?"No class: every class plays a motion this set does not have.":fits.Count<=12?string.Join(", ",fits)+".":$"{string.Join(", ",fits.Take(12))} and {fits.Count-12} more.",fits.Count==0);
            }
            sets.Add(new Choice(-value,SetLabel(-value)));
        }
        AnimationSetBox.ItemsSource=sets;
        AnimationSetBox.SelectedIndex=keepSet is int k && sets.FindIndex(s=>s.Value==k) is int found && found>=0?found:sets.Count>0?0:-1;
        if(AnimationSetBox.SelectedIndex>=0 && AnimationSetBox.SelectedValue is int same && same==keepSet)AnimationViewChanged(AnimationSetBox,null!);
    }

    private int MapSpriteWord(int classId,int word)
    {
        var field=MapSpriteData.Fields[word];
        return session is not null?session.Value(EditSession.MapSprites,classId,field.Key):FieldCodec.Read(primary!.MapSpriteBytes[classId].Span,field);
    }

    private (string Sprite,string? Shield) MapSpriteNames(int classId)
    {
        uint sprite=(uint)MapSpriteWord(classId,0),shield=(uint)MapSpriteWord(classId,1);
        return (spriteNames.TryGetValue(sprite,out var a)?a:"Unknown sprite",shield==0?null:spriteNames.TryGetValue(shield,out var b)?b:"Unknown sprite");
    }

    private void CloseMapSpriteEditor()
    {
        if(AnimationEditorPanel.Visibility!=Visibility.Visible || editing is not null)return;
        AnimationEditorPanel.Children.Clear();AnimationEditorPanel.Visibility=Visibility.Collapsed;AnimationDetails.Visibility=Visibility.Visible;
    }

    private void BeginMapSpriteEdit(int classId)
    {
        if(session is null || primary is null)return;
        EndEdit();
        var rows=primary.MapSpriteBytes;
        uint[] Row(int c)=>Enumerable.Range(0,MapSpriteData.Words).Select(w=>(uint)FieldCodec.Read(rows[c].Span,MapSpriteData.Fields[w])).ToArray();
        var current=Enumerable.Range(0,MapSpriteData.Words).Select(w=>(uint)MapSpriteWord(classId,w)).ToArray();
        // Each class's sprite on the disc, by class; the current one is the first class whose row matches.
        var choices=rows.Keys.Order().Select(c=>
        {
            var row=Row(c);string name=spriteNames.TryGetValue(row[0],out var n)?n:"Unknown sprite";
            return new Choice(c,$"{ClassTitle(c)}: {name}");
        }).ToList();
        int selected=rows.Keys.Order().FirstOrDefault(c=>c==classId && Row(c).SequenceEqual(current));
        if(selected==0)selected=rows.Keys.Order().FirstOrDefault(c=>Row(c).SequenceEqual(current));
        var panel=AnimationEditorPanel;panel.Children.Clear();
        panel.Children.Add(new TextBlock{Text=$"Map sprite of {ClassTitle(classId)}",FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
        panel.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(255,241,217)),BorderBrush=new SolidColorBrush(Color.FromRgb(223,204,158)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,0,10),
            Child=new TextBlock{Text="Every unit of this class shows the chosen class's map sprite, with that class's frame count and size. Battle animations are set separately in the rows above.",TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(114,82,33))}});
        panel.Children.Add(new TextBlock{Text="Use the map sprite of",Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,0,0,4)});
        var picker=new ComboBox{ItemsSource=choices,DisplayMemberPath="Text",SelectedValuePath="Value",IsTextSearchEnabled=true,Padding=new Thickness(6,4,6,4),Margin=new Thickness(0,0,0,10)};
        picker.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        if(selected!=0)picker.SelectedValue=selected;
        System.Windows.Automation.AutomationProperties.SetName(picker,"Use the map sprite of");
        panel.Children.Add(picker);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",Style=(Style)FindResource("SmallButton"),IsCancel=true};
        var apply=new Button{Content="Apply",Style=(Style)FindResource("SmallButton"),IsDefault=true,Background=(Brush)FindResource("Accent"),Foreground=Brushes.White,BorderBrush=(Brush)FindResource("Accent")};
        cancel.Click+=(_,_)=>{CloseMapSpriteEditor();Status.Text="Edit canceled.";};
        apply.Click+=(_,_)=>
        {
            if(picker.SelectedValue is not int source || session is null)return;
            var source_row=Row(source);
            var values=Enumerable.Range(0,MapSpriteData.Words).ToDictionary(w=>MapSpriteData.Fields[w].Key,w=>(int)source_row[w]);
            try
            {
                bool changed=session.Apply(EditSession.MapSprites,classId,values);
                CloseMapSpriteEditor();RefreshDocument();
                Status.Text=changed?"Map sprite updated. Save the project to keep your changes.":"No values were changed.";
            }
            catch(EditException ex){Status.Text=ex.Message;}
        };
        buttons.Children.Add(cancel);buttons.Children.Add(apply);panel.Children.Add(buttons);
        AnimationDetails.Visibility=Visibility.Collapsed;panel.Visibility=Visibility.Visible;
        picker.Focus();
    }

    private async void BeginAnimationEdit(bool palette,int classId,int row,string text)
    {
        if(session is null || animationTable is not { } table)return;
        TableField field;string? warning;
        if(palette)
        {
            // Palettes the class's sets carry, and the row's current value.
            var numbers=new SortedSet<int>{0};
            for(int r=0;r<table.Sets(classId).Count;r++)
                if(await AnimationSetFor(AnimationValue(false,classId,r,table.Sets(classId)[r].Value)) is { } set)numbers.UnionWith(set.Palettes);
            if(animationTable!=table || session is null)return;
            numbers.Add(AnimationValue(true,classId,row,table.Palettes(classId)[row].Value));
            field=AnimationTable.PaletteField with {Choices=numbers.Select(n=>n==0?"Set's own colours":$"Palette {n}").ToList(),ChoiceValues=numbers.ToList()};
            warning="Every unit this row applies to wears the chosen colours in all of this class's sets. Palettes are listed from the sets this class uses; a set without the chosen palette shows its own colours or wrong ones.";
        }
        else
        {
            var named=table.NamedSets;
            int original=table.Sets(classId)[row].Value,current=AnimationValue(false,classId,row,original);
            // Only sets with every motion of the row's own set: a motion the set lacks stops the battle when the class plays it.
            var needed=animationMotions.TryGetValue(original,out var m)?m:new HashSet<string>();
            var numbers=animationSetsOnDisc.Keys.Where(n=>n==original || n==current || animationMotions.TryGetValue(n,out var have) && have.Count>0 && AnimationSet.Covers(have,needed)).ToList();
            field=AnimationTable.SetField with {Choices=numbers.Select(n=>n==original?$"{SetLabel(n)} (the disc's)":named.Contains(n)?SetLabel(n):$"{SetLabel(n)} (no class uses it)").ToList(),ChoiceValues=numbers};
            warning=$"Every unit this row applies to plays the chosen set. Only the {numbers.Count} sets that have every motion of {SetLabel(original)} are listed, because a battle stops when a class plays a motion its set does not have. Units keep their map sprites.";
        }
        OpenEditor(new(View.Animations,AnimationTable.RowId(palette,classId,row),palette?"Colour row":"Animation row",AnimationEditorPanel,AnimationDetails,AnimationEditButton,
            [new(text,palette?EditSession.AnimationPalettes:EditSession.AnimationSets,[field])],warning));
    }

    private async void AnimationViewChanged(object sender,SelectionChangedEventArgs? e)
    {
        if(AnimationSetBox.SelectedValue is not int number)return;
        var set=await AnimationSetFor(number);
        if(AnimationSetBox.SelectedValue is not int now || now!=number)return;
        shownAnimationSet=set;
        // Set details go under the class rows, replacing any shown for the previous set.
        while(AnimationDetails.Children.Count>0 && AnimationDetails.Children[^1] is FrameworkElement { Tag: "set" })AnimationDetails.Children.RemoveAt(AnimationDetails.Children.Count-1);
        var muted=(Brush)FindResource("Muted");
        void Add(string text,bool bold=false)=>AnimationDetails.Children.Add(new TextBlock{Text=text,Tag="set",TextWrapping=TextWrapping.Wrap,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,bold?12:0,0,bold?4:3),Foreground=bold?Brushes.Black:muted});
        Add(AnimationSet.FileName(number),true);
        int weapon=number/10%100,variant=number%10;
        if(set is null)
        {
            Add("This set could not be read from the disc.");
            AnimationMotionBox.ItemsSource=null;AnimationPaletteBox.ItemsSource=null;AnimationImage.Source=null;AnimationFrameText.Text="";animationFrames=[];animationTimer.Stop();
            return;
        }
        Add($"Class art {number/1000}, weapon art {(SetWeapons.TryGetValue(weapon,out var w)?w:weapon.ToString())}, variant {variant}.");
        Add(set.HasFrames?$"{set.Motions.Count} motions: {string.Join(", ",set.Motions)}.":"This set has pictures but no frames, so it cannot be shown.");
        Add(set.Palettes.Count==0?"No numbered palettes.":$"Palettes: {string.Join(", ",set.Palettes)}.");
        // Colour choices: the set's own, each class row that picks a palette, and any palette no row uses.
        var colours=new List<Choice>{new(0,"Set's own colours")};
        if(animationTable is { } table && AnimationList.SelectedValue is int cls && cls>0)
            for(int r=0;r<table.Palettes(cls).Count;r++)
            {
                var row=table.Palettes(cls)[r];int palNumber=AnimationValue(true,cls,r,row.Value);
                if(palNumber<=0 || !set.Palettes.Contains(palNumber))continue;
                string who=row.Unit>0?UnitName(row.Unit):AnimationTable.SideText(row.Unit);
                if(row.Key!=0)who+=$" holding {AnimationTable.KeyText(row.Key)}";
                colours.Add(new Choice(palNumber,$"{who} (palette {palNumber})"));
            }
        foreach(int pal in set.Palettes.Where(p=>!colours.Any(c=>c.Value==p)))colours.Add(new Choice(pal,$"Palette {pal}"));
        var seen=new HashSet<string>();
        AnimationPaletteBox.ItemsSource=colours.Where(c=>seen.Add(c.Text)).ToList();
        AnimationPaletteBox.SelectedIndex=0;
        AnimationMotionBox.ItemsSource=set.Motions;
        AnimationMotionBox.SelectedIndex=set.Motions.Count>0?0:-1;
        if(set.Motions.Count==0){AnimationImage.Source=null;animationFrames=[];animationTimer.Stop();AnimationFrameText.Text="";}
    }

    private void AnimationMotionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(shownAnimationSet is not { } set || AnimationMotionBox.SelectedItem is not string motion)return;
        animationMotion=motion;animationFrames=set.Frames(motion);animationFrame=0;animationBounds=set.Bounds(motion);
        animationTicksLeft=animationFrames.Count>0?animationFrames[0].Time:0;
        DrawAnimationFrame();
        if(animationPlaying && animationFrames.Count>1 && view==View.Animations)animationTimer.Start();else animationTimer.Stop();
    }

    private void AnimationPaletteChanged(object sender,SelectionChangedEventArgs e)
    {
        animationPalette=AnimationPaletteBox.SelectedValue is int p?p:0;
        DrawAnimationFrame();
    }

    private void AnimationSizeChanged(object sender,SelectionChangedEventArgs e)=>SizeAnimationImage();

    private void SizeAnimationImage()
    {
        if(AnimationImage is null || AnimationSizeBox is null)return;
        int scale=AnimationSizeBox.SelectedIndex+1;
        AnimationImage.Width=animationBounds.Width*scale;AnimationImage.Height=animationBounds.Height*scale;
    }

    private void DrawAnimationFrame()
    {
        if(shownAnimationSet is not { } set || animationFrames.Count==0){AnimationImage.Source=null;AnimationFrameText.Text="";return;}
        var frame=animationFrames[Math.Clamp(animationFrame,0,animationFrames.Count-1)];
        var image=set.Draw(animationMotion,frame.Picture,animationPalette);
        var bitmap=BitmapSource.Create(image.Width,image.Height,96,96,PixelFormats.Bgra32,null,image.Pixels,image.Width*4);
        var (x,y,w,h)=animationBounds;
        var shown=new CroppedBitmap(bitmap,new Int32Rect(x,y,w,h));
        shown.Freeze();
        AnimationImage.Source=shown;SizeAnimationImage();
        AnimationFrameText.Text=$"Frame {animationFrame+1} of {animationFrames.Count}, picture {frame.Picture}, shown for {frame.Time}/60 s";
    }

    private void AnimationTick(object? sender,EventArgs e)
    {
        if(animationFrames.Count<2){animationTimer.Stop();return;}
        if(--animationTicksLeft>0)return;
        animationFrame=(animationFrame+1)%animationFrames.Count;
        animationTicksLeft=animationFrames[animationFrame].Time;
        DrawAnimationFrame();
    }

    private void ToggleAnimation(object sender,RoutedEventArgs e)
    {
        animationPlaying=!animationPlaying;
        AnimationPlayButton.Content=animationPlaying?"Pause":"Play";
        if(animationPlaying && animationFrames.Count>1)animationTimer.Start();else animationTimer.Stop();
    }

    private void StepAnimation(object sender,RoutedEventArgs e)
    {
        if(animationFrames.Count==0)return;
        animationPlaying=false;AnimationPlayButton.Content="Play";animationTimer.Stop();
        animationFrame=(animationFrame+1)%animationFrames.Count;animationTicksLeft=animationFrames[animationFrame].Time;
        DrawAnimationFrame();
    }

    // Maps: each battle's terrain and spots, read through the event library (which keeps the disc open).
    private string? mapsPath;
    // Battles are listed by number; maps that no battle uses are listed with their archive number made negative.
    private List<(int Battle,string Text,string File)> mapBattles=[];
    private Dictionary<int,List<int>> battleArmies=[];
    private async Task OpenMaps()
    {
        if(primary is null || !primary.Supported)return;
        if(mapsPath==primary.Path && mapBattles.Count>0)return;
        await OpenEvents();
        if(events is not { } library || primary is null)return;
        string path=primary.Path;var exeEntry=primary.ExecutableFile!;
        MapCount.Text="Reading the battles...";
        try
        {
            var (list,armies)=await Task.Run(()=>
            {
                byte[] exe;
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                using(var iso=new IsoImage(stream)) exe=iso.ReadRange(exeEntry.Offset,(int)exeEntry.Length,(int)exeEntry.Length);
                lock(library)
                {
                    var files=library.BattleMapFiles();
                    var named=LiveGame.Battles(exe,ArmyData.LoadBattleTitles(library.Archives));
                    var order=named.Select(b=>b.Battle).Distinct().ToList();
                    var names=named.GroupBy(b=>b.Battle).ToDictionary(g=>g.Key,g=>g.First().Text);
                    var onDisc=BattleMaps.Files(library.Archives);
                    var known=onDisc.Select(f=>f.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var result=files.Keys.Where(b=>known.Contains(files[b])).OrderBy(b=>order.IndexOf(b) is int i && i>=0?i:order.Count+b)
                        .Select(b=>(b,names.TryGetValue(b,out var n)?$"{n} (battle {b})":files[b].StartsWith("zmap",StringComparison.OrdinalIgnoreCase)?$"Title screen demo (battle {b})":$"Battle {b}",files[b])).ToList();
                    var usedFiles=files.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    result.AddRange(onDisc.Where(f=>!usedFiles.Contains(f.File)).Select(f=>(-f.Top,$"Unused map {Path.GetFileNameWithoutExtension(f.File)}",f.File)));
                    var byBattle=new Dictionary<int,List<int>>();
                    foreach(var (army,battles) in library.ArmyBattles())
                        if(battles.Count<=2) foreach(int b in battles)(byBattle.TryGetValue(b,out var l)?l:byBattle[b]=[]).Add(army);
                    return (result,byBattle);
                }
            });
            if(events!=library)return;
            mapBattles=list;battleArmies=armies;mapsPath=path;
            MapList.ItemsSource=mapBattles.Select(b=>new Choice(b.Battle,b.Text)).ToList();
            int unused=mapBattles.Count(b=>b.Battle<0);
            MapCount.Text=$"{mapBattles.Count-unused} battles, {unused} unused maps";
            if(mapBattles.Count>0)MapList.SelectedIndex=0;
        }
        catch(Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException){MapCount.Text="The battles could not be read from this disc.";}
    }
    private async void MapSelected(object sender,SelectionChangedEventArgs e)
    {
        if(events is not { } library || MapList.SelectedValue is not int battle)return;
        var entry=mapBattles.FirstOrDefault(b=>b.Battle==battle);
        BattleMap? map;BitmapSource? picture=null;
        try
        {
            (map,picture)=await Task.Run(()=>
            {
                BattleMap? read;MapPicture? art;
                lock(library)
                {
                    read=battle<0?BattleMaps.Load(library.Archives,null,-battle,0,entry.File):BattleMaps.Load(library.Archives,library,battle,entry.File);
                    // Maps no battle uses have no scripts to say which layers show; the first water frame (layer 4) is drawn for them.
                    art=read is null?null:BattleMaps.LoadPicture(library.Archives,read.Top,battle<0?[4]:library.StartingLayers(battle));
                }
                BitmapSource? image=null;
                if(art is not null){image=BitmapSource.Create(art.Width,art.Height,96,96,PixelFormats.Bgra32,null,art.Pixels,art.Width*4);image.Freeze();}
                return (read,image);
            });
        }
        catch(Exception ex) when (ex is IOException or InvalidDataException){map=null;}
        if(events!=library || MapList.SelectedValue is not int now || now!=battle)return;
        shownMap=map;shownPicture=picture;shownTitle=entry.Text;
        DrawMap(map,picture,entry.Text);
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
        if(MapMoveBox.IsChecked!=true || session is null || shownMap is not { } map || map.Battle<=0)return;
        e.Handled=true;
        var (x,y)=HexAt(e.GetPosition(MapCanvas));
        if(x<0 || y<0 || x>=map.Width || y>=map.Height)return;
        var here=PlacedUnits(map).Where(p=>p.Place.X==x && p.Place.Y==y).ToList();
        if(movingUnit is not { } moving || here.Count>0 && !here.Any(p=>p.Place.Group==moving.Group && p.Place.Index==moving.Index))
        {
            if(here.Count==0){Status.Text="Click a unit first, then the hex where it should start.";return;}
            var pick=here[0].Place;movingUnit=(pick.Group,pick.Index);
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
                if(map.Units.Count(u=>u.Group==moving.Group && u.Index==moving.Index)>1)Status.Text+=" Every unit placed in that slot moved with it.";
                movingUnit=null;RefreshDocument();
            }
        }
        catch(EditException ex){Status.Text=ex.Message;}
        DrawMap(shownMap,shownPicture,shownTitle);
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
                        updated=Deployment.AddOwnLimit(current,n,library.CommandNames);
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
        var armyIds=battleArmies.TryGetValue(map.Battle,out var ids)?ids:[];
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
        foreach(var r in map.Arrivals)
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

    // Event scripts are read from the disc on demand, so the disc stays open while the page is used.
    private FileStream? eventStream;
    private EventLibrary? events;
    private string? eventsPath;
    private readonly Dictionary<int,string> eventTexts=new();
    private void CloseEvents(){shops=null;shopsPath=null;ShopGrid.ItemsSource=null;ShopItemList.ItemsSource=null;ShopCount.Text="";if(textEditing)EndTextEdit();fixedTops=null;mapsPath=null;mapBattles=[];shownMap=null;shownPicture=null;shownTitle="";battleArmies=[];MapList.ItemsSource=null;MapCanvas.Children.Clear();MapDetails.Children.Clear();MapCount.Text="";CloseAnimations();events=null;eventsPath=null;lock(eventTexts)eventTexts.Clear();eventStream?.Dispose();eventStream=null;EventList.ItemsSource=null;EventText.Text="";EventCount.Text="";}
    private async Task OpenEvents()
    {
        if(primary is null || !primary.Supported)return;
        if(events is not null && eventsPath==primary.Path)return;
        CloseEvents();
        EventCount.Text="Reading the event list...";
        string path=primary.Path;
        try
        {
            var (stream,library)=await Task.Run(()=>{var s=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return (s,new EventLibrary(new GameArchives(new IsoImage(s))));});
            if(primary?.Path!=path){stream.Dispose();return;}
            eventStream=stream;events=library;eventsPath=path;
            ShowEventList(library.Numbers,$"{library.Numbers.Count:N0} events");
        }
        catch(Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            EventCount.Text="The event scripts could not be read from this disc.";
        }
    }
    private void ShowEventList(IReadOnlyList<int> numbers,string caption)
    {
        EventList.DisplayMemberPath="Text";EventList.SelectedValuePath="Value";
        EventList.ItemsSource=numbers.Select(n=>new Choice(n,$"Event {n}")).ToList();
        EventCount.Text=caption;
        if(numbers.Count>0) EventList.SelectedIndex=0; else EventText.Text="";
    }
    private string EventScript(EventLibrary library,int number)
    {
        lock(eventTexts)
        {
            if(eventTexts.TryGetValue(number,out var t))return t;
        }
        string text;
        lock(library) text=library.Text(number);
        lock(eventTexts) eventTexts[number]=text;
        return text;
    }
    private async void EventSelected(object sender,SelectionChangedEventArgs e)
    {
        if(events is not { } library || EventList.SelectedValue is not int number)return;
        if(editing?.Page==View.Events)EndEdit();
        AddLinePanel.Visibility=Visibility.Collapsed;
        await ShowEvent(library,number,scrollHome:true);
    }
    // The selected event as text, with the numbers changed in this project, and the list of its numbers.
    private async Task ShowEvent(EventLibrary library,int number,bool scrollHome)
    {
        var changes=ScriptChanges(number);
        bool replaced=session?.HasScript(number)==true;
        byte[]? projectScript=replaced?session!.EventScript(number):null;
        try
        {
            var (text,numbers,lines)=await Task.Run(()=>
            {
                byte[] bytes;
                if(projectScript is not null)bytes=projectScript;
                else{lock(library)bytes=library.Bytes(number);if(changes.Count>0)bytes=ScriptValues.Apply(bytes,changes);}
                var nodes=ScriptParser.Parse(bytes);
                return (changes.Count>0 || replaced?ScriptText.Format(nodes,library.CommandNames):EventScript(library,number),ScriptValues.Numbers(number,nodes,library.CommandNames),ScriptLines.Lines(nodes,library.CommandNames));
            });
            if(events!=library || EventList.SelectedValue is not int now || now!=number)return;
            double offset=EventText.VerticalOffset;
            if(!textEditing)
            {
                EventText.Text=text;
                if(scrollHome)EventText.ScrollToHome();else EventText.ScrollToVerticalOffset(offset);
            }
            int? selected=ScriptNumberList.SelectedValue as int?;
            scriptNumbers=numbers.ToList();
            ScriptNumberList.ItemsSource=numbers.Select(n=>new Choice(ScriptValues.Key(number,n.Index),
                $"{n.Index+1}. {(n.Command.Length>0?n.Command:"value")}{(n.Argument>0?$", argument {n.Argument}":"")}: {n.Value}{(changes.ContainsKey(n.Index)?"  (changed)":"")}")).ToList();
            if(selected is int key && ScriptValues.Split(key).Event==number)ScriptNumberList.SelectedValue=key;
            if(numbers.Count==0){ScriptNumberFields.Children.Clear();AddText(ScriptNumberFields,"This event has no numbers.");}
            int? line=lineEvent==number?ScriptLineList.SelectedIndex:null;
            scriptLines=lines.ToList();lineEvent=number;
            ScriptLineList.ItemsSource=lines.Select((l,i)=>new Choice(i,new string(' ',l.Depth*2)+l.Text)).ToList();
            if(pendingLine is { } wanted)
            {
                int found=lines.ToList().FindIndex(x=>x.Path.SequenceEqual(wanted));
                ScriptLineList.SelectedIndex=found>=0?found:Math.Min(line??0,lines.Count-1);pendingLine=null;
            }
            else if(line is int keep && keep>=0)ScriptLineList.SelectedIndex=Math.Min(keep,lines.Count-1);
            ShowLineNotes(number);
            UpdateTextButtons(number);
        }
        catch(InvalidDataException){EventText.Text="This event could not be read.";ScriptNumberList.ItemsSource=null;scriptNumbers=[];}
        UpdateEditButtons();
    }
    private List<ScriptNumber> scriptNumbers=[];
    // Archives holding the tables read at fixed places; events there can only grow up to their next sector.
    private IReadOnlySet<int>? fixedTops;
    // The numbers this project changes in one event, by their place in the script.
    private Dictionary<int,int> ScriptChanges(int number)=>session is null?[]:session.EditedRecords.Where(r=>r.Table==EditSession.ScriptValues && ScriptValues.Split(r.Id).Event==number)
        .ToDictionary(r=>ScriptValues.Split(r.Id).Index,r=>session.Value(EditSession.ScriptValues,r.Id,ScriptValues.Field.Key));
    private void ScriptNumberSelected(object sender,SelectionChangedEventArgs e)
    {
        if(editing?.Page==View.Events)EndEdit();
        ScriptNumberFields.Children.Clear();UpdateEditButtons();
        if(ScriptNumberList.SelectedValue is not int key)return;
        var (number,index)=ScriptValues.Split(key);
        if(index>=scriptNumbers.Count)return;
        var n=scriptNumbers[index];
        AddPair(ScriptNumberFields,"Event",number.ToString());
        if(n.Line.Length>0)AddPair(ScriptNumberFields,"In",n.Line);
        if(n.Argument>0)AddPair(ScriptNumberFields,"Argument",n.Argument.ToString());
        bool replaced=session?.HasScript(number)==true;
        if(!replaced && session?.IsEdited(EditSession.ScriptValues,key)==true)AddPair(ScriptNumberFields,"Value on the disc",session.OriginalValue(EditSession.ScriptValues,key,ScriptValues.Field.Key).ToString());
        AddPair(ScriptNumberFields,"Value",n.Value.ToString());
        if(replaced)AddText(ScriptNumberFields,"The lines of this event were changed, so a changed number is kept as part of its new script.");
        else if(session is not null)AddChangeList(ScriptNumberFields,ch=>ch.Table==EditSession.ScriptValues && ch.Id==key,()=>session.Revert(EditSession.ScriptValues,key));
    }
    private bool textEditing;
    private void UpdateTextButtons(int number)
    {
        EditTextButton.IsEnabled=session is not null && editing is null && !textEditing && number is not (602 or 727);
        EditTextButton.ToolTip=number is 602 or 727?"This event holds the shop's prices, which are changed on their own pages.":"Change the script as text";
        if(!textEditing)EventTextNote.Text=session?.HasScript(number)==true?"This event is changed in this project.":"";
    }
    private void BeginTextEdit(object sender,RoutedEventArgs e)
    {
        if(session is null || EventList.SelectedValue is not int number || number is 602 or 727)return;
        if(editing is not null)EndEdit();
        textEditing=true;AddLinePanel.Visibility=Visibility.Collapsed;
        EventText.IsReadOnly=false;EventText.AcceptsReturn=true;EventText.AcceptsTab=true;EventText.Background=new SolidColorBrush(Color.FromRgb(255,253,245));
        EventList.IsEnabled=false;EventSearch.IsEnabled=false;ScriptTabs.IsEnabled=false;
        EditTextButton.Visibility=Visibility.Collapsed;CancelTextButton.Visibility=Visibility.Visible;ApplyTextButton.Visibility=Visibility.Visible;
        EventTextNote.Foreground=(Brush)FindResource("Muted");
        EventTextNote.Text="Edit the script, keeping four spaces of indent for each level, then choose Apply text.";
        EventText.Focus();
    }
    private void EndTextEdit()
    {
        textEditing=false;
        EventText.IsReadOnly=true;EventText.AcceptsReturn=false;EventText.AcceptsTab=false;EventText.Background=Brushes.White;
        EventList.IsEnabled=true;EventSearch.IsEnabled=true;ScriptTabs.IsEnabled=true;
        EditTextButton.Visibility=Visibility.Visible;CancelTextButton.Visibility=Visibility.Collapsed;ApplyTextButton.Visibility=Visibility.Collapsed;
        EventTextNote.Foreground=(Brush)FindResource("Muted");EventTextNote.Text="";
    }
    private void CancelTextEdit(object sender,RoutedEventArgs e)
    {
        EndTextEdit();
        if(events is { } library && EventList.SelectedValue is int number)_=ShowEvent(library,number,scrollHome:false);
        Status.Text="Text edit canceled.";
    }
    private void ApplyTextEdit(object sender,RoutedEventArgs e)
    {
        if(session is null || events is not { } library || EventList.SelectedValue is not int number)return;
        try
        {
            var bytes=ScriptWriter.Write(ScriptCompiler.Compile(EventText.Text,library.CommandNames));
            CheckEventFits(library,number,bytes);
            bool changed=session.SetScript(number,bytes);
            EndTextEdit();RefreshDocument();
            Status.Text=changed?"Script updated. Save the project to keep your changes.":"The script is the same as before.";
        }
        catch(EditException ex)
        {
            EventTextNote.Foreground=new SolidColorBrush(Color.FromRgb(154,59,46));EventTextNote.Text=ex.Message;
            // Put the cursor on the line the message names.
            var m=System.Text.RegularExpressions.Regex.Match(ex.Message,@"^Line (\d+):");
            if(m.Success && int.TryParse(m.Groups[1].Value,out int line) && line-1<EventText.LineCount)
            {
                EventText.Focus();EventText.CaretIndex=EventText.GetCharacterIndexFromLineIndex(line-1);EventText.ScrollToLine(line-1);
            }
        }
    }
    private List<ScriptLine> scriptLines=[];
    private int? lineEvent;
    private IReadOnlyList<(int Arg,int Item)>? pendingLine;
    // The place of a line moved by some places within its own part of the script.
    private static IReadOnlyList<(int Arg,int Item)> Sibling(IReadOnlyList<(int Arg,int Item)> path,int by)=>[..path.Take(path.Count-1),(path[^1].Arg,Math.Max(0,path[^1].Item+by))];
    private IReadOnlyDictionary<int,IReadOnlySet<int>>? argumentCounts;
    private void CheckEventFits(EventLibrary library,int number,byte[] edited)
    {
        lock(library)EventCopies.CheckFits(library.Archives,number,edited,fixedTops??=ArchiveEditing.FixedTops(library.Archives,primary?.Profile));
    }
    private void ShowLineNotes(int number)
    {
        ScriptLineNotes.Children.Clear();
        bool can=session is not null && editing is null && number is not (602 or 727);
        foreach(var b in new[]{AddLineButton,DuplicateLineButton,RemoveLineButton,LineUpButton,LineDownButton})b.IsEnabled=can && ScriptLineList.SelectedIndex>=0 && ScriptLineList.SelectedIndex<scriptLines.Count && scriptLines[ScriptLineList.SelectedIndex].Text!="else";
        if(number is 602 or 727){AddText(ScriptLineNotes,"This event holds the shop's prices, which are changed on their own pages, so its lines are not changed here.");return;}
        AddText(ScriptLineNotes,"Changing lines changes what the game does at this point. Removing an if or while removes the lines under it too.");
        if(session?.HasScript(number)==true)
        {
            AddText(ScriptLineNotes,"The lines of this event are changed in this project.");
            var restore=new Button {Content="Restore the event's lines",Style=(Style)FindResource("SmallButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,0)};
            restore.Click+=(_,_)=>{session.Revert(EditSession.Scripts,number);RefreshDocument();Status.Text=$"Event {number} restored. Save the project to keep this change.";};
            ScriptLineNotes.Children.Add(restore);
        }
    }
    private void ScriptLineSelected(object sender,SelectionChangedEventArgs e){if(lineEvent is int n)ShowLineNotes(n);}
    private ScriptLine? SelectedLine=>ScriptLineList.SelectedIndex>=0 && ScriptLineList.SelectedIndex<scriptLines.Count?scriptLines[ScriptLineList.SelectedIndex]:null;
    // Applies a change to the selected event's lines as one undoable step, after checking the event still fits the disc.
    private void ChangeLines(Func<byte[],byte[]> change,string done,IReadOnlyList<(int Arg,int Item)> select)
    {
        if(session is null || events is not { } library || EventList.SelectedValue is not int number)return;
        try
        {
            var updated=change(session.EventScript(number)!);
            CheckEventFits(library,number,updated);
            if(!session.SetScript(number,updated)){Status.Text="Nothing was changed.";return;}
        }
        catch(EditException ex){Status.Text=ex.Message;return;}
        pendingLine=select;
        RefreshDocument();
        Status.Text=done+" Save the project to keep your changes.";
    }
    private void DuplicateLine(object sender,RoutedEventArgs e){if(SelectedLine is { } l)ChangeLines(b=>ScriptLines.Duplicate(b,l.Path),"Line duplicated.",Sibling(l.Path,1));}
    private void RemoveLine(object sender,RoutedEventArgs e){if(SelectedLine is { } l)ChangeLines(b=>ScriptLines.Remove(b,l.Path),"Line removed.",l.Path);}
    private void MoveLineUp(object sender,RoutedEventArgs e){if(SelectedLine is { } l)ChangeLines(b=>ScriptLines.Move(b,l.Path,-1),"Line moved up.",Sibling(l.Path,-1));}
    private void MoveLineDown(object sender,RoutedEventArgs e){if(SelectedLine is { } l)ChangeLines(b=>ScriptLines.Move(b,l.Path,1),"Line moved down.",Sibling(l.Path,1));}
    private async void BeginAddLine(object sender,RoutedEventArgs e)
    {
        if(SelectedLine is null || events is not { } library)return;
        AddLinePanel.Visibility=Visibility.Visible;AddLineError.Visibility=Visibility.Collapsed;
        if(argumentCounts is null)
        {
            AddLineHint.Text="Reading how each command is used...";
            argumentCounts=await Task.Run(()=>{lock(library)return ScriptLines.ArgumentCounts(library.Numbers.Select(library.Bytes).ToList());});
        }
        var names=library.CommandNames??[];
        AddLineCommand.ItemsSource=argumentCounts.Keys.Where(c=>c<names.Count).Select(c=>new Choice(c,ScriptText.Name(c,names))).OrderBy(c=>c.Text,StringComparer.OrdinalIgnoreCase).ToList();
        AddLineHint.Text="Choose a command, or type its name.";
        AddLineCommand.Focus();
    }
    private void AddLineCommandChanged(object sender,SelectionChangedEventArgs e)
    {
        if(AddLineCommand.SelectedValue is not int command || argumentCounts is null || !argumentCounts.TryGetValue(command,out var counts))return;
        var list=counts.Order().ToList();
        AddLineHint.Text=$"{ScriptText.Name(command,events?.CommandNames)} is used with {(list.Count==1?list[0].ToString():string.Join(", ",list.Take(list.Count-1))+" or "+list[^1])} {(list.Count==1 && list[0]==1?"argument":"arguments")} in the game's scripts.";
    }
    private void CancelAddLine(object sender,RoutedEventArgs e){AddLinePanel.Visibility=Visibility.Collapsed;}
    private void ApplyAddLine(object sender,RoutedEventArgs e)
    {
        if(SelectedLine is not { } l || argumentCounts is null)return;
        void Fail(string message){AddLineError.Text=message;AddLineError.Visibility=Visibility.Visible;}
        int? command=AddLineCommand.SelectedValue as int?;
        if(command is null && AddLineCommand.ItemsSource is IEnumerable<Choice> choices)
            command=choices.FirstOrDefault(c=>c.Text.Equals(AddLineCommand.Text.Trim(),StringComparison.OrdinalIgnoreCase))?.Value;
        if(command is not int chosen || !argumentCounts.TryGetValue(chosen,out var counts)){Fail("Choose a command from the list.");return;}
        ScriptNode node;
        try{node=ScriptLines.Command(chosen,AddLineArguments.Text);}
        catch(EditException ex){Fail(ex.Message);return;}
        if(!counts.Contains(node.Args.Count)){Fail($"{ScriptText.Name(chosen,events?.CommandNames)} is not used with {node.Args.Count} {(node.Args.Count==1?"argument":"arguments")} in the game's scripts. {AddLineHint.Text}");return;}
        AddLinePanel.Visibility=Visibility.Collapsed;AddLineArguments.Text="";
        ChangeLines(b=>ScriptLines.InsertAfter(b,l.Path,node),"Line added.",Sibling(l.Path,1));
    }
    private void BeginScriptNumberEdit(object sender,RoutedEventArgs e)
    {
        if(ScriptNumberList.SelectedValue is not int key)return;
        OpenEditor(new(View.Events,key,"Number",ScriptNumberEditor,ScriptNumberFields,EditScriptNumberButton,
            [new("",EditSession.ScriptValues,[ScriptValues.Field])],
            "The new value goes into every copy of this event. A script that grows larger than its space moves the files after it, or its archive moves to unused space on the disc."));
    }
    // A changed number must leave the event small enough for every copy's space on the disc.
    private string? ScriptFitProblem(int key,int value)
    {
        if(events is not { } library)return null;
        var (number,index)=ScriptValues.Split(key);
        try
        {
            byte[] edited;
            if(session?.HasScript(number)==true)edited=ScriptValues.Apply(session.EventScript(number)!,new Dictionary<int,int>{[index]=value});
            else{var changes=ScriptChanges(number);changes[index]=value;lock(library)edited=ScriptValues.Apply(library.Bytes(number),changes);}
            CheckEventFits(library,number,edited);
            return null;
        }
        catch(EditException ex){return ex.Message;}
    }
    private async void EventSearchKey(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter || events is not { } library)return;
        e.Handled=true;
        string query=EventSearch.Text.Trim();
        if(query.Length==0){ShowEventList(library.Numbers,$"{library.Numbers.Count:N0} events");return;}
        if(int.TryParse(query,out int number) && library.Numbers.Contains(number))
        {
            ShowEventList(library.Numbers,$"{library.Numbers.Count:N0} events");
            EventList.SelectedValue=number;EventList.ScrollIntoView(EventList.SelectedItem);return;
        }
        EventCount.Text="Searching every event...";
        var found=await Task.Run(()=>library.Numbers.Where(n=>{try{return EventScript(library,n).Contains(query,StringComparison.OrdinalIgnoreCase);}catch(InvalidDataException){return false;}}).ToList());
        if(events!=library)return;
        ShowEventList(found,found.Count==1?$"1 event contains \"{query}\"":$"{found.Count:N0} events contain \"{query}\"");
    }
    private void ShowDisc(object sender,RoutedEventArgs e) { view=View.Disc;UpdatePage(); }
    private void ShowLive(object sender,RoutedEventArgs e) { view=View.Live;UpdatePage(); }
    private void UpdatePage()
    {
        if(view is not (View.Disc or View.Mechanics) && primary is not null && !primary.Supported) view=View.Disc;
        if(editing is not null && editing.Page!=view) EndEdit();
        PageTitle.Text=view switch { View.Events=>"Events", View.Maps=>"Maps", View.Disc=>"Disc details", View.Characters=>"Characters", View.Armies=>"Armies", View.Furniture=>"Furniture", View.Dishes=>"Dishes", View.Shops=>"Shops", View.Classes=>"Classes", View.Movement=>"Movement", View.Terrain=>"Terrain", View.Skills=>"Skills", View.Rules=>"Game rules", View.Mechanics=>"Mechanics", View.Live=>"Live game", View.Animations=>"Battle animations", _=>"Item library" };
        bool supported=primary?.Supported==true;
        DiscPage.Visibility=view==View.Disc && primary is not null?Visibility.Visible:Visibility.Collapsed;
        ItemPage.Visibility=view==View.Items && supported?Visibility.Visible:Visibility.Collapsed;
        CharacterPage.Visibility=view==View.Characters && supported?Visibility.Visible:Visibility.Collapsed;
        ArmyPage.Visibility=view==View.Armies && supported?Visibility.Visible:Visibility.Collapsed;
        FurniturePage.Visibility=view==View.Furniture && supported?Visibility.Visible:Visibility.Collapsed;
        DishesPage.Visibility=view==View.Dishes && supported?Visibility.Visible:Visibility.Collapsed;
        ShopsPage.Visibility=view==View.Shops && supported?Visibility.Visible:Visibility.Collapsed;
        ClassPage.Visibility=view==View.Classes && supported?Visibility.Visible:Visibility.Collapsed;
        MovementPage.Visibility=view==View.Movement && supported?Visibility.Visible:Visibility.Collapsed;
        TerrainPage.Visibility=view==View.Terrain && supported?Visibility.Visible:Visibility.Collapsed;
        RulesPage.Visibility=view==View.Rules && supported?Visibility.Visible:Visibility.Collapsed;
        SkillsPage.Visibility=view==View.Skills && supported?Visibility.Visible:Visibility.Collapsed;
        EventsPage.Visibility=view==View.Events && supported?Visibility.Visible:Visibility.Collapsed;
        MapsPage.Visibility=view==View.Maps && supported?Visibility.Visible:Visibility.Collapsed;
        AnimationsPage.Visibility=view==View.Animations && supported?Visibility.Visible:Visibility.Collapsed;
        if(view!=View.Animations)animationTimer.Stop();else if(animationPlaying && animationFrames.Count>1)animationTimer.Start();
        MechanicsPage.Visibility=view==View.Mechanics?Visibility.Visible:Visibility.Collapsed;
        LivePage.Visibility=view==View.Live && supported?Visibility.Visible:Visibility.Collapsed;
        if(view==View.Mechanics) EmptyState.Visibility=Visibility.Collapsed;
        EmptyState.Visibility=primary is null && view!=View.Mechanics?Visibility.Visible:Visibility.Collapsed;
        var active=new SolidColorBrush(Color.FromRgb(60,84,69));
        ItemsNav.Background=view==View.Items?active:Brushes.Transparent;
        CharactersNav.Background=view==View.Characters?active:Brushes.Transparent;
        ArmiesNav.Background=view==View.Armies?active:Brushes.Transparent;
        FurnitureNav.Background=view==View.Furniture?active:Brushes.Transparent;
        DishesNav.Background=view==View.Dishes?active:Brushes.Transparent;
        ShopsNav.Background=view==View.Shops?active:Brushes.Transparent;
        ClassesNav.Background=view==View.Classes?active:Brushes.Transparent;
        MovementNav.Background=view==View.Movement?active:Brushes.Transparent;
        TerrainNav.Background=view==View.Terrain?active:Brushes.Transparent;
        RulesNav.Background=view==View.Rules?active:Brushes.Transparent;
        SkillsNav.Background=view==View.Skills?active:Brushes.Transparent;
        EventsNav.Background=view==View.Events?active:Brushes.Transparent;
        MapsNav.Background=view==View.Maps?active:Brushes.Transparent;
        AnimationsNav.Background=view==View.Animations?active:Brushes.Transparent;
        MechanicsNav.Background=view==View.Mechanics?active:Brushes.Transparent;
        LiveNav.Background=view==View.Live?active:Brushes.Transparent;
        if(view==View.Live)UpdateLiveButtons();
        DiscNav.Background=view==View.Disc?active:Brushes.Transparent;
    }
    private List<ArmyUnitRow> armyRows=[];
    private static readonly byte[] NoGrowth=new byte[CharacterDecoder.GrowthLength];
    private byte[] ArmyBytes(ArmyUnit unit)=>session is not null?session.EditedBytes(EditSession.ArmyUnits,unit.Key):primary!.ArmyUnitBytes[unit.Key].ToArray();
    private CharacterRecord ArmyRecord(ArmyUnit unit,IReadOnlyDictionary<int,string> itemNames)=>
        CharacterDecoder.Decode(new(unit.NameId,unit.Name,0),ArmyBytes(unit),NoGrowth,itemNames);
    private string ArmyDrops(ArmyUnit unit,CharacterRecord record)
    {
        var bytes=ArmyBytes(unit);
        var names=new List<string>();
        for(int slot=0;slot<ArmyData.ItemSlots;slot++)
        {
            int at=0xBC+slot*8;int item=bytes[at]|bytes[at+1]<<8;
            if(item!=0 && (bytes[at+3]&0x80)!=0) names.Add(record.Items.FirstOrDefault(i=>i.ItemId==item)?.Name??$"Item 0x{item:X3}");
        }
        return string.Join(", ",names);
    }
    private List<ArmyUnitRow> BuildArmyRows()
    {
        if(primary is null || !primary.Supported)return [];
        var rows=new List<ArmyUnitRow>();
        var itemNames=(session?.EditedItems()??primary.Items).ToDictionary(i=>i.Id,i=>i.DisplayName);
        foreach(var army in primary.Armies)
            foreach(var unit in army.Units)
            {
                var record=ArmyRecord(unit,itemNames);
                string className=classLookup.TryGetValue(record.ClassId,out var k)?k.Name:$"Class 0x{record.ClassId:X2}";
                rows.Add(new(army,unit,record,className,ArmyDrops(unit,record)) { Edited=session?.IsEdited(EditSession.ArmyUnits,unit.Key)==true });
            }
        return rows;
    }
    private void ApplyArmyFilter()
    {
        if(!ready)return;
        string query=ArmySearch.Text.Trim();ArmySearchHint.Visibility=query.Length==0?Visibility.Visible:Visibility.Collapsed;
        int? selected=(ArmyGrid.SelectedItem as ArmyUnitRow)?.Unit.Key;
        var filtered=armyRows.Where(r=>(NamedUnitsOnly.IsChecked!=true || r.Unit.Named || r.Edited)
            && (query.Length==0 || r.Name.Contains(query,StringComparison.OrdinalIgnoreCase) || r.ClassName.Contains(query,StringComparison.OrdinalIgnoreCase)
                || r.ArmyLabel.Contains(query,StringComparison.OrdinalIgnoreCase) || r.Army.Id.ToString()==query.TrimStart('0'))).ToList();
        ArmyGrid.ItemsSource=filtered;
        NoArmyUnits.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
        ArmyCount.Text=$"{filtered.Count:N0} of {armyRows.Count:N0} units in {primary?.Armies.Count??0:N0} armies";
        if(filtered.Count>0) ArmyGrid.SelectedItem=filtered.FirstOrDefault(r=>r.Unit.Key==selected)??filtered[0];
        else ClearArmyUnit();
    }
    private void ArmyFilterChanged(object sender,TextChangedEventArgs e)=>ApplyArmyFilter();
    private void ArmyOptionChanged(object sender,RoutedEventArgs e)=>ApplyArmyFilter();
    private void ClearArmyUnit()
    {
        if(ArmyUnitName is null)return;ArmyUnitName.Text="Select a unit";ArmyUnitIdentity.Text="";ArmyUnitFields.Children.Clear();
    }
    private void ArmyUnitSelected(object sender,SelectionChangedEventArgs e)
    {
        if(ArmyGrid.SelectedItem is not ArmyUnitRow row){ClearArmyUnit();return;}
        var c=row.Record;
        if(editing?.Page==View.Armies && editing.Id!=row.Unit.Key) EndEdit();
        UpdateEditButtons();
        ArmyUnitName.Text=row.Name;
        ArmyUnitIdentity.Text=$"{row.ArmyLabel}, unit {row.Unit.Index+1} of {row.Army.Units.Count} · {row.ClassName} · Level {c.Level}";
        var panel=ArmyUnitFields;panel.Children.Clear();
        if(classLookup.TryGetValue(c.ClassId,out var k))
        {
            AddSection(panel,"Class base plus bonus");
            foreach(var (label,value) in new[]{("HP",k.Hp+c.HpBonus),("Strength",k.Strength+c.StrengthBonus),("Defense",k.Defense+c.DefenseBonus),("Agility",k.Agility+c.AgilityBonus),("Mind",k.Mind+c.MindBonus),("Move",k.Move)}) AddPair(panel,label,value.ToString());
        }
        AddSection(panel,"Bonus over class base");
        foreach(var (label,value) in new[]{("HP",c.HpBonus),("Strength",c.StrengthBonus),("Defense",c.DefenseBonus),("Agility",c.AgilityBonus),("Mind",c.MindBonus)}) AddPair(panel,label,CharacterRecord.Signed(value));
        if(c.WeaponLevels.Count>0){AddSection(panel,"Weapon levels");foreach(var w in c.WeaponLevels)AddPair(panel,w.Weapon,CharacterRecord.WeaponLevel(w.Value));}
        AddSection(panel,"Personal skills");panel.Children.Add(new TextBlock {Text=c.Skills.Count==0?"None":string.Join(", ",c.Skills),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
        AddSection(panel,"Items");
        if(c.Items.Count==0)panel.Children.Add(new TextBlock {Text="None"});
        else foreach(var i in c.Items)AddPair(panel,i.Name,i.Durability.ToString());
        AddSection(panel,"Dropped when defeated");
        panel.Children.Add(new TextBlock {Text=row.Drops.Length==0?"Nothing":row.Drops,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
        AddChangeList(panel,ch=>ch.Table==EditSession.ArmyUnits && ch.Id==row.Unit.Key,()=>session!.Revert(EditSession.ArmyUnits,row.Unit.Key));
    }
    private void BeginArmyUnitEdit(object sender,RoutedEventArgs e)
    {
        if(ArmyGrid.SelectedItem is not ArmyUnitRow row)return;
        var fields=ArmyData.Fields;
        OpenEditor(new(View.Armies,row.Unit.Key,"Unit",ArmyUnitEditorPanel,ArmyUnitFields,EditArmyUnitButton,
        [
            new("Unit record",EditSession.ArmyUnits,fields.Where(f=>f.Group.Length==0).ToList()),
            new("Weapon levels",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Weapon levels").ToList()),
            new("Personal skills",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Personal skills").ToList()),
            new("Items",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Items").ToList()),
            new("Dropped when defeated",EditSession.ArmyUnits,fields.Where(f=>f.Group=="Drops").ToList()),
        ],"This unit's changes apply wherever this army is used. A class change affects only this unit; its stats become the new class's base plus its own bonus."));
    }
    private void ApplyCharacterFilter()
    {
        if(!ready)return;
        string query=CharacterSearch.Text.Trim();CharacterSearchHint.Visibility=query.Length==0?Visibility.Visible:Visibility.Collapsed;
        int? selected=(CharacterGrid.SelectedItem as CharacterRow)?.Character.Id;
        var filtered=characterRows.Where(r=>(CharacterDifferencesOnly.IsChecked!=true || comparison is null || r.Differences.Count>0)
            && (query.Length==0 || r.Character.Name.Contains(query,StringComparison.OrdinalIgnoreCase)
                || r.Character.IdText.Equals(query,StringComparison.OrdinalIgnoreCase) || r.Character.Id.ToString()==query)).ToList();
        CharacterGrid.ItemsSource=filtered;
        NoCharacters.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
        CharacterCount.Text=$"{filtered.Count:N0} of {characterRows.Count:N0} characters";
        if(filtered.Count>0) CharacterGrid.SelectedItem=filtered.FirstOrDefault(r=>r.Character.Id==selected)??filtered[0];
        else ClearCharacter();
    }
    private void CharacterFilterChanged(object sender,TextChangedEventArgs e)=>ApplyCharacterFilter();
    private void CharacterOptionChanged(object sender,RoutedEventArgs e)=>ApplyCharacterFilter();
    private void ClearCharacter()
    {
        if(CharacterName is null)return;CharacterName.Text="Select a character";CharacterIdentity.Text="";CharacterFields.Children.Clear();CharacterComparisonDetail.Text="";
    }
    private void CharacterSelected(object sender,SelectionChangedEventArgs e)
    {
        if(CharacterGrid.SelectedItem is not CharacterRow row){ClearCharacter();return;}
        var c=row.Character;
        if(editing?.Page==View.Characters && editing.Id!=c.Id) EndEdit();
        UpdateEditButtons();
        CharacterName.Text=c.Name;
        CharacterIdentity.Text=string.Join(" · ",new[]{c.IdText,c.ClassName,$"Level {c.Level}",$"Luck {c.Luck}"}.Concat(c.WeaponLevels.Select(w=>$"{w.Weapon} {CharacterRecord.WeaponLevel(w.Value)}")));
        CharacterFields.Children.Clear();
        if(classLookup.TryGetValue(c.ClassId,out var k))
        {
            Section("Base stats");
            foreach(var (label,value) in new[]{("HP",k.Hp+c.HpBonus),("Strength",k.Strength+c.StrengthBonus),("Defense",k.Defense+c.DefenseBonus),("Agility",k.Agility+c.AgilityBonus),("Mind",k.Mind+c.MindBonus),("Move",k.Move)}) Pair(label,value.ToString());
        }
        Section("Bonus over class base");
        foreach(var (label,value) in new[]{("HP",c.HpBonus),("Strength",c.StrengthBonus),("Defense",c.DefenseBonus),("Agility",c.AgilityBonus),("Mind",c.MindBonus)}) Pair(label,CharacterRecord.Signed(value));
        Section("Growth rates");
        foreach(var (label,value) in new[]{("HP",c.HpGrowth),("Strength",c.StrengthGrowth),("Defense",c.DefenseGrowth),("Agility",c.AgilityGrowth)}) Pair(label,$"{value}%");
        Pair("Mind (stored value)",c.MindGrowth.ToString());
        Pair("Growth bracket",c.BracketText);
        if(c.WeaponGrowths.Count>0){Section("Weapon growth");foreach(var w in c.WeaponGrowths)Pair(w.Weapon,$"{w.Value}%");}
        if(classLookup.TryGetValue(c.ClassId,out var cls))ExpectedStats(c,cls);
        Section("Personal skills");Text(c.Skills.Count==0?"None":string.Join(", ",c.Skills));
        Section("Learned on level up");
        if(c.Learned.Count==0)Text("None");else foreach(var l in c.Learned)Pair(l.Skill,$"Level {l.Level}");
        Section("Starting items");
        if(c.Items.Count==0)Text("None");else foreach(var i in c.Items)Pair(i.Name,i.Durability.ToString());
        if(primary?.BagBytes.TryGetValue(c.Id,out var bag)==true)
        {
            Section("Bag contents");
            int count=BagData.Count(bag.Span);bool any=false;
            for(int slot=0;slot<count;slot++)
            {
                int item=CharacterValue(EditSession.Bags,c.Id,BagData.ItemKey(slot));if(item==0)continue;any=true;
                Pair(DescribeValue(BagData.Fields.First(f=>f.Key==BagData.ItemKey(slot)),item),CharacterValue(EditSession.Bags,c.Id,BagData.UsesKey(slot)).ToString());
            }
            if(!any)Text("Empty");
        }
        if(primary?.PartnerBytes.ContainsKey(c.Id)==true)
        {
            Section("Partners");bool any=false;
            for(int slot=0;slot<PartnerData.Slots;slot++)
            {
                int partner=CharacterValue(EditSession.Partners,c.Id,PartnerData.PartnerKey(slot));if(partner==0)continue;any=true;
                int bond=CharacterValue(EditSession.Partners,c.Id,PartnerData.BondKey(slot));
                Pair(primary.Characters.FirstOrDefault(p=>p.Id==partner)?.Name??$"Character 0x{partner:X3}",bond/10>0?$"Bond {bond}, +{bond/10} hit":$"Bond {bond}");
            }
            if(!any)Text("None");
        }
        if(primary?.MercenaryBytes.ContainsKey(c.Id)==true)
        {
            Section("Mercenary fee");
            int baseFee=CharacterValue(EditSession.Mercenaries,c.Id,"mercenaryBase"),perLevel=CharacterValue(EditSession.Mercenaries,c.Id,"mercenaryPerLevel");
            Pair("Base fee",baseFee.ToString("N0"));Pair("Fee per level",perLevel.ToString("N0"));
            if(baseFee==0 && perLevel==0)Text("The game picks a random fee.");
            else Pair($"At level {c.Level}",MercenaryData.Fee(baseFee,perLevel,c.Level).ToString("N0"));
        }
        AddChangeList(CharacterFields,ch=>ch.Id==c.Id && CharacterTables.Contains(ch.Table),()=>session!.Revert(CharacterTables.Where(t=>session.IsEdited(t,c.Id)).Select(t=>(t,c.Id)).ToArray()));
        CharacterComparisonDetail.Text=!row.HasComparison?"":row.Differences.Count==0?"This character's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    private void ApplyClassFilter()
    {
        if(!ready)return;
        string query=ClassSearch.Text.Trim();ClassSearchHint.Visibility=query.Length==0?Visibility.Visible:Visibility.Collapsed;
        int? selected=(ClassGrid.SelectedItem as ClassRow)?.Class.Id;
        var filtered=classRows.Where(r=>(StartingClassesOnly.IsChecked!=true || r.StartingCharacters.Count>0)
            && (ClassDifferencesOnly.IsChecked!=true || comparison is null || r.Differences.Count>0)
            && (query.Length==0 || r.Class.Name.Contains(query,StringComparison.OrdinalIgnoreCase)
                || r.Class.IdText.Equals(query,StringComparison.OrdinalIgnoreCase) || r.Class.Id.ToString()==query)).ToList();
        ClassGrid.ItemsSource=filtered;
        NoClasses.Visibility=filtered.Count==0?Visibility.Visible:Visibility.Collapsed;
        ClassCount.Text=$"{filtered.Count:N0} of {classRows.Count:N0} classes";
        if(filtered.Count>0) ClassGrid.SelectedItem=filtered.FirstOrDefault(r=>r.Class.Id==selected)??filtered[0];
        else ClearClass();
    }
    private void ClassFilterChanged(object sender,TextChangedEventArgs e)=>ApplyClassFilter();
    private void ClassOptionChanged(object sender,RoutedEventArgs e)=>ApplyClassFilter();
    private void ClearClass()
    {
        if(ClassName is null)return;ClassName.Text="Select a class";ClassIdentity.Text="";ClassFields.Children.Clear();ClassComparisonDetail.Text="";
    }
    private void ClassSelected(object sender,SelectionChangedEventArgs e)
    {
        if(ClassGrid.SelectedItem is not ClassRow row){ClearClass();return;}
        var k=row.Class;
        if(editing?.Page==View.Classes && editing.Id!=k.Id) EndEdit();
        UpdateEditButtons();
        ClassName.Text=k.Name;ClassIdentity.Text=$"{k.IdText} · {k.MovementTypeText} movement · {k.MountText}";
        ClassFields.Children.Clear();
        AddSection(ClassFields,"Base stats");
        foreach(var (label,value) in new[]{("HP",k.Hp),("Strength",k.Strength),("Defense",k.Defense),("Agility",k.Agility),("Mind",k.Mind),("Move",k.Move),("Experience value",k.Experience)}) AddPair(ClassFields,label,value.ToString());
        AddSection(ClassFields,"Class skills");AddText(ClassFields,k.SkillText);
        AddSection(ClassFields,"Weapon level caps");
        if(k.WeaponCaps.Count==0)AddText(ClassFields,"None");else foreach(var w in k.WeaponCaps)AddPair(ClassFields,w.Weapon,w.Value.ToString());
        AddSection(ClassFields,"Shield sizes");AddText(ClassFields,k.ShieldSizeText);
        AddSection(ClassFields,"Characters starting in this class");AddText(ClassFields,row.StartingCharacters.Count==0?"None":string.Join(", ",row.StartingCharacters));
        AddChangeList(ClassFields,ch=>ch.Id==k.Id && ch.Table==EditSession.Classes,()=>session!.Revert(EditSession.Classes,k.Id));
        ClassComparisonDetail.Text=!row.HasComparison?"":row.Differences.Count==0?"This class's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    // Live game: the running game in PCSX2, reached through PINE on this computer.
    private LiveGame? live;
    private List<LiveUnitRow> liveRows=[];
    private static readonly TableField LiveHp=new("currentHp","Current HP",20,6,7,0,127);
    private void SetLiveStatus(string text){LiveStatus.Text=text;}
    private void UpdateLiveButtons()
    {
        bool on=live is not null;
        LiveConnectButton.Content=on?"Disconnect":"Connect";
        LiveRefreshButton.IsEnabled=on;LiveUndoButton.IsEnabled=on;
        LiveBattleBox.IsEnabled=on && LiveBattleBox.Items.Count>0;LiveSendButton.IsEnabled=LiveBattleBox.IsEnabled;LiveOwnBattlesButton.IsEnabled=on && live!.SortiesChanged;
        LiveRulesButton.IsEnabled=on && session is not null && session.Changes.Any(c=>c.Table==EditSession.Rules);
    }
    private void LiveConnect(object sender,RoutedEventArgs e)
    {
        if(live is not null){EndLive();return;}
        if(primary is null || !primary.Supported)return;
        try
        {
            var game=LiveGame.Connect();
            string id=game.GameId();
            if(!id.Equals(primary.Serial,StringComparison.OrdinalIgnoreCase)){game.Dispose();SetLiveStatus($"PCSX2 is running {(id.Length>0?id:"no game")}, not Berwick Saga.");return;}
            byte[] exe;IReadOnlyDictionary<string,string> titles;
            using(var stream=new FileStream(primary.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var iso=new IsoImage(stream))
            {
                var entry=primary.ExecutableFile!;
                exe=iso.ReadRange(entry.Offset,(int)entry.Length,(int)entry.Length);
                titles=ArmyData.LoadBattleTitles(new GameArchives(iso));
            }
            if(!game.MatchesProgram(exe)){game.Dispose();SetLiveStatus($"PCSX2 is running a different edition of Berwick Saga than the opened disc ({primary.Edition}). Open the matching disc first.");return;}
            live=game;SetLiveStatus($"Connected to Berwick Saga ({primary.Edition}) in PCSX2.");
            LiveBattleBox.ItemsSource=LiveGame.Battles(exe,titles).Select(b=>new Choice(b.Battle,$"{b.Text} (battle {b.Battle})")).ToList();LiveBattleBox.SelectedIndex=0;
            LiveRefresh(this,new());
        }
        catch(IOException ex){SetLiveStatus(ex.Message);}
        UpdateLiveButtons();
    }
    private void EndLive()
    {
        if(live is null)return;
        if(live.ChangedWords>0 && MessageBox.Show(this,"Put back everything Berwick Workshop changed in the running game before disconnecting?","Disconnect",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            try{live.UndoAll();}catch(IOException){}
        live.Dispose();live=null;liveRows=[];LiveGrid.ItemsSource=null;LiveBattleBox.ItemsSource=null;LiveEditor.Children.Clear();LiveUnitName.Text="Select a unit";
        NoLiveUnits.Text="Connect to see the units on the current map.";NoLiveUnits.Visibility=Visibility.Visible;LiveCount.Text="";SetLiveStatus("Not connected.");UpdateLiveButtons();
    }
    private void LiveRefresh(object sender,RoutedEventArgs e)
    {
        if(live is null)return;
        try
        {
            int? selected=(LiveGrid.SelectedItem as LiveUnitRow)?.Unit.Address is uint a?(int)a:null;
            var classes=(session?.EditedClasses()??primary!.Classes).ToDictionary(k=>k.Id,k=>k.Name);
            liveRows=live.Units(ArmyData.Names).Select(u=>new LiveUnitRow(u,classes.TryGetValue(u.ClassId,out var n)?n:$"Class 0x{u.ClassId:X2}")).ToList();
            LiveGrid.ItemsSource=liveRows;
            NoLiveUnits.Visibility=liveRows.Count==0?Visibility.Visible:Visibility.Collapsed;
            NoLiveUnits.Text="No units found. Start or continue a battle in the game, then refresh.";
            LiveCount.Text=liveRows.Count==0?"":$"{liveRows.Count} units on the map";
            if(liveRows.Count>0) LiveGrid.SelectedItem=liveRows.FirstOrDefault(r=>(int)r.Unit.Address==selected)??liveRows[0];
        }
        catch(IOException ex){SetLiveStatus(ex.Message);}
    }
    private void LiveUnitSelected(object sender,SelectionChangedEventArgs e)
    {
        LiveEditor.Children.Clear();
        if(live is null || LiveGrid.SelectedItem is not LiveUnitRow row){LiveUnitName.Text="Select a unit";return;}
        LiveUnitName.Text=row.Unit.Name;
        byte[] record;IReadOnlyList<LiveItem> items;
        try{record=live.ReadRecord(row.Unit);items=live.Items(row.Unit);}
        catch(IOException ex){SetLiveStatus(ex.Message);return;}
        var panel=LiveEditor;
        panel.Children.Add(new TextBlock {Text=$"{row.Side} unit at {row.Position}",Foreground=(Brush)FindResource("Muted"),FontSize=12,Margin=new Thickness(0,0,0,8)});
        var inputs=new List<(TableField Field,Control Input)>();
        TextBlock Label(string t)=>new() {Text=t,Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,0,0,4)};
        void Number(TableField field)
        {
            var grid=new Grid {Margin=new Thickness(0,0,0,6)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(72)});
            grid.Children.Add(new TextBlock {Text=field.Label,Foreground=(Brush)FindResource("Muted"),FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
            var box=new TextBox {Text=FieldCodec.Format(field,FieldCodec.Read(record,field)),Padding=new Thickness(6,3,6,3),HorizontalContentAlignment=HorizontalAlignment.Right,ToolTip=FieldCodec.RangeText(field)};
            System.Windows.Automation.AutomationProperties.SetName(box,field.Label);
            Grid.SetColumn(box,1);grid.Children.Add(box);panel.Children.Add(grid);inputs.Add((field,box));
        }
        var classField=CoreCharacterFields.Record.First(f=>f.Key=="class");
        panel.Children.Add(Label("Class"));
        var classChoices=ClassChoices();int currentClass=FieldCodec.Read(record,classField);
        if(!classChoices.Any(c=>c.Value==currentClass)) classChoices.Add(new(currentClass,$"Class 0x{currentClass:X2}"));
        var classBox=new ComboBox {ItemsSource=classChoices,DisplayMemberPath="Text",SelectedValuePath="Value",SelectedValue=currentClass,IsTextSearchEnabled=true,Margin=new Thickness(0,0,0,8)};
        classBox.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        System.Windows.Automation.AutomationProperties.SetName(classBox,"Class");
        panel.Children.Add(classBox);inputs.Add((classField,classBox));
        Number(CoreCharacterFields.Record.First(f=>f.Key=="level"));Number(LiveHp);
        foreach(var f in CoreCharacterFields.Record.Where(f=>f.Group.Length==0 && f.Key.EndsWith("Bonus",StringComparison.Ordinal))) Number(f);
        AddSection(panel,"Weapon levels");
        foreach(var f in CoreCharacterFields.Record.Where(f=>f.Group=="Weapon levels")) Number(f);
        AddSection(panel,"Skills");
        var skillGrid=new System.Windows.Controls.Primitives.UniformGrid {Columns=2,Margin=new Thickness(0,0,0,8)};
        foreach(var f in CoreCharacterFields.Record.Where(f=>f.Group=="Personal skills"))
        {
            var box=new CheckBox {Content=f.Label,IsChecked=FieldCodec.Read(record,f)!=0,Margin=new Thickness(0,0,6,4),FontSize=12};
            skillGrid.Children.Add(box);inputs.Add((f,box));
        }
        panel.Children.Add(skillGrid);
        AddSection(panel,"Horse");
        int oldHorse=FieldCodec.Read(record,LiveHorse.Item),oldHorseHp=FieldCodec.Read(record,LiveHorse.Hp),oldHorseMax=FieldCodec.Read(record,LiveHorse.MaxHp);
        var horseItems=(session?.EditedItems()??[]).Where(i=>LiveHorse.IsHorse(i.Id)).ToList();
        var horseChoices=new List<Choice>{new(0,"No horse")};horseChoices.AddRange(horseItems.Select(i=>new Choice(i.Id,i.DisplayName)));
        if(oldHorse!=0 && !horseChoices.Any(c=>c.Value==oldHorse)) horseChoices.Add(new(oldHorse,$"Item 0x{oldHorse:X3}"));
        var horseBox=new ComboBox {ItemsSource=horseChoices,DisplayMemberPath="Text",SelectedValuePath="Value",SelectedValue=oldHorse,Margin=new Thickness(0,0,0,6)};
        System.Windows.Automation.AutomationProperties.SetName(horseBox,"Horse");
        panel.Children.Add(horseBox);
        TextBox HorseNumber(string label,int value)
        {
            var grid=new Grid {Margin=new Thickness(0,0,0,6)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(72)});
            grid.Children.Add(new TextBlock {Text=label,Foreground=(Brush)FindResource("Muted"),FontSize=13,VerticalAlignment=VerticalAlignment.Center});
            var box=new TextBox {Text=value.ToString(),Padding=new Thickness(6,3,6,3),HorizontalContentAlignment=HorizontalAlignment.Right,ToolTip="0 to 1023"};
            System.Windows.Automation.AutomationProperties.SetName(box,label);
            Grid.SetColumn(box,1);grid.Children.Add(box);panel.Children.Add(grid);return box;
        }
        var horseHp=HorseNumber("Horse HP",oldHorseHp);var horseMax=HorseNumber("Horse most HP",oldHorseMax);
        horseBox.SelectionChanged+=(_,_)=>
        {
            if(horseBox.SelectedValue is not int h || h==oldHorse) return;
            int most=h==0?0:(horseItems.FirstOrDefault(i=>i.Id==h)?.Uses??0)*10;
            horseMax.Text=most.ToString();horseHp.Text=most.ToString();
        };
        bool mounted=(session?.EditedClasses().FirstOrDefault(c=>c.Id==currentClass)?.Mount??0)==1;
        panel.Children.Add(new TextBlock {Text=mounted?"Riding now. A different horse can be given; to take the horse away, choose Dismount in the game first.":"On foot. After a horse is given, the unit can choose Mount in the game.",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=11,Margin=new Thickness(0,0,0,8)});
        AddSection(panel,"Items");
        var itemChoices=ItemChoices();
        var itemInputs=new List<(int Slot,ComboBox Item,TextBox Uses,LiveItem Old)>();
        foreach(var item in items)
        {
            var options=itemChoices.ToList();if(!options.Any(c=>c.Value==item.ItemId))options.Add(new(item.ItemId,$"Item 0x{item.ItemId:X3}"));
            var pick=new ComboBox {ItemsSource=options,DisplayMemberPath="Text",SelectedValuePath="Value",SelectedValue=item.ItemId,IsTextSearchEnabled=true};
            pick.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            var uses=new TextBox {Text=item.Uses.ToString(),Width=52,Padding=new Thickness(4,3,4,3),HorizontalContentAlignment=HorizontalAlignment.Right,Margin=new Thickness(6,0,0,0),ToolTip="Durability or uses, 0 to 255"};
            System.Windows.Automation.AutomationProperties.SetName(pick,$"Slot {item.Slot+1}");System.Windows.Automation.AutomationProperties.SetName(uses,$"Slot {item.Slot+1} uses");
            var line=new DockPanel {Margin=new Thickness(0,0,0,6)};DockPanel.SetDock(uses,Dock.Right);line.Children.Add(uses);line.Children.Add(pick);
            panel.Children.Add(line);itemInputs.Add((item.Slot,pick,uses,item));
        }
        var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,4,0,4)};
        panel.Children.Add(error);
        var apply=new Button {Content="Apply to the game",Style=(Style)FindResource("SmallButton"),HorizontalAlignment=HorizontalAlignment.Left,Background=(Brush)FindResource("Accent"),Foreground=Brushes.White,BorderBrush=(Brush)FindResource("Accent")};
        apply.Click+=(_,_)=>
        {
            error.Text="";
            var edited=record.ToArray();
            foreach(var (field,input) in inputs)
            {
                int value;
                if(input is CheckBox box) value=box.IsChecked==true?1:0;
                else if(input is ComboBox combo){if(combo.SelectedValue is not int v){error.Text=$"Choose a value for {field.Label}.";return;}value=v;}
                else if(!FieldCodec.TryParse(field,((TextBox)input).Text,out value,out string message)){error.Text=message;input.Focus();return;}
                FieldCodec.Write(edited,field,value);
            }
            if(horseBox.SelectedValue is not int horse){error.Text="Choose a horse, or No horse.";return;}
            if(!int.TryParse(horseHp.Text.Trim(),out int hHp)){error.Text="Horse HP must be a whole number.";horseHp.Focus();return;}
            if(!int.TryParse(horseMax.Text.Trim(),out int hMax)){error.Text="Horse most HP must be a whole number.";horseMax.Focus();return;}
            if(horse!=oldHorse || hHp!=oldHorseHp || hMax!=oldHorseMax)
            {
                if(LiveHorse.Apply(edited,horse,hHp,hMax,mounted) is { } horseError){error.Text=horseError;return;}
            }
            var itemEdits=new List<(int Slot,int Item,int Uses)>();
            foreach(var (slot,pick,usesBox,old) in itemInputs)
            {
                if(pick.SelectedValue is not int id){error.Text=$"Choose an item for slot {slot+1}.";return;}
                if(!int.TryParse(usesBox.Text.Trim(),out int uses) || uses is <0 or >255){error.Text=$"Slot {slot+1}: uses must be a whole number from 0 to 255.";usesBox.Focus();return;}
                if(id!=old.ItemId || uses!=old.Uses) itemEdits.Add((slot,id,uses));
            }
            try
            {
                live.WriteRecord(row.Unit,edited);
                foreach(var (slot,id,uses) in itemEdits) live.SetItem(row.Unit,slot,id,uses);
                SetLiveStatus($"{row.Unit.Name} changed in the running game. Undo all changes puts it back.");
                LiveRefresh(this,new());
            }
            catch(IOException ex){error.Text=ex.Message;}
        };
        panel.Children.Add(apply);
        panel.Children.Add(new TextBlock {Text="Some changes show at once; class and stats show the next time the game recalculates them, for example when you open the unit's screen again or start a fight.",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=11,Margin=new Thickness(0,8,0,0)});
    }
    private void LiveTryRules(object sender,RoutedEventArgs e)
    {
        if(live is null || session is null)return;
        int written=0,skipped=0;
        try
        {
            foreach(var id in session.Changes.Where(c=>c.Table==EditSession.Rules).Select(c=>c.Id).Distinct())
            {
                var rule=GameRules.ById(id);
                if(live.WriteRule(rule,session.Value(EditSession.Rules,id,rule.Key))) written++; else skipped++;
            }
            SetLiveStatus(skipped==0?$"{written} rules from this project are now active in the running game. Undo all changes puts the game's own rules back."
                :$"{written} rules are now active in the running game; {skipped} were left alone because the game has different code at that point.");
        }
        catch(IOException ex){SetLiveStatus(ex.Message);}
    }
    private void LiveSendSorties(object sender,RoutedEventArgs e)
    {
        if(live is null || LiveBattleBox.SelectedItem is not Choice choice)return;
        try
        {
            int n=live.SendSorties(choice.Value);
            SetLiveStatus($"The next sortie loads {choice.Text}, whichever mission or quest you start ({n} sorties changed). It takes effect from the base; a battle already on the map stays as it is.");
        }
        catch(IOException ex){SetLiveStatus(ex.Message);}
        UpdateLiveButtons();
    }
    private void LiveOwnBattles(object sender,RoutedEventArgs e)
    {
        if(live is null)return;
        try{live.RestoreSorties();SetLiveStatus("Every mission and quest loads its own battle again.");}
        catch(IOException ex){SetLiveStatus(ex.Message);}
        UpdateLiveButtons();
    }
    private void LiveUndo(object sender,RoutedEventArgs e)
    {
        if(live is null)return;
        try
        {
            int n=live.ChangedWords;int left=live.UndoAll();
            SetLiveStatus(n==0?"Nothing to undo.":left==0?"Everything Berwick Workshop changed in the running game has been put back."
                :$"Changes put back, except {left} the game has changed again since (for example HP after a fight, or items used); those were left as they are.");
            LiveRefresh(this,new());
        }
        catch(IOException ex){SetLiveStatus(ex.Message);}
        UpdateLiveButtons();
    }
    private byte[] DishBytes(int id)=>session?.EditedBytes(EditSession.Dishes,id)??primary!.DishBytes[id].ToArray();
    private void ApplyDishList()
    {
        if(primary is null || primary.DishBytes.Count==0){DishGrid.ItemsSource=null;return;}
        int? selected=(DishGrid.SelectedItem as DishRow)?.Id;
        var rows=primary.DishBytes.Keys.Order().Select(id=>
        {
            var b=DishBytes(id);
            return new DishRow(id,DishData.Name(id),System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8)),System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4)),DishData.Describe(b,0),session?.IsEdited(EditSession.Dishes,id)==true);
        }).ToList();
        DishGrid.ItemsSource=rows;
        DishCount.Text=$"{rows.Count} dishes";
        if(rows.Count>0)DishGrid.SelectedItem=rows.FirstOrDefault(r=>r.Id==selected)??rows[0];
    }
    private void DishSelected(object sender,SelectionChangedEventArgs e)
    {
        UpdateEditButtons();
        if(DishGrid.SelectedItem is not DishRow row){DishName.Text="Select a dish";DishFields.Children.Clear();return;}
        if(editing?.Page==View.Dishes && editing.Id!=row.Id) EndEdit();
        DishName.Text=row.Name;DishFields.Children.Clear();
        var b=DishBytes(row.Id);
        AddPair(DishFields,"Price",$"{row.PriceText} (the menu rounds down to tens; as the daily special, 20% less, or 30% less after the arena quest)");
        AddPair(DishFields,"Menu tier",$"{row.Tier} (the random menu takes 1, 2, 3, 2 and 1 dishes from tiers 0 to 4)");
        int kind=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b);AddPair(DishFields,"Kind",kind==4?"4 (never on the menu)":kind.ToString());
        for(int s=0;s<DishData.Sets;s++)AddPair(DishFields,DishData.SetNames[s],DishData.Describe(b,s));
        var names=(session?.EditedCharacters()??primary!.Characters).ToDictionary(c=>c.Id,c=>c.Name);
        string Who(int choice)=>string.Join(", ",primary!.TasteBytes.Keys.Where(c=>TasteValue(c,row.Id)==choice).Select(c=>names.TryGetValue(c,out var n)?n:$"Character 0x{c:X3}").Order());
        AddPair(DishFields,"Liked by",Who(0) is {Length:>0} liked?liked:"No one");
        AddPair(DishFields,"Disliked by",Who(2) is {Length:>0} disliked?disliked:"No one");
        AddText(DishFields,"Characters not listed find it neutral. A unit with no taste row, such as most mercenaries and guests, gets no effects from any dish.");
        AddChangeList(DishFields,ch=>ch.Table==EditSession.Dishes && ch.Id==row.Id,()=>session!.Revert(EditSession.Dishes,row.Id));
    }
    private int TasteValue(int character,int dish)=>session?.Value(EditSession.Tastes,character,TasteData.Key(dish))
        ??System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(primary!.TasteBytes[character].Span[(dish*4)..]);
    private void BeginDishEdit(object sender,RoutedEventArgs e)
    {
        if(DishGrid.SelectedItem is not DishRow row)return;
        OpenEditor(new(View.Dishes,row.Id,"Dish",DishEditorPanel,DishFields,EditDishButton,
        [
            new("",EditSession.Dishes,DishData.Fields.Where(f=>f.Group=="Dish").ToList()),
            new("Liked",EditSession.Dishes,DishData.Fields.Where(f=>f.Group=="Liked").ToList()),
            new("Neutral",EditSession.Dishes,DishData.Fields.Where(f=>f.Group=="Neutral").ToList()),
            new("Disliked",EditSession.Dishes,DishData.Fields.Where(f=>f.Group=="Disliked").ToList()),
        ],"Amounts apply in the next battle. An effect set to None is left out. The random menu takes dishes by menu tier and never offers kind 4, the drinks."));
    }
    private void ApplyFurnitureList()
    {
        if(!ready || primary is null)return;
        int? selected=(FurnitureGrid.SelectedItem as FurnitureRow)?.Id;
        var field=FurniturePrices.Table.Field;
        var rows=primary.FurniturePriceBytes.Keys.Order().Select(id=>
        {
            int original=FieldCodec.Read(primary.FurniturePriceBytes[id].Span,field);
            return new FurnitureRow(id,FurniturePrices.Names.TryGetValue(id,out var n)?n:$"Piece {id}",session?.Value(EditSession.FurniturePrices,id,field.Key)??original,original);
        }).ToList();
        FurnitureGrid.ItemsSource=rows;
        FurnitureCount.Text=$"{rows.Count} pieces of furniture";
        if(rows.Count>0) FurnitureGrid.SelectedItem=rows.FirstOrDefault(r=>r.Id==selected)??rows[0];
    }
    private void FurnitureSelected(object sender,SelectionChangedEventArgs e)
    {
        if(FurnitureGrid.SelectedItem is not FurnitureRow row){FurnitureName.Text="Select a piece";FurnitureFields.Children.Clear();return;}
        if(editing?.Page==View.Furniture && editing.Id!=row.Id) EndEdit();
        UpdateEditButtons();
        FurnitureName.Text=row.Name;FurnitureFields.Children.Clear();
        if(row.Edited) AddPair(FurnitureFields,"Price on the disc",row.Original.ToString("N0"));
        AddPair(FurnitureFields,"Price",row.PriceText);
        if(FurniturePrices.Effects.TryGetValue(row.Id,out var effect))
        {
            if(FurniturePrices.EffectRules.TryGetValue(row.Id,out int ruleId))
            {
                int value=RuleSetting(ruleId);
                effect=string.Format(effect,ruleId==250?(value==1?"Reese has Miracle: a hit that would defeat him is rolled again at 10 less hit.":"No effect (turned off on the Game rules page)."):value.ToString());
            }
            AddPair(FurnitureFields,"While shown in Reese's room",effect);
        }
        AddChangeList(FurnitureFields,ch=>ch.Table==EditSession.FurniturePrices && ch.Id==row.Id,()=>session!.Revert(EditSession.FurniturePrices,row.Id));
    }
    private void BeginFurnitureEdit(object sender,RoutedEventArgs e)
    {
        if(FurnitureGrid.SelectedItem is not FurnitureRow row)return;
        OpenEditor(new(View.Furniture,row.Id,"Price",FurnitureEditorPanel,FurnitureFields,EditFurnitureButton,
            [new("",EditSession.FurniturePrices,[FurniturePrices.Table.Field])],null));
    }
    private void ApplyTerrainList()
    {
        if(!ready)return;
        int? selected=(TerrainGrid.SelectedItem as TerrainKindRow)?.Kind.Id;
        TerrainGrid.ItemsSource=terrainRows;
        TerrainCount.Text=$"{terrainRows.Count} kinds of terrain";
        if(terrainRows.Count>0) TerrainGrid.SelectedItem=terrainRows.FirstOrDefault(r=>r.Kind.Id==selected)??terrainRows[0];
        else ClearTerrain();
    }
    private void ClearTerrain()
    {
        if(TerrainName is null)return;TerrainName.Text="Select a terrain";TerrainIdentity.Text="";TerrainFields.Children.Clear();
    }
    private void TerrainSelected(object sender,SelectionChangedEventArgs e)
    {
        if(TerrainGrid.SelectedItem is not TerrainKindRow row){ClearTerrain();return;}
        var k=row.Kind;
        if(editing?.Page==View.Terrain && editing.Id!=k.Id) EndEdit();
        TerrainName.Text=TerrainData.TerrainNames[k.Id];
        TerrainIdentity.Text=$"Terrain kind {k.Id}";
        TerrainFields.Children.Clear();
        AddSection(TerrainFields,"Effects");
        AddPair(TerrainFields,"Avoid bonus",k.Avoid.ToString());
        AddPair(TerrainFields,"Recovery while resting",k.RecoveryText);
        AddPair(TerrainFields,"Flyers can stop here",k.FlyersCannotStop?"No, they can only pass over":"Yes");
        AddSection(TerrainFields,"Cost to cross");
        foreach(var m in movementRows) AddPair(TerrainFields,m.Type.Name,m.Type.Cost(k.Id));
        AddChangeList(TerrainFields,ch=>ch.Id==k.Id && ch.Table==EditSession.TerrainKinds,()=>session!.Revert(EditSession.TerrainKinds,k.Id));
        UpdateEditButtons();
    }
    private void BeginTerrainEdit(object sender,RoutedEventArgs e)
    {
        if(TerrainGrid.SelectedItem is not TerrainKindRow row)return;
        OpenEditor(new(View.Terrain,row.Kind.Id,"Terrain",TerrainEditorPanel,TerrainFields,EditTerrainButton,
        [
            new("Effects",EditSession.TerrainKinds,TerrainKindData.Fields),
        ],"Changes apply to every hex of this terrain on every map, for enemies too. A flying unit can only stop where its movement type can also enter."));
    }
    private void ApplyMovementList()
    {
        if(!ready)return;
        int? selected=(MovementGrid.SelectedItem as MovementRow)?.Type.Id;
        MovementGrid.ItemsSource=movementRows;
        MovementCount.Text=$"{movementRows.Count} movement types, {TerrainData.Terrains} kinds of terrain";
        if(movementRows.Count>0) MovementGrid.SelectedItem=movementRows.FirstOrDefault(r=>r.Type.Id==selected)??movementRows[0];
        else ClearMovement();
    }
    private void ClearMovement()
    {
        if(MovementName is null)return;MovementName.Text="Select a movement type";MovementIdentity.Text="";MovementFields.Children.Clear();
    }
    private void MovementSelected(object sender,SelectionChangedEventArgs e)
    {
        if(MovementGrid.SelectedItem is not MovementRow row){ClearMovement();return;}
        var m=row.Type;
        if(editing?.Page==View.Movement && editing.Id!=m.Id) EndEdit();
        UpdateEditButtons();
        MovementName.Text=m.Name;
        MovementIdentity.Text=$"{m.MountText} · {(row.ClassCount==1?"1 class":$"{row.ClassCount} classes")}";
        MovementFields.Children.Clear();
        AddSection(MovementFields,"Cost per hex");
        int hidden=0;
        for(int t=0;t<TerrainData.Terrains;t++)
        {
            if(TerrainData.TerrainGroups[t]=="Blocked" && m.Costs[t]==TerrainData.Blocked){hidden++;continue;}
            AddPair(MovementFields,TerrainData.TerrainLabel(t),m.Cost(t));
        }
        if(hidden>0) AddText(MovementFields,$"Walls and doors ({hidden} kinds) cannot be entered. Edit to open them up.");
        AddSection(MovementFields,"Classes with this movement type");
        AddText(MovementFields,row.ClassCount==0?"None":string.Join(", ",row.Classes));
        AddChangeList(MovementFields,ch=>ch.Id==m.Id && ch.Table==EditSession.Terrain,()=>session!.Revert(EditSession.Terrain,m.Id));
    }
    private void AddSection(Panel panel,string title)=>panel.Children.Add(new TextBlock {Text=title,Style=(Style)FindResource("Caption"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
    private static void AddText(Panel panel,string text)=>panel.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
    private void AddPair(Panel panel,string label,string value)
    {
        var grid=new Grid {Margin=new Thickness(0,0,0,7)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        grid.Children.Add(new TextBlock {Text=label,Foreground=(Brush)FindResource("Muted"),FontSize=13,TextWrapping=TextWrapping.Wrap});
        var v=new TextBlock {Text=value,FontWeight=FontWeights.SemiBold,Margin=new Thickness(12,0,0,0)};Grid.SetColumn(v,1);grid.Children.Add(v);panel.Children.Add(grid);
    }
    private static readonly string[] CharacterTables=[EditSession.Characters,EditSession.Growths,EditSession.Partners,EditSession.Bags,EditSession.Mercenaries];
    private int CharacterValue(string table,int id,string key)
    {
        if(session is not null)return session.Value(table,id,key);
        var (records,fields)=table switch
        {
            EditSession.Partners=>(primary!.PartnerBytes,PartnerData.Fields),
            EditSession.Mercenaries=>(primary!.MercenaryBytes,MercenaryData.Fields),
            _=>(primary!.BagBytes,BagData.Fields),
        };
        return FieldCodec.Read(records[id].Span,fields.First(f=>f.Key==key));
    }
    private int RuleSetting(int id)
    {
        var rule=GameRules.ById(id);
        return session is not null && session.IsRuleAvailable(id)?session.Value(EditSession.Rules,id,rule.Key):rule.Default;
    }
    /// <summary>Expected stats at later levels: start value plus growth times levels gained, kept within the bonus limits.</summary>
    private void ExpectedStats(CharacterRecord c,ClassRecord k)
    {
        const int levelCap=30;
        var levels=Enumerable.Range(c.Level+1,Math.Max(0,levelCap-c.Level)).Where(l=>l%5==0 || l==c.Level+1 || l==levelCap).Distinct().ToList();
        if(levels.Count==0)return;
        Section("Expected stats by level");
        int bandRule=c.Bracket==2?236:c.Bracket==3?237:0;
        string band=bandRule==0?"This character has no growth band, so real stats can drift far from this."
            :$"The {(c.Bracket==2?"Loose":"Tight")} band keeps HP, Strength and Mind within {GameRules.RangeLow(RuleSetting(bandRule))} of this and Defense and Agility within {GameRules.RangeHigh(RuleSetting(bandRule))}.";
        Text($"Starting value plus growth rate times levels gained, in the current class, capped by the stat limits on the Rules page. {band}");
        var stats=new (string Name,int Start,int Growth,int Base,int Limit)[]
        {
            ("HP",k.Hp+c.HpBonus,c.HpGrowth,k.Hp,RuleSetting(3)),("Str",k.Strength+c.StrengthBonus,c.StrengthGrowth,k.Strength,RuleSetting(4)),
            ("Def",k.Defense+c.DefenseBonus,c.DefenseGrowth,k.Defense,RuleSetting(7)),("Agi",k.Agility+c.AgilityBonus,c.AgilityGrowth,k.Agility,RuleSetting(5)),
            ("Mind",k.Mind+c.MindBonus,c.MindGrowth/2,k.Mind,RuleSetting(8)),
        };
        var table=new Grid {Margin=new Thickness(0,4,0,4)};
        for(int col=0;col<=stats.Length;col++)table.ColumnDefinitions.Add(new ColumnDefinition{Width=col==0?new GridLength(1.4,GridUnitType.Star):new GridLength(1,GridUnitType.Star)});
        void Cell(int row,int col,string text,bool head)
        {
            var t=new TextBlock{Text=text,FontSize=13,Margin=new Thickness(0,0,0,5),HorizontalAlignment=col==0?HorizontalAlignment.Left:HorizontalAlignment.Right};
            if(head||col==0)t.Foreground=(Brush)FindResource("Muted");else t.FontWeight=FontWeights.SemiBold;
            Grid.SetRow(t,row);Grid.SetColumn(t,col);table.Children.Add(t);
        }
        table.RowDefinitions.Add(new RowDefinition());Cell(0,0,"Level",true);
        for(int i=0;i<stats.Length;i++)Cell(0,i+1,stats[i].Name,true);
        for(int r=0;r<levels.Count;r++)
        {
            table.RowDefinitions.Add(new RowDefinition());Cell(r+1,0,levels[r].ToString(),false);
            for(int i=0;i<stats.Length;i++)
            {
                var st=stats[i];
                double expected=Math.Min(st.Start+st.Growth*(levels[r]-c.Level)/100.0,st.Base+st.Limit);
                Cell(r+1,i+1,((int)Math.Round(expected,MidpointRounding.AwayFromZero)).ToString(),false);
            }
        }
        CharacterFields.Children.Add(table);
    }
    private void Section(string title)=>CharacterFields.Children.Add(new TextBlock {Text=title,Style=(Style)FindResource("Caption"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
    private void Text(string text)=>CharacterFields.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
    private void Pair(string label,string value)
    {
        var grid=new Grid {Margin=new Thickness(0,0,0,7)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        grid.Children.Add(new TextBlock {Text=label,Foreground=(Brush)FindResource("Muted"),FontSize=13,TextWrapping=TextWrapping.Wrap});
        var v=new TextBlock {Text=value,FontWeight=FontWeights.SemiBold,Margin=new Thickness(12,0,0,0)};Grid.SetColumn(v,1);grid.Children.Add(v);CharacterFields.Children.Add(grid);
    }
    private void RemoveComparison(object sender,RoutedEventArgs e)
    {
        comparison=null;OnlyDifferences.IsChecked=false;RefreshDocument();
    }
    private void Notify(string message) {MessageText.Text=message;MessageBar.Visibility=Visibility.Visible;}
    private void DismissMessage(object sender,RoutedEventArgs e)=>MessageBar.Visibility=Visibility.Collapsed;
    private void CancelLoad(object sender,RoutedEventArgs e)=>loading?.Cancel();
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(!closing && !ConfirmDiscard()){e.Cancel=true;return;}
        closing=true;loading?.Cancel();CloseEvents();EndLive();
    }

    // Editing and projects

    private string ProjectTitle => session?.FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : "New project";
    private void UpdateProjectBar()
    {
        ProjectPanel.Visibility=session is null?Visibility.Collapsed:Visibility.Visible;
        if(session is null){Title="Berwick Workshop";return;}
        ProjectName.Text=ProjectTitle;
        string count=session.ChangeCount switch { 0=>"No changes", 1=>"1 change", var n=>$"{n} changes" };
        ProjectState.Text=session.IsDirty?$"{count} · Not saved":count;
        UndoButton.IsEnabled=session.CanUndo && loading is null;RedoButton.IsEnabled=session.CanRedo && loading is null;
        ExportButton.IsEnabled=session.ChangeCount>0 && loading is null;
        Title=session.FilePath is null && !session.IsDirty?"Berwick Workshop":$"{ProjectTitle}{(session.IsDirty?" (not saved)":"")} · Berwick Workshop";
    }
    private bool ConfirmDiscard()
    {
        if(session?.IsDirty!=true)return true;
        var answer=MessageBox.Show(this,$"Save changes to {ProjectTitle} first?","Berwick Workshop",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        return answer switch { MessageBoxResult.Yes=>SaveProjectFile(false), MessageBoxResult.No=>true, _=>false };
    }
    private bool SaveProjectFile(bool chooseFile)
    {
        if(session is null)return false;
        string? path=session.FilePath;
        if(chooseFile || path is null)
        {
            var picker=new SaveFileDialog {Title="Save project",Filter="Berwick Workshop project (*.bwproj)|*.bwproj",DefaultExt=".bwproj",AddExtension=true,FileName=path is null?"Berwick project":Path.GetFileName(path)};
            picker.InitialDirectory=path is null?Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments):Path.GetDirectoryName(path);
            if(picker.ShowDialog(this)!=true)return false;
            path=picker.FileName;
        }
        try
        {
            if (comparison is { } other && string.Equals(Path.GetFullPath(path),Path.GetFullPath(other.Path),StringComparison.OrdinalIgnoreCase))
                throw new EditException("Choose a project file name. Open disc images are never overwritten.");
            session.Save(path,typeof(MainWindow).Assembly.GetName().Version?.ToString(3));
            UpdateProjectBar();Status.Text=$"Project saved. Your game files were not changed.";return true;
        }
        catch(EditException ex){Notify(ex.Message);return false;}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("The project could not be saved. Choose a folder you can write to.");return false;
        }
    }
    private void SaveProject(object sender,RoutedEventArgs e)=>SaveProjectFile(false);
    private void OpenProject(object sender,RoutedEventArgs e)
    {
        if(primary?.Supported!=true || loading is not null || !ConfirmDiscard())return;
        var picker=new OpenFileDialog {Title="Open project",Filter="Berwick Workshop project (*.bwproj)|*.bwproj",CheckFileExists=true};
        picker.InitialDirectory=session?.FilePath is { } current?Path.GetDirectoryName(current):Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(picker.ShowDialog(this)!=true)return;
        try
        {
            EndEdit();session=EditSession.Load(primary,picker.FileName);
            RefreshDocument();Status.Text=$"Project opened with {session.ChangeCount} changes.";
        }
        catch(EditException ex){Notify(ex.Message);}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){Notify("The project could not be opened. Check that the file is available.");}
    }
    private async void ExportDisc(object sender,RoutedEventArgs e)
    {
        if(loading is not null || primary?.Supported!=true || session is null || session.ChangeCount==0)return;
        if(editing is not null){Notify("Apply or cancel the changes you are editing before exporting.");return;}
        string folder=Path.GetDirectoryName(primary.Path)??Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var picker=new SaveFileDialog {Title="Export patched disc",OverwritePrompt=false,Filter="ISO disc images (*.iso)|*.iso",DefaultExt=".iso",AddExtension=true,InitialDirectory=folder,
            FileName=$"{Path.GetFileNameWithoutExtension(primary.Path)} ({ProjectTitle}).iso"};
        if(picker.ShowDialog(this)!=true)return;
        string target=Path.GetFullPath(picker.FileName);
        if(File.Exists(target)){Notify("Choose a new file name. Existing disc images are never overwritten.");return;}
        if(new[]{primary.Path,comparison?.Path}.Any(p=>p is not null && string.Equals(Path.GetFullPath(p),target,StringComparison.OrdinalIgnoreCase)))
        {Notify("Choose a new file name. Open disc images are never overwritten.");return;}
        int items=session.Changes.Select(c=>c.Id).Distinct().Count();
        string summary=$"Export a patched copy of {primary.Edition}?\n\nOriginal: {Path.GetFileName(primary.Path)} (not changed)\nNew file: {Path.GetFileName(target)}\nChanges: {session.ChangeCount} {(session.ChangeCount==1?"value":"values")} in {items} {(items==1?"item":"items")}\n\nThis writes a full copy of the disc, about {primary.Size/(1024.0*1024*1024):0.0} GB.";
        if(MessageBox.Show(this,summary,"Export disc",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
        await RunExport(target);
    }
    private async Task RunExport(string target)
    {
        if(loading is not null || primary?.Supported!=true || session is null)return;
        using var cancellation=new CancellationTokenSource();loading=cancellation;
        SetBusy(true);MessageBar.Visibility=Visibility.Collapsed;Progress.Value=0;Status.Text="Exporting disc";
        var progress=new Progress<InspectionProgress>(p=>
        {
            if(closing || loading!=cancellation)return;
            Progress.Value=p.Fraction*100;Status.Text=$"{p.Message} · {p.Fraction:P0}";
        });
        var disc=primary;var edits=session;
        try
        {
            var patches=Exporter.PatchesFor(edits,disc);
            var result=await Task.Run(()=>Exporter.Export(disc.Path,disc.Sha256,target,patches,progress,cancellation.Token));
            if(closing)return;
            Status.Text="Disc exported. The original disc image was not changed.";
            Notify($"Exported {Path.GetFileName(result.Path)}. Load it in PCSX2 to play with your changes, or open it here to check it.");
        }
        catch(OperationCanceledException){if(!closing)Status.Text="Export canceled. No file was written.";}
        catch(EditException ex){if(!closing){Notify(ex.Message);Status.Text="Disc not exported";}}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){if(!closing){Notify("The disc could not be exported. Check that the folder can be written to and that the original disc image is available.");Status.Text="Disc not exported";}}
        finally{loading=null;if(!closing){SetBusy(false);UpdateProjectBar();}}
    }
    private void UndoEdit(object sender,RoutedEventArgs e){if(session?.CanUndo!=true || loading is not null)return;EndEdit();session.Undo();RefreshDocument();Status.Text="Undone.";}
    private void RedoEdit(object sender,RoutedEventArgs e){if(session?.CanRedo!=true || loading is not null)return;EndEdit();session.Redo();RefreshDocument();Status.Text="Redone.";}
    private sealed record EditSection(string Title,string Table,IReadOnlyList<TableField> Fields);
    private sealed record Choice(int Value,string Text);
    private List<Choice> ClassChoices() =>
        (session?.EditedClasses()??[]).OrderBy(k=>k.Name).Select(k=>new Choice(k.Id,$"{k.Name} ({k.IdText})")).ToList();
    private List<Choice> ItemChoices() =>
        [new(0,"Empty"),..(session?.EditedItems()??[]).OrderBy(i=>i.IsUnused).ThenBy(i=>i.DisplayName).Select(i=>new Choice(i.Id,$"{i.DisplayName} ({i.IdText})"))];
    private void NewItem(object sender,RoutedEventArgs e)
    {
        if(session is null || loading is not null || primary is null)return;
        EndEdit();
        var free=session.FreeItemSlots();
        if(free.Count==0){Notify("There are no free item slots left on this disc.");return;}
        var items=session.EditedItems();
        var templates=items.Where(i=>!i.IsUnused).OrderBy(i=>i.DisplayName).Select(i=>new Choice(i.Id,$"{i.DisplayName} ({i.Category})")).ToList();
        string SlotText(int id){var placeholder=primary.Items.First(i=>i.Id==id);return $"{placeholder.IdText} (among {placeholder.Category} items)";}
        var slots=free.Select(id=>new Choice(id,SlotText(id))).ToList();

        var dialog=new Window {Title="New item",Owner=this,Width=480,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
        var panel=new StackPanel {Margin=new Thickness(24)};
        TextBlock Label(string text)=>new() {Text=text,Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,0,0,4)};
        var name=new TextBox {Margin=new Thickness(0,0,0,14)};
        var template=new ComboBox {ItemsSource=templates,DisplayMemberPath="Text",SelectedValuePath="Value",IsTextSearchEnabled=true,Padding=new Thickness(6,4,6,4),Margin=new Thickness(0,0,0,14)};
        template.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        var slot=new ComboBox {ItemsSource=slots,DisplayMemberPath="Text",SelectedValuePath="Value",Padding=new Thickness(6,4,6,4),Margin=new Thickness(0,0,0,14)};
        template.SelectionChanged+=(_,_)=>
        {
            if(template.SelectedValue is not int t)return;
            int type=items.First(i=>i.Id==t).Type;
            int match=free.FirstOrDefault(id=>primary.Items.First(i=>i.Id==id).Type==type);
            slot.SelectedValue=match!=0?match:free[0];
        };
        int start=ItemGrid.SelectedItem is ItemRow selected && !selected.Item.IsUnused?selected.Item.Id:templates[0].Value;
        template.SelectedValue=start;if(slot.SelectedValue is null)slot.SelectedValue=free[0];
        var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,10)};
        panel.Children.Add(Label("Name"));panel.Children.Add(name);
        panel.Children.Add(Label("Start from"));panel.Children.Add(template);
        panel.Children.Add(Label("Slot"));panel.Children.Add(slot);
        panel.Children.Add(new TextBlock {Text="The new item starts as a full copy of the item you choose, then you can edit it like any other item. It uses one of the disc's unused item slots. Your name is used in Berwick Workshop and saved in the project; the game shows the slot's own name until text editing is available.",
            TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=12,LineHeight=18,Margin=new Thickness(0,0,0,14)});
        panel.Children.Add(error);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var create=new Button {Content="Create",IsDefault=true,Style=(Style)FindResource("PrimaryButton")};
        create.Click+=(_,_)=>
        {
            try
            {
                if(template.SelectedValue is not int t || slot.SelectedValue is not int s){error.Text="Choose an item to start from and a slot.";return;}
                session.CreateItem(s,t,name.Text);
                dialog.DialogResult=true;
            }
            catch(EditException ex){error.Text=ex.Message;}
        };
        buttons.Children.Add(cancel);buttons.Children.Add(create);panel.Children.Add(buttons);
        dialog.Content=panel;dialog.Loaded+=(_,_)=>name.Focus();
        if(dialog.ShowDialog()!=true)return;
        int createdSlot=(int)slot.SelectedValue;
        Search.Clear();Category.SelectedIndex=0;
        RefreshDocument();
        ItemGrid.SelectedItem=rows.FirstOrDefault(r=>r.Item.Id==createdSlot);ItemGrid.ScrollIntoView(ItemGrid.SelectedItem);
        Status.Text="New item created. Edit it, give it to a character, then save the project.";
    }
    private void RandomizeItems(object sender,RoutedEventArgs e)
    {
        if(session is null || loading is not null)return;
        EndEdit();
        var dialog=new Window {Title="Randomize items",Owner=this,Width=460,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
        var panel=new StackPanel {Margin=new Thickness(24)};
        CheckBox Option(string text,bool on)=>new() {Content=new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap},IsChecked=on,Margin=new Thickness(0,0,0,8),FontSize=13};
        panel.Children.Add(new TextBlock {Text="Each starting item is replaced by a random item of the same type. Items tied to one character or class, and items that cannot be bought or sold, stay where they are. A full item stays full.",
            TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=12,LineHeight=18,Margin=new Thickness(0,0,0,14)});
        var weapons=Option("Weapons, orbs, staves and shields",true);
        var ammunition=Option("Arrows and bolts",true);
        var other=Option("Accessories and consumables",false);
        var bags=Option("Bag contents too",true);
        var level=Option("Never pick an item with a higher level than the one it replaces",true);
        foreach(var box in new[]{weapons,ammunition,other,bags,level})panel.Children.Add(box);
        panel.Children.Add(new TextBlock {Text="Seed",Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,6,0,4)});
        var seed=new TextBox {Text=Random.Shared.Next(1,1000000).ToString(),Margin=new Thickness(0,0,0,6)};
        panel.Children.Add(seed);
        panel.Children.Add(new TextBlock {Text="The same seed and options give the same items. The result is one change you can undo.",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=12,Margin=new Thickness(0,0,0,12)});
        var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,10)};
        panel.Children.Add(error);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var go=new Button {Content="Randomize",IsDefault=true,Style=(Style)FindResource("PrimaryButton")};
        int changed=0;
        go.Click+=(_,_)=>
        {
            if(!int.TryParse(seed.Text.Trim(),out int value) || value<0){error.Text="The seed must be a whole number of 0 or more.";return;}
            var options=new RandomizeOptions(value) {Weapons=weapons.IsChecked==true,Ammunition=ammunition.IsChecked==true,Other=other.IsChecked==true,Bags=bags.IsChecked==true,LimitLevel=level.IsChecked==true};
            if(!options.Weapons && !options.Ammunition && !options.Other){error.Text="Choose at least one kind of item.";return;}
            try
            {
                var edits=Randomizer.StartingItems(session,options);
                changed=edits.Where(x=>x.Field.EndsWith("Uses",StringComparison.Ordinal)==false).Count(x=>session.Value(x.Table,x.Id,x.Field)!=x.Value);
                session.Apply(edits);dialog.DialogResult=true;
            }
            catch(EditException ex){error.Text=ex.Message;}
        };
        buttons.Children.Add(cancel);buttons.Children.Add(go);panel.Children.Add(buttons);
        dialog.Content=panel;
        if(dialog.ShowDialog()!=true)return;
        RefreshDocument();
        Status.Text=changed==0?"No items were changed.":$"{changed} starting items changed. Save the project to keep them, or undo to go back.";
    }
    private void RenameItem(int id,string current)
    {
        if(session is null)return;
        var dialog=new Window {Title="Rename item",Owner=this,Width=420,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
        var panel=new StackPanel {Margin=new Thickness(24)};
        var name=new TextBox {Text=current,Margin=new Thickness(0,4,0,10)};
        var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,10)};
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var ok=new Button {Content="Rename",IsDefault=true,Style=(Style)FindResource("PrimaryButton")};
        ok.Click+=(_,_)=>{try{session.Rename(EditSession.Items,id,name.Text);dialog.DialogResult=true;}catch(EditException ex){error.Text=ex.Message;}};
        buttons.Children.Add(cancel);buttons.Children.Add(ok);
        panel.Children.Add(new TextBlock {Text="Name",Foreground=(Brush)FindResource("Muted"),FontSize=13});panel.Children.Add(name);panel.Children.Add(error);panel.Children.Add(buttons);
        dialog.Content=panel;dialog.Loaded+=(_,_)=>{name.Focus();name.SelectAll();};
        if(dialog.ShowDialog()==true){RefreshDocument();Status.Text="Item renamed. Save the project to keep this change.";}
    }
    private string DescribeValue(TableField field,int value)
    {
        if(field.ClassReference) return session?.Disc.Classes.FirstOrDefault(k=>k.Id==value)?.Name??$"Class 0x{value:X2}";
        if(field.ItemReference) return value==0?"Empty":session?.Disc.ItemBytes.ContainsKey(value)==true?session.EditedItem(value).DisplayName:$"Item 0x{value:X3}";
        if(field.IsFlag) return value!=0?"Added":"Removed";
        return FieldCodec.Format(field,value);
    }
    private sealed record EditTarget(View Page,int Id,string Noun,Panel Host,Panel ViewPanel,Button EditButton,IReadOnlyList<EditSection> Sections,string? Warning);
    private void BeginEdit(object sender,RoutedEventArgs e)
    {
        if(ItemGrid.SelectedItem is not ItemRow row)return;
        OpenEditor(new(View.Items,row.Item.Id,"Item",EditorPanel,DetailFields,EditItemButton,
        [
            new("",EditSession.Items,ItemEncoder.Fields.Where(f=>f.Group.Length==0).ToList()),
            new("Bonuses while equipped",EditSession.Items,ItemEncoder.Fields.Where(f=>f.Group=="Bonuses").ToList()),
            new("Resistances",EditSession.Items,ItemEncoder.Fields.Where(f=>f.Group=="Resistances").ToList()),
            new("Special effect",EditSession.Items,ItemEncoder.Fields.Where(f=>f.Group=="Special effect").ToList()),
            new("Stable",EditSession.StablePrices,session?.Disc.StablePriceBytes.ContainsKey(row.Item.Id)==true?[StablePrices.Field]:[]),
            new("Atelier recipe",EditSession.Recipes,session?.Disc.RecipeBytes.ContainsKey(row.Item.Id)==true?RecipeData.Fields:[]),
            new("Effects",EditSession.Items,ItemEncoder.Fields.Where(f=>f.Group=="Effects").ToList()),
        ],null));
    }
    private void BeginCharacterEdit(object sender,RoutedEventArgs e)
    {
        if(CharacterGrid.SelectedItem is not CharacterRow row)return;
        OpenEditor(new(View.Characters,row.Character.Id,"Character",CharacterEditorPanel,CharacterFields,EditCharacterButton,
        [
            new("Starting record",EditSession.Characters,CoreCharacterFields.Record.Where(f=>f.Group.Length==0).ToList()),
            new("Growth rates",EditSession.Growths,CoreCharacterFields.Growth.Where(f=>f.Group.Length==0).ToList()),
            new("Weapon levels",EditSession.Characters,CoreCharacterFields.Record.Where(f=>f.Group=="Weapon levels").ToList()),
            new("Weapon growth",EditSession.Growths,CoreCharacterFields.Growth.Where(f=>f.Group=="Weapon growth").ToList()),
            new("Personal skills",EditSession.Characters,CoreCharacterFields.Record.Where(f=>f.Group=="Personal skills").ToList()),
            new("Starting items",EditSession.Characters,CoreCharacterFields.Record.Where(f=>f.Group=="Starting items").ToList()),
            new("Bag contents",EditSession.Bags,session?.Disc.BagBytes.TryGetValue(row.Character.Id,out var bag)==true?BagData.EditableFor(BagData.Count(bag.Span)):[]),
            new("Partners",EditSession.Partners,session?.Disc.PartnerBytes.ContainsKey(row.Character.Id)==true?PartnerData.Editable:[]),
            new("Mercenary fee",EditSession.Mercenaries,session?.Disc.MercenaryBytes.ContainsKey(row.Character.Id)==true?MercenaryData.Fields:[]),
            new("Dish tastes",EditSession.Tastes,session?.Disc.TasteBytes.ContainsKey(row.Character.Id)==true?TasteData.Fields.Take(47).ToList():[]),
        ],"Changing a character's class changes their base stats, movement and allowed weapons to the new class's. Weapon levels above the new class's caps stay as they are."));
    }
    private void BeginClassEdit(object sender,RoutedEventArgs e)
    {
        if(ClassGrid.SelectedItem is not ClassRow row)return;
        string warning=row.StartingCharacters.Count>0
            ?$"Changes apply to every unit in this class, including enemies and {string.Join(", ",row.StartingCharacters)}."
            :"Changes apply to every unit in this class, including enemies.";
        OpenEditor(new(View.Classes,row.Class.Id,"Class",ClassEditorPanel,ClassFields,EditClassButton,
        [
            new("Base stats",EditSession.Classes,CoreClassFields.Fields.Where(f=>f.Group.Length==0).ToList()),
            new("Weapon level caps",EditSession.Classes,CoreClassFields.Fields.Where(f=>f.Group=="Weapon level caps").ToList()),
            new("Class skills",EditSession.Classes,CoreClassFields.Fields.Where(f=>f.Group=="Class skills").ToList()),
            new("Shield sizes",EditSession.Classes,CoreClassFields.Fields.Where(f=>f.Group=="Shield sizes").ToList()),
        ],warning+" A shield size needs both its box ticked here and a shield level cap above 0."));
    }
    private void BeginMovementEdit(object sender,RoutedEventArgs e)
    {
        if(MovementGrid.SelectedItem is not MovementRow row)return;
        string warning=row.ClassCount==0
            ?"No class uses this movement type yet. Give a class this type on the Classes page to use these costs."
            :$"Changes apply to every unit of these {row.ClassCount} classes, including enemies. Enter costs in movement points, such as 2 or 1.5, or × if the terrain cannot be entered.";
        OpenEditor(new(View.Movement,row.Type.Id,"Movement type",MovementEditorPanel,MovementFields,EditMovementButton,
        [
            new("Cost per hex",EditSession.Terrain,TerrainData.Fields.Where(f=>f.Group.Length==0).ToList()),
            new("Walls and doors",EditSession.Terrain,TerrainData.Fields.Where(f=>f.Group=="Blocked").ToList()),
        ],warning));
    }
    private void OpenEditor(EditTarget target)
    {
        if(session is null || loading is not null)return;
        EndEdit();editing=target;editors.Clear();
        var host=target.Host;host.Children.Clear();
        editorError=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,8)};
        host.Children.Add(editorError);
        if(target.Warning is { } warning)
            host.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(255,241,217)),BorderBrush=new SolidColorBrush(Color.FromRgb(223,204,158)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,0,10),
                Child=new TextBlock {Text=warning,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=new SolidColorBrush(Color.FromRgb(114,82,33))}});
        foreach(var section in target.Sections.Where(s=>s.Fields.Count>0))
        {
            if(section.Title.Length>0) host.Children.Add(new TextBlock {Text=section.Title,Style=(Style)FindResource("Caption"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,8)});
            if(section.Fields.Count>0 && section.Fields.All(f=>f.IsFlag))
            {
                var flags=new System.Windows.Controls.Primitives.UniformGrid {Columns=section.Fields.Max(f=>f.Label.Length)>16?1:2,Margin=new Thickness(0,0,0,8)};
                foreach(var field in section.Fields)
                {
                    var box=new CheckBox {Content=new TextBlock {Text=field.Label,TextWrapping=TextWrapping.Wrap},VerticalContentAlignment=VerticalAlignment.Top,IsChecked=session.Value(section.Table,target.Id,field.Key)!=0,Margin=new Thickness(0,0,6,6),FontSize=13};
                    System.Windows.Automation.AutomationProperties.SetName(box,$"{section.Title}: {field.Label}");
                    flags.Children.Add(box);editors.Add((section.Table,field,box,new TextBlock()));
                }
                host.Children.Add(flags);
                continue;
            }
            foreach(var field in section.Fields)
            {
                if(field.ItemReference || field.ClassReference)
                {
                    host.Children.Add(new TextBlock {Text=field.Label,Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,0,0,4)});
                    var options=field.ClassReference?ClassChoices():ItemChoices();
                    int chosen=session.Value(section.Table,target.Id,field.Key);
                    if(!options.Any(c=>c.Value==chosen)) options.Add(new(chosen,field.ClassReference?$"Class 0x{chosen:X2}":$"Item 0x{chosen:X3}"));
                    var picker=new ComboBox {ItemsSource=options,DisplayMemberPath="Text",SelectedValuePath="Value",IsTextSearchEnabled=true,Padding=new Thickness(6,4,6,4),Margin=new Thickness(0,0,0,8)};
                    picker.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
                    picker.SelectedValue=session.Value(section.Table,target.Id,field.Key);
                    System.Windows.Automation.AutomationProperties.SetName(picker,$"{section.Title}: {field.Label}");
                    host.Children.Add(picker);
                    var pickerError=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,-4,0,8),Visibility=Visibility.Collapsed};
                    host.Children.Add(pickerError);editors.Add((section.Table,field,picker,pickerError));
                    continue;
                }
                var grid=new Grid {Margin=new Thickness(0,0,0,8)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(72)});
                grid.Children.Add(new TextBlock {Text=field.Label,Foreground=(Brush)FindResource("Muted"),FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap});
                int current=session.Value(section.Table,target.Id,field.Key);
                Control input;
                if(field.Choices is { } choices)
                {
                    var combo=new ComboBox {Padding=new Thickness(6,4,6,4),DisplayMemberPath="Text",SelectedValuePath="Value",
                        ItemsSource=choices.Select((text,i)=>new Choice(field.ChoiceValue(i),text)).Concat(field.ChoiceIndex(current)<0?[new Choice(current,FieldCodec.Format(field,current))]:[]).ToList()};
                    combo.SelectedValue=current;input=combo;
                    if(choices.Any(c=>c.Length>8))
                    {
                        host.Children.Add(new TextBlock {Text=field.Label,Foreground=(Brush)FindResource("Muted"),FontSize=13,Margin=new Thickness(0,0,0,4)});
                        combo.Margin=new Thickness(0,0,0,8);combo.ToolTip=null;
                        System.Windows.Automation.AutomationProperties.SetName(combo,section.Title.Length>0?$"{section.Title}: {field.Label}":field.Label);
                        host.Children.Add(combo);
                        var wideError=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,-4,0,8),Visibility=Visibility.Collapsed};
                        host.Children.Add(wideError);editors.Add((section.Table,field,combo,wideError));
                        continue;
                    }
                }
                else input=new TextBox {Text=FieldCodec.Format(field,current),Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right};
                System.Windows.Automation.AutomationProperties.SetName(input,section.Title.Length>0?$"{section.Title}: {field.Label}":field.Label);
                input.ToolTip=FieldCodec.RangeText(field);
                Grid.SetColumn(input,1);grid.Children.Add(input);host.Children.Add(grid);
                var fieldError=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,-4,0,8),Visibility=Visibility.Collapsed};
                host.Children.Add(fieldError);editors.Add((section.Table,field,input,fieldError));
            }
        }
        StackPanel Buttons(bool primary)
        {
            var row=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=primary?new Thickness(0,4,0,0):new Thickness(0,0,0,10)};
            var cancel=new Button {Content="Cancel",Style=(Style)FindResource("SmallButton"),IsCancel=primary};cancel.Click+=(_,_)=>{EndEdit();Status.Text="Edit canceled.";};
            var apply=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),IsDefault=primary,Background=(Brush)FindResource("Accent"),Foreground=Brushes.White,BorderBrush=(Brush)FindResource("Accent")};apply.Click+=ApplyEdit;
            row.Children.Add(cancel);row.Children.Add(apply);return row;
        }
        if(editors.Count>12) host.Children.Insert(1,Buttons(false));
        host.Children.Add(Buttons(true));
        target.ViewPanel.Visibility=Visibility.Collapsed;host.Visibility=Visibility.Visible;target.EditButton.Visibility=Visibility.Collapsed;
        if(editors.Select(x=>x.Input).OfType<TextBox>().FirstOrDefault() is { } first){first.Focus();first.SelectAll();}
    }
    private void ApplyEdit(object sender,RoutedEventArgs e)
    {
        if(session is null || editing is null || editorError is null)return;
        var edits=new List<FieldEdit>();
        editorError.Text="";
        foreach(var row in editors){row.Error.Text="";row.Error.Visibility=Visibility.Collapsed;}
        foreach(var (table,field,input,fieldError) in editors)
        {
            int value;
            if(input is CheckBox box) value=box.IsChecked==true?1:0;
            else if(input is ComboBox combo)
            {
                if(combo.SelectedValue is not int chosen){fieldError.Text=$"Choose a value for {field.Label}.";fieldError.Visibility=Visibility.Visible;fieldError.BringIntoView();return;}
                value=chosen;
            }
            else if(!FieldCodec.TryParse(field,((TextBox)input).Text,out value,out string error))
            {
                fieldError.Text=error;fieldError.Visibility=Visibility.Visible;
                input.Focus();((TextBox)input).SelectAll();fieldError.BringIntoView();return;
            }
            edits.Add(new(table,editing.Id,field.Key,value));
        }
        if(editing.Page==View.Events && edits.Count==1 && ScriptFitProblem(edits[0].Id,edits[0].Value) is { } tooLarge){editorError.Text=tooLarge;editorError.BringIntoView();return;}
        if(editing.Page==View.Events && edits.Count==1 && ScriptValues.Split(edits[0].Id) is var (scriptEvent,place) && session.HasScript(scriptEvent))
        {
            try
            {
                bool changedScript=session.SetScript(scriptEvent,ScriptValues.Apply(session.EventScript(scriptEvent)!,new Dictionary<int,int>{[place]=edits[0].Value}));
                EndEdit();RefreshDocument();
                Status.Text=changedScript?"Number updated. Save the project to keep your changes.":"No values were changed.";
            }
            catch(EditException ex){editorError.Text=ex.Message;editorError.BringIntoView();}
            return;
        }
        try
        {
            bool changed=session.Apply(edits);string noun=editing.Noun;
            EndEdit();RefreshDocument();
            Status.Text=changed?$"{noun} updated. Save the project to keep your changes.":"No values were changed.";
        }
        catch(EditException ex){editorError.Text=ex.Message;editorError.BringIntoView();}
    }
    private void EndEdit()
    {
        if(editing is { } target)
        {
            editing=null;editors.Clear();target.Host.Children.Clear();target.Host.Visibility=Visibility.Collapsed;target.ViewPanel.Visibility=Visibility.Visible;
        }
        UpdateEditButtons();
    }
    private void UpdateEditButtons()
    {
        if(EditItemButton is null || EditCharacterButton is null || EditClassButton is null || EditMovementButton is null || EditTerrainButton is null)return;
        bool can=session is not null && editing is null;
        NewItemButton.Visibility=session is not null?Visibility.Visible:Visibility.Collapsed;NewItemButton.IsEnabled=can && loading is null;
        RandomizeItemsButton.Visibility=NewItemButton.Visibility;RandomizeItemsButton.IsEnabled=can && loading is null;
        EditItemButton.Visibility=can && ItemGrid.SelectedItem is ItemRow?Visibility.Visible:Visibility.Collapsed;
        EditCharacterButton.Visibility=can && CharacterGrid.SelectedItem is CharacterRow?Visibility.Visible:Visibility.Collapsed;
        EditArmyUnitButton.Visibility=can && ArmyGrid.SelectedItem is ArmyUnitRow?Visibility.Visible:Visibility.Collapsed;
        EditFurnitureButton.Visibility=can && FurnitureGrid.SelectedItem is FurnitureRow?Visibility.Visible:Visibility.Collapsed;
        EditDishButton.Visibility=can && DishGrid.SelectedItem is DishRow?Visibility.Visible:Visibility.Collapsed;
        EditScriptNumberButton.Visibility=can && ScriptNumberList.SelectedValue is int?Visibility.Visible:Visibility.Collapsed;
        EditClassButton.Visibility=can && ClassGrid.SelectedItem is ClassRow?Visibility.Visible:Visibility.Collapsed;
        EditMovementButton.Visibility=can && MovementGrid.SelectedItem is MovementRow?Visibility.Visible:Visibility.Collapsed;
        EditTerrainButton.Visibility=can && TerrainGrid.SelectedItem is TerrainKindRow?Visibility.Visible:Visibility.Collapsed;
    }
    private void BuildMechanics()
    {
        MechanicsList.Children.Clear();
        MechanicsList.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(234,240,229)),CornerRadius=new CornerRadius(5),Padding=new Thickness(14,12,14,12),Margin=new Thickness(0,0,0,16),
            Child=new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=13,LineHeight=20,
                Text="How the game's systems work, as far as they are known. Each entry says how sure that knowledge is: published by players, found in the game program, or confirmed in a running game. Change it shows what you can edit today: a rule, or the item, character and class values the formula uses."}});
        foreach(var section in Mechanics.All.GroupBy(m=>m.Section))
        {
            MechanicsList.Children.Add(new TextBlock {Text=section.Key,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,10)});
            foreach(var m in section)
            {
                var card=new StackPanel();
                MechanicsList.Children.Add(new Border {Background=Brushes.White,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(18,14,18,14),Margin=new Thickness(0,0,0,10),Child=card});
                var head=new DockPanel();
                var (bg,fg)=m.Status switch
                {
                    MechanicStatus.Confirmed=>(Color.FromRgb(225,235,221),Color.FromRgb(36,60,47)),
                    MechanicStatus.Located=>(Color.FromRgb(255,241,217),Color.FromRgb(114,82,33)),
                    _=>(Color.FromRgb(238,240,236),Color.FromRgb(90,98,92)),
                };
                var chip=new Border {Background=new SolidColorBrush(bg),CornerRadius=new CornerRadius(10),Padding=new Thickness(9,2,9,3),VerticalAlignment=VerticalAlignment.Center,
                    Child=new TextBlock {Text=Mechanics.StatusText(m.Status),FontSize=11,Foreground=new SolidColorBrush(fg)}};
                DockPanel.SetDock(chip,Dock.Right);head.Children.Add(chip);
                head.Children.Add(new TextBlock {Text=m.Name,FontSize=16,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
                card.Children.Add(head);
                card.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(246,247,243)),CornerRadius=new CornerRadius(4),Padding=new Thickness(10,6,10,6),Margin=new Thickness(0,8,0,8),
                    Child=new TextBlock {Text=m.Formula,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Consolas"),FontSize=13}});
                card.Children.Add(new TextBlock {Text=m.Explanation,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=13,LineHeight=20});
                if(m.Changes.Count>0)
                {
                    var links=new WrapPanel {Margin=new Thickness(0,10,0,0)};
                    links.Children.Add(new TextBlock {Text="Change it:",FontSize=12,Foreground=(Brush)FindResource("Muted"),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,4)});
                    foreach(var link in m.Changes)
                    {
                        var b=new Button {Content=link.Label,Style=(Style)FindResource("SmallButton"),Margin=new Thickness(0,0,6,4)};
                        b.Click+=(_,_)=>
                        {
                            if(primary?.Supported!=true){Notify("Open a supported disc first to edit these values.");return;}
                            view=link.Page switch { "items"=>View.Items, "characters"=>View.Characters, "classes"=>View.Classes, "movement"=>View.Movement, "terrain"=>View.Terrain, "skills"=>View.Skills, "furniture"=>View.Furniture, "live"=>View.Live, _=>View.Rules };UpdatePage();
                        };
                        links.Children.Add(b);
                    }
                    card.Children.Add(links);
                }
                if(m.NotYetEditable is { } note)
                    card.Children.Add(new TextBlock {Text=note,TextWrapping=TextWrapping.Wrap,FontSize=12,FontStyle=FontStyles.Italic,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,8,0,0)});
                card.Children.Add(new TextBlock {Text=$"Source: {m.Source}",FontSize=11,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,6,0,0)});
            }
            if(section.Key=="Movement") MechanicsList.Children.Add(TerrainTable());
        }
    }
    private Border TerrainTable()
    {
        var grid=new Grid {Margin=new Thickness(0,4,0,0)};
        string[] head=["Terrain","Avoid %",..Mechanics.TerrainColumns];
        for(int c=0;c<head.Length;c++) grid.ColumnDefinitions.Add(new ColumnDefinition {Width=c==0?new GridLength(118):new GridLength(c==1?58:56)});
        void Cell(int r,int c,string text,bool bold=false,bool muted=false)
        {
            var t=new TextBlock {Text=text,FontSize=12,Padding=new Thickness(4,3,4,3),FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,
                Foreground=muted?(Brush)FindResource("Muted"):(Brush)FindResource("Ink"),HorizontalAlignment=c>0?HorizontalAlignment.Center:HorizontalAlignment.Left};
            Grid.SetRow(t,r);Grid.SetColumn(t,c);grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition());
        for(int c=0;c<head.Length;c++) Cell(0,c,head[c],bold:true);
        int row=1;
        foreach(var t in Mechanics.Terrain)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            if(row%2==0){var band=new Border {Background=new SolidColorBrush(Color.FromRgb(248,249,246))};Grid.SetRow(band,row);Grid.SetColumnSpan(band,head.Length);grid.Children.Add(band);}
            Cell(row,0,t.Note.Length>0?t.Terrain+" *":t.Terrain);Cell(row,1,t.Avoid.ToString());
            for(int i=0;i<t.Costs.Length;i++) Cell(row,2+i,t.Costs[i]?.ToString()??"×",muted:t.Costs[i] is null);
            row++;
        }
        var stack=new StackPanel();
        stack.Children.Add(new DockPanel {Children={new TextBlock {Text="Terrain costs by movement type",FontSize=16,FontWeight=FontWeights.SemiBold}}});
        stack.Children.Add(new TextBlock {Text="Movement points each hex costs, as published by players; × means the terrain cannot be entered. Avoid % is the terrain's avoid bonus. The game's own table matches these costs and can be edited on the Movement page.",
            TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,4,0,8)});
        stack.Children.Add(new ScrollViewer {Content=grid,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled});
        foreach(var t in Mechanics.Terrain.Where(t=>t.Note.Length>0))
            stack.Children.Add(new TextBlock {Text=$"* {t.Terrain}: {t.Note}",FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,2,0,0),TextWrapping=TextWrapping.Wrap});
        stack.Children.Add(new TextBlock {Text="Source: gamekouryaku.com, Berwick Saga terrain list",FontSize=11,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,6,0,0)});
        return new Border {Background=Brushes.White,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(18,14,18,14),Margin=new Thickness(0,0,0,10),Child=stack};
    }
    private void BuildRules()
    {
        RulesList.Children.Clear();SkillsList.Children.Clear();
        if(primary is null || !primary.Supported)return;
        SkillsList.Children.Add(new Border {Background=new SolidColorBrush(Color.FromRgb(234,240,229)),CornerRadius=new CornerRadius(5),Padding=new Thickness(14,12,14,12),Margin=new Thickness(0,0,0,16),
            Child=new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=13,LineHeight=20,
                Text="Every skill in the game: what it does, how it works, and which starting characters and classes have it. Where the game's numbers can be changed, the card shows them with the game's value; chances and bonuses apply to every unit with the skill, enemies included, and After moving changes the command menu of your own units. Each changeable number was seen working in a running game before being offered. The line under each name says how its explanation is known. Which characters and classes have a skill is set on the Characters and Classes pages."}});
        var search=new TextBox {Padding=new Thickness(8,6,8,6),Text=skillSearch,ToolTip="Show only skills whose name or description contains this text"};
        System.Windows.Automation.AutomationProperties.SetName(search,"Search skills");
        var hint=new TextBlock {Text="Search by name or description",IsHitTestVisible=false,Foreground=new SolidColorBrush(Color.FromRgb(0x89,0x92,0x86)),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(11,0,0,0)};
        var searchBox=new Grid {Margin=new Thickness(0,0,0,14)};searchBox.Children.Add(search);searchBox.Children.Add(hint);
        SkillsList.Children.Add(searchBox);
        int firstCard=SkillsList.Children.Count;
        var ruleGroups=GameRules.All.Where(r=>r.Confirmed && r.Section==GameRules.SkillsSection).Select(r=>r.Group).ToHashSet();
        var skillTexts=new List<string>();
        foreach(var name in SkillCatalog.All.Select(s=>s.Name).Concat(ruleGroups).Distinct().Order(StringComparer.OrdinalIgnoreCase))
        {
            var info=SkillCatalog.Find(name);
            BuildRuleGroup(name,SkillsList,info);
            skillTexts.Add($"{name} {info?.Summary} {info?.Details} {(GameRules.GroupDescriptions.TryGetValue(name,out var d)?d:"")}");
        }
        void FilterSkills()
        {
            skillSearch=search.Text.Trim();hint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
            for(int i=0;i<skillTexts.Count;i++)
                SkillsList.Children[firstCard+i].Visibility=skillSearch.Length==0 || skillTexts[i].Contains(skillSearch,StringComparison.OrdinalIgnoreCase)?Visibility.Visible:Visibility.Collapsed;
        }
        search.TextChanged+=(_,_)=>FilterSkills();FilterSkills();
        var intro=new Border {Background=new SolidColorBrush(Color.FromRgb(234,240,229)),CornerRadius=new CornerRadius(5),Padding=new Thickness(14,12,14,12),Margin=new Thickness(0,0,0,16),
            Child=new TextBlock {TextWrapping=TextWrapping.Wrap,FontSize=13,LineHeight=20,
                Text="Rules change how the game itself plays, not a single item or character. They apply to the whole game, enemies included, and are written into the game program when you export. Each rule here was checked in a running game before being offered."}};
        RulesList.Children.Add(intro);
        var shownGroups=new HashSet<string>();
        foreach(var rule in GameRules.All.Where(r=>r.Confirmed && r.Section.Length==0))
        {
            if(rule.Group.Length>0){if(shownGroups.Add(rule.Group))BuildRuleGroup(rule.Group,RulesList);continue;}
            var card=new StackPanel();
            var border=new Border {Background=Brushes.White,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(18,16,18,16),Margin=new Thickness(0,0,0,12),Child=card};
            RulesList.Children.Add(border);
            card.Children.Add(new TextBlock {Text=rule.Name,FontSize=17,FontWeight=FontWeights.SemiBold});
            card.Children.Add(new TextBlock {Text=rule.Description,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=13,LineHeight=20,Margin=new Thickness(0,6,0,12)});
            if(session is null || !session.IsRuleAvailable(rule.Id))
            {
                card.Children.Add(new TextBlock {Text="Not available for this disc: its game program has different code at this point, so the rule is left alone.",TextWrapping=TextWrapping.Wrap,FontSize=13,Foreground=Brushes.DarkGoldenrod});
                continue;
            }
            int current=session.Value(EditSession.Rules,rule.Id,rule.Key), original=session.OriginalValue(EditSession.Rules,rule.Id,rule.Key);
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            card.Children.Add(row);
            var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),FontSize=12,Margin=new Thickness(0,8,0,0),TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed};
            error.SetBinding(UIElement.VisibilityProperty,new System.Windows.Data.Binding("Text"){Source=error,Converter=new TextVisibility()});
            void Set(int value)
            {
                if(loading is not null)return;
                try
                {
                    if(session.Apply(EditSession.Rules,rule.Id,new Dictionary<string,int>{[rule.Key]=value}))
                    {RefreshDocument();Status.Text=$"{rule.Name} set to {rule.Describe(value)}. Save the project to keep this change.";}
                }
                catch(EditException ex){error.Text=ex.Message;}
            }
            if(rule.Kind==RuleKind.Pair)
            {
                TextBox Box(int v,string name){var b=new TextBox {Width=48,Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right,Text=v.ToString()};System.Windows.Automation.AutomationProperties.SetName(b,name);return b;}
                var first=Box(GameRules.RangeLow(current),rule.PairLabels[0]);var second=Box(GameRules.RangeHigh(current),rule.PairLabels[1]);
                var applyPair=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(8,0,0,0)};
                int most=GameRules.RangeLow(rule.Max);
                void ApplyPair()
                {
                    if(!int.TryParse(first.Text.Trim(),out int a) || !int.TryParse(second.Text.Trim(),out int b) || a<0 || b<0 || a>most || b>most){error.Text=$"Enter two whole numbers from 0 to {most}.";return;}
                    error.Text="";Set(GameRules.RangeSetting(a,b));
                }
                applyPair.Click+=(_,_)=>ApplyPair();
                first.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyPair();e.Handled=true;}};second.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyPair();e.Handled=true;}};
                TextBlock Label(string t)=>new TextBlock {Text=t,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0),FontSize=12};
                var pairPanel=new WrapPanel();
                pairPanel.Children.Add(Label(rule.PairLabels[0]));pairPanel.Children.Add(first);
                var secondLabel=Label(rule.PairLabels[1]);secondLabel.Margin=new Thickness(12,0,6,0);pairPanel.Children.Add(secondLabel);pairPanel.Children.Add(second);pairPanel.Children.Add(applyPair);
                row.Children.Add(pairPanel);
                row.Children.Add(new TextBlock {Text=$"Game default {GameRules.RangeLow(rule.Default)} and {GameRules.RangeHigh(rule.Default)}",Foreground=(Brush)FindResource("Muted"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
            }
            else if(rule.Kind==RuleKind.Range)
            {
                TextBox Box(int v,string name){var b=new TextBox {Width=56,Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right,Text=v.ToString()};System.Windows.Automation.AutomationProperties.SetName(b,name);return b;}
                var low=Box(GameRules.RangeLow(current),$"{rule.Name} low");var high=Box(GameRules.RangeHigh(current),$"{rule.Name} high");
                var applyRange=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(8,0,0,0)};
                void ApplyRange()
                {
                    if(!int.TryParse(low.Text.Trim(),out int a) || !int.TryParse(high.Text.Trim(),out int b) || a<0 || b>99 || a>b){error.Text="Enter two whole numbers from 0 to 99, the first no higher than the second.";return;}
                    error.Text="";Set(GameRules.RangeSetting(a,b));
                }
                applyRange.Click+=(_,_)=>ApplyRange();
                low.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyRange();e.Handled=true;}};high.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyRange();e.Handled=true;}};
                row.Children.Add(low);row.Children.Add(new TextBlock {Text="to",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)});row.Children.Add(high);row.Children.Add(applyRange);
                row.Children.Add(new TextBlock {Text=$"Game default {rule.Describe(rule.Default).Replace(" (normal)","")}",Foreground=(Brush)FindResource("Muted"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
            }
            else if(rule.Kind==RuleKind.Limit)
            {
                var limit=new TextBox {Width=70,Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right,Text=current.ToString()};
                var applyLimit=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(8,0,0,0)};
                System.Windows.Automation.AutomationProperties.SetName(limit,rule.Name);
                void ApplyLimit()
                {
                    if(!int.TryParse(limit.Text.Trim(),out int n) || n<rule.Min || n>rule.Max){error.Text=$"Enter a whole number from {rule.Min} to {rule.Max}.";return;}
                    error.Text="";Set(n);
                }
                applyLimit.Click+=(_,_)=>ApplyLimit();
                limit.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyLimit();e.Handled=true;}};
                row.Children.Add(limit);row.Children.Add(applyLimit);
                row.Children.Add(new TextBlock {Text=$"Game default {rule.Default}, highest {rule.Max}",Foreground=(Brush)FindResource("Muted"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
            }
            else if(rule.Choices is { } choices)
            {
                var combo=new ComboBox {Width=180,Padding=new Thickness(8,5,8,5),ItemsSource=choices,SelectedIndex=current};
                System.Windows.Automation.AutomationProperties.SetName(combo,rule.Name);
                combo.SelectionChanged+=(_,_)=>{if(combo.SelectedIndex>=0 && combo.SelectedIndex!=current)Set(combo.SelectedIndex);};
                row.Children.Add(combo);
            }
            else
            {
                var mode=new ComboBox {Width=180,Padding=new Thickness(8,5,8,5),ItemsSource=new[]{"Normal","Fixed amount"},SelectedIndex=current==0?0:1};
                var amount=new TextBox {Width=70,Margin=new Thickness(10,0,0,0),Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right,Text=current==0?"100":current.ToString(),IsEnabled=current!=0};
                var apply=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(8,0,0,0),IsEnabled=current!=0};
                System.Windows.Automation.AutomationProperties.SetName(mode,rule.Name);System.Windows.Automation.AutomationProperties.SetName(amount,$"{rule.Name} amount");
                void ApplyAmount()
                {
                    if(!int.TryParse(amount.Text.Trim(),out int n) || n<1 || n>rule.Max){error.Text=$"Enter a whole number from 1 to {rule.Max}.";return;}
                    error.Text="";Set(n);
                }
                mode.SelectionChanged+=(_,_)=>
                {
                    bool fixedAmount=mode.SelectedIndex==1;amount.IsEnabled=fixedAmount;apply.IsEnabled=fixedAmount;
                    if(!fixedAmount && current!=0)Set(0); else if(fixedAmount && current==0)ApplyAmount();
                };
                apply.Click+=(_,_)=>ApplyAmount();
                amount.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyAmount();e.Handled=true;}};
                row.Children.Add(mode);row.Children.Add(amount);row.Children.Add(apply);
            }
            card.Children.Add(error);
            if(current!=original)
            {
                var changed=new DockPanel {Margin=new Thickness(0,10,0,0)};
                var restore=new Button {Content="Restore original",Style=(Style)FindResource("SmallButton")};
                restore.Click+=(_,_)=>{if(loading is not null)return;session.Revert(EditSession.Rules,rule.Id);RefreshDocument();Status.Text=$"{rule.Name} restored. Save the project to keep this change.";};
                DockPanel.SetDock(restore,Dock.Right);changed.Children.Add(restore);
                changed.Children.Add(new TextBlock {Text=$"Changed in this project: {rule.Describe(original)} → {rule.Describe(current)}",Foreground=(Brush)FindResource("Accent"),FontSize=13,VerticalAlignment=VerticalAlignment.Center});
                card.Children.Add(changed);
            }
        }
    }
    private string skillSearch="";
    private void BuildRuleGroup(string group,Panel target,SkillInfo? info=null)
    {
        var rules=GameRules.All.Where(r=>r.Confirmed && r.Group==group).ToList();
        var card=new StackPanel();
        target.Children.Add(new Border {Background=Brushes.White,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(18,16,18,16),Margin=new Thickness(0,0,0,12),Child=card});
        card.Children.Add(new TextBlock {Text=group,FontSize=17,FontWeight=FontWeights.SemiBold});
        if(info is not null)
        {
            card.Children.Add(new TextBlock {Text=$"{info.Kind} · {info.Source}",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=11,Margin=new Thickness(0,3,0,8)});
            card.Children.Add(new TextBlock {Text=info.Summary,TextWrapping=TextWrapping.Wrap,FontSize=14,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,4)});
            if(info.Details.Length>0) card.Children.Add(new TextBlock {Text=info.Details,TextWrapping=TextWrapping.Wrap,FontSize=13,LineHeight=20,Margin=new Thickness(0,0,0,6)});
            var people=(session?.EditedCharacters()??primary?.Characters??[]).Where(c=>c.Skills.Contains(group)).Select(c=>c.Name).ToList();
            var kinds=(session?.EditedClasses()??primary?.Classes??[]).Where(k=>k.Skills.Contains(group)).Select(k=>k.Name).ToList();
            string who=(people.Count>0?$"Starting characters: {string.Join(", ",people)}. ":"")+(kinds.Count>0?$"Classes: {string.Join(", ",kinds.Take(12))}{(kinds.Count>12?$" and {kinds.Count-12} more":"")}.":"");
            if(who.Length>0) card.Children.Add(new TextBlock {Text=who,TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new Thickness(0,0,0,6)});
            if(rules.Count==0)return;
        }
        card.Children.Add(new TextBlock {Text=GameRules.GroupDescriptions.TryGetValue(group,out var d)?d:"",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),FontSize=13,LineHeight=20,Margin=new Thickness(0,6,0,12)});
        if(session is null || rules.Any(r=>!session.IsRuleAvailable(r.Id)))
        {
            card.Children.Add(new TextBlock {Text="Not available for this disc: its game program has different code at this point, so these values are left alone.",TextWrapping=TextWrapping.Wrap,FontSize=13,Foreground=Brushes.DarkGoldenrod});
            return;
        }
        var wrap=new WrapPanel();card.Children.Add(wrap);
        var boxes=new List<(GameRule Rule,Control Box,int Current)>();
        foreach(var r in rules)
        {
            int current=session.Value(EditSession.Rules,r.Id,r.Key);
            bool wide=r.Choices?.Any(c=>c.Length>10)==true;
            var cell=new StackPanel {Width=wide?168:118,Margin=new Thickness(0,0,10,12)};
            var label=new TextBlock {Text=r.Short,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)};
            if(r.Swatch.Length>0)
            {
                var dot=new System.Windows.Shapes.Ellipse {Width=10,Height=10,Fill=new SolidColorBrush((Color)ColorConverter.ConvertFromString(r.Swatch)),Stroke=(Brush)FindResource("Line"),Margin=new Thickness(0,3,6,0),VerticalAlignment=VerticalAlignment.Top};
                var head=new DockPanel();DockPanel.SetDock(dot,Dock.Left);head.Children.Add(dot);head.Children.Add(label);cell.Children.Add(head);
            }
            else cell.Children.Add(label);
            Control box=r.Choices is { } choices
                ?new ComboBox {Width=wide?160:110,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(8,4,8,4),ItemsSource=choices,SelectedIndex=current-r.Min}
                :new TextBox {Width=70,HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right,Text=current.ToString()};
            System.Windows.Automation.AutomationProperties.SetName(box,r.Name);
            cell.Children.Add(box);
            cell.Children.Add(new TextBlock {Text=current==r.Default?"Game value":$"Game value {(r.Choices is null?r.Default.ToString():r.Describe(r.Default))}",FontSize=11,Foreground=current==r.Default?(Brush)FindResource("Muted"):(Brush)FindResource("Accent"),Margin=new Thickness(0,3,0,0)});
            wrap.Children.Add(cell);boxes.Add((r,box,current));
        }
        var error=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),FontSize=12,Margin=new Thickness(0,4,0,0),TextWrapping=TextWrapping.Wrap};
        void ApplyGroup()
        {
            if(loading is not null)return;
            var edits=new List<FieldEdit>();
            foreach(var (r,box,current) in boxes)
            {
                int n;
                if(box is ComboBox combo) n=combo.SelectedIndex+r.Min;
                else if(!int.TryParse(((TextBox)box).Text.Trim(),out n) || n<r.Min || n>r.Max){error.Text=$"{r.Short}: enter a whole number from {r.Min} to {r.Max}.";box.Focus();return;}
                if(n!=current) edits.Add(new(EditSession.Rules,r.Id,r.Key,n));
            }
            error.Text="";
            if(edits.Count==0)return;
            try{if(session.Apply(edits)){RefreshDocument();Status.Text=$"{group} updated. Save the project to keep this change.";}}
            catch(EditException ex){error.Text=ex.Message;}
        }
        foreach(var (_,box,_) in boxes.Where(b=>b.Box is TextBox)) box.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){ApplyGroup();e.Handled=true;}};
        var buttons=new StackPanel {Orientation=Orientation.Horizontal};
        var apply=new Button {Content="Apply",Style=(Style)FindResource("SmallButton")};apply.Click+=(_,_)=>ApplyGroup();
        buttons.Children.Add(apply);
        var edited=rules.Where(r=>session.IsEdited(EditSession.Rules,r.Id)).ToList();
        if(edited.Count>0)
        {
            var restore=new Button {Content="Restore original",Style=(Style)FindResource("SmallButton"),Margin=new Thickness(8,0,0,0)};
            restore.Click+=(_,_)=>{if(loading is not null)return;session.Revert(edited.Select(r=>(EditSession.Rules,r.Id)).ToArray());RefreshDocument();Status.Text=$"{group} restored. Save the project to keep this change.";};
            buttons.Children.Add(restore);
            buttons.Children.Add(new TextBlock {Text=edited.Count==1?"1 value changed in this project":$"{edited.Count} values changed in this project",Foreground=(Brush)FindResource("Accent"),FontSize=13,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)});
        }
        card.Children.Add(buttons);card.Children.Add(error);
    }
    private void AddChangeList(Panel panel,Func<FieldChange,bool> belongs,Action restore)
    {
        if(session is null)return;
        var changes=session.Changes.Where(belongs).ToList();
        if(changes.Count==0)return;
        AddSection(panel,"Changed in this project");
        foreach(var c in changes)
        {
            var field=session.Table(c.Table).Field(c.Field);
            string label=field.Group switch
            {
                "Weapon levels"=>$"{field.Label} level", "Weapon growth"=>$"{field.Label} growth", "Weapon level caps"=>$"{field.Label} cap",
                "Personal skills" or "Class skills"=>field.Label, "Starting items"=>$"Starting item {field.Label.ToLowerInvariant()}", _=>field.Label
            };
            AddPair(panel,label,field.IsFlag?DescribeValue(field,c.Value):$"{DescribeValue(field,c.Original)} → {DescribeValue(field,c.Value)}");
        }
        var button=new Button {Content="Restore original values",Style=(Style)FindResource("SmallButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,0)};
        button.Click+=(_,_)=>{if(loading is not null)return;restore();RefreshDocument();Status.Text="Original values restored. Save the project to keep this change.";};
        panel.Children.Add(button);
    }
    private async void OnDrop(object sender,DragEventArgs e)
    {
        if(loading is not null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)return;
        if(paths.Length is <1 or >2 || paths.Any(p=>!Path.GetExtension(p).Equals(".iso",StringComparison.OrdinalIgnoreCase))) {Notify("Drop one ISO to open it, or two ISOs to compare them.");return;}
        if(!ConfirmDiscard())return;
        if(await LoadDisc(paths[0],false) && paths.Length==2 && primary?.Supported==true) await LoadDisc(paths[1],true);
    }
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape && loading is not null){loading.Cancel();e.Handled=true;}
        if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;
        bool shift=(Keyboard.Modifiers&ModifierKeys.Shift)!=0, typing=Keyboard.FocusedElement is TextBox;
        if(e.Key==Key.S && session is not null){e.Handled=true;SaveProjectFile(shift);return;}
        if(!typing && (e.Key==Key.Y || (e.Key==Key.Z && shift))){e.Handled=true;RedoEdit(this,new());return;}
        if(!typing && e.Key==Key.Z){e.Handled=true;UndoEdit(this,new());return;}
        if(e.Key==Key.N && session is not null && editing is null && view==View.Items){e.Handled=true;NewItem(this,new());return;}
        if(e.Key==Key.E && session is not null && editing is null)
        {
            e.Handled=true;
            if(view==View.Items)BeginEdit(this,new());else if(view==View.Characters)BeginCharacterEdit(this,new());else if(view==View.Armies)BeginArmyUnitEdit(this,new());else if(view==View.Furniture)BeginFurnitureEdit(this,new());else if(view==View.Dishes)BeginDishEdit(this,new());else if(view==View.Events)BeginScriptNumberEdit(this,new());else if(view==View.Classes)BeginClassEdit(this,new());else if(view==View.Movement)BeginMovementEdit(this,new());else if(view==View.Terrain)BeginTerrainEdit(this,new());
            return;
        }
        if(e.Key==Key.O){e.Handled=true;if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)OpenComparison(this,new());else OpenPrimary(this,new());}
        if(e.Key==Key.F && primary?.Supported==true)
        {
            if(view is not (View.Characters or View.Classes or View.Armies))view=View.Items;
            UpdatePage();var box=view switch { View.Characters=>CharacterSearch, View.Classes=>ClassSearch, View.Armies=>ArmySearch, _=>Search };box.Focus();box.SelectAll();e.Handled=true;
        }
    }
    private void ShowHelp(object sender,RoutedEventArgs e)
    {
        var content=new StackPanel {Margin=new Thickness(28)};
        content.Children.Add(new TextBlock {Text="Berwick Workshop",FontFamily=new FontFamily("Georgia"),FontSize=26,Margin=new Thickness(0,0,0,18)});
        foreach(var (title,text) in new[]{
            ("Opening a disc","Choose an uncompressed ISO of the Japanese retail release or the English v2.3 translation. Each disc is checked in full before its data is shown. Modified copies of those discs open too and are labeled as modified. Other images can be inspected, but their game data is not loaded."),
            ("Browsing items","Search by name, decimal ID, or hexadecimal ID such as 0x022. Names follow the English translation catalogue for both editions. Show unused includes placeholder entries. Base prices and durability grades come from the disc; shop prices and remaining durability may differ in play. Horses also show their stable price, which is what the stable charges."),
            ("Characters","Shows each recruitable character's starting record: level, stat bonuses added to the class base, growth rates, weapon growth, weapon levels, personal skills, skills learned on level up, starting items with their durability or uses, bag contents, partners with their bond, and the fee of each mercenary. Class skills belong to the class and are not listed here. Mind growth is shown exactly as stored on the disc; published tables list half of this value."),
            ("Armies","Lists every unit in the game's armies: enemies, bosses, guests and the units of story scenes, 3,042 units in 275 armies. Named units only shows units with a name of their own, such as bosses and leaders. Where the game's events show which battle places an army, its name says so, for example Army 371 (Quest 6: Cruel Brothers), and searching for a mission or quest title lists its armies. Each unit has a class, level, stat bonuses, weapon levels, personal skills, up to seven items, and which items it drops when defeated. An army is stored twice on the disc; both copies are changed together."),
            ("Shops","The Shops page lists what each shop sells: the 13 shops at the base, named by their goods, and the 11 shops on battle maps, named by their battle. Each item is always for sale, sold from a stock that refills over time, or a special purchase that sets off an event when bought. Choose a shop and an item, then Change item to sell another item in its place (a stocked item keeps the stock of the one it replaces), Add item to add an item that is always for sale, or Remove item. Restore this shop puts the disc's version back. The changes are made in the shop's script, so they are kept in the project and written into every copy when you export a disc. The Stable lists every horse it can sell with its price, and below it the horses offered each chapter: how many of each, when the Spry and Tough Horses come, the chances of the Strong Horse and of one rare horse, and how many horses in all. The Kingfisher Pavilion entry has Any unit can order, which lets the server take orders for any unit in your army."),
            ("Furniture","Lists the pieces the furniture shop sells with their prices, which can be changed from 1 to 99,999, and what each piece does while it is shown in Reese's room. The bonuses go to Reese only and their sizes are set on the Game rules page under Furniture in Reese's room. The shop's own script sets these prices; they are separate from item prices. The shop charges the price set here, and the export also writes it into the shop's list of pieces, which keeps the order of the original prices."),
            ("Classes","Lists base stats, movement, movement type, shield sizes, experience value, class skills and weapon level caps for every class on the disc. Starting classes only limits the list to classes the recruitable characters begin in. Mounted and dismounted forms are separate classes. A character's base stats on the Characters page are the class base plus that character's own bonus."),
            ("Skills","The Skills page explains every skill in the game: what it does, how it works, whether it is a command, and which starting characters and classes have it. The line under each name says how the explanation is known: checked in a running game, read from the game program, or taken from the game's own description. Search filters the cards by name or description. Where the game's numbers can be changed, the card shows them: activation chances (Iaido, Knock Away, the bane skills, Parry), Pursuit's speed lead, the bonuses of Aim, Deadeye, Sunder, Desperation, Shieldfaire and Paragon, the highest level at which Paragon counts, how long each command waits before it can be used again, whether a command can be chosen after moving, Swimmer while mounted, the steal limit, Robbery's chance and range, Hurry's extra move, how much Miracle lowers a killing hit, which weapon type Pitchfork, Lance and Blade allow, what Debility divides stats by, the rounds of Deathmatch, whether Guard halves the guarding unit's own defense and avoid, whether Canto is offered after taking damage, Battle Cry's hit and attack for each use and its most uses, the base chance of Mug and Mug+, Evasion's avoid, the move change of Celerity and Slow Start, Camouflage's terrain avoid, Armsthrift's chance, Despoil's gold, Watchful's range, the range of each aura skill (Commander, Leader, Safe Zone, Charisma, Supporter, Resistor, Protector), whether an aura helps its holder and whether several holders add up, whether Supporter needs a partner, Blessing's range and healing, whether Celerity works while mounted, whether Vantage needs the faster speed, whether units without Counter keep countering after taking damage, whether Iaido's counter bonus also follows a hit for 0 damage or a parry, Provoke's range and how long it lasts, whether Flourish halves attack and its cripple bonus, the strikes of Astra, Triple-Shot and Double-Shot, whether Pulverize doubles attack and drops the unit's defenses, Adept's speed needed, chance bonus and strikes, Vengeful's chance and weapon, Axe Guard's block bonus, which weapons each attack command, Iaido and Huntsman can use (one weapon type or any weapon), how much Expert lowers item ranks, Limited's highest rank and level, whether Desperation drops the unit's defenses, and the hit bonuses of Focus Chant and Throw."),
            ("Maps","The Maps page shows each battle's map as the game paints it, with the river and other water drawn the way the battle starts, and the hex grid over it. Hover over a hex to see its position and terrain. Numbered markers show the spots that add a command to a unit's menu, such as gates, chests, villages to search and places to seize, and the side panel lists them with the battle's armies. Terrain colors colors each hex by its terrain; turn off Map art to see the colors alone. Size changes how large the map is drawn. Units shows where every unit stands when the battle opens: red squares for enemies, green for other armies, a gold edge for leaders and bosses, and blue hexes where you can place your own units. Arrivals shows where units come in later, read from the battle's scripts: a white square with the turn it arrives on, the first turn and a plus when it comes every turn for a while, or no number when an event in the battle brings it. Hover over it for the unit, where it comes from and when: on a turn, each turn over a range of turns, when a unit uses a spot such as Withdraw, when one unit talks to another, or under conditions the battle checks, such as an event flag. Maps that no battle in the game uses are listed at the end. The page is for reading only. Edit deployment lets you click hexes to add or remove places where your units can start; the details show the most units you can deploy, which can be changed for each battle. The game marks only as many start hexes as that limit, the first ones in the order they were added, so extra hexes are drawn dashed. Move units lets you click a unit and then the hex where it should start the battle."),
            ("Battle animations","The Battle animations page shows which animation set each class plays in battle and the colours each character wears in it. The game first looks for rows made for the unit itself, then for rows for any unit in the class; within each it prefers a row for the exact item held, then one for the weapon type, then the row for any weapon. Colours follow the same order, with rows for the player, enemy and allied sides in between. Choose a set, a motion and colours to see it play at the game's speed. Motion names are the ones in the game files; m0_0 is the standing pose. Sets that no class uses are listed at the end, with every palette they carry. Choose Change beside a row to give it another set or other colours, for example a set no class uses or another character's palette; the change goes into the project and the exported disc. A battle stops when a class plays a motion its set does not have, so only sets with every motion of the row's own set are offered (checked in a running game: the Spear Knight given the mounted winged set froze after riding in; on foot, given the winged set that has all his motions, the fight played through). Units keep their map sprites. Each class also has a map sprite, the figure units of the class show on the battle map, with a second one while they hold a shield; Change gives the class another class's map sprite, copied whole with its frame count and size (checked in a running game: Spear Knights on foot given the Wyvern Knight's on-foot sprite showed it after dismounting). The sets of art 008, 021 (the winged rider), 023, 092, 103 and 120 have no map sprite of their own, so a class given one of them needs another class's map sprite. Changing a colour row was checked in a running game: Adel given palette 6 fought in Leon's colours."),
            ("Events","The Events page shows the game's event scripts as readable commands: story scenes, map events and base events. Type an event number and press Enter to jump to it, or text to list every event that contains it. Commands carry the names the game itself gives them, such as SetEventFlag or GUnitCalcMercenaryCost, read from the opened disc. Searching for a name lists the events that use it. The numbers in a script can be changed: the list on the right shows each number with the command it belongs to; select one and choose Edit. A turn, a hex, a unit, an item or an amount of gold in a script are all numbers. The change is saved in the project and written into every copy of the event when you export a disc. A script that becomes larger than its space on the disc makes room by moving the files stored after it, and when its archive runs out of room the archive moves to unused space on the disc. Events that are also stored with the game's base data can only grow a little. The Lines tab lists the event's commands: Add after adds a command chosen by name, with numbers, text in double quotes or local values such as v0 as its arguments (only argument counts the game's own scripts use are accepted); Duplicate copies a line; Remove deletes it, with the lines under it for if and while; Up and Down move it within its part of the script. Restore the event's lines puts the disc's version back. The shop price events are changed on their own pages. Edit text, above the script, lets you change the whole script as text: keep four spaces of indent for each level and the forms the page shows, then choose Apply text. A mistake is reported with its line, and the cursor moves there. Changing lines changes what the game does, so test the exported disc."),
            ("Terrain","Each kind of terrain has an avoid bonus for the unit standing on it, a change to recovery while resting, and a flag that keeps flying units from ending their move there (houses and other buildings have it). Edit a terrain on the Terrain page. A flyer can only stop where its movement type can also enter, so check the Movement page too."),
            ("Movement","Every class has a movement type, and each type has a cost for each of 64 kinds of terrain. A unit keeps moving while the costs it has paid stay within its Move. Edit a type on the Movement page to change its costs; tenths such as 1.5 work, and × means the terrain cannot be entered. Give a class another movement type on the Classes page to change where it can go. Terrain kinds are named after the published terrain they match, since the game does not store the names as text."),
            ("Live game","The Live game page changes the game while it runs in PCSX2, which is useful for trying a change without playing up to it. Turn on PINE in PCSX2 (Settings, Advanced, slot 28011), start the game from the same edition as the opened disc, and choose Connect. The page lists the units on the current map; select one to change its class, level, current HP, stat bonuses, weapon levels, personal skills and items, then choose Apply to the game. Try this project's rules writes every rule you changed into the running game, so you can test it before exporting. To test on a map you have not reached, choose a battle under Next sortie loads (listed by their titles, in English on both editions) and Send sorties there while the game is at the base, then start any mission or quest: the game loads the chosen battle with its own map, units and events. Use each sortie's own battle puts the missions back. Undo all changes puts back everything the page changed. Nothing on the disc or in your saves changes, and loading a save state also undoes it. Berwick Workshop connects only to PCSX2 on this computer, and only when you choose Connect."),
            ("Comparing discs","Open a second ISO with Compare ISO. Items are matched by ID, including entries that share a name. Comparison covers decoded item, character and class fields, not text, artwork, scripts, or the entire game. Unclassified effects are kept as numbers."),
            ("Editing","Select an item, character or class and choose Edit. Items: might, accuracy, weight, range, critical, level, base price, durability grade, durability pool, bonuses while equipped, resistances, special effect and effects, and for horses the stable price. Armies: class, level, bonuses, weapon levels, skills, items and drops of any enemy, boss or guest unit. Furniture: the shop price of each piece. Characters: class, level, stat bonuses, growth rates, growth bracket, weapon levels, weapon growth, personal skills, the first five starting items, bag contents, partners and, for mercenaries, the fee. Randomize items on the Characters page gives every character random starting items of the same type. Classes: base stats, move, movement type, experience value, weapon level caps, shield sizes and class skills. Apply updates the lists right away and marks the entry as edited. Undo and Redo step through your changes, and Restore original values puts an entry back the way the disc has it."),
            ("Editing classes","A class is shared by every unit in it, including enemies, so a class change affects all of them. The editor names the recruitable characters who start in the class. To change one character only, edit that character's bonuses or personal skills instead."),
            ("Mechanics","The Mechanics page lists how the game's systems work: damage, hit and avoid, shields, critical hits, growth, experience and movement, with the formula, a short explanation, how well each is verified, and buttons to the values you can change today."),
            ("Game rules","The Rules page changes how the game plays: parry and shield block can be left normal, made to always succeed or never succeed, shield guard can let part of a shield's Might count when a block fails, shield and weapon training speed set how fast levels rise in battle, failed attempts can train at every level, the critical hit bonus can be set, experience after a fight can follow the game's formula or be a fixed amount, and the stat limits set how far each stat can grow above the class base. Rules apply to every unit, enemies included, and are written into the game program on export. Only rules confirmed in a running game are offered, and a rule is left alone if a disc's program has different code at that point."),
            ("New items","New item turns one of the disc's unused item slots into a new item. It starts as a full copy of an item you choose, including properties not shown in the editor, and can then be edited and given to characters as a starting item. Your name for it is saved in the project; until text editing is added, the game shows the slot's own name. Remove new item frees the slot again."),
            ("Growth bracket","Stats grow toward start value plus growth rate times levels gained, and the bracket keeps the result close to that expectation. Loose keeps HP, Strength and Mind within 3 of it and Defense and Agility within 2; Tight keeps every stat within 1; None lets them drift (only Daoud and Lynette use None). A stat too far ahead cannot rise, and one too far behind always rises. The Rules page can change both bands."),
            ("Projects","Edits live in a project file, not in your disc image. Save project writes a small .bwproj file that lists only what you changed. A project only opens with the disc it was made for, and only if that disc still has the values the project expects."),
            ("Exporting a disc","Export disc writes a new ISO with your changes applied, ready to load in PCSX2. The original image is read, checked against the fingerprint taken when it was opened, and copied; it is never changed. Both copies of each changed table on the disc are updated, and the changed bytes are read back from the new file before it is kept. Exporting needs free space for a full copy of the disc. Cancel stops the export and removes the unfinished file."),
            ("Modified discs","A disc that is not an exact original release, such as an exported disc or another modification, is shown as a modified disc when its game program and data layout match a supported edition. Its data can be browsed, compared and edited, but it is read from the disc as it is, so values may differ from the original release."),
            ("Keeping your files safe","Berwick Workshop never changes your ISO files, memory cards, save states, or emulator settings. No internet connection is used. The Live game page connects only to PCSX2 on this computer, and only when you choose Connect."),
            ("Keyboard shortcuts","Ctrl+O opens a disc. Ctrl+Shift+O chooses a comparison disc. Ctrl+F searches the current list. Ctrl+E edits the selected item, character, class or movement type. Ctrl+N creates a new item. Ctrl+Z and Ctrl+Y undo and redo. Ctrl+S saves the project, and Ctrl+Shift+S saves it under a new name. Escape cancels a disc check.")})
        {
            content.Children.Add(new TextBlock {Text=title,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,10,0,5)});
            content.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),LineHeight=21});
        }
        var help=new Window {Title="Help",Owner=this,Width=600,Height=710,MinHeight=420,MinWidth=460,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false};
        var close=new Button {Content="Close",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,22,0,0),IsCancel=true};
        close.Click+=(_,_)=>help.Close();content.Children.Add(close);
        help.Content=new ScrollViewer {Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};help.ShowDialog();
    }
}

internal sealed class TextVisibility : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
