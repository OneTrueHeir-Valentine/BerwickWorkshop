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
    private int animationPreviewVersion;
    private bool updatingAnimationSelectors;
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
        animationPreviewVersion++;
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
            Heading("Colors");
            var palettes=table.Palettes(value);
            if(palettes.Count==0)Line("The set's own colors for everyone.",true);
            for(int r=0;r<palettes.Count;r++)
            {
                var row=palettes[r];int number=AnimationValue(true,value,r,row.Value);int at=r;
                string who=row.Unit>0?UnitName(row.Unit):AnimationTable.SideText(row.Unit);
                string when=row.Key==0?"":$" holding {AnimationTable.KeyText(row.Key)}";
                string text=$"{who}{when}: {(number==0?"the set's own colors":$"palette {number}")}";
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
        // A class change can select the same motion name in a different set. Do not
        // let binding notifications draw its frames using the previous crop bounds.
        animationPreviewVersion++;
        animationTimer.Stop();
        shownAnimationSet=null;
        animationFrames=[];
        animationMotion="";
        AnimationImage.Source=null;
        AnimationFrameText.Text="";
        updatingAnimationSelectors=true;
        try
        {
            AnimationSetBox.ItemsSource=sets;
            AnimationSetBox.SelectedIndex=keepSet is int k && sets.FindIndex(s=>s.Value==k) is int found && found>=0?found:sets.Count>0?0:-1;
        }
        finally { updatingAnimationSelectors=false; }
        if(AnimationSetBox.SelectedValue is int)AnimationViewChanged(AnimationSetBox,null);
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
            field=AnimationTable.PaletteField with {Choices=numbers.Select(n=>n==0?"Set's own colors":$"Palette {n}").ToList(),ChoiceValues=numbers.ToList()};
            warning="Every unit this row applies to wears the chosen colors in all of this class's sets. Palettes are listed from the sets this class uses; a set without the chosen palette shows its own colors or wrong ones.";
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
        OpenEditor(new(View.Animations,AnimationTable.RowId(palette,classId,row),palette?"Color row":"Animation row",AnimationEditorPanel,AnimationDetails,AnimationEditButton,
            [new(text,palette?EditSession.AnimationPalettes:EditSession.AnimationSets,[field])],warning));
    }

    private async void AnimationViewChanged(object sender,SelectionChangedEventArgs? e)
    {
        if(updatingAnimationSelectors || AnimationSetBox.SelectedValue is not int number)return;
        int version=++animationPreviewVersion;
        animationTimer.Stop();
        shownAnimationSet=null;
        animationFrames=[];
        animationMotion="";
        AnimationImage.Source=null;
        AnimationFrameText.Text="";
        var set=await AnimationSetFor(number);
        if(version!=animationPreviewVersion || AnimationSetBox.SelectedValue is not int now || now!=number)return;
        shownAnimationSet=set;

        // Set details go under the class rows, replacing any shown for the previous set.
        while(AnimationDetails.Children.Count>0 && AnimationDetails.Children[^1] is FrameworkElement { Tag: "set" })AnimationDetails.Children.RemoveAt(AnimationDetails.Children.Count-1);
        var muted=(Brush)FindResource("Muted");
        void Add(string text,bool bold=false)=>AnimationDetails.Children.Add(new TextBlock{Text=text,Tag="set",TextWrapping=TextWrapping.Wrap,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Margin=new Thickness(0,bold?12:0,0,bold?4:3),Foreground=bold?Brushes.Black:muted});
        Add(AnimationSet.FileName(number),true);

        if(set is null)
        {
            Add("This set could not be read from the disc.");
            updatingAnimationSelectors=true;
            try
            {
                AnimationMotionBox.ItemsSource=null;
                AnimationPaletteBox.ItemsSource=null;
            }
            finally { updatingAnimationSelectors=false; }
            return;
        }

        int weapon=number/10%100,variant=number%10;
        Add($"Class art {number/1000}, weapon art {(SetWeapons.TryGetValue(weapon,out var w)?w:weapon.ToString())}, variant {variant}.");
        Add(set.HasFrames?$"{set.Motions.Count} motions: {string.Join(", ",set.Motions)}.":"This set has pictures but no frames, so it cannot be shown.");
        Add(set.Palettes.Count==0?"No numbered palettes.":$"Palettes: {string.Join(", ",set.Palettes)}.");

        var colors=new List<Choice>{new(0,"Set's own colors")};
        if(animationTable is { } table && AnimationList.SelectedValue is int cls && cls>0)
            for(int r=0;r<table.Palettes(cls).Count;r++)
            {
                var row=table.Palettes(cls)[r];int palNumber=AnimationValue(true,cls,r,row.Value);
                if(palNumber<=0 || !set.Palettes.Contains(palNumber))continue;
                string who=row.Unit>0?UnitName(row.Unit):AnimationTable.SideText(row.Unit);
                if(row.Key!=0)who+=$" holding {AnimationTable.KeyText(row.Key)}";
                colors.Add(new Choice(palNumber,$"{who} (palette {palNumber})"));
            }
        foreach(int pal in set.Palettes.Where(p=>!colors.Any(c=>c.Value==p)))colors.Add(new Choice(pal,$"Palette {pal}"));
        var seen=new HashSet<string>();

        // Populate the controls together, then initialize frames and bounds explicitly.
        // WPF need not raise SelectionChanged when the motion name stays the same.
        updatingAnimationSelectors=true;
        try
        {
            AnimationPaletteBox.ItemsSource=colors.Where(c=>seen.Add(c.Text)).ToList();
            AnimationPaletteBox.SelectedIndex=0;
            AnimationMotionBox.ItemsSource=set.Motions;
            AnimationMotionBox.SelectedIndex=set.Motions.Count>0?0:-1;
        }
        finally { updatingAnimationSelectors=false; }

        animationPalette=AnimationPaletteBox.SelectedValue is int palette?palette:0;
        if(AnimationMotionBox.SelectedItem is string motion)ShowAnimationMotion(set,motion);
    }

    private void ExportAnimation(object sender,RoutedEventArgs e)
    {
        if(shownAnimationSet is not { } set || primary is null || loading is not null || !set.Motions.Contains(animationMotion) || (set.Frames(animationMotion).Count==0 && set.EffectTracks(animationMotion).Count==0))return;
        new AnimationExportWindow(set,animationMotion,animationPalette,primary.Edition,primary.Sha256){Owner=this}.ShowDialog();
    }

    private void AnimationMotionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updatingAnimationSelectors || shownAnimationSet is not { } set || AnimationMotionBox.SelectedItem is not string motion)return;
        ShowAnimationMotion(set,motion);
    }

    private void ShowAnimationMotion(AnimationSet set,string motion)
    {
        animationTimer.Stop();
        animationMotion=motion;
        animationFrame=0;
        animationFrames=[];
        try
        {
            animationBounds=set.Bounds(motion);
            animationFrames=set.Frames(motion);
        }
        catch(EditException ex)
        {
            AnimationImage.Source=null;
            AnimationFrameText.Text=ex.Message;
            return;
        }
        animationTicksLeft=animationFrames.Count>0?animationFrames[0].Time:0;
        AnimationFrameScroll.ScrollToHome();
        DrawAnimationFrame();
        if(animationPlaying && animationFrames.Count>1 && view==View.Animations)animationTimer.Start();
    }

    private void AnimationPaletteChanged(object sender,SelectionChangedEventArgs e)
    {
        if(updatingAnimationSelectors)return;
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

}
