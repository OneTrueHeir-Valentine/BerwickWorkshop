using Berwick.Workshop;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

/// <summary>
/// Lays out a list, splitter and details grid at several sizes. The grid is hosted in a window that is never
/// shown, so the tests do not take focus.
/// </summary>
internal static class ResponsiveSplitTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Wide pages keep the details beside the list at their designed width", () => OnSta(() =>
        {
            using var host = new Host(1200, 700, detailsWidth: 300);
            Check(!ResponsiveSplit.IsStacked(host.Grid), "A wide page was stacked.");
            Check(Grid.GetColumn(host.Details) == 2 && Grid.GetRow(host.Details) == 0, "The details are not beside the list.");
            Check(Math.Abs(host.Details.ActualWidth - 300) < 2, $"The details are {host.Details.ActualWidth:0} wide, not 300.");
            Check(host.Splitter.ResizeDirection == GridResizeDirection.Columns && Math.Abs(host.Splitter.ActualWidth - 16) < 1,
                "The splitter lost its column direction or width.");
        }));
        test("Narrow pages stack the list above the details and return when widened", () => OnSta(() =>
        {
            using var host = new Host(1200, 700, detailsWidth: 300);
            host.Resize(700, 600);
            Check(ResponsiveSplit.IsStacked(host.Grid), "A narrow page was not stacked.");
            Check(Grid.GetRow(host.List) == 0 && Grid.GetRow(host.Splitter) == 1 && Grid.GetRow(host.Details) == 2 &&
                Grid.GetColumn(host.Details) == 0, "The stacked order is not list, splitter, details.");
            Check(host.Splitter.ResizeDirection == GridResizeDirection.Rows, "The splitter does not resize rows when stacked.");
            Check(host.List.ActualWidth > 650 && host.Details.ActualWidth > 650, "Stacked panels do not use the full width.");
            Check(host.List.ActualHeight >= 150 && host.Details.ActualHeight >= 140, "A stacked panel is too short to use.");
            host.Resize(1200, 700);
            Check(!ResponsiveSplit.IsStacked(host.Grid) && Grid.GetColumn(host.Details) == 2, "The page did not return to side by side.");
            Check(host.Grid.RowDefinitions.Count == 3, "Rows were added again.");
        }));
        test("Splitter changes are kept as a share when the window changes size", () => OnSta(() =>
        {
            using var host = new Host(1200, 700, detailsWidth: 300);
            host.Grid.ColumnDefinitions[0].Width = new GridLength(400);
            host.Settle();
            host.Splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            host.Settle();
            double share = host.List.ActualWidth / (1200 - 16);
            host.Resize(1500, 700);
            double after = host.List.ActualWidth / (1500 - 16);
            Check(Math.Abs(share - after) < .01, $"The list share changed from {share:0.000} to {after:0.000}.");
        }));
        test("Very short and very narrow pages keep both panels reachable", () => OnSta(() =>
        {
            using var host = new Host(1200, 700, detailsWidth: 380);
            host.Resize(500, 300);
            Check(host.List.ActualHeight >= 150, "The list collapsed.");
            host.Resize(900, 700);
            Check(host.List.ActualWidth >= 240 && host.Details.ActualWidth >= 260 - 1, "A side-by-side panel became too narrow.");
        }));
        test("Showing a page again does not attach the layout twice", () => OnSta(() =>
        {
            using var host = new Host(1200, 700, detailsWidth: 300);
            host.Reload();
            host.Reload();
            Check(host.Grid.RowDefinitions.Count == 3, "Rows were added when the page was shown again.");
            host.Resize(700, 600);
            Check(ResponsiveSplit.IsStacked(host.Grid), "The layout stopped responding after the page was shown again.");
        }));
        test("A grid without the expected shape keeps its layout instead of failing", () => OnSta(() =>
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(new Border());
            ResponsiveSplit.SetEnabled(grid, true);
            using var source = Host.Source(grid, 800, 600);
            Host.Pump();
            Check(grid.RowDefinitions.Count == 0 && !ResponsiveSplit.IsStacked(grid), "An unexpected grid was changed.");
        }));
    }

    private sealed class Host : IDisposable
    {
        private HwndSource source;
        private readonly Border root = new();
        public Grid Grid { get; } = new();
        public Border List { get; } = new();
        public GridSplitter Splitter { get; } = new() { Width = 16, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        public Border Details { get; } = new();

        public Host(double width, double height, double detailsWidth)
        {
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(detailsWidth), MinWidth = 200 });
            Grid.SetColumn(Splitter, 1);
            Grid.SetColumn(Details, 2);
            Grid.Children.Add(Splitter);
            Grid.Children.Add(List);
            Grid.Children.Add(Details);
            ResponsiveSplit.SetEnabled(Grid, true);
            root.Child = Grid;
            source = Source(root, width, height);
            Resize(width, height);
        }

        public static HwndSource Source(System.Windows.Media.Visual visual, double width, double height)
        {
            // No WS_VISIBLE: the window exists for layout and events but is never shown or activated.
            var parameters = new HwndSourceParameters("Responsive layout test", (int)width, (int)height) { WindowStyle = unchecked((int)0x80000000) };
            return new HwndSource(parameters) { RootVisual = visual };
        }

        public void Resize(double width, double height)
        {
            root.Width = width;
            root.Height = height;
            Settle();
        }

        public void Reload()
        {
            source.RootVisual = null;
            source.Dispose();
            Pump();
            source = Source(root, root.Width, root.Height);
            Settle();
        }

        public void Settle()
        {
            Pump();
            root.UpdateLayout();
            Pump();
            root.UpdateLayout();
        }

        public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });

        public void Dispose()
        {
            source.RootVisual = null;
            source.Dispose();
        }
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Responsive layout test timed out");
        error?.Throw();
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
