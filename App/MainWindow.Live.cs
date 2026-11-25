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
        int applied=0,partial=0,skipped=0,exportOnly=0;
        try
        {
            foreach(var id in session.Changes.Where(c=>c.Table==EditSession.Rules).Select(c=>c.Id).Distinct())
            {
                var rule=GameRules.ById(id);
                var support=LiveRuleReporting.Support(rule);
                if(support==LiveRuleSupport.ExportOnly){exportOnly++;continue;}
                if(!live.WriteRule(rule,session.Value(EditSession.Rules,id,rule.Key))){skipped++;continue;}
                if(support==LiveRuleSupport.Partial)partial++;else applied++;
            }
            SetLiveStatus(LiveRuleReporting.Summary(applied,partial,exportOnly,skipped));
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
}
