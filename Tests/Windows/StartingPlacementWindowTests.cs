using Berwick.Core;
using Berwick.Workshop;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;

internal static class StartingPlacementWindowTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Unverified starting records remain inspectable with distinct identities", () => OnSta(() =>
        {
            var eligible = Candidate(100, false);
            var unverified = Candidate(101, false) with
            {
                Unit = new(101, 5, 1, 1, "Unit", 256, 436), Slot = 2,
                PlacementProblem = "This source slot has no supported starting position."
            };
            var window = new StartingPlacementWindow(9, [eligible, unverified], null, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var choices = panel.Children.OfType<ComboBox>().Single();
                Check(choices.Items.Count == 1, "Unverified record was offered by default");
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("Show")).IsChecked = true;
                Check(choices.Items.Count == 2, "Unverified record cannot be inspected");
                choices.SelectedIndex = 1;
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("I understand")).IsChecked = true;
                var buttons = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                Check(!buttons[0].IsEnabled && !buttons[1].IsEnabled, "An unverified unused record enabled an edit");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("source slot")), "Loading refusal was hidden");
                string label = choices.SelectedItem.ToString()!;
                Check(choices.DisplayMemberPath == "Label" && label.Contains("record 1") && label.Contains("slot 2") && label.Contains("cannot be restored"),
                    "Duplicate names lack a distinct record label");
            }
            finally { window.Close(); }
        }));
        test("Unverified coordinates retain their warning and can be removed", () => OnSta(() =>
        {
            var candidate = Candidate(100, true) with { PlacementProblem = "This source slot has no supported starting position." };
            var window = new StartingPlacementWindow(9, [candidate], null, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("Show")).IsChecked = true;
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("I understand")).IsChecked = true;
                var buttons = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                Check(!buttons[0].IsEnabled && buttons[1].IsEnabled, "Unverified coordinates disabled removal or enabled placement");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("source slot") && text.Text.Contains("Removal clears only")),
                    "Writing coordinates concealed the loading limitation");
            }
            finally { window.Close(); }
        }));
        test("Restoring uses the original hex and offers no coordinate entry", () => OnSta(() =>
        {
            (int X, int Y)? applied = null;
            var window = new StartingPlacementWindow(9, [Candidate(100, false)], null, (_, removing, x, y, _) =>
            {
                if (!removing) applied = (x, y);
                return false;
            });
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                Check(!panel.Children.OfType<StackPanel>().SelectMany(row => row.Children.OfType<TextBox>()).Any(), "Coordinate entry is still offered");
                var buttons = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                Check(buttons[0].Content.ToString() == "Restore original position", "The restore button has the wrong name");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("column 3, row 4")), "The original hex is not shown");
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("I understand")).IsChecked = true;
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(applied == (3, 4), "Restoration did not use the original hex");
            }
            finally { window.Close(); }
        }));
        test("Starting placement selector defaults to eligible unused slots", () => OnSta(() =>
        {
            var window = new StartingPlacementWindow(9, [Candidate(101, true), Candidate(100, false), Candidate(102, false) with { Problem = "Ambiguous group" }], 101, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var choices = panel.Children.OfType<ComboBox>().Single();
                Check(choices.Items.Count == 1 && choices.SelectedIndex == 0, "Default selector includes occupied or unsupported records");
                var showAll = panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("Show"));
                showAll.IsChecked = true;
                Check(choices.Items.Count == 3, "Existing placements and refusal reasons are inaccessible");
                showAll.IsChecked = false;
                Check(choices.Items.Count == 1, "Eligibility filter was not restored");
            }
            finally { window.Close(); }
        }));
        test("Starting placement no-op remains open with feedback", () => OnSta(() =>
        {
            var window = new StartingPlacementWindow(9, [Candidate(100, false)], null, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("I understand")).IsChecked = true;
                var place = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().First();
                place.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.DialogResult is null, "No-op reported dialog success");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("No changes")), "No-op has no explanation");
            }
            finally { window.Close(); }
        }));
        test("Starting placement empty filter explains how to inspect existing records", () => OnSta(() =>
        {
            var window = new StartingPlacementWindow(9, [Candidate(101, true)], 101, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                Check(panel.Children.OfType<ComboBox>().Single().Items.Count == 0, "Placed unit bypassed the default filter");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("No removed starting positions")), "Empty filter has no explanation");
                panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("Show")).IsChecked = true;
                Check(panel.Children.OfType<ComboBox>().Single().Items.Count == 1, "Removal record was hidden permanently");
                Check(panel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("Removal clears only")), "Removal explanation missing");
            }
            finally { window.Close(); }
        }));
        test("Starting placement dialog displays source identity and shared effects", () => OnSta(() =>
        {
            var candidate = Candidate(100, false);
            var window = new StartingPlacementWindow(9, [candidate], null, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                string text = string.Join("\n", panel.Children.OfType<TextBlock>().Select(block => block.Text));
                Check(text.Contains("ud005") && text.Contains("key 0x64") && text.Contains("Team 1, group 2, slot 1"), "Record identity missing");
                Check(text.Contains("Opening event 12") && text.Contains("9, 10") && text.Contains("never deletes"), "Source or shared-effects explanation missing");
            }
            finally { window.Close(); }
        }));
        test("Starting placement dialog requires fresh shared acknowledgement and refuses unsupported choices", () => OnSta(() =>
        {
            var window = new StartingPlacementWindow(9, [Candidate(100, false), Candidate(101, true), Candidate(102, false) with { Problem = "Ambiguous group" }], null, (_, _, _, _, _) => false);
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var choices = panel.Children.OfType<ComboBox>().Single();
                var acknowledge = panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("I understand"));
                var showAll = panel.Children.OfType<CheckBox>().Single(box => box.Content.ToString()!.StartsWith("Show"));
                var buttons = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                Check(!buttons[0].IsEnabled && !buttons[1].IsEnabled, "Shared change enabled before acknowledgement");
                acknowledge.IsChecked = true;
                Check(buttons[0].IsEnabled && !buttons[1].IsEnabled, "Unused slot action incorrect");
                showAll.IsChecked = true;
                Check(acknowledge.IsChecked == false, "Acknowledgement carried across filter changes");
                choices.SelectedIndex = 1;
                Check(acknowledge.IsChecked == false && !buttons[1].IsEnabled, "Acknowledgement carried to another choice");
                acknowledge.IsChecked = true;
                Check(!buttons[0].IsEnabled && buttons[1].IsEnabled, "Occupied slot action incorrect");
                choices.SelectedIndex = 2;
                acknowledge.IsChecked = true;
                Check(!buttons[0].IsEnabled && !buttons[1].IsEnabled, "Unsupported choice enabled");
            }
            finally { window.Close(); }
        }));
    }

    private static StartingUnitCandidate Candidate(int key, bool occupied) => new(new(key, 5, 0, 1, "Unit", 0, 180),
        new(9, 12, 12, 0, 5, 1, 2, false), 1, occupied, null, [9, 10], [9, 10], [12, 13]) { OriginalPosition = (3, 4) };
    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Starting placement window test timed out");
        error?.Throw();
    }
    private static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
    }
}
