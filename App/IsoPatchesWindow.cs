using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Berwick.Workshop;

/// <summary>
/// Makes and applies xdelta3 patches for whole disc images: from the open project or from two images, one patch, or
/// several in order. xdelta3 is not included; the user points to the official 3.2.2 program, which is checked by hash.
/// </summary>
/// <summary>The open project as the patch window needs it: the original disc, the project's changes and how to describe them.</summary>
internal sealed record PatchProject(string Path, string Sha256, IReadOnlyList<DiscPatch> Changes, string Title, string Edition, int ChangeCount);

internal sealed class IsoPatchesWindow : Window
{
    private readonly Func<PatchProject?> project;
    private XdeltaTool? tool;
    private BundledTool? bundled;
    private CancellationTokenSource? running;

    private readonly TextBlock toolText = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) };
    private readonly TabControl tabs = new() { Margin = new(0, 12, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 6, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new(0, 12, 0, 0) };
    private readonly TextBlock result = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly Button cancel = new() { Content = "Cancel", MinWidth = 90, IsEnabled = false, Margin = new(8, 0, 0, 0) };
    private readonly List<Button> actions = [];

    // Make a patch
    private readonly RadioButton fromProject = Option("From the open project (the original disc and this project's changes)", "source", true);
    private readonly RadioButton fromImages = Option("From two disc images (an original and a changed copy)", "source", false);
    private readonly PathRow makeOriginal = new("Original image", "Disc images (*.iso)|*.iso|All files|*.*");
    private readonly PathRow makeChanged = new("Changed image", "Disc images (*.iso)|*.iso|All files|*.*");
    private readonly PathRow makeOutput = new("New patch file", "xdelta patches (*.xdelta)|*.xdelta", save: true);
    private readonly RadioButton compatible = Option("Compatible (recommended): no extra compression. Older xdelta3 3.0.11 applied this format correctly in testing.", "format", true);
    private readonly RadioButton compact = Option("Compact: adds LZMA compression. The program that applies it must support LZMA, and the patch is not always smaller.", "format", false);
    private readonly TextBlock makeEstimate = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };

    // Apply a patch
    private readonly PathRow applySource = new("Disc image to patch", "Disc images (*.iso)|*.iso|All files|*.*");
    private readonly PathRow applyPatch = new("Patch file", "Patches (*.xdelta;*.vcdiff)|*.xdelta;*.vcdiff|All files|*.*");
    private readonly PathRow applyOutput = new("New disc image", "Disc images (*.iso)|*.iso", save: true);

    // Several in order
    private readonly PathRow chainSource = new("Starting disc image", "Disc images (*.iso)|*.iso|All files|*.*");
    private readonly ListBox chainList = new() { Height = 110, Margin = new(0, 6, 0, 6) };
    private readonly PathRow chainOutput = new("Final disc image", "Disc images (*.iso)|*.iso", save: true);

    public IsoPatchesWindow(Func<PatchProject?> project)
    {
        this.project = project;
        Title = "ISO patches";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 60);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Content = Build();
        bundled = XdeltaPatches.FindBundled(AppContext.BaseDirectory);
        tool = bundled.Tool;
        UpdateTool();
        UpdateMake();
        cancel.Click += (_, _) => running?.Cancel();
        Closing += (_, e) => { if (running is not null) { e.Cancel = true; result.Text = "Cancel the running task before closing."; } };
    }

    private ScrollViewer Build()
    {
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "ISO patches", FontSize = 22 });
        panel.Children.Add(Wrap("A patch holds only the differences between two disc images, so it is small enough to share. Applying it to the same original disc gives the changed image. Share the patch, and say which original disc it needs (for example English v2.3); never share disc images themselves. Patches are made and applied with xdelta3 3.2.2, which comes with Berwick Workshop in its tools folder and is checked before every use. Original disc images and existing files are never changed or replaced.", new(0, 8, 0, 12)));
        var toolButton = Action("Choose xdelta3...", ChooseTool);
        toolButton.IsEnabled = true;
        panel.Children.Add(toolText);
        panel.Children.Add(Row(toolButton));

        var make = new StackPanel { Margin = new(12) };
        make.Children.Add(fromProject);
        make.Children.Add(fromImages);
        make.Children.Add(makeOriginal.Panel);
        make.Children.Add(makeChanged.Panel);
        make.Children.Add(makeOutput.Panel);
        make.Children.Add(new TextBlock { Text = "Patch format", FontWeight = FontWeights.SemiBold, Margin = new(0, 10, 0, 4) });
        make.Children.Add(compatible);
        make.Children.Add(compact);
        make.Children.Add(makeEstimate);
        make.Children.Add(Row(Action("Make patch", Make)));
        fromProject.Checked += (_, _) => UpdateMake();
        fromImages.Checked += (_, _) => UpdateMake();
        tabs.Items.Add(new TabItem { Header = "Make a patch", Content = make });

        var apply = new StackPanel { Margin = new(12) };
        apply.Children.Add(Wrap("If the patch has a manifest file next to it (the same name with .json added), the disc image must be the one it names and the result must match the hash it gives. A match shows the file is the one described, not who made it or whether it plays well. Without a manifest, the disc image must be a known original edition, and the result is reported as a known edition or as unrecognized. The drive needs room for one more disc image.", new(0, 0, 0, 8)));
        apply.Children.Add(applySource.Panel);
        apply.Children.Add(applyPatch.Panel);
        apply.Children.Add(applyOutput.Panel);
        apply.Children.Add(Row(Action("Apply patch", Apply)));
        tabs.Items.Add(new TabItem { Header = "Apply a patch", Content = apply });

        var chain = new StackPanel { Margin = new(12) };
        chain.Children.Add(Wrap("Applies patches one after another. Every patch needs its manifest, and each one must start from the result of the one before it; the order is checked before anything runs. A patch without a manifest can still be applied on its own on the Apply a patch tab. Only the final image is kept, but the drive needs room for about two disc images while this runs.", new(0, 0, 0, 8)));
        chain.Children.Add(chainSource.Panel);
        chain.Children.Add(new TextBlock { Text = "Patches, in order", FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 0) });
        AutomationProperties.SetName(chainList, "Patches, in order");
        chain.Children.Add(chainList);
        var add = Action("Add patch...", AddChainPatch); add.IsEnabled = true;
        var up = Action("Move up", () => MoveChain(-1)); up.IsEnabled = true;
        var remove = Action("Remove", () => { if (chainList.SelectedIndex >= 0) chainList.Items.RemoveAt(chainList.SelectedIndex); }); remove.IsEnabled = true;
        chain.Children.Add(Row(add, up, remove));
        chain.Children.Add(chainOutput.Panel);
        chain.Children.Add(Row(Action("Apply in order", Chain)));
        tabs.Items.Add(new TabItem { Header = "Apply several in order", Content = chain });

        panel.Children.Add(tabs);
        panel.Children.Add(progress);
        panel.Children.Add(result);
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new(8, 0, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        footer.Children.Add(cancel);
        footer.Children.Add(close);
        panel.Children.Add(footer);
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Button Action(string text, Action handler)
    {
        var button = new Button { Content = text, MinWidth = 110, Margin = new(0, 8, 8, 0), Padding = new(10, 4, 10, 4) };
        button.Click += (_, _) => handler();
        if (text is "Make patch" or "Apply patch" or "Apply in order") actions.Add(button);
        return button;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>A choice whose label wraps at the window edge instead of being cut off; screen readers still hear the whole label.</summary>
    private static RadioButton Option(string text, string group, bool chosen)
    {
        var option = new RadioButton { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, GroupName = group, IsChecked = chosen,
            Margin = chosen ? new(0) : new(0, 4, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(option, text);
        return option;
    }

    private static TextBlock Wrap(string text, Thickness margin) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };

    private void UpdateTool()
    {
        const string choose = " You can also choose a copy of the official xdelta3 3.2.2 for Windows x64 (xdelta3-3.2.2-windows-x86_64.zip from github.com/jmacd/xdelta); other versions and builds are refused without being run.";
        toolText.Text = tool is not null
            ? tool.Path == bundled?.Path ? "xdelta3 3.2.2, included with Berwick Workshop (verified)." : $"xdelta3 3.2.2 (verified): {tool.Path}"
            : bundled?.State == BundledToolState.NotVerified
                ? "The xdelta3 in Berwick Workshop's tools folder is not the verified 3.2.2 program, so it will not be run. Unzip the release download again into a new folder." + choose
                : "xdelta3 was not found in Berwick Workshop's tools folder. Unzip the whole release download, keeping the tools folder next to Berwick Workshop.exe." + choose;
        foreach (var b in actions) b.IsEnabled = tool is not null && running is null;
    }

    private void UpdateMake()
    {
        var open = project();
        fromProject.IsEnabled = open is not null;
        if (open is null && fromProject.IsChecked == true) { fromImages.IsChecked = true; return; } // Checked runs this again.
        bool images = fromImages.IsChecked == true;
        makeOriginal.Panel.Visibility = makeChanged.Panel.Visibility = images ? Visibility.Visible : Visibility.Collapsed;
        makeEstimate.Text = images
            ? "Making a patch reads both images in full. The patch itself is usually small."
            : open is { } p
                ? $"Project: {p.Title}, {p.ChangeCount} {(p.ChangeCount == 1 ? "change" : "changes")} to {p.Edition}. The patch will need the original {p.Edition} disc. The changes are first written to a temporary disc image next to the patch, about {new FileInfo(p.Path).Length / 1073741824.0:0.0} GB, which is deleted afterwards, so the drive needs that much free space."
                : "Open a disc and make some changes first, or choose two disc images.";
    }

    private void ChooseTool()
    {
        var picker = new OpenFileDialog { Title = "Choose xdelta3.exe", Filter = "xdelta3 (xdelta3.exe)|xdelta3.exe|Programs (*.exe)|*.exe" };
        if (picker.ShowDialog(this) != true) return;
        string sha = XdeltaPatches.HashFile(picker.FileName);
        if (!string.Equals(sha, XdeltaTool.Release322WindowsX64Sha256, StringComparison.OrdinalIgnoreCase))
        {
            result.Text = "That program is not the official xdelta3 3.2.2 for Windows x64, so it will not be used. Nothing was run.";
            return;
        }
        tool = new XdeltaTool(picker.FileName, sha, "3.2.2");
        result.Text = "";
        UpdateTool();
    }

    private void AddChainPatch()
    {
        var picker = new OpenFileDialog { Title = "Add a patch", Filter = "Patches (*.xdelta;*.vcdiff)|*.xdelta;*.vcdiff|All files|*.*", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (string file in picker.FileNames) chainList.Items.Add(file);
    }

    private void MoveChain(int step)
    {
        int i = chainList.SelectedIndex;
        if (i < 0 || i + step < 0 || i + step >= chainList.Items.Count) return;
        var item = chainList.Items[i];
        chainList.Items.RemoveAt(i);
        chainList.Items.Insert(i + step, item);
        chainList.SelectedIndex = i + step;
    }

    private XdeltaProfile Profile => compact.IsChecked == true ? XdeltaProfile.Compact : XdeltaProfile.Compatible;

    private void Make()
    {
        if (makeOutput.Path is not { } output) { result.Text = "Choose a name for the new patch file."; return; }
        var profile = Profile;
        if (fromImages.IsChecked == true)
        {
            if (makeOriginal.Path is not { } original || makeChanged.Path is not { } changed) { result.Text = "Choose both disc images."; return; }
            Run("Making patch", (t, ph) => XdeltaPatches.Create(tool!, original, XdeltaPatches.HashFile(original), changed, output, profile, t, ph),
                m => $"Patch made: {Path.GetFileName(output)} and its manifest {Path.GetFileName(XdeltaPatches.ManifestPathFor(output))}.\nOriginal SHA-256 {m.SourceSha256}\nResult SHA-256 {m.TargetSha256}\nFormat: {m.Profile}.");
            return;
        }
        if (project() is not { } p) { result.Text = "Open a disc and make some changes first."; return; }
        Run("Making patch from the project", (t, ph) => XdeltaPatches.CreateFromProject(tool!, p.Path, p.Sha256, p.Changes, output, profile, t, ph),
            m => $"Patch made from {p.Title}: {Path.GetFileName(output)} and its manifest.\nIt needs the original {p.Edition} disc ({m.SourceSha256[..12]}…) and gives the same disc image as exporting the project ({m.TargetSha256[..12]}…). Format: {m.Profile}. The project was not changed.");
    }

    private void Apply()
    {
        if (applySource.Path is not { } source || applyPatch.Path is not { } patch || applyOutput.Path is not { } output)
        { result.Text = "Choose the disc image, the patch and a name for the new disc image."; return; }
        string sidecar = XdeltaPatches.ManifestPathFor(patch);
        Run("Applying patch", (t, ph) =>
        {
            var manifest = File.Exists(sidecar) ? XdeltaPatches.ReadManifest(sidecar) : null;
            return XdeltaPatches.Apply(tool!, source, patch, output, manifest, t, ph);
        }, Describe);
    }

    private void Chain()
    {
        if (chainSource.Path is not { } source || chainOutput.Path is not { } output || chainList.Items.Count == 0)
        { result.Text = "Choose the starting disc image, at least one patch and a name for the final disc image."; return; }
        var patches = chainList.Items.Cast<string>().ToList();
        Run("Applying patches in order", (t, ph) =>
        {
            var steps = patches.Select(p =>
            {
                string sidecar = XdeltaPatches.ManifestPathFor(p);
                if (!File.Exists(sidecar)) throw new EditException($"{Path.GetFileName(p)} has no manifest, so its place in the order cannot be checked.");
                return (p, XdeltaPatches.ReadManifest(sidecar));
            }).ToList();
            return XdeltaPatches.ApplyChain(tool!, source, steps, output, t, ph);
        }, Describe);
    }

    private static string Describe(PatchResult r) => r.Identity switch
    {
        PatchedIdentity.ExpectedTarget => $"Done. {Path.GetFileName(r.Path)} matches the result the patch's manifest describes" +
                                          (r.Edition is { } e ? $" ({e})." : ".") + $"\nSHA-256 {r.Sha256}",
        PatchedIdentity.KnownEdition => $"Done. {Path.GetFileName(r.Path)} is {r.Edition}.\nSHA-256 {r.Sha256}",
        _ => $"Done, but the result is not a disc Berwick Workshop knows, and the patch had no manifest to check it against. Treat {Path.GetFileName(r.Path)} as unverified.\nSHA-256 {r.Sha256}",
    };

    private async void Run<T>(string what, Func<CancellationToken, IProgress<string>, T> work, Func<T, string> done)
    {
        if (running is not null || tool is null) return;
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        progress.Visibility = Visibility.Visible;
        cancel.IsEnabled = true;
        tabs.IsEnabled = false;
        UpdateTool();
        result.Text = $"{what}… This can take a few minutes for a whole disc.";
        var phase = new Progress<string>(text => { if (running == cancellation) result.Text = $"{what}: {text}… xdelta3 does not report how far it has got, so the bar only shows that work is going on."; });
        try
        {
            var value = await Task.Run(() => work(cancellation.Token, phase));
            result.Text = done(value);
        }
        catch (OperationCanceledException) { result.Text = "Canceled. No new file was kept."; }
        catch (EditException ex) { result.Text = $"{ex.Message}\nNo new file was kept, and the chosen files were not changed."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { result.Text = "A file could not be read or written. Check that the folders can be written to and the files are not open elsewhere."; }
        finally
        {
            running = null;
            progress.Visibility = Visibility.Collapsed;
            cancel.IsEnabled = false;
            tabs.IsEnabled = true;
            UpdateTool();
        }
    }

    /// <summary>A labelled file name with a Choose button.</summary>
    private sealed class PathRow
    {
        private readonly TextBox box = new() { IsReadOnly = true, MinWidth = 420, Margin = new(0, 2, 8, 0) };
        public StackPanel Panel { get; } = new() { Margin = new(0, 8, 0, 0) };
        public string? Path => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text;

        public PathRow(string label, string filter, bool save = false)
        {
            Panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
            AutomationProperties.SetName(box, label);
            var choose = new Button { Content = "Choose...", MinWidth = 90 };
            AutomationProperties.SetName(choose, $"Choose {label.ToLowerInvariant()}");
            choose.Click += (_, _) =>
            {
                FileDialog picker = save ? new SaveFileDialog { Filter = filter, OverwritePrompt = false, AddExtension = true } : new OpenFileDialog { Filter = filter };
                picker.Title = label;
                if (picker.ShowDialog(Window.GetWindow(Panel)) == true) box.Text = picker.FileName;
            };
            var row = new DockPanel();
            DockPanel.SetDock(choose, Dock.Right);
            row.Children.Add(choose);
            row.Children.Add(box);
            Panel.Children.Add(row);
        }
    }
}
