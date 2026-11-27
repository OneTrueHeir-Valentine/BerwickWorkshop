using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void RandomizeProficiencies(object sender, RoutedEventArgs e)
    {
        if (session is null || loading is not null) return;
        EndEdit();
        var window = new Window { Title = "Randomizer: weapon proficiency", Owner = this, Width = 700,
            Height = 780, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var settingsLayout = new DockPanel(); window.Content = settingsLayout;
        // Each field is announced by the heading above it, so screen readers do not read it as blank.
        TextBlock? lastHeading = null;
        T Labeled<T>(T control) where T : FrameworkElement
        {
            if (lastHeading is not null) System.Windows.Automation.AutomationProperties.SetLabeledBy(control, lastHeading);
            return control;
        }
        TextBlock Header(string s) => lastHeading = new TextBlock { Text = s, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 4) };
        TextBlock Note(string s) => new() { Text = s, Foreground = Brushes.DimGray,
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        TextBox Field(string s) => Labeled(new TextBox { Text = s, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(Note("Change characters' starting weapon proficiency or proficiency growth rates. A preview does not change the project. Class limits apply, but they do not necessarily mean a character can equip that weapon."));
        panel.Children.Add(Header("Profile name"));
        panel.Children.Add(Note("Give these settings a name so you can save and reuse them later."));
        var name = Field("Weapon proficiency variation"); panel.Children.Add(name);
        panel.Children.Add(Header("How to randomize"));
        var algorithm = Labeled(new ComboBox { ItemsSource = new[] { "Change each proficiency independently", "Move points between proficiencies (keep total)" }, SelectedIndex = 0 });
        panel.Children.Add(algorithm);
        var methodHelp = Note(""); panel.Children.Add(methodHelp);
        panel.Children.Add(Header("What to change"));
        var target = Labeled(new ComboBox { ItemsSource = new[] { "Starting weapon and shield levels", "Weapon and shield improvement rates" }, SelectedIndex = 0 });
        panel.Children.Add(target);
        var targetHelp = Note(""); panel.Children.Add(targetHelp);
        panel.Children.Add(Header("Advanced: include previously untrained weapon types"));
        var compatibility = Labeled(new ComboBox { ItemsSource = new[] {
            "No, leave previously untrained types unchanged (recommended)",
            "Yes, if current class limits allow them (experimental)" }, SelectedIndex = 0 });
        panel.Children.Add(compatibility);
        panel.Children.Add(Note("By default, only proficiencies with a nonzero stored value are changed. Allowing new proficiencies is experimental: a class limit does not guarantee the character can equip that weapon. Shield improvement is shared across all shield sizes."));
        panel.Children.Add(Header("Keep trained weapon levels"));
        var keepTrained = Labeled(new CheckBox { Content = "Never lower a level the character already has to zero (recommended)",
            IsChecked = true, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(keepTrained);
        var minimumHeading = Header(""); panel.Children.Add(minimumHeading);
        var trainedMinimum = Field("1"); panel.Children.Add(trainedMinimum);
        var keepHelp = Note(""); panel.Children.Add(keepHelp);
        panel.Children.Add(Header("Proficiencies to change"));
        panel.Children.Add(Note("Tick the weapon or shield types you want to change. Unticked types stay as they are."));
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
        panel.Children.Add(options);
        var chosen = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        void RebuildFields(string[] keys, IEnumerable<string>? selection = null)
        {
            var active = selection?.ToHashSet(StringComparer.Ordinal) ?? keys.ToHashSet(StringComparer.Ordinal);
            chosen.Clear(); options.Children.Clear();
            foreach (string key in keys)
            {
                var check = new CheckBox { Content = RandomizerFieldName(key), IsChecked = active.Contains(key),
                    Width = 150, Margin = new Thickness(0, 0, 6, 6) };
                chosen.Add(key, check); options.Children.Add(check);
            }
        }
        RebuildFields(R0ProficiencyProfile.StartingKeys);
        target.SelectionChanged += (_, _) => RebuildFields(target.SelectedIndex == 0
            ? R0ProficiencyProfile.StartingKeys : R0ProficiencyProfile.GrowthKeys);
        panel.Children.Add(Header("Random seed (repeatable with the same settings)"));
        var seed = Field("1729"); panel.Children.Add(seed);
        var limitHeading = Header(""); panel.Children.Add(limitHeading);
        var maximum = Field("5"); panel.Children.Add(maximum);
        var limitHelp = Note(""); panel.Children.Add(limitHelp);
        panel.Children.Add(Header("Characters to change"));
        var selection = new RandomizerCharacterSelection(session, window);
        panel.Children.Add(selection.View);
        var advancedPanel = new StackPanel();
        advancedPanel.Children.Add(Header("Redistribution attempts (1 to 512)"));
        var attempts = Field("32"); advancedPanel.Children.Add(attempts);
        advancedPanel.Children.Add(Note("How many times to try moving points between eligible proficiencies. The maximum change still applies. A character with fewer than two eligible fields may remain unchanged."));
        var advanced = new Expander { Header = "Advanced redistribution settings", Content = advancedPanel };
        panel.Children.Add(advanced);
        void ExplainSettings()
        {
            bool starting = target.SelectedIndex == 0;
            bool budget = algorithm.SelectedIndex == 1;
            methodHelp.Text = budget ? "Moves points between eligible selected proficiencies for each character. Their combined stored total stays the same."
                : "Each eligible selected proficiency can increase or decrease independently within the limit you set.";
            targetHelp.Text = starting ? "Changes the weapon and shield levels a character starts with. Class limits apply."
                : "Changes how quickly weapon and shield proficiency improves. The game stores these rates in steps of 10 percentage points.";
            limitHeading.Text = starting ? "Maximum level change (1 to 50 points)" : "Maximum improvement-rate change (1 to 50 steps of 10 percentage points)";
            limitHelp.Text = starting ? "With a limit of 5, a level of 20.3 may become 15.3 to 25.3, subject to class limits."
                : "With a limit of 2, a 40% rate may become 20% to 60%, within the game's limits. One step equals 10 percentage points.";
            advanced.Visibility = budget ? Visibility.Visible : Visibility.Collapsed;
            bool keep = keepTrained.IsChecked == true;
            minimumHeading.Text = starting ? "Keep trained levels at least (1 to 15 levels)"
                : "Keep trained improvement rates at least (1 to 15 steps of 10 percentage points)";
            minimumHeading.Visibility = trainedMinimum.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
            keepHelp.Text = keep
                ? "This keeps existing training; it is not a minimum for every level. A level the character already has stays at this value or more, or at its own starting value if that is lower, and never goes above the class limit. Untrained types stay at zero."
                : "Older profiles (version 1) work this way and give exactly the same preview as before, but a level can drop to zero. Such a preview can't be applied.";
        }
        keepTrained.Checked += (_, _) => ExplainSettings();
        keepTrained.Unchecked += (_, _) => ExplainSettings();
        algorithm.SelectionChanged += (_, _) => ExplainSettings();
        target.SelectionChanged += (_, _) => ExplainSettings();
        ExplainSettings();
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 7) };

        R0ProficiencyProfile Current()
        {
            if (!int.TryParse(seed.Text.Trim(), out int s) || !int.TryParse(maximum.Text.Trim(), out int max)
                || !int.TryParse(attempts.Text.Trim(), out int count))
                throw new EditException("Seed, maximum difference and attempts must be integers.");
            var profile = new R0ProficiencyProfile(name.Text,
                algorithm.SelectedIndex == 0 ? R0StatAlgorithm.BoundedVariance : R0StatAlgorithm.BudgetRedistribution,
                target.SelectedIndex == 0 ? R0ProficiencyTarget.StartingProficiencies : R0ProficiencyTarget.ProficiencyGrowths,
                compatibility.SelectedIndex == 0 ? R0ProficiencyCompatibility.PreserveTrainedChannels
                    : R0ProficiencyCompatibility.AllowClassPermittedChannels,
                s, max, count, selection.GetIds(), chosen.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray());
            if (keepTrained.IsChecked == true)
            {
                if (!int.TryParse(trainedMinimum.Text.Trim(), out int lowest))
                    throw new EditException("The lowest trained level must be a whole number from 1 to 15.");
                profile = profile with { SchemaVersion = R0ProficiencyProfile.KeepTrainedSchema, TrainedMinimum = lowest };
            }
            profile.Validate(); return profile;
        }
        void Show(R0ProficiencyProfile p)
        {
            selection.SetIds(p.UnitIds);
            name.Text = p.Name;
            algorithm.SelectedIndex = p.Mode == R0StatAlgorithm.BoundedVariance ? 0 : 1;
            target.SelectedIndex = p.Target == R0ProficiencyTarget.StartingProficiencies ? 0 : 1;
            compatibility.SelectedIndex = p.Compatibility == R0ProficiencyCompatibility.PreserveTrainedChannels ? 0 : 1;
            RebuildFields(p.Target == R0ProficiencyTarget.StartingProficiencies
                ? R0ProficiencyProfile.StartingKeys : R0ProficiencyProfile.GrowthKeys, p.Fields);
            seed.Text = p.Seed.ToString(); maximum.Text = p.MaximumChange.ToString();
            attempts.Text = p.TransferAttempts.ToString();
            keepTrained.IsChecked = p.SchemaVersion == R0ProficiencyProfile.KeepTrainedSchema;
            trainedMinimum.Text = Math.Max(1, p.TrainedMinimum).ToString();
            ExplainSettings();
        }
        var profiles = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var load = new Button { Content = "Load profile", Margin = new Thickness(0, 0, 10, 8) };
        var save = new Button { Content = "Save new profile", Margin = new Thickness(0, 0, 0, 8) };
        load.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Weapon proficiency profiles (*.bwr0proficiency.json)|*.bwr0proficiency.json" };
            if (picker.ShowDialog(window) != true) return;
            try { Show(R0ProficiencyProfileFiles.Load(picker.FileName)); error.Text = ""; }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or FormatException)
            { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "Weapon proficiency profiles (*.bwr0proficiency.json)|*.bwr0proficiency.json",
                FileName = "r0-proficiency.bwr0proficiency.json", OverwritePrompt = true };
            if (picker.ShowDialog(window) != true) return;
            try { R0ProficiencyProfileFiles.SaveNew(picker.FileName, Current()); error.Text = "Profile saved without changing the game."; }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or FormatException)
            { error.Text = ex.Message + " Existing profiles will not be overwritten."; }
        };
        panel.Children.Add(Note("Saving a profile keeps your settings for another run; it does not apply changes to the project."));
        profiles.Children.Add(load); profiles.Children.Add(save); panel.Children.Add(profiles);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Close", Margin = new Thickness(0, 0, 12, 0) };
        var previewButton = new Button { Content = "Preview changes", Style = (Style)FindResource("PrimaryButton") };
        cancel.Click += (_, _) => window.Close();
        previewButton.Click += (_, _) =>
        {
            if (session is null) return;
            try
            {
                var preview = R0ProficiencyRandomizer.Preview(session, Current());
                var dlg = new Window { Title = "Review proficiency changes (nothing applied yet)", Owner = window,
                    Width = 900, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var layout = new DockPanel { Margin = new Thickness(14) };
                bool starting = preview.Profile.Target == R0ProficiencyTarget.StartingProficiencies;
                string Value(int v) => starting ? (v / 10m).ToString("0.#") : $"{v * 10}%";
                var msg = Note($"{preview.Changes.Count} {(starting ? "starting level" : "improvement rate")} changes for "
                    + $"{preview.Changes.Select(x => x.UnitId).Distinct().Count()} characters. Nothing has been applied. "
                    + "Class limits do not guarantee equipment compatibility.");
                DockPanel.SetDock(msg, Dock.Top); layout.Children.Add(msg);
                var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
                var export = new Button { Content = "Save detailed report (JSON)", Margin = new Thickness(0, 0, 10, 0) };
                var stop = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
                var zeroed = R0ProficiencyRandomizer.ZeroedTrainedChannels(preview);
                var apply = new Button { Content = "Apply to project (one undo step)", IsEnabled = preview.Changes.Count > 0 && zeroed.Count == 0, Style = (Style)FindResource("PrimaryButton") };
                if (zeroed.Count > 0)
                {
                    var notice = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                    notice.Children.Add(new TextBlock { Foreground = Brushes.DarkGoldenrod, TextWrapping = TextWrapping.Wrap,
                        Text = $"This older profile lowers {zeroed.Count} trained {(zeroed.Count == 1 ? "level" : "levels")} to zero (" +
                            string.Join(", ", zeroed.Take(6).Select(z => $"{z.UnitName} {RandomizerFieldName(z.Field)}")) + (zeroed.Count > 6 ? ", ..." : "") +
                            "). Those characters can still attack, with very low accuracy. To avoid it, close this review, tick “Never lower a level the character already has to zero” and preview again." });
                    var allowZero = new CheckBox { Content = "Let these trained levels become zero", Margin = new Thickness(0, 4, 0, 0) };
                    allowZero.Checked += (_, _) => apply.IsEnabled = preview.Changes.Count > 0;
                    allowZero.Unchecked += (_, _) => apply.IsEnabled = false;
                    notice.Children.Add(allowZero);
                    DockPanel.SetDock(notice, Dock.Top); layout.Children.Add(notice);
                }
                export.Click += (_, _) =>
                {
                    var picker = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "r0-proficiency-preview.json" };
                    if (picker.ShowDialog(dlg) == true)
                    {
                        try { File.WriteAllText(picker.FileName, preview.ToJson(), new UTF8Encoding(false)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { MessageBox.Show(dlg, ex.Message, "Preview export failed"); }
                    }
                };
                stop.Click += (_, _) => dlg.DialogResult = false;
                apply.Click += (_, _) => dlg.DialogResult = true;
                buttons.Children.Add(export); buttons.Children.Add(stop); buttons.Children.Add(apply);
                DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
                if (preview.Warnings.Count > 0)
                {
                    string FriendlyWarning(string warning)
                    {
                        foreach (string key in R0ProficiencyProfile.StartingKeys.Concat(R0ProficiencyProfile.GrowthKeys))
                            warning = warning.Replace(key, RandomizerFieldName(key), StringComparison.Ordinal);
                        return warning;
                    }
                    var warnings = new Expander { Header = $"Compatibility warnings ({preview.Warnings.Count})", IsExpanded = true,
                        Content = new ScrollViewer { MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            Content = Note(string.Join(Environment.NewLine, preview.Warnings.Select(FriendlyWarning))) } };
                    DockPanel.SetDock(warnings, Dock.Top); layout.Children.Add(warnings);
                }
                layout.Children.Add(RandomizerReviewGrid(preview.Changes.Select(c => new RandomizerReviewRow(c.UnitName,
                    RandomizerFieldName(c.Field) + (starting ? " level" : " improvement rate"), Value(c.BeforeRaw), Value(c.AfterRaw))), "Proficiency"));
                dlg.Content = layout;
                if (dlg.ShowDialog() != true) return;
                // Apply is only enabled once any zero levels have been confirmed.
                bool didApply = R0ProficiencyRandomizer.Apply(session, preview, allowZeroedTrainedLevels: true);
                RefreshDocument();
                Status.Text = didApply ? $"Applied {preview.Changes.Count} proficiency changes in one undo step."
                    : "The preview produced no proficiency changes.";
                window.Close();
            }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or FormatException or OverflowException)
            { error.Text = "Unable to preview or apply proficiencies: " + ex.Message; }
        };
        actions.Children.Add(cancel); actions.Children.Add(previewButton);
        var footer = new StackPanel { Margin = new Thickness(18, 6, 18, 10) };
        footer.Children.Add(error); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); settingsLayout.Children.Add(footer);
        settingsLayout.Children.Add(scroll);
        window.ShowDialog();
    }
}
