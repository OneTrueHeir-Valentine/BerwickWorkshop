using Berwick.Core;
using System.Windows;
using System.Windows.Controls;

namespace Berwick.Workshop;

public partial class MainWindow
{
    private List<PromotionPath> promotionRows = [];
    private bool promotionStacked;
    private double promotionWideShare = 0.42;
    private double promotionStackedShare = 0.40;
    private double promotionLastWidth = -1;
    private double promotionLastHeight = -1;

    // Reflow the list above the details when the available tab width no longer fits both.
    // Preserve manual splitter proportions on subsequent window resizes.
    private void PromotionSplit_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PromotionListColumn is null || PromotionSplit is null || PromotionSplitter is null)
            return;
        double width = e.NewSize.Width, height = e.NewSize.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
            return;

        bool stacked = width < 1050;
        if (stacked == promotionStacked
            && Math.Abs(width - promotionLastWidth) < 1
            && Math.Abs(height - promotionLastHeight) < 1)
            return;
        promotionLastWidth = width;
        promotionLastHeight = height;
        promotionStacked = stacked;

        if (stacked)
        {
            PromotionListColumn.Width = new GridLength(1, GridUnitType.Star);
            PromotionDividerColumn.Width = new GridLength(0);
            PromotionDetailColumn.Width = new GridLength(0);
            PromotionListRow.Height = new GridLength(Math.Clamp(height * promotionStackedShare, 150, Math.Max(150, height - 140)));
            PromotionDividerRow.Height = new GridLength(10);
            PromotionDetailRow.Height = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(PromotionListPane, 0);
            Grid.SetRow(PromotionListPane, 0);
            Grid.SetColumn(PromotionDetailPane, 0);
            Grid.SetRow(PromotionDetailPane, 2);
            Grid.SetColumn(PromotionSplitter, 0);
            Grid.SetRow(PromotionSplitter, 1);
            PromotionSplitter.ResizeDirection = GridResizeDirection.Rows;
            PromotionSplitter.Width = double.NaN;
            PromotionSplitter.Height = 10;
            PromotionSplitter.Cursor = System.Windows.Input.Cursors.SizeNS;
            PromotionSplitter.ToolTip = "Drag to resize the list and promotion details";
        }
        else
        {
            PromotionListRow.Height = new GridLength(1, GridUnitType.Star);
            PromotionDividerRow.Height = new GridLength(0);
            PromotionDetailRow.Height = new GridLength(0);
            double listWidth = Math.Clamp((width - 12) * promotionWideShare, 460, Math.Max(460, width - 460));
            PromotionListColumn.Width = new GridLength(listWidth);
            PromotionDividerColumn.Width = new GridLength(12);
            PromotionDetailColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(PromotionListPane, 0);
            Grid.SetRow(PromotionListPane, 0);
            Grid.SetColumn(PromotionDetailPane, 2);
            Grid.SetRow(PromotionDetailPane, 0);
            Grid.SetColumn(PromotionSplitter, 1);
            Grid.SetRow(PromotionSplitter, 0);
            PromotionSplitter.ResizeDirection = GridResizeDirection.Columns;
            PromotionSplitter.Height = double.NaN;
            PromotionSplitter.Width = 12;
            PromotionSplitter.Cursor = System.Windows.Input.Cursors.SizeWE;
            PromotionSplitter.ToolTip = "Drag to resize the promotion list";
        }
    }

    private void PromotionSplitter_DragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (PromotionSplit is null) return;
        if (promotionStacked)
        {
            if (PromotionSplit.ActualHeight > 10)
                promotionStackedShare = Math.Clamp(PromotionListPane.ActualHeight / (PromotionSplit.ActualHeight - 10), 0.2, 0.8);
        }
        else
        {
            if (PromotionSplit.ActualWidth > 12)
                promotionWideShare = Math.Clamp(PromotionListPane.ActualWidth / (PromotionSplit.ActualWidth - 12), 0.2, 0.8);
        }
    }

    private void RefreshPromotions()
    {
        if (primary is null) return;
        var catalogue = PromotionCatalogue.Read(primary);
        PromotionsNav.IsEnabled = primary.Supported;
        promotionRows = catalogue.Paths.ToList();
        if (session is not null)
        {
            promotionRows = promotionRows.Select(path =>
            {
                try
                {
                    return path with
                    {
                        ProjectMinimumLevel = PromotionRequirements.ReadLevel(session, path.UnitId).Level,
                        ProjectDestinationClassId = PromotionDestinations.TryCurrentTarget(session, path.UnitId)
                    };
                }
                catch (Exception ex) when (ex is EditException or System.IO.IOException) { return path; }
            }).ToList();
        }
        OtherClassChangeGrid.ItemsSource = catalogue.OtherClassChanges;
        OtherClassChangeStatus.Text = catalogue.OriginalSourceVerified
            ? "Separate from true promotions. These are documented behaviors, not yet verified native class-change bindings. They cannot be edited."
            : "Open a verified original game image to view reference-only class changes.";
        PromotionSourceText.Text = catalogue.SourceDescription
            + (session?.ChangeCount > 0 && catalogue.OriginalSourceVerified
                ? " Editing controls show current project values. Original-condition details remain source references."
                : "");
        int? prior = (PromotionGrid.SelectedItem as PromotionPath)?.UnitId;
        PromotionGrid.ItemsSource = EvidenceVisibility.Filter(promotionRows, p => p.Evidence,
            PromotionVerifiedOnly.IsChecked == true).ToList();
        PromotionCount.Text = catalogue.OriginalSourceVerified
            ? $"{promotionRows.Count} source-verified literal promotion callers. This is not a complete promotion-path inventory."
            : "Original promotion conditions are unavailable for this source.";
        PromotionCoverageText.Text = catalogue.OriginalSourceVerified
            ? "Research gaps: " + string.Join("  |  ", catalogue.CoverageGaps.Select(g => g.Category))
            : "";
        var shown = PromotionGrid.ItemsSource.Cast<PromotionPath>().ToList();
        PromotionGrid.SelectedItem = shown.FirstOrDefault(r => r.UnitId == prior)
            ?? shown.FirstOrDefault();
        if (promotionRows.Count == 0) ClearPromotion();
    }

    private void PromotionEvidenceFilterChanged(object sender, RoutedEventArgs e)
    {
        if (PromotionGrid is null || promotionRows is null || PromotionVerifiedOnly is null) return;
        int? selected = (PromotionGrid.SelectedItem as PromotionPath)?.UnitId;
        var visible = EvidenceVisibility.Filter(promotionRows, p => p.Evidence,
            PromotionVerifiedOnly.IsChecked == true).ToList();
        PromotionGrid.ItemsSource = visible;
        PromotionGrid.SelectedItem = visible.FirstOrDefault(p => p.UnitId == selected) ?? visible.FirstOrDefault();
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
        if (path.ProjectDestinationClassId is int projectClass && projectClass != path.DestinationClassId)
            PromotionIdentity.Text += $"  ·  Project destination class 0x{projectClass:X2}";
        AddSection(PromotionDetails, "Evidence and safety");
        AddText(PromotionDetails, $"{path.EvidenceLabel}. This describes the original source route, not gameplay acceptance.");
        if (path.Evidence != EvidenceLevel.Verified)
            AddText(PromotionDetails, "Some source and application details remain unverified. Exact, safely bound edits can be experimental; unresolved binary locations remain read-only.");
        AddPromotionDestinationEditor(path);
        AddPromotionLevelEditor(path);
        AddPromotionThresholdEditors(path);
        AddPromotionWindowEditor(path);
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
        if (path.UnitId is 87 or 88)
        {
            AddPair(PromotionDetails, "Promotion ceremony", path.UnitId == 87 ? "Event 1354" : "Event 1368");
            AddPair(PromotionDetails, "Iron Lance request", path.UnitId == 87 ? "Event 168" : "Event 170");
            AddPair(PromotionDetails, "Item delivery", "Events 129 and 131");
        }
        // New source-only graph. Do NOT replace earlier Town entry metadata: it may
        // identify an upstream dispatcher, whereas the stage below is post-application.
        if (NativePromotionRoutes.ForUnit(path.UnitId) is { } native)
        {
            AddSection(PromotionDetails, "Native quest hashes and staged ceremony (source observed)");
            AddText(PromotionDetails, "Original executable table verified across both original editions. Script-body parity, live quest refresh and gameplay are untested. Existing town-entry research is retained separately.");
            AddPair(PromotionDetails, "Quest check", $"Event {native.CheckEvent} / {native.CheckHash}");
            AddPair(PromotionDetails, "Native application", $"{native.ApplicationText} / {native.ApplicationHash}");
            if (native.IntroEvent is int intro)
                AddPair(PromotionDetails, "Quest introduction", $"Event {intro} (NOT the application pointer)");
            AddPair(PromotionDetails, "Conversion request event", $"Event {native.ConversionEvent}");
            AddPair(PromotionDetails, "Scene setup after application", native.SceneSetup);
            AddPair(PromotionDetails, "Ceremony branches", string.Join(", ", native.SceneEvents.Select(e => $"Event {e}")));
            if (native.RelatedItemEvents.Count > 0)
                AddPair(PromotionDetails, "Item or horse-related events", string.Join(", ", native.RelatedItemEvents.Select(e => $"Event {e}")));
            AddText(PromotionDetails, native.ResearchNote);
            if (!native.ResolvedApplication)
                AddText(PromotionDetails, "The original quest application hash did not resolve in the indexed event table. Do not substitute the intro event or allow a native application writer.");
        }
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
