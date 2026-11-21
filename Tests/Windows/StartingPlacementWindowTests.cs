using Berwick.Core;
using Berwick.Workshop;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;

internal static class StartingPlacementWindowTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Starting placement dialog displays source identity and shared effects", () => OnSta(() =>
        {
            var candidate = Candidate(100, false);
            var window = new StartingPlacementWindow(9, [candidate], null, (_, _, _, _, _) => { });
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
            var window = new StartingPlacementWindow(9, [Candidate(100, false), Candidate(101, true), Candidate(102, false) with { Problem = "Ambiguous group" }], null, (_, _, _, _, _) => { });
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var choices = panel.Children.OfType<ComboBox>().Single();
                var acknowledge = panel.Children.OfType<CheckBox>().Single();
                var buttons = panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                Check(!buttons[0].IsEnabled && !buttons[1].IsEnabled, "Shared change enabled before acknowledgement");
                acknowledge.IsChecked = true;
                Check(buttons[0].IsEnabled && !buttons[1].IsEnabled, "Unused slot action incorrect");
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
        new(9, 12, 12, 0, 5, 1, 2, false), 1, occupied, null, [9, 10], [9, 10], [12, 13]);
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
