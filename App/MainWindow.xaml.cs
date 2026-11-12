using Berwick.Core;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Berwick.Workshop;

public partial class MainWindow : Window
{
    private InspectionResult? primary, comparison;
    private List<ItemRow> rows=[];
    private List<CharacterRow> characterRows=[];
    private List<ClassRow> classRows=[];
    private enum View { Items, Characters, Classes, Disc }
    private View view=View.Items;
    private CancellationTokenSource? loading;
    private bool ready, closing;
    private EditSession? session;
    private int? editingId;
    private readonly Dictionary<string,Control> editors=[];
    private TextBlock? editorError;

    public MainWindow()
    {
        InitializeComponent();
        Category.Items.Add("All types");
        foreach(string type in ItemDecoder.Types) Category.Items.Add(type);
        Category.Items.Add("Other"); Category.SelectedIndex=0;
        DiscNav.IsEnabled=false; ready=true;
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
                primary=result;comparison=null;editingId=null;
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
        CancelButton.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        Progress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
    }
    private void RefreshDocument()
    {
        if(primary is null)return;
        DiscStrip.Visibility=Visibility.Visible;EmptyState.Visibility=Visibility.Collapsed;DiscNav.IsEnabled=true;
        EditionText.Text=primary.Edition;FileText.Text=Path.GetFileName(primary.Path);FileText.ToolTip=primary.Path;
        VerifiedText.Text=primary.Supported?"Verified disc":"Unknown disc";
        VerifiedText.Foreground=primary.Supported?(Brush)FindResource("Accent"):Brushes.DarkGoldenrod;
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
        var characterDifferences=comparison is null?[]:RecordComparison.Compare(primary.Characters,comparison.Characters);
        var characterGroups=characterDifferences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        characterRows=primary.Characters.Select(c=>new CharacterRow(c,characterGroups.TryGetValue(c.Id,out var list)?list:[],comparison is not null)).ToList();
        CharactersNav.IsEnabled=primary.Supported;
        CharacterComparisonBar.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        CharacterChangeColumn.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        if(comparison is not null)
            CharacterComparisonText.Text=$"{primary.Edition} / {comparison.Edition}\n"+(characterDifferences.Count==0?$"All {characterRows.Count} characters match in the compared fields.":$"{characterGroups.Count} characters have differences in the compared fields.");
        var classDifferences=comparison is null?[]:ClassComparison.Compare(primary.Classes,comparison.Classes);
        var classGroups=classDifferences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        var starters=primary.Characters.GroupBy(c=>c.ClassId).ToDictionary(g=>g.Key,g=>(IReadOnlyList<string>)g.Select(c=>c.Name).ToList());
        classRows=primary.Classes.Select(k=>new ClassRow(k,classGroups.TryGetValue(k.Id,out var list)?list:[],comparison is not null,starters.TryGetValue(k.Id,out var s)?s:[])).ToList();
        ClassesNav.IsEnabled=primary.Supported;
        ClassComparisonBar.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        ClassChangeColumn.Visibility=comparison is null?Visibility.Collapsed:Visibility.Visible;
        if(comparison is not null)
            ClassComparisonText.Text=$"{primary.Edition} / {comparison.Edition}\n"+(classDifferences.Count==0?$"All {classRows.Count} classes match in the compared fields.":$"{classGroups.Count} classes have differences in the compared fields.");
        PopulateDiscDetails();ApplyFilter();ApplyCharacterFilter();ApplyClassFilter();UpdatePage();
        if(primary.DifferentTableRecords>0) Notify($"The disc contains {primary.DifferentTableRecords} items with differing table copies. Values shown are from the first copy.");
    }
    private void PopulateDiscDetails()
    {
        DiscFields.Children.Clear();if(primary is null)return;
        AddInfo(DiscFields,"Edition",primary.Edition);AddInfo(DiscFields,"Serial",primary.Serial);
        AddInfo(DiscFields,"Disc version",primary.DiscVersion);AddInfo(DiscFields,"Size",$"{primary.Size:N0} bytes");
        AddInfo(DiscFields,"Executable CRC",primary.Crc);AddInfo(DiscFields,"Image SHA-256",primary.Sha256);
        AddInfo(DiscFields,"Executable SHA-256",primary.ExecutableSha256);AddInfo(DiscFields,"File",primary.Path);
        if(primary.Supported) AddInfo(DiscFields,"Character tables",primary.CharacterCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
        if(primary.Supported) AddInfo(DiscFields,"Class tables",primary.ClassCopiesMatch?"Both copies match.":"The two copies differ. Values shown are from the first copy.");
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
        if(editingId is not null && editingId!=item.Id) EndEdit();
        EditItemButton.Visibility=session is not null && editingId is null?Visibility.Visible:Visibility.Collapsed;
        DetailFields.Children.Clear();
        string Was(params string[] keys)
        {
            if(session is null || !keys.Any(k=>session.Value(item.Id,k)!=session.OriginalValue(item.Id,k)))return "";
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
        if(session?.IsEdited(item.Id)==true)
        {
            var revert=new Button {Content="Restore original values",Style=(Style)FindResource("SmallButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,4,0,0)};
            revert.Click+=(_,_)=>{session.Revert(item.Id);RefreshDocument();Status.Text="Item restored. Save the project to keep this change.";};
            DetailFields.Children.Add(revert);
        }
        ItemEffect.Text=item.EffectDescription;
        ItemComparisonText.Text=!row.HasComparison?"":row.Differences.Count==0?"This item's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    private void ClearItem()
    {
        if(ItemName is null)return;EndEdit();EditItemButton.Visibility=Visibility.Collapsed;ItemName.Text="Select an item";ItemIdentity.Text="";DetailFields.Children.Clear();ItemEffect.Text="";ItemComparisonText.Text="";
    }
    private void ShowItems(object sender,RoutedEventArgs e) { view=View.Items;UpdatePage(); }
    private void ShowCharacters(object sender,RoutedEventArgs e) { view=View.Characters;UpdatePage(); }
    private void ShowClasses(object sender,RoutedEventArgs e) { view=View.Classes;UpdatePage(); }
    private void ShowDisc(object sender,RoutedEventArgs e) { view=View.Disc;UpdatePage(); }
    private void UpdatePage()
    {
        if(view!=View.Disc && primary is not null && !primary.Supported) view=View.Disc;
        PageTitle.Text=view switch { View.Disc=>"Disc details", View.Characters=>"Characters", View.Classes=>"Classes", _=>"Item library" };
        bool supported=primary?.Supported==true;
        DiscPage.Visibility=view==View.Disc && primary is not null?Visibility.Visible:Visibility.Collapsed;
        ItemPage.Visibility=view==View.Items && supported?Visibility.Visible:Visibility.Collapsed;
        CharacterPage.Visibility=view==View.Characters && supported?Visibility.Visible:Visibility.Collapsed;
        ClassPage.Visibility=view==View.Classes && supported?Visibility.Visible:Visibility.Collapsed;
        EmptyState.Visibility=primary is null?Visibility.Visible:Visibility.Collapsed;
        var active=new SolidColorBrush(Color.FromRgb(60,84,69));
        ItemsNav.Background=view==View.Items?active:Brushes.Transparent;
        CharactersNav.Background=view==View.Characters?active:Brushes.Transparent;
        ClassesNav.Background=view==View.Classes?active:Brushes.Transparent;
        DiscNav.Background=view==View.Disc?active:Brushes.Transparent;
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
        var c=row.Character;CharacterName.Text=c.Name;CharacterIdentity.Text=$"{c.IdText} · {c.ClassName} · Level {c.Level}";
        CharacterFields.Children.Clear();
        if(primary?.ClassOf(c) is { } k)
        {
            Section("Base stats");
            foreach(var (label,value) in new[]{("HP",k.Hp+c.HpBonus),("Strength",k.Strength+c.StrengthBonus),("Defense",k.Defense+c.DefenseBonus),("Agility",k.Agility+c.AgilityBonus),("Mind",k.Mind+c.MindBonus),("Move",k.Move)}) Pair(label,value.ToString());
        }
        Section("Bonus over class base");
        foreach(var (label,value) in new[]{("HP",c.HpBonus),("Strength",c.StrengthBonus),("Defense",c.DefenseBonus),("Agility",c.AgilityBonus),("Mind",c.MindBonus)}) Pair(label,CharacterRecord.Signed(value));
        Section("Growth rates");
        foreach(var (label,value) in new[]{("HP",c.HpGrowth),("Strength",c.StrengthGrowth),("Defense",c.DefenseGrowth),("Agility",c.AgilityGrowth)}) Pair(label,$"{value}%");
        Pair("Mind (stored value)",c.MindGrowth.ToString());
        if(c.WeaponGrowths.Count>0){Section("Weapon growth");foreach(var w in c.WeaponGrowths)Pair(w.Weapon,$"{w.Value}%");}
        if(c.WeaponLevels.Count>0){Section("Weapon levels");foreach(var w in c.WeaponLevels)Pair(w.Weapon,CharacterRecord.WeaponLevel(w.Value));}
        Section("Personal skills");Text(c.Skills.Count==0?"None":string.Join(", ",c.Skills));
        Section("Learned on level up");
        if(c.Learned.Count==0)Text("None");else foreach(var l in c.Learned)Pair(l.Skill,$"Level {l.Level}");
        Section("Starting items");
        if(c.Items.Count==0)Text("None");else foreach(var i in c.Items)Pair(i.Name,i.Durability.ToString());
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
        var k=row.Class;ClassName.Text=k.Name;ClassIdentity.Text=k.IdText;
        ClassFields.Children.Clear();
        AddSection(ClassFields,"Base stats");
        foreach(var (label,value) in new[]{("HP",k.Hp),("Strength",k.Strength),("Defense",k.Defense),("Agility",k.Agility),("Mind",k.Mind),("Move",k.Move),("Experience value",k.Experience)}) AddPair(ClassFields,label,value.ToString());
        AddSection(ClassFields,"Class skills");AddText(ClassFields,k.SkillText);
        AddSection(ClassFields,"Weapon level caps");
        if(k.WeaponCaps.Count==0)AddText(ClassFields,"None");else foreach(var w in k.WeaponCaps)AddPair(ClassFields,w.Weapon,w.Value.ToString());
        AddSection(ClassFields,"Characters starting in this class");AddText(ClassFields,row.StartingCharacters.Count==0?"None":string.Join(", ",row.StartingCharacters));
        ClassComparisonDetail.Text=!row.HasComparison?"":row.Differences.Count==0?"This class's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    private void AddSection(Panel panel,string title)=>panel.Children.Add(new TextBlock {Text=title,Style=(Style)FindResource("Caption"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,14,0,8)});
    private static void AddText(Panel panel,string text)=>panel.Children.Add(new TextBlock {Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)});
    private void AddPair(Panel panel,string label,string value)
    {
        var grid=new Grid {Margin=new Thickness(0,0,0,7)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        grid.Children.Add(new TextBlock {Text=label,Foreground=(Brush)FindResource("Muted"),FontSize=13,TextWrapping=TextWrapping.Wrap});
        var v=new TextBlock {Text=value,FontWeight=FontWeights.SemiBold,Margin=new Thickness(12,0,0,0)};Grid.SetColumn(v,1);grid.Children.Add(v);panel.Children.Add(grid);
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
        closing=true;loading?.Cancel();
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
        UndoButton.IsEnabled=session.CanUndo;RedoButton.IsEnabled=session.CanRedo;
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
            session.Save(path,typeof(MainWindow).Assembly.GetName().Version?.ToString(3));
            UpdateProjectBar();Status.Text=$"Project saved. Your game files were not changed.";return true;
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notify("The project could not be saved. Choose a folder you can write to.");return false;
        }
    }
    private void SaveProject(object sender,RoutedEventArgs e)=>SaveProjectFile(false);
    private void OpenProject(object sender,RoutedEventArgs e)
    {
        if(primary?.Supported!=true || !ConfirmDiscard())return;
        var picker=new OpenFileDialog {Title="Open project",Filter="Berwick Workshop project (*.bwproj)|*.bwproj",CheckFileExists=true};
        picker.InitialDirectory=session?.FilePath is { } current?Path.GetDirectoryName(current):Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(picker.ShowDialog(this)!=true)return;
        try
        {
            session=EditSession.Load(primary,picker.FileName);editingId=null;
            RefreshDocument();Status.Text=$"Project opened with {session.ChangeCount} changes.";
        }
        catch(EditException ex){Notify(ex.Message);}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){Notify("The project could not be opened. Check that the file is available.");}
    }
    private void UndoEdit(object sender,RoutedEventArgs e){if(session?.CanUndo!=true)return;EndEdit();session.Undo();RefreshDocument();Status.Text="Undone.";}
    private void RedoEdit(object sender,RoutedEventArgs e){if(session?.CanRedo!=true)return;EndEdit();session.Redo();RefreshDocument();Status.Text="Redone.";}
    private void BeginEdit(object sender,RoutedEventArgs e)
    {
        if(session is null || ItemGrid.SelectedItem is not ItemRow row)return;
        int id=row.Item.Id;editingId=id;editors.Clear();EditorPanel.Children.Clear();
        foreach(var field in ItemEncoder.Fields)
        {
            var grid=new Grid {Margin=new Thickness(0,0,0,8)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(72)});
            grid.Children.Add(new TextBlock {Text=field.Label,Foreground=(Brush)FindResource("Muted"),FontSize=13,VerticalAlignment=VerticalAlignment.Center});
            Control input;
            if(field.Key=="durability")
            {
                var combo=new ComboBox {Padding=new Thickness(6,4,6,4)};foreach(char grade in "SABCDEF")combo.Items.Add(grade.ToString());
                combo.SelectedIndex=Math.Clamp(session.Value(id,field.Key),0,6);input=combo;
            }
            else input=new TextBox {Text=session.Value(id,field.Key).ToString(),Padding=new Thickness(6,4,6,4),HorizontalContentAlignment=HorizontalAlignment.Right};
            System.Windows.Automation.AutomationProperties.SetName(input,field.Label);
            input.ToolTip=$"{field.Min} to {field.Max}";
            Grid.SetColumn(input,1);grid.Children.Add(input);EditorPanel.Children.Add(grid);editors[field.Key]=input;
        }
        editorError=new TextBlock {Foreground=new SolidColorBrush(Color.FromRgb(154,59,46)),TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,8)};
        EditorPanel.Children.Insert(0,editorError);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="Cancel",Style=(Style)FindResource("SmallButton"),IsCancel=true};cancel.Click+=(_,_)=>{EndEdit();Status.Text="Edit canceled.";};
        var apply=new Button {Content="Apply",Style=(Style)FindResource("SmallButton"),IsDefault=true,Background=(Brush)FindResource("Accent"),Foreground=Brushes.White,BorderBrush=(Brush)FindResource("Accent")};apply.Click+=ApplyEdit;
        buttons.Children.Add(cancel);buttons.Children.Add(apply);EditorPanel.Children.Add(buttons);
        DetailFields.Visibility=Visibility.Collapsed;EditorPanel.Visibility=Visibility.Visible;EditItemButton.Visibility=Visibility.Collapsed;
        (editors["might"] as TextBox)?.Focus();(editors["might"] as TextBox)?.SelectAll();
    }
    private void ApplyEdit(object sender,RoutedEventArgs e)
    {
        if(session is null || editingId is not int id || editorError is null)return;
        var values=new Dictionary<string,int>();
        foreach(var (key,input) in editors)
        {
            if(input is ComboBox combo){values[key]=combo.SelectedIndex;continue;}
            string text=((TextBox)input).Text.Trim().Replace(",","");
            if(!int.TryParse(text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int value))
            {editorError.Text=$"{ItemEncoder.Field(key).Label} must be a whole number.";editorError.BringIntoView();input.Focus();return;}
            values[key]=value;
        }
        try
        {
            bool changed=session.Apply(id,values);
            EndEdit();RefreshDocument();
            Status.Text=changed?"Item updated. Save the project to keep your changes.":"No values were changed.";
        }
        catch(EditException ex){editorError.Text=ex.Message;editorError.BringIntoView();}
    }
    private void EndEdit()
    {
        if(EditorPanel is null)return;
        editingId=null;editors.Clear();EditorPanel.Children.Clear();EditorPanel.Visibility=Visibility.Collapsed;DetailFields.Visibility=Visibility.Visible;
        EditItemButton.Visibility=session is not null && ItemGrid.SelectedItem is ItemRow?Visibility.Visible:Visibility.Collapsed;
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
        if(e.Key==Key.E && view==View.Items && session is not null && editingId is null){e.Handled=true;BeginEdit(this,new());return;}
        if(e.Key==Key.O){e.Handled=true;if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)OpenComparison(this,new());else OpenPrimary(this,new());}
        if(e.Key==Key.F && primary?.Supported==true)
        {
            if(view is not (View.Characters or View.Classes))view=View.Items;
            UpdatePage();var box=view switch { View.Characters=>CharacterSearch, View.Classes=>ClassSearch, _=>Search };box.Focus();box.SelectAll();e.Handled=true;
        }
    }
    private void ShowHelp(object sender,RoutedEventArgs e)
    {
        var content=new StackPanel {Margin=new Thickness(28)};
        content.Children.Add(new TextBlock {Text="Berwick Workshop",FontFamily=new FontFamily("Georgia"),FontSize=26,Margin=new Thickness(0,0,0,18)});
        foreach(var (title,text) in new[]{
            ("Opening a disc","Choose an uncompressed ISO of the Japanese retail release or the English v2.3 translation. Each disc is checked in full before its item data is shown. Other editions can be inspected, but their item tables are not loaded."),
            ("Browsing items","Search by name, decimal ID, or hexadecimal ID such as 0x022. Names follow the English translation catalogue for both editions. Show unused includes placeholder entries. Base prices and durability grades come from the disc; shop prices and remaining durability may differ in play."),
            ("Characters","Shows each recruitable character's starting record: level, stat bonuses added to the class base, growth rates, weapon growth, weapon levels, personal skills, skills learned on level up, and starting items with their durability or uses. Class skills belong to the class and are not listed here. Mind growth is shown exactly as stored on the disc; published tables list half of this value."),
            ("Classes","Lists base stats, movement, experience value, class skills and weapon level caps for every class on the disc. Starting classes only limits the list to classes the recruitable characters begin in. Mounted and dismounted forms are separate classes. A character's base stats on the Characters page are the class base plus that character's own bonus."),
            ("Comparing discs","Open a second ISO with Compare ISO. Items are matched by ID, including entries that share a name. Comparison covers decoded item, character and class fields, not text, artwork, scripts, or the entire game. Unclassified effects are kept as numbers."),
            ("Editing items","Select an item and choose Edit to change its might, accuracy, weight, range, critical, level, base price, durability grade or durability pool. Apply updates the list right away and marks the item as edited. Undo and Redo step through your changes. Restore original values puts an item back the way the disc has it."),
            ("Projects","Edits live in a project file, not in your disc image. Save project writes a small .bwproj file that lists only what you changed. A project only opens with the disc it was made for, and only if that disc still has the values the project expects. This version does not yet write a patched disc."),
            ("Keeping your files safe","Berwick Workshop never changes your ISO files, memory cards, save states, or emulator settings. No internet connection is used."),
            ("Keyboard shortcuts","Ctrl+O opens a disc. Ctrl+Shift+O chooses a comparison disc. Ctrl+F searches the current list. Ctrl+E edits the selected item. Ctrl+Z and Ctrl+Y undo and redo. Ctrl+S saves the project, and Ctrl+Shift+S saves it under a new name. Escape cancels a disc check.")})
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
