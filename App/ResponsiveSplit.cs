using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Berwick.Workshop;

/// <summary>
/// Responsive layout for the existing list/splitter/details three-column pages.
/// Retains splitter choices while keeping the details readable in narrower windows.
/// </summary>
public static class ResponsiveSplit
{
    private static readonly ConditionalWeakTable<Grid, Layout> Layouts = new();

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ResponsiveSplit),
            new PropertyMetadata(false, EnabledChanged));

    public static bool GetEnabled(DependencyObject source) =>
        (bool)source.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject source, bool value) =>
        source.SetValue(EnabledProperty, value);

    private static void EnabledChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not Grid grid) return;
        if ((bool)args.NewValue)
        {
            Layouts.GetValue(grid, panel => new Layout(panel)).Start();
        }
        else if (Layouts.TryGetValue(grid, out var layout))
        {
            layout.Stop();
            Layouts.Remove(grid);
        }
    }

    private sealed class Layout(Grid grid)
    {
        private GridSplitter? splitter;
        private UIElement? list;
        private UIElement? details;
        private bool started;
        private bool applying;
        private bool stacked;
        private double columnShare = .44;
        private double rowShare = .44;
        private const double Breakpoint = 880;

        public void Start()
        {
            if (started) return;
            started = true;
            grid.Loaded += Loaded;
            if (grid.IsLoaded) Loaded(grid, new RoutedEventArgs());
        }

        public void Stop()
        {
            grid.Loaded -= Loaded;
            grid.SizeChanged -= Sized;
            if (splitter is not null) splitter.DragCompleted -= Dragged;
            started = false;
        }

        private void Loaded(object sender, RoutedEventArgs e)
        {
            if (splitter is not null) return;
            if (grid.ColumnDefinitions.Count != 3 || grid.RowDefinitions.Count != 0)
                throw new InvalidOperationException("Responsive split requires the existing three-column grid.");
            splitter = grid.Children.OfType<GridSplitter>()
                .SingleOrDefault(item => Grid.GetColumn(item) == 1);
            list = grid.Children.Cast<UIElement>()
                .SingleOrDefault(item => item is not GridSplitter && Grid.GetColumn(item) == 0);
            details = grid.Children.Cast<UIElement>()
                .SingleOrDefault(item => item is not GridSplitter && Grid.GetColumn(item) == 2);
            if (splitter is null || list is null || details is null || grid.Children.Count != 3)
                throw new InvalidOperationException("Responsive split needs one list, one splitter and one details panel.");

            // Original column minimums are incompatible with row stacking. The breakpoint
            // protects readable widths in wide mode while the grid handles very narrow sizes.
            foreach (var col in grid.ColumnDefinitions) col.MinWidth = 0;
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            splitter.DragCompleted += Dragged;
            grid.SizeChanged += Sized;
            Adjust(grid.ActualWidth, grid.ActualHeight);
        }

        private void Sized(object sender, SizeChangedEventArgs e)
        {
            Adjust(e.NewSize.Width, e.NewSize.Height);
        }

        private void Adjust(double width, double height)
        {
            if (applying || splitter is null || list is null || details is null
                || width < 1 || height < 1 || !double.IsFinite(width) || !double.IsFinite(height))
                return;
            applying = true;
            try
            {
                bool vertical = width < Breakpoint;
                stacked = vertical;
                if (vertical)
                {
                    grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                    grid.ColumnDefinitions[1].Width = new GridLength(0);
                    grid.ColumnDefinitions[2].Width = new GridLength(0);
                    grid.RowDefinitions[0].Height = new GridLength(
                        Math.Clamp(height * rowShare, 150, Math.Max(150, height - 140)));
                    grid.RowDefinitions[1].Height = new GridLength(10);
                    grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
                    Grid.SetColumn(list, 0);
                    Grid.SetRow(list, 0);
                    Grid.SetColumn(details, 0);
                    Grid.SetRow(details, 2);
                    Grid.SetColumn(splitter, 0);
                    Grid.SetRow(splitter, 1);
                    splitter.Width = double.NaN;
                    splitter.Height = 10;
                    splitter.ResizeDirection = GridResizeDirection.Rows;
                    splitter.Cursor = Cursors.SizeNS;
                    splitter.ToolTip = "Drag to resize the list and details";
                }
                else
                {
                    grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
                    grid.RowDefinitions[1].Height = new GridLength(0);
                    grid.RowDefinitions[2].Height = new GridLength(0);
                    grid.ColumnDefinitions[0].Width = new GridLength(
                        Math.Clamp((width - 12) * columnShare, 240, Math.Max(240, width - 260)));
                    grid.ColumnDefinitions[1].Width = new GridLength(12);
                    grid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                    Grid.SetColumn(list, 0);
                    Grid.SetRow(list, 0);
                    Grid.SetColumn(details, 2);
                    Grid.SetRow(details, 0);
                    Grid.SetColumn(splitter, 1);
                    Grid.SetRow(splitter, 0);
                    splitter.Width = 12;
                    splitter.Height = double.NaN;
                    splitter.ResizeDirection = GridResizeDirection.Columns;
                    splitter.Cursor = Cursors.SizeWE;
                    splitter.ToolTip = "Drag to resize the list";
                }
            }
            finally { applying = false; }
        }

        private void Dragged(object sender, DragCompletedEventArgs e)
        {
            if (list is null) return;
            if (stacked)
            {
                double available = grid.ActualHeight - 10;
                if (available > 0)
                    rowShare = Math.Clamp(list.RenderSize.Height / available, .2, .8);
            }
            else
            {
                double available = grid.ActualWidth - 12;
                if (available > 0)
                    columnShare = Math.Clamp(list.RenderSize.Width / available, .2, .8);
            }
        }
    }
}
