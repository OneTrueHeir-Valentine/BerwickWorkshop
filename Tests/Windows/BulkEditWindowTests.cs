using Berwick.Core;
using Berwick.Workshop;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

internal static class BulkEditWindowTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Loading a recipe for the same table replaces its steps and invalidates the preview", () => OnSta(() =>
        {
            var session = new EditSession(BulkEditTests.Fixture());
            var window = new BulkEditWindow(session, () => { });
            try
            {
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var steps = panel.Children.OfType<ListBox>().Single(x => AutomationProperties.GetName(x) == "Steps, in order");
                Button FindButton(string name) => panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()).Single(x => Equals(x.Content, name));
                var first = Recipe("might", 1);
                var second = Recipe("accuracy", 2);
                window.ShowRecipe(first);
                FindButton("Preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(FindButton("Apply").IsEnabled, "First recipe did not produce an applicable preview");
                window.ShowRecipe(second);
                Check(steps.Items.Count == 1 && steps.Items[0].ToString()!.Contains("Accuracy"), "Old steps were retained");
                Check(!FindButton("Apply").IsEnabled, "The old preview is still applicable");
                FindButton("Preview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FindButton("Apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(session.Value(EditSession.Items, 1, "might") == 8, "The replaced recipe changed Might");
                Check(session.Value(EditSession.Items, 1, "accuracy") == 72, "The new recipe did not change Accuracy");
            }
            finally { window.Close(); }
        }));
        test("A recipe with a mismatched schema leaves the current window unchanged", () => OnSta(() =>
        {
            var session = new EditSession(BulkEditTests.Fixture());
            var window = new BulkEditWindow(session, () => { });
            try
            {
                window.ShowRecipe(Recipe("might", 1));
                var panel = (StackPanel)((ScrollViewer)window.Content).Content;
                var steps = panel.Children.OfType<ListBox>().Single(x => AutomationProperties.GetName(x) == "Steps, in order");
                bool refused = false;
                try { window.ShowRecipe(Recipe("accuracy", 2) with { Schema = "obsolete" }); }
                catch (EditException) { refused = true; }
                Check(refused, "An incompatible recipe was accepted");
                Check(steps.Items.Count == 1 && steps.Items[0].ToString()!.Contains("Might"), "Refused recipe changed the steps");
                Check(!session.CanUndo, "Refused recipe edited the project");
            }
            finally { window.Close(); }
        }));
    }

    private static BulkRecipe Recipe(string field, int amount) => BulkEdit.Load(BulkEdit.Save(new(
        BulkRecipe.CurrentVersion, "Test", EditSession.Items, new BulkSelection([1], [], [], []),
        [new BulkStep(field, BulkOperation.Add, amount)])));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
