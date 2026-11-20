using System.IO;
using System.Windows;
using System.Windows.Controls;
using Berwick.Core;
using Microsoft.Win32;

namespace Berwick.Workshop;

internal sealed class AnimationExportWindow : Window
{
    private readonly ComboBox scope = new() { ItemsSource = new[] { "Current motion", "All motions in this set" }, SelectedIndex = 0 };
    private readonly ComboBox output = new() { ItemsSource = new[] { "PNG frames and sprite sheets", "PNG frames", "Sprite sheets", "Effect layers with metadata" }, SelectedIndex = 0 };
    private readonly ComboBox canvas = new() { ItemsSource = new[] { "Expanded canvas", "Crop to the whole motion", "Legacy 288 by 288" }, SelectedIndex = 0 };
    private readonly TextBox parent = new() { IsReadOnly = true };
    private readonly TextBox name = new();
    private readonly TextBlock explanation = new() { Text = AnimationExport.Limitations, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 16, 0, 0) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 100, Margin = new(0, 12, 0, 0) };
    private readonly Button export = new() { Content = "Export", Margin = new(0, 0, 8, 0) };
    private readonly Button cancel = new() { Content = "Close" };
    private readonly Button browse = new() { Content = "Choose folder", Margin = new(8, 0, 0, 0) };
    private CancellationTokenSource? cancellation;

    public AnimationExportWindow(AnimationSet set, string motion, int palette, string edition, string sourceHash)
    {
        Title = "Export animation"; Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Export animation", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = $"{AnimationSet.FileName(set.Number)} · {motion} · Palette {palette}", Margin = new(0, 8, 0, 16) });
        void Field(string label, UIElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) }); panel.Children.Add(control);
        }
        Field("Motions", scope); Field("Files", output); Field("Frame area", canvas);
        var location = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); location.Children.Add(browse); location.Children.Add(parent);
        Field("Save in", location);
        parent.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        name.Text = $"{AnimationSet.FileName(set.Number)[..^3]}-{DateTime.Now:yyyyMMdd-HHmmss}";
        Field("New export folder", name);
        panel.Children.Add(explanation);
        output.SelectionChanged += (_, _) =>
        {
            bool effects = output.SelectedIndex == 3;
            canvas.IsEnabled = !effects;
            explanation.Text = effects ? EffectExport.Description : AnimationExport.Limitations;
            if (effects) scope.SelectedIndex = 1;
        };
        if (set.Frames(motion).Count == 0 && set.EffectTracks(motion).Count > 0) output.SelectedIndex = 3;
        panel.Children.Add(progress); panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0) };
        buttons.Children.Add(export); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        browse.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "Choose animation export location", InitialDirectory = parent.Text }; if (picker.ShowDialog(this) == true) parent.Text = picker.FolderName; };
        cancel.Click += (_, _) => { if (cancellation is not null) { cancellation.Cancel(); status.Text = "Canceling export..."; } else Close(); };
        Closing += (_, e) => { if (cancellation is not null) { e.Cancel = true; cancellation.Cancel(); status.Text = "Canceling export..."; } };
        export.Click += async (_, _) =>
        {
            if (cancellation is not null) return;
            string folderName = name.Text.Trim();
            if (folderName.Length == 0 || folderName is "." or ".." || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName.EndsWith('.') || folderName.EndsWith(' '))
            { status.Text = "Choose a valid new export folder name."; return; }
            string destination;
            try { destination = Path.Combine(Path.GetFullPath(parent.Text), folderName); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { status.Text = "Choose an existing location for the export."; return; }
            string[] motions = scope.SelectedIndex == 0 ? new[] { motion } : set.Motions.ToArray();
            bool effects = output.SelectedIndex == 3;
            bool frames = output.SelectedIndex != 2, sheets = output.SelectedIndex != 1, crop = canvas.SelectedIndex == 1, legacy = canvas.SelectedIndex == 2;
            using var token = new CancellationTokenSource(); cancellation = token;
            foreach (var control in new Control[] { scope, output, canvas, parent, name, browse, export }) control.IsEnabled = false;
            cancel.Content = "Cancel"; progress.IsIndeterminate = true; status.Text = "Preparing animation frames...";
            var reporter = new Progress<AnimationExportProgress>(p => { progress.IsIndeterminate = false; progress.Value = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total; status.Text = $"{p.Motion}: {p.Completed} of {p.Total} frames"; });
            try
            {
                int skipped = 0;
                await Task.Run(() =>
                {
                    if (effects)
                    {
                        EffectExport.Write(destination, new(edition,sourceHash,set.Number,palette),set,motions,AnimationPng.Write,reporter,token.Token);
                        return;
                    }
                    var plans = new List<AnimationMotionExport>();
                    foreach (string selected in motions)
                    {
                        token.Token.ThrowIfCancellationRequested();
                        var sequence = set.Frames(selected);
                        if (sequence.Count == 0) { skipped++; continue; }
                        plans.Add(AnimationExport.Plan(selected, sequence, picture => set.Draw(selected, picture, palette, legacy), crop, cancellation: token.Token));
                    }
                    if (plans.Count == 0) throw new EditException("The selected motions have no display frames to export.");
                    AnimationExport.Write(destination, new(edition, sourceHash, set.Number, palette), plans,
                        (selected, picture) => set.Draw(selected, picture, palette, legacy), AnimationPng.Write, frames, sheets, reporter, token.Token);
                });
                progress.Value = 100; status.Text = $"Exported to {destination}" + (skipped > 0 ? $". Skipped {skipped} motions without display frames." : ".");
            }
            catch (OperationCanceledException) { status.Text = "Export canceled. No completed export was created."; }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { status.Text = "The animation could not be exported. " + ex.Message; }
            finally
            {
                cancellation = null; progress.IsIndeterminate = false; cancel.Content = "Close";
                foreach (var control in new Control[] { scope, output, canvas, parent, name, browse, export }) control.IsEnabled = true;
                canvas.IsEnabled = output.SelectedIndex != 3;
            }
        };
    }
}
