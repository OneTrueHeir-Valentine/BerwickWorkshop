using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private List<PromotionPath> promotionRows = [];

    private void RefreshPromotions()
    {
        if (primary is null) return;
        var catalogue = PromotionCatalogue.Read(primary);
        PromotionsNav.IsEnabled = primary.Supported;
        promotionRows = catalogue.Paths.ToList();
        OtherClassChangeGrid.ItemsSource = catalogue.OtherClassChanges;
        OtherClassChangeStatus.Text = catalogue.OriginalSourceVerified
            ? "Separate from true promotions. These are documented behaviors, not yet verified native class-change bindings. They cannot be edited."
            : "Open a verified original game image to view reference-only class changes.";
        PromotionSourceText.Text = catalogue.SourceDescription
            + (session?.ChangeCount > 0 && catalogue.OriginalSourceVerified
                ? " Project edits may change these original conditions; they are not reflected here."
                : "");
        int? prior = (PromotionGrid.SelectedItem as PromotionPath)?.UnitId;
        PromotionGrid.ItemsSource = promotionRows;
        PromotionCount.Text = catalogue.OriginalSourceVerified
            ? $"{promotionRows.Count} source-verified literal promotion callers. This is not a complete promotion-path inventory."
            : "Original promotion conditions are unavailable for this source.";
        PromotionCoverageText.Text = catalogue.OriginalSourceVerified
            ? "Research gaps: " + string.Join("  |  ", catalogue.CoverageGaps.Select(g => g.Category))
            : "";
        PromotionGrid.SelectedItem = promotionRows.FirstOrDefault(r => r.UnitId == prior)
            ?? promotionRows.FirstOrDefault();
        if (promotionRows.Count == 0) ClearPromotion();
    }

    private void ShowPromotions(object sender, RoutedEventArgs e)
    {
        view = View.Promotions;
        UpdatePage();
    }

    private void PromotionTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // WPF SelectionChanged bubbles from DataGrids. Only react to a tab change.
        if (PromotionTabs is null || !ReferenceEquals(e.Source, PromotionTabs) || PromotionCount is null)
            return;
        var showingPromotions = PromotionTabs.SelectedIndex == 0;
        PromotionCount.Visibility = showingPromotions ? Visibility.Visible : Visibility.Collapsed;
        PromotionCoverageText.Visibility = showingPromotions ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearPromotion()
    {
        if (PromotionName is null) return;
        PromotionName.Text = "Select a promotion";
        PromotionIdentity.Text = "";
        PromotionDetails.Children.Clear();
    }

    private void PromotionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PromotionName is null) return;
        if (PromotionGrid.SelectedItem is not PromotionPath path) { ClearPromotion(); return; }
        PromotionDetails.Children.Clear();
        PromotionName.Text = path.CharacterName;
        var destinationName = primary?.Classes.FirstOrDefault(c => c.Id == path.DestinationClassId)?.Name;
        PromotionIdentity.Text = $"{path.UnitText}  ·  {path.DestinationText}"
            + (destinationName is null ? "" : $" ({destinationName})")
            + $"  ·  {path.MinimumLevelText}";
        AddSection(PromotionDetails, "Known original conditions and interpretation");
        AddText(PromotionDetails, "Eligibility groups combine with AND. Internal OR conditions and class-dependent branches are shown explicitly.");
        foreach (var condition in path.KnownConditions)
        {
            AddText(PromotionDetails, condition.Scope);
            AddText(PromotionDetails, condition.Expression);
        }
        AddSection(PromotionDetails, "Original event route");
        AddPair(PromotionDetails, "Caller", $"Event {path.CallerEvent}");
        AddPair(PromotionDetails, "Quest", path.QuestText);
        AddPair(PromotionDetails, "Check", path.CheckEvent is int check ? $"Event {check}" : "Not traced");
        AddPair(PromotionDetails, "Application", path.ApplicationEvent is int apply ? $"Event {apply}" : "Not traced");
        AddPair(PromotionDetails, "Town entry", path.RootText);
        AddSection(PromotionDetails, "Shared destination");
        AddText(PromotionDetails, "Known callers: " + string.Join(", ", path.ConfirmedDestinationUsers
            .Select(id => CharacterDecoder.LoadRoster().FirstOrDefault(c => c.Id == id)?.Name ?? $"Unit 0x{id:X3}")) + ".");
        AddText(PromotionDetails, "Other native and computed callers may exist. This is not a complete list of affected units.");
        AddSection(PromotionDetails, "Known downstream effects");
        if (path.KnownConsequences.Count == 0) AddText(PromotionDetails, "Not fully traced.");
        else foreach (var detail in path.KnownConsequences) AddText(PromotionDetails, detail);
        AddSection(PromotionDetails, "Unresolved conditions");
        foreach (var unknown in path.UnresolvedConditions) AddText(PromotionDetails, unknown);
        AddSection(PromotionDetails, "Live eligibility");
        AddText(PromotionDetails, "Unknown. An ISO contains original scripts, not a timestamped snapshot of the current game.");
    }
}
