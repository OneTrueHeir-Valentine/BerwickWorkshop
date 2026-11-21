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
    // Text page: the game's texts with a font table (names, explanations, mission texts, event items, guide messages, dialogue).
    private List<TextRow> textRows=[];
    private TextRecords? discTexts;
    private string? discTextsPath;
    private TextRecords? TextSource=>discTexts;
    private void ApplyTextList()
    {
        if(primary is null || view!=View.Texts)return;
        if(discTextsPath!=primary.Path)
        {
            discTexts=new TextRecords(primary.Path,primary.Edition.Contains("English",StringComparison.OrdinalIgnoreCase));
            discTextsPath=primary.Path;
            List<string> kinds=["Every kind"];
            try{kinds.AddRange(discTexts.Texts.Select(GameTexts.Kind).Distinct().Order());}
            catch(Exception ex) when (ex is IOException or InvalidDataException){discTexts=null;}
            TextKindBox.ItemsSource=kinds;TextKindBox.SelectedIndex=0;
        }
        if(discTexts is not { } loaded || loaded.Texts.Count==0){TextGrid.ItemsSource=null;TextCount.Text="This disc's texts could not be read.";return;}
        int? selected=(TextGrid.SelectedItem as TextRow)?.Id;
        textRows=loaded.Texts.Select((t,i)=>new TextRow(i,t.Key,GameTexts.Kind(t),Flat(session?.Text(i) ?? loaded.FontOf(t)?.Decode(t.Bytes) ?? ""),session?.IsEdited(EditSession.Texts,i)==true)).ToList();
        string query=TextSearch.Text.Trim();
        string kind=TextKindBox.SelectedItem as string ?? "Every kind";
        var shown=textRows.Where(r=>(kind=="Every kind" || r.Kind==kind) && (query.Length==0 || r.Text.Contains(query,StringComparison.OrdinalIgnoreCase) || r.Name.Contains(query,StringComparison.OrdinalIgnoreCase))).ToList();
        TextGrid.ItemsSource=shown;
        TextCount.Text=$"{shown.Count:N0} of {textRows.Count:N0} texts{(session is not null && textRows.Any(r=>r.Edited)?$", {textRows.Count(r=>r.Edited)} changed":"")}";
        if(shown.Count>0)TextGrid.SelectedItem=shown.FirstOrDefault(r=>r.Id==selected)??shown[0];
    }
    private static string Flat(string text)=>text.Replace('\n',' ');
    private void TextFilterChanged(object sender,EventArgs e){if(ready)ApplyTextList();}
    private void TextSelected(object sender,SelectionChangedEventArgs e)
    {
        GameTextFields.Children.Clear();
        if(TextGrid.SelectedItem is not TextRow row || discTexts is not { } loaded){GameTextName.Text="Select a text";GameTextEditor.Visibility=ApplyGameTextButton.Visibility=RestoreGameTextButton.Visibility=Visibility.Collapsed;GameTextNote.Text="";return;}
        var entry=loaded.Texts[row.Id];
        GameTextName.Text=row.Kind;
        AddPair(GameTextFields,"Entry",row.Name);
        string original=loaded.FontOf(entry)?.Decode(entry.Bytes)??"";
        AddPair(GameTextFields,"On the disc",original);
        string current=session?.Text(row.Id)??original;
        if(row.Edited)AddPair(GameTextFields,"In this project",current);
        GameTextEditor.AcceptsReturn=entry.Archive.Codes;
        if(entry.Archive.Codes)AddText(GameTextFields,"New lines start a new line in the game. Codes in braces, such as {84:2}, set who speaks, pauses and the end of a message; keep them where they are.");
        bool can=session is not null;
        GameTextEditor.Visibility=can?Visibility.Visible:Visibility.Collapsed;
        ApplyGameTextButton.Visibility=can?Visibility.Visible:Visibility.Collapsed;
        RestoreGameTextButton.Visibility=can && row.Edited?Visibility.Visible:Visibility.Collapsed;
        GameTextEditor.Text=current;
        if(!can)GameTextNote.Text="Open or start a project to change texts.";
    }
    private void GameTextChanged(object sender,TextChangedEventArgs e)
    {
        if(discTexts is not { } loaded || TextGrid.SelectedItem is not TextRow row || loaded.FontOf(loaded.Texts[row.Id]) is not { } font)return;
        string text=GameTextEditor.Text;
        var missing=font.Missing(text);
        int originalLength=font.Decode(loaded.Texts[row.Id].Bytes).Length;
        GameTextNote.Text=missing.Count>0?$"The game's font has no {string.Join(" ",missing)}. Use other characters."
            :$"{text.Length} characters (the disc's text has {originalLength}). Longer names may not fit the space the game draws them in.";
        GameTextNote.Foreground=missing.Count>0?new SolidColorBrush(Color.FromRgb(154,59,46)):(Brush)FindResource("Muted");
        ApplyGameTextButton.IsEnabled=missing.Count==0 && text.Length>0;
    }
    private void ApplyGameText(object sender,RoutedEventArgs e)
    {
        if(session is null || TextGrid.SelectedItem is not TextRow row)return;
        try
        {
            if(session.SetText(row.Id,GameTextEditor.Text)){Status.Text="Text changed. Save the project to keep this change.";RefreshDocument();ApplyTextList();}
            else Status.Text="Nothing was changed.";
        }
        catch(EditException ex){Status.Text=ex.Message;}
    }
    private void RestoreGameText(object sender,RoutedEventArgs e)
    {
        if(session is null || TextGrid.SelectedItem is not TextRow row)return;
        session.Revert(EditSession.Texts,row.Id);Status.Text="Text restored. Save the project to keep this change.";RefreshDocument();ApplyTextList();
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

}
