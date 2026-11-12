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
    private enum View { Items, Characters, Disc }
    private View view=View.Items;
    private CancellationTokenSource? loading;
    private bool ready, closing;

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
                primary=result;comparison=null;
                Search.Clear();Category.SelectedIndex=0;OnlyDifferences.IsChecked=false;
                CharacterSearch.Clear();CharacterDifferencesOnly.IsChecked=false;
                view=result.Supported?(view==View.Characters?View.Characters:View.Items):View.Disc;
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
        var differences=comparison is null?[]:ItemComparison.Compare(primary.Items,comparison.Items);
        var groups=differences.GroupBy(d=>d.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        rows=primary.Items.Select(i=>new ItemRow(i,groups.TryGetValue(i.Id,out var list)?list:[],comparison is not null)).ToList();
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
        PopulateDiscDetails();ApplyFilter();ApplyCharacterFilter();UpdatePage();
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
        DetailFields.Children.Clear();
        foreach(var field in new (string,string)[]{("Might",item.Might.ToString()),("Accuracy",item.Accuracy.ToString()),("Weight",item.Weight.ToString()),("Range",item.Range),("Critical",item.Critical.ToString()),("Level",item.Level.ToString()),("Base price",item.Price.ToString("N0")),("Durability",item.DurabilityText)})
        {
            var grid=new Grid {Margin=new Thickness(0,0,0,10)};grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            grid.Children.Add(new TextBlock {Text=field.Item1,Foreground=(Brush)FindResource("Muted"),FontSize=13});
            var value=new TextBlock {Text=field.Item2,FontWeight=FontWeights.SemiBold};Grid.SetColumn(value,1);grid.Children.Add(value);DetailFields.Children.Add(grid);
        }
        ItemEffect.Text=item.EffectDescription;
        ItemComparisonText.Text=!row.HasComparison?"":row.Differences.Count==0?"This item's compared fields match.":string.Join("\n",row.Differences.Select(d=>$"{d.Field}: {d.First} → {d.Second}"));
    }
    private void ClearItem()
    {
        if(ItemName is null)return;ItemName.Text="Select an item";ItemIdentity.Text="";DetailFields.Children.Clear();ItemEffect.Text="";ItemComparisonText.Text="";
    }
    private void ShowItems(object sender,RoutedEventArgs e) { view=View.Items;UpdatePage(); }
    private void ShowCharacters(object sender,RoutedEventArgs e) { view=View.Characters;UpdatePage(); }
    private void ShowDisc(object sender,RoutedEventArgs e) { view=View.Disc;UpdatePage(); }
    private void UpdatePage()
    {
        if(view!=View.Disc && primary is not null && !primary.Supported) view=View.Disc;
        PageTitle.Text=view switch { View.Disc=>"Disc details", View.Characters=>"Characters", _=>"Item library" };
        bool supported=primary?.Supported==true;
        DiscPage.Visibility=view==View.Disc && primary is not null?Visibility.Visible:Visibility.Collapsed;
        ItemPage.Visibility=view==View.Items && supported?Visibility.Visible:Visibility.Collapsed;
        CharacterPage.Visibility=view==View.Characters && supported?Visibility.Visible:Visibility.Collapsed;
        EmptyState.Visibility=primary is null?Visibility.Visible:Visibility.Collapsed;
        var active=new SolidColorBrush(Color.FromRgb(60,84,69));
        ItemsNav.Background=view==View.Items?active:Brushes.Transparent;
        CharactersNav.Background=view==View.Characters?active:Brushes.Transparent;
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
        var c=row.Character;CharacterName.Text=c.Name;CharacterIdentity.Text=$"{c.IdText} · Level {c.Level}";
        CharacterFields.Children.Clear();
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
    private void OnClosing(object? sender,CancelEventArgs e) {closing=true;loading?.Cancel();}
    private async void OnDrop(object sender,DragEventArgs e)
    {
        if(loading is not null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)return;
        if(paths.Length is <1 or >2 || paths.Any(p=>!Path.GetExtension(p).Equals(".iso",StringComparison.OrdinalIgnoreCase))) {Notify("Drop one ISO to open it, or two ISOs to compare them.");return;}
        if(await LoadDisc(paths[0],false) && paths.Length==2 && primary?.Supported==true) await LoadDisc(paths[1],true);
    }
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape && loading is not null){loading.Cancel();e.Handled=true;}
        if((Keyboard.Modifiers&ModifierKeys.Control)==0)return;
        if(e.Key==Key.O){e.Handled=true;if((Keyboard.Modifiers&ModifierKeys.Shift)!=0)OpenComparison(this,new());else OpenPrimary(this,new());}
        if(e.Key==Key.F && primary?.Supported==true)
        {
            if(view!=View.Characters)view=View.Items;
            UpdatePage();var box=view==View.Characters?CharacterSearch:Search;box.Focus();box.SelectAll();e.Handled=true;
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
            ("Comparing discs","Open a second ISO with Compare ISO. Items are matched by ID, including entries that share a name. Comparison covers decoded item and character fields, not text, artwork, scripts, or the entire game. Unclassified effects are kept as numbers."),
            ("Keeping your files safe","This version only reads your discs. It does not edit game files, memory cards, save states, or emulator settings. No internet connection is used."),
            ("Keyboard shortcuts","Ctrl+O opens a disc. Ctrl+Shift+O chooses a comparison disc. Ctrl+F searches the current list. Escape cancels a disc check.")})
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
