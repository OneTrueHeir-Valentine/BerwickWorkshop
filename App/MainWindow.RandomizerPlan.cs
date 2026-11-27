using Berwick.Core;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Berwick.Workshop;

public partial class MainWindow
{
    /// <summary>
    /// One window for a whole Randomizer plan: each part has its own switch, seed and settings, the plan has one roster, locked
    /// characters and presets, and the preview is grouped by part and applies as one undo step.
    /// </summary>
    private void OpenRandomizerPlan(object sender, RoutedEventArgs e)
    {
        if (session is null || loading is not null) return;
        EndEdit();
        var window = new Window { Title = "Randomizer plan", Owner = this, Width = 760, Height = 860, MinWidth = 600, MinHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var layout = new DockPanel(); window.Content = layout;
        TextBlock? lastHeading = null;
        T Labeled<T>(T control) where T : FrameworkElement
        {
            if (lastHeading is not null) AutomationProperties.SetLabeledBy(control, lastHeading);
            return control;
        }
        TextBlock Header(string s, double size = 14) => lastHeading = new TextBlock { Text = s, FontWeight = FontWeights.SemiBold, FontSize = size,
            Margin = new Thickness(0, 9, 0, 4), TextWrapping = TextWrapping.Wrap };
        TextBlock Note(string s) => new() { Text = s, Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        TextBox Box(string s, double width = 140) => Labeled(new TextBox { Text = s, Width = width, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) });
        ComboBox Method() => Labeled(new ComboBox { ItemsSource = new[] { "Change each value on its own", "Move points between values (keep the total)" },
            SelectedIndex = 0, Width = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) });
        (WrapPanel View, Dictionary<string, CheckBox> Boxes) Fields(IEnumerable<string> keys, string suffix = "")
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var boxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                var check = new CheckBox { Content = RandomizerFieldName(key) + suffix, IsChecked = true, Width = 150, Margin = new Thickness(0, 0, 6, 6) };
                boxes[key] = check; wrap.Children.Add(check);
            }
            return (wrap, boxes);
        }
        void SetFields(Dictionary<string, CheckBox> boxes, IEnumerable<string> keys)
        {
            var on = keys.ToHashSet(StringComparer.Ordinal);
            foreach (var (key, box) in boxes) box.IsChecked = on.Contains(key);
        }
        string[] Chosen(Dictionary<string, CheckBox> boxes) => boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key).ToArray();
        int Number(TextBox box, string what)
        {
            if (!int.TryParse(box.Text.Trim(), out int value)) throw new EditException($"{what} must be a whole number.");
            return value;
        }
        var rosters = new List<(string Part, RandomizerCharacterSelection Roster)>();
        (CheckBox Include, StackPanel Body, RandomizerCharacterSelection Roster) Part(string title, string help)
        {
            var border = new Border { BorderBrush = Brushes.Gainsboro, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 10) };
            var stack = new StackPanel(); border.Child = stack;
            var include = new CheckBox { Content = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 4, 0, 4) };
            stack.Children.Add(include);
            stack.Children.Add(Note(help));
            var body = new StackPanel();
            stack.Children.Add(body);
            include.Checked += (_, _) => body.IsEnabled = true;
            include.Unchecked += (_, _) => body.IsEnabled = false;
            body.IsEnabled = false;
            body.Children.Add(Header("Characters for this part"));
            var roster = new RandomizerCharacterSelection(session, window);
            body.Children.Add(roster.View);
            rosters.Add((title, roster));
            panel.Children.Add(border);
            return (include, body, roster);
        }

        panel.Children.Add(Note("Turn on any parts you want and set each one on its own. Every part keeps its own seed, so it gives the same result here as in its own window. A preview does not change the project; applying is one undo step."));
        panel.Children.Add(Header("Plan name"));
        var name = Labeled(new TextBox { Text = "My Randomizer plan", Margin = new Thickness(0, 0, 0, 6) }); panel.Children.Add(name);

        panel.Children.Add(Header("Start from a preset"));
        var presetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var preset = Labeled(new ComboBox { ItemsSource = RandomizerPlans.PresetNames, SelectedIndex = 1, Width = 160, Margin = new Thickness(0, 0, 10, 0) });
        presetRow.Children.Add(preset);
        presetRow.Children.Add(new TextBlock { Text = "Seed", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var presetSeed = new TextBox { Text = "1729", Width = 110, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(presetSeed, "Preset seed");
        presetRow.Children.Add(presetSeed);
        var usePreset = new Button { Content = "Use preset", Padding = new Thickness(9, 3, 9, 3) };
        presetRow.Children.Add(usePreset);
        panel.Children.Add(presetRow);
        panel.Children.Add(Note("Light makes small changes and leaves items alone. Balanced keeps each character's totals and changes starting items within their level. Wild makes large changes. Every preset keeps trained weapon levels. Using a preset replaces the settings below; you can change any of them afterwards."));

        var items = Part("Starting items", "Replace the items characters start with. Items that events give or take are listed before you apply.");
        items.Body.Children.Add(Header("Seed")); var itemSeed = Box("1729"); items.Body.Children.Add(itemSeed);
        var itemWeapons = new CheckBox { Content = "Weapons, staves and shields", Margin = new Thickness(0, 0, 0, 4) };
        var itemAmmo = new CheckBox { Content = "Arrows and bolts", Margin = new Thickness(0, 0, 0, 4) };
        var itemOther = new CheckBox { Content = "Accessories and consumables", Margin = new Thickness(0, 0, 0, 4) };
        var itemBags = new CheckBox { Content = "Include items in bags", Margin = new Thickness(0, 0, 0, 4) };
        var itemExtended = new CheckBox { Content = "Include occupied starting slots 6–8 (empty slots stay empty)", Margin = new Thickness(0, 0, 0, 4) };
        var itemLevel = new CheckBox { Content = "Only pick items no higher in level than the one replaced", Margin = new Thickness(0, 0, 0, 6) };
        foreach (var c in new[] { itemWeapons, itemAmmo, itemOther, itemBags, itemExtended, itemLevel }) items.Body.Children.Add(c);

        var bonuses = Part("Stat bonuses", "Change each character's personal stat bonuses. Class base stats are not changed.");
        bonuses.Body.Children.Add(Header("Seed")); var bonusSeed = Box("1730"); bonuses.Body.Children.Add(bonusSeed);
        bonuses.Body.Children.Add(Header("How to change them")); var bonusMethod = Method(); bonuses.Body.Children.Add(bonusMethod);
        bonuses.Body.Children.Add(Header("Largest change (1 to 63 points)")); var bonusMax = Box("2"); bonuses.Body.Children.Add(bonusMax);
        bonuses.Body.Children.Add(Header("Stats to change")); var bonusFields = Fields(R0StatProfile.BonusFieldKeys); bonuses.Body.Children.Add(bonusFields.View);

        var growths = Part("Growth rates", "Change each character's personal growth rates, in percentage points.");
        growths.Body.Children.Add(Header("Seed")); var growthSeed = Box("1731"); growths.Body.Children.Add(growthSeed);
        growths.Body.Children.Add(Header("How to change them")); var growthMethod = Method(); growths.Body.Children.Add(growthMethod);
        var growthChangeHeading = Header("Largest change (1 to 63 percentage points)"); growths.Body.Children.Add(growthChangeHeading); var growthMax = Box("10"); growths.Body.Children.Add(growthMax);
        growths.Body.Children.Add(Header("Growth rates to change")); var growthFields = Fields(R0StatProfile.GrowthFieldKeys); growths.Body.Children.Add(growthFields.View);

        var levels = Part("Starting weapon levels", "Change the weapon and shield levels characters start with. Changes stay within class limits. The saved profile determines whether untrained types can change.");
        levels.Body.Children.Add(Header("Seed")); var levelSeed = Box("1732"); levels.Body.Children.Add(levelSeed);
        levels.Body.Children.Add(Header("How to change them")); var levelMethod = Method(); levels.Body.Children.Add(levelMethod);
        levels.Body.Children.Add(Header("Largest change (1 to 50 levels)")); var levelMax = Box("5"); levels.Body.Children.Add(levelMax);
        levels.Body.Children.Add(Header("Keep trained levels at least (0 to 15, 0 = no minimum)")); var levelLowest = Box("1"); levels.Body.Children.Add(levelLowest);
        levels.Body.Children.Add(Note("This keeps existing training; it is not a minimum for every level. A level the character already has never drops below this number, or below its own starting value if that is lower, and never goes above the class limit. Untrained types stay at zero when the profile preserves trained channels. 0 means the older behaviour, which can lower a level to zero: a preview that lowers training to zero requires confirmation before applying."));
        levels.Body.Children.Add(Header("Weapon and shield types")); var levelFields = Fields(R0ProficiencyProfile.StartingKeys); levels.Body.Children.Add(levelFields.View);

        var weaponGrowth = Part("Weapon growth", "Change how quickly weapon and shield levels improve, in steps of 10 percentage points.");
        weaponGrowth.Body.Children.Add(Header("Seed")); var wgSeed = Box("1733"); weaponGrowth.Body.Children.Add(wgSeed);
        weaponGrowth.Body.Children.Add(Header("How to change them")); var wgMethod = Method(); weaponGrowth.Body.Children.Add(wgMethod);
        weaponGrowth.Body.Children.Add(Header("Largest change (1 to 50 steps)")); var wgMax = Box("2"); weaponGrowth.Body.Children.Add(wgMax);
        weaponGrowth.Body.Children.Add(Header("Keep trained rates at least (0 to 15 steps of 10 percentage points)")); var wgLowest = Box("1"); weaponGrowth.Body.Children.Add(wgLowest);
        weaponGrowth.Body.Children.Add(Header("Weapon and shield types")); var wgFields = Fields(R0ProficiencyProfile.GrowthKeys); weaponGrowth.Body.Children.Add(wgFields.View);

        panel.Children.Add(Header("Default characters", 15));
        panel.Children.Add(Note("Each part above has its own characters. Choose a default here and copy it to every part in one step; parts that already differ are listed first so nothing changes without your say."));
        var defaults = new RandomizerCharacterSelection(session, window);
        panel.Children.Add(defaults.View);
        var copyDefaults = new Button { Content = "Use the default for every part…", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(copyDefaults);
        var rosterNote = Note(""); panel.Children.Add(rosterNote);
        copyDefaults.Click += (_, _) =>
        {
            var chosen = defaults.GetIds();
            var differ = rosters.Where(r => !r.Roster.GetIds().Order().SequenceEqual(chosen.Order())).Select(r => r.Part).ToList();
            if (differ.Count == 0) { rosterNote.Text = "Every part already uses the default characters."; return; }
            if (MessageBox.Show(window, "This replaces the characters chosen for: " + string.Join(", ", differ) + ".\n\nUse the default for every part?",
                    "Use the default characters", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            foreach (var r in rosters) r.Roster.SetIds(chosen);
            rosterNote.Text = $"The default characters are now used by every part ({differ.Count} changed).";
        };
        panel.Children.Add(Header("Locked characters", 15));
        panel.Children.Add(Note("Locked characters are left exactly as they are by every part."));
        var locks = new RandomizerLockSelection(session, window);
        panel.Children.Add(locks.View);
        panel.Children.Add(Header("Warn when a character's best weapon level would end below (0 to 30, 0 = off)"));
        var weak = Box("2", 80); panel.Children.Add(weak);
        panel.Children.Add(Note("A low level is a warning, not a block: a weak character can still fight. The plan stops if the loadout check finds that a character loses their last usable attack or healing item. Class permissions and ammunition are checked; animations and other restrictions need separate testing."));

        RandomizerPlan loaded = RandomizerPlans.Preset("Balanced", 1729);
        var replayNote = Note("");
        panel.Children.Add(replayNote);
        void Show(RandomizerPlan plan)
        {
            RandomizerPlans.Check(session, plan);
            loaded = plan;
            replayNote.Text = plan.GrowthRates?.UsesStoredMindPoints == true
                ? "This plan uses the older growth algorithm: Mind changes are stored points, two per percentage point. Its original algorithm is kept. Choose a preset to start a new percentage-based plan."
                : "Each part keeps its saved algorithm and compatibility settings.";
            growthChangeHeading.Text = plan.GrowthRates?.UsesStoredMindPoints == true
                ? "Largest change (1 to 63 stored points)" : "Largest change (1 to 63 percentage points)";
            string Policy(R0ProficiencyProfile? p) => p?.Compatibility == R0ProficiencyCompatibility.AllowClassPermittedChannels
                ? "allow class-permitted untrained types" : "preserve trained types";
            replayNote.Text += $" Weapon levels: {Policy(plan.StartingProficiency)}; weapon growth: {Policy(plan.ProficiencyGrowth)}.";
            name.Text = plan.Name;
            items.Include.IsChecked = plan.StartingItems is not null;
            var o = plan.StartingItems ?? new RandomizeOptions(1729);
            itemSeed.Text = o.Seed.ToString(); itemWeapons.IsChecked = o.Weapons; itemAmmo.IsChecked = o.Ammunition;
            itemOther.IsChecked = o.Other; itemBags.IsChecked = o.Bags; itemExtended.IsChecked = o.ExtendedInventory; itemLevel.IsChecked = o.LimitLevel;
            void Stat(R0StatProfile? p, R0StatProfile fallback, CheckBox include, TextBox seed, ComboBox method, TextBox max, Dictionary<string, CheckBox> fields)
            {
                include.IsChecked = p is not null;
                p ??= fallback;
                seed.Text = p.Seed.ToString(); method.SelectedIndex = p.Mode == R0StatAlgorithm.BoundedVariance ? 0 : 1;
                max.Text = p.MaximumChange.ToString(); SetFields(fields, p.Fields);
            }
            void Level(R0ProficiencyProfile? p, R0ProficiencyProfile fallback, CheckBox include, TextBox seed, ComboBox method, TextBox max, TextBox lowest, Dictionary<string, CheckBox> fields)
            {
                include.IsChecked = p is not null;
                p ??= fallback;
                seed.Text = p.Seed.ToString(); method.SelectedIndex = p.Mode == R0StatAlgorithm.BoundedVariance ? 0 : 1;
                max.Text = p.MaximumChange.ToString(); lowest.Text = p.SchemaVersion == R0ProficiencyProfile.KeepTrainedSchema ? p.TrainedMinimum.ToString() : "0";
                SetFields(fields, p.Fields);
            }
            Stat(plan.StatBonuses, RandomizerPlans.Preset("Balanced", 1729).StatBonuses!, bonuses.Include, bonusSeed, bonusMethod, bonusMax, bonusFields.Boxes);
            Stat(plan.GrowthRates, R0StatProfile.VariancePreset(), growths.Include, growthSeed, growthMethod, growthMax, growthFields.Boxes);
            Level(plan.StartingProficiency, R0ProficiencyProfile.StartingVariance().Migrated(), levels.Include, levelSeed, levelMethod, levelMax, levelLowest, levelFields.Boxes);
            Level(plan.ProficiencyGrowth, R0ProficiencyProfile.GrowthVariance().Migrated(), weaponGrowth.Include, wgSeed, wgMethod, wgMax, wgLowest, wgFields.Boxes);
            items.Roster.SetIds(plan.StartingItems?.UnitIds ?? []);
            bonuses.Roster.SetIds(plan.StatBonuses?.UnitIds ?? []);
            growths.Roster.SetIds(plan.GrowthRates?.UnitIds ?? []);
            levels.Roster.SetIds(plan.StartingProficiency?.UnitIds ?? []);
            weaponGrowth.Roster.SetIds(plan.ProficiencyGrowth?.UnitIds ?? []);
            rosterNote.Text = "";
            locks.SetIds(plan.Locked.ToArray());
            weak.Text = plan.WarnBelowLevel.ToString();
        }

        RandomizerPlan Current()
        {
            R0StatProfile Stat(R0StatProfile original, RandomizerCharacterSelection roster, TextBox seed, ComboBox method, TextBox max, Dictionary<string, CheckBox> fields, string part)
                => RandomizerPlans.UpdateStatSettings(original, Number(seed, part + " seed"), Mode(method), Number(max, part + " largest change"),
                    roster.GetIds(), Chosen(fields));
            R0StatAlgorithm Mode(ComboBox box) => box.SelectedIndex == 0 ? R0StatAlgorithm.BoundedVariance : R0StatAlgorithm.BudgetRedistribution;
            R0ProficiencyProfile? Level(CheckBox include, RandomizerCharacterSelection roster, R0ProficiencyProfile start, TextBox seed, ComboBox method, TextBox max, TextBox lowest, Dictionary<string, CheckBox> fields, string part)
            {
                if (include.IsChecked != true) return null;
                return RandomizerPlans.UpdateProficiencySettings(start, Number(seed, part + " seed"), Mode(method),
                    Number(max, part + " largest change"), Number(lowest, part + " lowest trained level"), roster.GetIds(), Chosen(fields));
            }
            var plan = new RandomizerPlan(RandomizerPlan.CurrentVersion, name.Text.Trim(),
                items.Include.IsChecked == true ? new RandomizeOptions(Number(itemSeed, "Starting item seed"))
                {
                    Weapons = itemWeapons.IsChecked == true, Ammunition = itemAmmo.IsChecked == true, Other = itemOther.IsChecked == true,
                    Bags = itemBags.IsChecked == true, LimitLevel = itemLevel.IsChecked == true,
                    ExtendedInventory = itemExtended.IsChecked == true,
                    UnitIds = items.Roster.GetIds() is { Length: > 0 } itemIds ? itemIds : null,
                } : null,
                bonuses.Include.IsChecked == true ? Stat(loaded.StatBonuses ?? RandomizerPlans.Preset("Balanced", 1729).StatBonuses!, bonuses.Roster,
                    bonusSeed, bonusMethod, bonusMax, bonusFields.Boxes, "Stat bonus") : null,
                growths.Include.IsChecked == true ? Stat(loaded.GrowthRates ?? R0StatProfile.VariancePreset(), growths.Roster,
                    growthSeed, growthMethod, growthMax, growthFields.Boxes, "Growth rate") : null,
                Level(levels.Include, levels.Roster, loaded.StartingProficiency ?? R0ProficiencyProfile.StartingVariance().Migrated(), levelSeed, levelMethod, levelMax, levelLowest, levelFields.Boxes, "weapon levels"),
                Level(weaponGrowth.Include, weaponGrowth.Roster, loaded.ProficiencyGrowth ?? R0ProficiencyProfile.GrowthVariance().Migrated(), wgSeed, wgMethod, wgMax, wgLowest, wgFields.Boxes, "weapon growth"),
                locks.GetIds()) { WarnBelowLevel = Number(weak, "The warning level") };
            return plan;
        }

        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 7) };
        usePreset.Click += (_, _) =>
        {
            try { Show(RandomizerPlans.Preset((string)preset.SelectedItem, Number(presetSeed, "The preset seed"))); error.Text = ""; }
            catch (EditException ex) { error.Text = ex.Message; }
        };
        Show(RandomizerPlans.Preset("Balanced", 1729));

        var files = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var load = new Button { Content = "Load plan", Margin = new Thickness(0, 0, 10, 8) };
        var save = new Button { Content = "Save new plan", Margin = new Thickness(0, 0, 0, 8) };
        const string filter = "Randomizer plans (*.bwplan.json)|*.bwplan.json";
        load.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = filter };
            if (picker.ShowDialog(window) != true) return;
            try { Show(RandomizerPlans.FromJson(File.ReadAllText(picker.FileName))); error.Text = "Plan loaded. Nothing has been changed."; }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { error.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = filter, FileName = "randomizer.bwplan.json", OverwritePrompt = false };
            if (picker.ShowDialog(window) != true) return;
            try
            {
                var plan = Current();
                RandomizerPlans.Preview(session, plan); // checks the plan before it is kept
                using var file = new FileStream(picker.FileName, FileMode.CreateNew, FileAccess.Write);
                file.Write(new UTF8Encoding(false).GetBytes(RandomizerPlans.ToJson(plan)));
                error.Text = "Plan saved. Nothing in the project was changed.";
            }
            catch (IOException) when (File.Exists(picker.FileName)) { error.Text = "That file already exists. Existing plans are never replaced; choose a new name."; }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException) { error.Text = ex.Message; }
        };
        files.Children.Add(load); files.Children.Add(save); panel.Children.Add(files);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", Margin = new Thickness(0, 0, 12, 0), IsCancel = true };
        var previewButton = new Button { Content = "Preview plan", Style = (Style)FindResource("PrimaryButton") };
        close.Click += (_, _) => window.Close();
        previewButton.Click += async (_, _) =>
        {
            if (session is null) return;
            previewButton.IsEnabled = false;
            try
            {
                var plan = Current();
                ScriptDependencyIndex? index = null;
                if (plan.StartingItems is not null)
                {
                    error.Text = "Checking the game's events for items they give or take…";
                    index = await DependencyIndexForRandomizerAsync();
                }
                error.Text = "";
                var preview = RandomizerPlans.Preview(session, plan, index);
                var zeroed = RandomizerPlans.ZeroedTrainedLevels(session, plan);
                switch (ShowPlanPreview(window, preview, zeroed))
                {
                    case PlanReview.Apply:
                        var fresh = plan.StartingItems is null ? null : await DependencyIndexForRandomizerAsync(reopenDisc: true);
                        // Reaching Apply means any event items and zero levels were confirmed in the review.
                        bool applied = RandomizerPlans.Apply(session, preview, fresh, permitScriptReferences: true, allowZeroedTrainedLevels: true);
                        RefreshDocument();
                        Status.Text = applied ? $"Applied the Randomizer plan “{plan.Name}”: {preview.Rows.Count} values in one undo step." : "The plan changed nothing.";
                        window.Close();
                        break;
                    case PlanReview.LockProblems:
                        var named = preview.Problems.Select(p => p.CharacterId).Concat(RandomizerPlans.ZeroedTrainedCharacters(session, plan))
                            .Where(id => !plan.Locked.Contains(id)).Distinct().ToList();
                        locks.SetIds([.. plan.Locked, .. named]);
                        error.Text = $"Locked {named.Count} {(named.Count == 1 ? "character" : "characters")}. Preview again to check the plan.";
                        break;
                }
            }
            catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { error.Text = "Unable to preview or apply the plan: " + ex.Message; }
            finally { previewButton.IsEnabled = true; }
        };
        actions.Children.Add(close); actions.Children.Add(previewButton);
        var footer = new StackPanel { Margin = new Thickness(18, 6, 18, 10) };
        footer.Children.Add(error); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        window.ShowDialog();
    }

    private enum PlanReview { Cancel, Apply, LockProblems }

    /// <summary>Shows a plan preview grouped by part. Apply is offered only when nothing blocks the plan.</summary>
    private PlanReview ShowPlanPreview(Window owner, RandomizerPlanPreview preview, IReadOnlyList<string> zeroed)
    {
        var dialog = new Window { Title = "Review the Randomizer plan (nothing applied yet)", Owner = owner, Width = 940, Height = 700,
            MinWidth = 600, MinHeight = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var layout = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        TextBlock Line(string text, Brush? colour = null) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
            Foreground = colour ?? Brushes.Black };
        int characters = preview.Rows.Select(r => r.CharacterId).Distinct().Count();
        top.Children.Add(Line($"“{preview.Plan.Name}”: {preview.Rows.Count} values for {characters} {(characters == 1 ? "character" : "characters")}. Nothing has been applied."));
        top.Children.Add(Line("Characters each part may change: " + string.Join("; ", RandomizerPlans.Scopes(session!, preview.Plan)
            .Select(s => $"{s.Part} {s.Count} of {s.Total}{(s.Locked > 0 ? $" ({s.Locked} locked)" : "")}")) + ".", Brushes.DimGray));
        if (preview.Plan.StartingItems is { } itemOptions)
            top.Children.Add(Line(itemOptions.ExtendedInventory ? "Starting inventory: slots 1–8" : "Starting inventory: slots 1–5", Brushes.DimGray));
        foreach (var p in preview.Problems)
            top.Children.Add(Line($"Blocked: {p.Character}. {p.Reason}", Brushes.Firebrick));
        CheckBox? allowZero = null;
        if (zeroed.Count > 0)
        {
            top.Children.Add(Line("An older weapon level part lowers trained levels to zero (" + string.Join(", ", zeroed.Take(8)) + (zeroed.Count > 8 ? ", ..." : "") +
                "). Those characters can still attack, with very low accuracy. To avoid it, set the part's “Keep trained levels at least” to 1 or more and preview again.", Brushes.DarkGoldenrod));
            allowZero = new CheckBox { Content = "Let these trained levels become zero", Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(allowZero);
        }
        CheckBox? acknowledge = null;
        if (preview.ScriptReferences.Count > 0)
        {
            top.Children.Add(Line($"{preview.ScriptReferences.Count} new starting {(preview.ScriptReferences.Count == 1 ? "item is" : "items are")} also given, taken or checked by the game's events. Review them in the Starting items tab before applying.", Brushes.DarkGoldenrod));
            acknowledge = new CheckBox { Content = "I have reviewed the items that events also use", Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(acknowledge);
        }
        if (preview.Warnings.Count > 0)
            top.Children.Add(new Expander { Header = $"Warnings ({preview.Warnings.Count})", IsExpanded = preview.Warnings.Count <= 6, Margin = new Thickness(0, 0, 0, 6),
                Content = new ScrollViewer { MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = Line(string.Join(Environment.NewLine, preview.Warnings.Select(w =>
                    {
                        foreach (string key in R0ProficiencyProfile.StartingKeys.Concat(R0ProficiencyProfile.GrowthKeys))
                            w = w.Replace(key, RandomizerFieldName(key), StringComparison.Ordinal);
                        return w;
                    }))) } });

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var export = new Button { Content = "Save preview report (JSON)", Margin = new Thickness(0, 0, 10, 0) };
        var lockProblems = new Button { Content = "Lock the characters named above", Margin = new Thickness(0, 0, 10, 0),
            Visibility = preview.Problems.Count > 0 || zeroed.Count > 0 ? Visibility.Visible : Visibility.Collapsed };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        var apply = new Button { Content = "Apply plan (one undo step)", Style = (Style)FindResource("PrimaryButton") };
        void UpdateApply() => apply.IsEnabled = preview.CanApply && (allowZero is null || allowZero.IsChecked == true) &&
            (acknowledge is null || acknowledge.IsChecked == true);
        if (acknowledge is not null) { acknowledge.Checked += (_, _) => UpdateApply(); acknowledge.Unchecked += (_, _) => UpdateApply(); }
        if (allowZero is not null) { allowZero.Checked += (_, _) => UpdateApply(); allowZero.Unchecked += (_, _) => UpdateApply(); }
        UpdateApply();
        var result = PlanReview.Cancel;
        export.Click += (_, _) =>
        {
            var picker = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "randomizer-plan-preview.json" };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                File.WriteAllText(picker.FileName, System.Text.Json.JsonSerializer.Serialize(new { preview.Plan.Name, preview.Rows, preview.Warnings, preview.Problems },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(dialog, ex.Message, "The report was not saved"); }
        };
        lockProblems.Click += (_, _) => { result = PlanReview.LockProblems; dialog.DialogResult = true; };
        apply.Click += (_, _) => { result = PlanReview.Apply; dialog.DialogResult = true; };
        buttons.Children.Add(export); buttons.Children.Add(lockProblems); buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);

        var tabs = new TabControl();
        AutomationProperties.SetName(tabs, "Changes by part");
        foreach (var group in preview.Rows.GroupBy(r => r.Module))
            tabs.Items.Add(new TabItem { Header = $"{group.Key} ({group.Count()})",
                Content = RandomizerReviewGrid(group.Select(r => new RandomizerReviewRow(r.Character, r.Field, r.Before, r.After)), "Value") });
        if (tabs.Items.Count == 0) tabs.Items.Add(new TabItem { Header = "No changes", Content = Line("This plan changes nothing for the chosen characters.") });
        layout.Children.Add(tabs);
        dialog.Content = layout;
        return dialog.ShowDialog() == true ? result : PlanReview.Cancel;
    }

    /// <summary>Characters every part leaves alone. Unlike the roster, an empty list means nobody is locked.</summary>
    private sealed class RandomizerLockSelection
    {
        private readonly (int Id, string Name)[] roster;
        private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        private int[] ids = [];
        public StackPanel View { get; } = new();

        public RandomizerLockSelection(EditSession source, Window owner)
        {
            roster = source.Disc.Characters.Select(c => (c.Id, c.Name)).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id).ToArray();
            var choose = new Button { Content = "Choose locked characters…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 4, 9, 4) };
            choose.Click += (_, _) => Choose(owner);
            View.Children.Add(choose); View.Children.Add(summary);
            Update();
        }

        public int[] GetIds() => (int[])ids.Clone();

        public void SetIds(int[] value)
        {
            if (value.Any(id => !roster.Any(c => c.Id == id)))
                throw new EditException("A locked character in this plan is not on this disc.");
            ids = value.Distinct().Order().ToArray();
            Update();
        }

        private void Update() => summary.Text = ids.Length == 0 ? "No characters locked." :
            $"{ids.Length} locked: " + string.Join(", ", roster.Where(c => ids.Contains(c.Id)).Take(6).Select(c => c.Name)) + (ids.Length > 6 ? ", and others" : "");

        private void Choose(Window owner)
        {
            var selected = ids.ToHashSet();
            var dialog = new Window { Owner = owner, Title = "Choose locked characters", Width = 460, Height = 570,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
            var layout = new DockPanel { Margin = new Thickness(14) };
            var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
            top.Children.Add(new TextBlock { Text = "Tick the characters every part must leave alone. Searching does not change your selection.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7) });
            var search = new TextBox { Margin = new Thickness(0, 0, 0, 7) };
            AutomationProperties.SetName(search, "Search character names");
            top.Children.Add(search);
            var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
            var count = new TextBlock { Margin = new Thickness(0, 7, 0, 7) }; footer.Children.Add(count);
            var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var clear = new Button { Content = "Unlock all", Margin = new Thickness(0, 0, 8, 4) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 4) };
            var use = new Button { Content = "Use selection", IsDefault = true, Margin = new Thickness(0, 0, 0, 4) };
            buttons.Children.Add(clear); buttons.Children.Add(cancel); buttons.Children.Add(use); footer.Children.Add(buttons);
            var list = new StackPanel();
            layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            void Refresh()
            {
                list.Children.Clear();
                foreach (var c in roster.Where(c => c.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    string label = roster.Count(x => x.Name == c.Name) > 1 ? $"{c.Name} ({c.Id:X})" : c.Name;
                    var check = new CheckBox { Content = label, IsChecked = selected.Contains(c.Id), Margin = new Thickness(3, 3, 0, 3) };
                    check.Checked += (_, _) => { selected.Add(c.Id); count.Text = $"{selected.Count} locked"; };
                    check.Unchecked += (_, _) => { selected.Remove(c.Id); count.Text = $"{selected.Count} locked"; };
                    list.Children.Add(check);
                }
                if (list.Children.Count == 0) list.Children.Add(new TextBlock { Text = "No matching characters." });
                count.Text = $"{selected.Count} locked";
            }
            search.TextChanged += (_, _) => Refresh();
            clear.Click += (_, _) => { selected.Clear(); Refresh(); };
            use.Click += (_, _) => dialog.DialogResult = true;
            Refresh(); dialog.Content = layout;
            if (dialog.ShowDialog() == true) SetIds(selected.ToArray());
        }
    }
}
