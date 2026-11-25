using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Berwick.Workshop;

/// <summary>
/// Responsive layout for the list, splitter and details pages. Wide pages keep the list beside the details;
/// narrow pages stack the list above the details. The splitter position is kept as a share of the space.
/// </summary>
public static class ResponsiveSplit
{
    /// <summary>Below this page width the list moves above the details.</summary>
    public const double Breakpoint = 880;
    private const double StackedSplitter = 10;
    private const double MinimumList = 240;
    private const double MinimumDetails = 260;
    private const double MinimumStackedList = 150;
    private const double MinimumStackedDetails = 140;

    private static readonly ConditionalWeakTable<Grid, Layout> Layouts = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ResponsiveSplit),
            new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject source) => (bool)source.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject source, bool value) => source.SetValue(EnabledProperty, value);

    /// <summary>Whether the grid currently shows the list above the details.</summary>
    internal static bool IsStacked(Grid grid) => Layouts.TryGetValue(grid, out var layout) && layout.Stacked;

    /// <summary>
    /// Checks that a grid has the expected shape: three columns, no rows, and exactly a list in column 0,
    /// a splitter in column 1 and details in column 2. Returns null when it does, or the reason it does not.
    /// </summary>
    internal static string? StructureProblem(Grid grid)
    {
        if (grid.ColumnDefinitions.Count != 3 || grid.RowDefinitions.Count != 0)
            return "The grid needs three columns and no rows.";
        var children = grid.Children.Cast<UIElement>().ToList();
        if (children.Count != 3 ||
            children.Count(child => child is GridSplitter && Grid.GetColumn(child) == 1) != 1 ||
            children.Count(child => child is not GridSplitter && Grid.GetColumn(child) == 0) != 1 ||
            children.Count(child => child is not GridSplitter && Grid.GetColumn(child) == 2) != 1)
            return "The grid needs one list, one splitter and one details panel.";
        return null;
    }

    private static void EnabledChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not Grid grid) return;
        if ((bool)args.NewValue)
            Layouts.GetValue(grid, panel => new Layout(panel)).Start();
        else if (Layouts.TryGetValue(grid, out var layout))
            layout.Stop();
    }

    private sealed class Layout(Grid grid)
    {
        private GridSplitter? splitter;
        private UIElement? list;
        private UIElement? details;
        private bool started;
        private bool applying;
        private double splitterWidth;
        private double originalDetailsWidth;
        private double? columnShare;
        private double rowShare = .44;

        public bool Stacked { get; private set; }

        public void Start()
        {
            if (started) return;
            started = true;
            grid.Loaded += Loaded;
            grid.SizeChanged += Sized;
            if (splitter is not null) splitter.DragCompleted += Dragged;
            if (grid.IsLoaded) Loaded(grid, new RoutedEventArgs());
        }

        public void Stop()
        {
            if (!started) return;
            started = false;
            grid.Loaded -= Loaded;
            grid.SizeChanged -= Sized;
            if (splitter is not null) splitter.DragCompleted -= Dragged;
        }

        private void Loaded(object sender, RoutedEventArgs e)
        {
            if (splitter is null)
            {
                // A page without the expected shape keeps its fixed layout. The XAML test checks every page
                // that enables this, so this only guards against a crash.
                if (StructureProblem(grid) is not null) return;
                var children = grid.Children.Cast<UIElement>().ToList();
                splitter = children.OfType<GridSplitter>().Single();
                list = children.Single(child => child is not GridSplitter && Grid.GetColumn(child) == 0);
                details = children.Single(child => child is not GridSplitter && Grid.GetColumn(child) == 2);
                splitterWidth = grid.ColumnDefinitions[1].Width.IsAbsolute ? grid.ColumnDefinitions[1].Width.Value : 16;
                originalDetailsWidth = grid.ColumnDefinitions[2].Width.IsAbsolute ? grid.ColumnDefinitions[2].Width.Value : 300;

                // The column minimums would stop the list and details from stacking. The breakpoint and the
                // clamps below keep both readable instead.
                foreach (var column in grid.ColumnDefinitions) column.MinWidth = 0;
                grid.RowDefinitions.Add(new RowDefinition());
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
                splitter.DragCompleted += Dragged;
            }
            // Loaded also runs when the page is shown again; the handlers are attached only once.
            Adjust(grid.ActualWidth, grid.ActualHeight);
        }

        private void Sized(object sender, SizeChangedEventArgs e) => Adjust(e.NewSize.Width, e.NewSize.Height);

        private void Adjust(double width, double height)
        {
            if (applying || splitter is null || list is null || details is null ||
                width < 1 || height < 1 || !double.IsFinite(width) || !double.IsFinite(height))
                return;
            applying = true;
            try
            {
                Stacked = width < Breakpoint;
                if (Stacked)
                    Stack(height);
                else
                    PlaceSideBySide(width);
            }
            finally { applying = false; }
        }

        private void Stack(double height)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = new GridLength(0);
            grid.ColumnDefinitions[2].Width = new GridLength(0);
            double listHeight = Math.Clamp(height * rowShare, MinimumStackedList,
                Math.Max(MinimumStackedList, height - StackedSplitter - MinimumStackedDetails));
            grid.RowDefinitions[0].Height = new GridLength(listHeight);
            grid.RowDefinitions[1].Height = new GridLength(StackedSplitter);
            grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            Place(list!, 0, 0);
            Place(splitter!, 0, 1);
            Place(details!, 0, 2);
            splitter!.Width = double.NaN;
            splitter.Height = StackedSplitter;
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.Cursor = Cursors.SizeNS;
            splitter.ToolTip = "Drag to resize the list and details";
        }

        private void PlaceSideBySide(double width)
        {
            double available = width - splitterWidth;
            // Until the splitter is moved, the details keep the width the page was designed with.
            columnShare ??= Math.Clamp((available - originalDetailsWidth) / available, .2, .8);
            double listWidth = Math.Clamp(available * columnShare.Value, MinimumList,
                Math.Max(MinimumList, available - MinimumDetails));
            grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            grid.RowDefinitions[1].Height = new GridLength(0);
            grid.RowDefinitions[2].Height = new GridLength(0);
            grid.ColumnDefinitions[0].Width = new GridLength(listWidth);
            grid.ColumnDefinitions[1].Width = new GridLength(splitterWidth);
            grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            Place(list!, 0, 0);
            Place(splitter!, 1, 0);
            Place(details!, 2, 0);
            splitter!.Width = splitterWidth;
            splitter.Height = double.NaN;
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.Cursor = Cursors.SizeWE;
            splitter.ToolTip = "Drag to resize";
        }

        private static void Place(UIElement element, int column, int row)
        {
            Grid.SetColumn(element, column);
            Grid.SetRow(element, row);
        }

        private void Dragged(object sender, DragCompletedEventArgs e)
        {
            if (Stacked)
            {
                double available = grid.ActualHeight - StackedSplitter;
                if (available > 0)
                    rowShare = Math.Clamp(grid.RowDefinitions[0].ActualHeight / available, .2, .8);
            }
            else
            {
                double available = grid.ActualWidth - splitterWidth;
                if (available > 0)
                    columnShare = Math.Clamp(grid.ColumnDefinitions[0].ActualWidth / available, .2, .8);
            }
            Adjust(grid.ActualWidth, grid.ActualHeight);
        }
    }
}
