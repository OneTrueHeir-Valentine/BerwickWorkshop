using Berwick.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private void AddPromotionDestinationEditor(PromotionPath path)
    {
        if (session is null) return;
        AddSection(PromotionDetails, "Destination class (Phase 2 pilot)");
        if (!PromotionDestinations.Supports(path.UnitId))
        {
            AddText(PromotionDetails, "This character's original application and eligibility pair has not been verified for a coordinated destination write. Original destination remains read-only.");
            return;
        }
        PromotionDestinationBinding binding;
        try { binding = PromotionDestinations.Read(session, path.UnitId); }
        catch (Exception ex) when (ex is EditException or IOException)
        {
            AddText(PromotionDetails, ex.Message);
            return;
        }
        AddText(PromotionDetails, $"Original class: 0x{path.DestinationClassId:X2}. Project class: 0x{binding.Destination:X2}. "
            + $"This edits events {binding.CallerEvent} and {binding.ApplicationEvent} together, as one undoable operation.");
        AddText(PromotionDetails, "Only classes with an explicit branch in original shared event 237 are selectable. "
            + "Current class caps, scripted item grants, mounts, story flags, scenes and promotion eligibility may still be incompatible. "
            + "Changing a destination does not alter its ceremony, equipment or reward.");
        var candidates = session.EditedClasses().Where(c =>
        {
            try { _ = PromotionPredicates.Read(c.Id, session.OriginalEventScript(237)!); return true; }
            catch (EditException) { return false; }
        }).OrderBy(c => c.Name).ToList();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        var select = new ComboBox { Width = 240, DisplayMemberPath = "Text", SelectedValuePath = "Value" };
        select.ItemsSource = candidates.Select(c => new Choice(c.Id, $"{c.Name} (0x{c.Id:X2})")).ToList();
        select.SelectedValue = binding.Destination;
        row.Children.Add(select);
        var apply = new Button { Content = "Apply", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(10, 0, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (session is null || loading is not null || select.SelectedValue is not int target) return;
            if (target == binding.Destination) { Status.Text = "The destination is unchanged."; return; }
            var cls = session.EditedClasses().First(c => c.Id == target);
            var oldClass = session.EditedClasses().FirstOrDefault(c => c.Id == binding.Destination);
            string warning = $"Change {path.CharacterName}'s destination to {cls.Name} (0x{target:X2})?\n\n"
                + $"Both the eligibility caller ({binding.CallerEvent}) and class-change application ({binding.ApplicationEvent}) "
                + "will be updated together. One Undo restores both.\n\n"
                + $"Original scripted reward remains Iron Lance, and the town ceremony remains character-specific. "
                + "The new class's weapon access, mounted status and effective skills may differ.\n"
                + $"Mount: {oldClass?.MountText ?? "unknown"} → {cls.MountText}. "
                + $"Spear cap: {cls.WeaponCaps.FirstOrDefault(v => v.Weapon == "Spear")?.Value ?? 0}.\n\n"
                + "Existing saves may contain cached promotion quests. This operation has not passed in-game acceptance.";
            if (MessageBox.Show(this, warning, "Confirm coordinated destination edit",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                bool changed = PromotionDestinations.SetDestination(session, path.UnitId, target);
                if (changed) RefreshDocument();
                Status.Text = changed ? "Coordinated promotion destination changed. Test on a disposable ISO and save." : "The destination is unchanged.";
            }
            catch (Exception ex) when (ex is EditException or IOException) { Status.Text = ex.Message; }
        };
        row.Children.Add(apply);
        PromotionDetails.Children.Add(row);
    }

    private void AddPromotionLevelEditor(PromotionPath path)
    {
        if (session is null) return;
        AddSection(PromotionDetails, "Project requirements");
        PromotionLevelBinding binding;
        try { binding = PromotionRequirements.ReadLevel(session, path.UnitId); }
        catch (Exception ex) when (ex is EditException or IOException)
        {
            AddText(PromotionDetails, ex.Message);
            return;
        }

        AddText(PromotionDetails, "Minimum level applies to this character's caller. Shared proficiency requirements and story gates still apply.");
        AddText(PromotionDetails, $"Original minimum: {path.MinimumLevel}. Project minimum: {binding.Level}.");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 8) };
        row.Children.Add(new TextBlock { Text = "Minimum level", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var input = new TextBox { Text = binding.Level.ToString(CultureInfo.InvariantCulture), Width = 65, VerticalContentAlignment = VerticalAlignment.Center };
        row.Children.Add(input);
        var apply = new Button { Content = "Apply", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        apply.Click += (_, _) =>
        {
            if (session is null || loading is not null) return;
            if (!int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
            {
                Status.Text = "Enter a whole minimum level from 1 to 63.";
                return;
            }
            try
            {
                bool changed = PromotionRequirements.SetLevel(session, path.UnitId, level);
                if (changed) RefreshDocument();
                Status.Text = changed ? $"{path.CharacterName}'s promotion minimum level changed to {level}." : "The promotion minimum level is unchanged.";
            }
            catch (Exception ex) when (ex is EditException or IOException) { Status.Text = ex.Message; }
        };
        row.Children.Add(apply);
        PromotionDetails.Children.Add(row);
        AddText(PromotionDetails, "An existing save can retain a previously adopted promotion quest. Project changes do not show whether the running game has refreshed it.");
    }

    private void AddPromotionThresholdEditors(PromotionPath path)
    {
        if (session is null) return;
        AddSection(PromotionDetails, "Shared project requirements");
        IReadOnlyList<PromotionThreshold> fields;
        try { fields = PromotionPredicates.Read(session, path.UnitId); }
        catch (Exception ex) when (ex is EditException or IOException)
        {
            AddText(PromotionDetails, ex.Message);
            return;
        }
        if (fields.Count == 0)
        {
            AddText(PromotionDetails, "No additional proficiency requirement in this destination branch.");
            return;
        }
        int projectClass = PromotionDestinations.TryCurrentTarget(session, path.UnitId) ?? path.DestinationClassId;
        var original = PromotionPredicates.Read(projectClass, session.OriginalEventScript(237)!);
        var roster = CharacterDecoder.LoadRoster().ToDictionary(c => c.Id, c => c.Name);
        AddText(PromotionDetails, "These thresholds can be shared by multiple destinations. Each change lists its known affected characters; other inherited or computed callers may also be affected.");
        foreach (var group in fields.GroupBy(f => (f.SourceClasses, f.RequireAll)))
        {
            AddText(PromotionDetails, $"{group.Key.SourceClasses}: {(group.Key.RequireAll ? "all requirements must be met" : "at least one requirement must be met")}.");
            foreach (var field in group)
            {
                int before = original.Single(f => f.LiteralIndex == field.LiteralIndex).Minimum;
                string users = string.Join(", ", PromotionPredicates.KnownUsers(session, path.UnitId, field.LiteralIndex)
                    .Select(id => roster.GetValueOrDefault(id, $"Unit {id}")));
                if (!field.Editable)
                {
                    AddText(PromotionDetails, $"{field.Name} >= {field.Minimum}. Editing this derived-stat requirement is not verified.");
                    continue;
                }
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock { Text = $"{field.Name} (original {before})", Width = 195, VerticalAlignment = VerticalAlignment.Center });
                var input = new TextBox { Text = field.Minimum.ToString(CultureInfo.InvariantCulture), Width = 65 };
                var button = new Button { Content = "Apply", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
                button.Click += (_, _) => ApplyPromotionThreshold(path, field, input.Text, users);
                row.Children.Add(input);
                row.Children.Add(button);
                PromotionDetails.Children.Add(row);

                var startingClass = session.EditedCharacters().FirstOrDefault(c => c.Id == path.UnitId)?.ClassId;
                var cls = session.EditedClasses().FirstOrDefault(c => c.Id == startingClass);
                int cap = cls?.WeaponCaps.FirstOrDefault(c => string.Equals(c.Weapon, field.Name, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;
                if (cls is not null && field.Minimum > cap)
                    AddText(PromotionDetails, $"This exceeds the starting class {cls.Name}'s {field.Name} cap of {cap}. The actual qualifying source class may differ.");
            }
        }
    }

    private void ApplyPromotionThreshold(PromotionPath path, PromotionThreshold field, string text, string users)
    {
        if (session is null || loading is not null) return;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value is < 0 or > 63)
        {
            Status.Text = "Enter a whole proficiency requirement from 0 to 63.";
            return;
        }
        if (value == field.Minimum) { Status.Text = "The promotion requirement is unchanged."; return; }
        if (MessageBox.Show(this,
            $"Change the shared {field.Name} requirement from {field.Minimum} to {value}?\n\nKnown affected characters: {users}. Other inherited or computed callers may also be affected.",
            "Shared promotion requirement", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            bool changed = PromotionPredicates.SetThreshold(session, path.UnitId, field.LiteralIndex, value);
            if (changed) RefreshDocument();
            Status.Text = changed ? "Shared promotion requirement changed." : "The promotion requirement is unchanged.";
        }
        catch (Exception ex) when (ex is EditException or IOException) { Status.Text = ex.Message; }
    }
}
