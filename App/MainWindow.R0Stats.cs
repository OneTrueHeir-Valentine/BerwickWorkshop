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
    /// <summary>Configure and review personal stat and growth changes.</summary>
    private void RandomizeStats(object sender, RoutedEventArgs e)
    {
        if (session is null || loading is not null) return;
        EndEdit();
        var window = new Window { Title = "Randomizer: stats and growth rates", Owner = this,
            Width = 640, Height = 710, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(22) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var settingsLayout = new DockPanel(); window.Content = settingsLayout;
        TextBlock Explanation(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 9) };
        // Each field is announced by the heading above it, so screen readers do not read it as blank.
        TextBlock? lastHeading = null;
        T Labeled<T>(T control) where T : FrameworkElement
        {
            if (lastHeading is not null) System.Windows.Automation.AutomationProperties.SetLabeledBy(control, lastHeading);
            return control;
        }
        TextBlock Heading(string message) => lastHeading = new TextBlock { Text = message, FontWeight = FontWeights.SemiBold,
            FontSize = 13, Margin = new Thickness(0, 10, 0, 5) };
        TextBox Text(string defaultValue) => Labeled(new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 7) });
        panel.Children.Add(Explanation("Change how characters start or improve when they level up. The class base stats stay the same. Review the changes before applying them to your project."));
        panel.Children.Add(Heading("Profile name"));
        panel.Children.Add(Explanation("Give these settings a name so you can save and reuse them later."));
        var profileName = Text("Character stat variation"); panel.Children.Add(profileName);
        panel.Children.Add(Heading("How to randomize"));
        var mode = Labeled(new ComboBox { ItemsSource = new[] { "Change each stat independently", "Move points between stats (keep total)" },
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(mode);
        var methodHelp = Explanation(""); panel.Children.Add(methodHelp);
        panel.Children.Add(Heading("What to change"));
        var target = Labeled(new ComboBox { ItemsSource = new[] { "Growth rates (level-up chances)", "Starting stat bonuses (personal contribution)" },
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(target);
        var interpretation = Explanation("");
        panel.Children.Add(interpretation);
        // Shown only after loading a version 1 growth profile, which changed Mind in stored points.
        var storedPoints = new CheckBox { Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Text = "Replay the loaded profile in stored points (Mind changes count two stored points per percentage point, as when it was saved)" },
            Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        panel.Children.Add(storedPoints);
        panel.Children.Add(Heading("Stats to change"));
        panel.Children.Add(Explanation("Tick the stats you want to change. Unticked stats stay as they are."));
        var fields = new[] { "HP", "Strength", "Defense", "Agility", "Mind" }
            .ToDictionary(k => k, k => new CheckBox { Content = k, IsChecked = true, Margin = new Thickness(0, 0, 0, 4) });
        var fieldGrid = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var c in fields.Values) { c.Width = 110; fieldGrid.Children.Add(c); }
        panel.Children.Add(fieldGrid);
        panel.Children.Add(Heading("Random seed (same seed and settings give the same results)"));
        var seed = Text("1729"); panel.Children.Add(seed);
        var limitHeading = Heading(""); panel.Children.Add(limitHeading);
        var maxDelta = Text("10"); panel.Children.Add(maxDelta);
        var limitHelp = Explanation(""); panel.Children.Add(limitHelp);
        panel.Children.Add(Heading("Characters to change"));
        var selection = new RandomizerCharacterSelection(session, window);
        panel.Children.Add(selection.View);
        var advancedPanel = new StackPanel();
        advancedPanel.Children.Add(Heading("Redistribution attempts (1 to 256)"));
        var attempts = Text("24"); advancedPanel.Children.Add(attempts);
        advancedPanel.Children.Add(Explanation("How many times to try moving points between the selected stats. More attempts do not guarantee more change. The limit above still applies."));
        var advanced = new Expander { Header = "Advanced redistribution settings", Content = advancedPanel };
        panel.Children.Add(advanced);
        void ExplainSettings()
        {
            bool growth = target.SelectedIndex == 0;
            bool budget = mode.SelectedIndex == 1;
            bool legacy = growth && storedPoints.Visibility == Visibility.Visible && storedPoints.IsChecked == true;
            methodHelp.Text = budget ? "Moves points between two or more selected stats for each character. Their combined total stays the same. This is not a guarantee of equal combat strength."
                : "Each selected stat can increase or decrease separately, within the limit you set. The total can change.";
            interpretation.Text = !growth ? "A personal bonus is the character's contribution to a stat, added to the class base. Changing it is not the same as directly setting the final displayed stat."
                : legacy ? "Growth rates determine how stats improve on level-up. This loaded profile changes Mind in stored points: the game halves the stored Mind value, so two stored points make one percentage point."
                : "Growth rates determine how stats improve on level-up. Changing a 25% growth to 35% adds 10 percentage points. Mind is shown and changed as the percentage the game uses.";
            limitHeading.Text = growth ? "Maximum growth change (1 to 63 points)" : "Maximum bonus change (1 to 63 points)";
            limitHelp.Text = !growth ? "With a limit of 3, a personal bonus of 5 may become 2 to 8, within the game's limits."
                : legacy ? "With a limit of 10, a 25% growth may become 15% to 35%, within the game's limits. For Mind the limit counts stored points, so 10 is at most 5 percentage points."
                : "With a limit of 10, a 25% growth may become 15% to 35%, within the game's limits.";
            advanced.Visibility = budget ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => ExplainSettings();
        target.SelectionChanged += (_, _) => ExplainSettings();
        storedPoints.Checked += (_, _) => ExplainSettings();
        storedPoints.Unchecked += (_, _) => ExplainSettings();
        ExplainSettings();
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 8) };

        R0StatProfile CurrentProfile()
        {
            if (!int.TryParse(seed.Text.Trim(), out int s) || !int.TryParse(maxDelta.Text.Trim(), out int delta)
                || !int.TryParse(attempts.Text.Trim(), out int count))
                throw new EditException("Seed, maximum change and transfer attempts must be whole numbers.");
            string[] keys = target.SelectedIndex == 0 ? R0StatProfile.GrowthFieldKeys : R0StatProfile.BonusFieldKeys;
            string[] selected = fields.Select((pair, i) => (pair, i))
                .Where(x => x.pair.Value.IsChecked == true).Select(x => keys[x.i]).ToArray();
            bool growth = target.SelectedIndex == 0;
            bool legacy = storedPoints.Visibility == Visibility.Visible && storedPoints.IsChecked == true;
            var profile = new R0StatProfile(profileName.Text,
                mode.SelectedIndex == 0 ? R0StatAlgorithm.BoundedVariance : R0StatAlgorithm.BudgetRedistribution,
                growth ? R0StatTarget.GrowthRates : R0StatTarget.PersonalBonuses,
                s, delta, count, selection.GetIds(), selected)
            { SchemaVersion = growth && !legacy ? R0StatProfile.PercentSchema : R0StatProfile.StoredPointSchema };
            profile.Validate();
            return profile;
        }
        void ShowProfile(R0StatProfile value)
        {
            selection.SetIds(value.UnitIds);
            profileName.Text = value.Name;
            mode.SelectedIndex = value.Mode == R0StatAlgorithm.BoundedVariance ? 0 : 1;
            target.SelectedIndex = value.Target == R0StatTarget.GrowthRates ? 0 : 1;
            seed.Text = value.Seed.ToString(); maxDelta.Text = value.MaximumChange.ToString();
            attempts.Text = value.TransferAttempts.ToString();
            string[] keys = value.Target == R0StatTarget.GrowthRates
                ? R0StatProfile.GrowthFieldKeys : R0StatProfile.BonusFieldKeys;
            int i = 0;
            foreach (var item in fields) item.Value.IsChecked = value.Fields.Contains(keys[i++], StringComparer.Ordinal);
            // A saved version 1 growth profile keeps its stored-point meaning unless the user unticks this.
            storedPoints.Visibility = value.UsesStoredMindPoints ? Visibility.Visible : Visibility.Collapsed;
            storedPoints.IsChecked = value.UsesStoredMindPoints;
            ExplainSettings();
        }
        var profileActions = new WrapPanel { Margin = new Thickness(0, 3, 0, 9) };
        var load = new Button { Content = "Load saved profile", Margin = new Thickness(0, 0, 10, 6) };
        var save = new Button { Content = "Save new profile", Margin = new Thickness(0, 0, 10, 6) };
        load.Click += (_, _) =>
        {
            var pick = new OpenFileDialog { Filter = "Stat randomizer profiles (*.bwr0stats.json)|*.bwr0stats.json", Multiselect = false };
            if (pick.ShowDialog(window) != true) return;
            try { ShowProfile(R0StatProfileFiles.Load(pick.FileName)); error.Text = ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EditException or FormatException)
            { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            try
            {
                var value = CurrentProfile();
                var pick = new SaveFileDialog { Filter = "Stat randomizer profiles (*.bwr0stats.json)|*.bwr0stats.json",
                    FileName = value.Mode == R0StatAlgorithm.BoundedVariance ? "r0-bounded.bwr0stats.json" : "r0-budget.bwr0stats.json",
                    OverwritePrompt = false };
                if (pick.ShowDialog(window) != true) return;
                R0StatProfileFiles.SaveNew(pick.FileName, value);
                error.Text = "New profile saved. No project edits made.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EditException or FormatException or OverflowException)
            { error.Text = ex.Message + " Choose a new filename if the profile already exists."; }
        };
        panel.Children.Add(Explanation("Saving a profile keeps your settings for another run; it does not apply changes to the project."));
        profileActions.Children.Add(load); profileActions.Children.Add(save); panel.Children.Add(profileActions);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Close", Margin = new Thickness(0, 0, 10, 5) };
        var previewButton = new Button { Content = "Preview changes", Style = (Style)FindResource("PrimaryButton"),
            Margin = new Thickness(0, 0, 0, 5) };
        cancel.Click += (_, _) => window.Close();
        previewButton.Click += (_, _) =>
        {
            if (session is null) return;
            try
            {
                var preview = R0StatRandomizer.Preview(session, CurrentProfile());
                var inspect = new Window { Title = "Review stat changes (nothing applied yet)", Owner = window,
                    Width = 850, Height = 630, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var pane = new DockPanel { Margin = new Thickness(14) };
                bool growth = preview.Profile.Target == R0StatTarget.GrowthRates;
                bool legacy = preview.Profile.UsesStoredMindPoints;
                string Value(string field, int v) => !growth ? v.ToString("+0;-0;0")
                    : legacy && field == MindGrowth.Field ? $"{v} stored ({MindGrowth.Percent(v)}%)" : $"{v}%";
                var header = Explanation($"{preview.Changes.Count} {(growth ? "growth rate" : "personal bonus")} changes for "
                    + $"{preview.Changes.Select(c => c.UnitId).Distinct().Count()} characters. Nothing has been applied. "
                    + (preview.Profile.Mode == R0StatAlgorithm.BudgetRedistribution
                        ? (legacy ? "Selected stored totals stay the same; this does not guarantee equal gameplay strength."
                            : "Each character's selected total stays the same; this does not guarantee equal gameplay strength.")
                        : "Each selected value was varied within the chosen limit."));
                DockPanel.SetDock(header, Dock.Top); pane.Children.Add(header);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var export = new Button { Content = "Save detailed report (JSON)", Margin = new Thickness(0, 0, 12, 0) };
                var stop = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
                var apply = new Button { Content = "Apply to project (one undo step)", IsEnabled = preview.Changes.Count > 0, Style = (Style)FindResource("PrimaryButton") };
                export.Click += (_, _) =>
                {
                    var picker = new SaveFileDialog { FileName = "r0-stat-preview.json", Filter = "JSON (*.json)|*.json" };
                    if (picker.ShowDialog(inspect) == true)
                    {
                        try { File.WriteAllText(picker.FileName, preview.ToJson(), new UTF8Encoding(false)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { MessageBox.Show(inspect, ex.Message, "Preview export error"); }
                    }
                };
                stop.Click += (_, _) => inspect.DialogResult = false;
                apply.Click += (_, _) => inspect.DialogResult = true;
                buttons.Children.Add(export); buttons.Children.Add(stop); buttons.Children.Add(apply);
                DockPanel.SetDock(buttons, Dock.Bottom); pane.Children.Add(buttons);
                pane.Children.Add(RandomizerReviewGrid(preview.Changes.Select(c => new RandomizerReviewRow(c.UnitName,
                    RandomizerFieldName(c.Field) + (growth ? (legacy && c.Field == MindGrowth.Field ? " growth (stored points)" : " growth rate") : " personal bonus"), Value(c.Field, c.Before), Value(c.Field, c.After))), "Stat"));
                inspect.Content = pane;
                if (inspect.ShowDialog() != true) return;
                bool didApply = R0StatRandomizer.Apply(session, preview);
                RefreshDocument();
                Status.Text = didApply ? $"Applied {preview.Changes.Count} stat changes in one undo step. Save your project or select Undo."
                    : "The preview produced no stat changes.";
                window.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EditException or FormatException or OverflowException)
            { error.Text = "Could not preview or apply stat changes: " + ex.Message; }
        };
        actions.Children.Add(cancel); actions.Children.Add(previewButton);
        var footer = new StackPanel { Margin = new Thickness(18, 6, 18, 10) };
        footer.Children.Add(error); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); settingsLayout.Children.Add(footer);
        settingsLayout.Children.Add(scroll);
        window.ShowDialog();
    }
}
