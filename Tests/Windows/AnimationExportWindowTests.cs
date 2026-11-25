using Berwick.Workshop;
using System.Runtime.ExceptionServices;
using System.Windows.Controls;

internal static class AnimationExportWindowTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Export dialog defaults to current body motion, both files and expanded canvas", () => OnSta(() =>
        {
            var window = new AnimationExportWindow(new AnimationFixture().Set(), "m0_0", 5, "Synthetic", "fixture");
            try
            {
                var (scope, output, canvas) = Choices(window);
                Check(scope.SelectedIndex == 0 && output.SelectedIndex == 0 && canvas.SelectedIndex == 0 && canvas.IsEnabled, "Body export defaults changed");
            }
            finally { window.Close(); }
        }));
        test("Effect mode selects all motions, disables crop and restores body controls", () => OnSta(() =>
        {
            var window = new AnimationExportWindow(new AnimationFixture().Set(), "m0_0", 5, "Synthetic", "fixture");
            try
            {
                var (scope, output, canvas) = Choices(window);
                output.SelectedIndex = 3;
                Check(scope.SelectedIndex == 1 && !canvas.IsEnabled, "Effect mode did not select all motions or disable crop");
                output.SelectedIndex = 0;
                Check(canvas.IsEnabled, "Body canvas control did not re-enable");
            }
            finally { window.Close(); }
        }));
        test("Effect-only motion defaults to effect export and all motions", () => OnSta(() =>
        {
            var window = new AnimationExportWindow(new AnimationFixture().Set(), "m2_1", 5, "Synthetic", "fixture");
            try
            {
                var (scope, output, canvas) = Choices(window);
                Check(output.SelectedIndex == 3 && scope.SelectedIndex == 1 && !canvas.IsEnabled, "Effect-only defaults changed");
            }
            finally { window.Close(); }
        }));
    }

    private static (ComboBox Scope, ComboBox Output, ComboBox Canvas) Choices(AnimationExportWindow window)
    {
        var controls = ((StackPanel)window.Content).Children.OfType<ComboBox>().ToArray();
        if (controls.Length != 3) throw new Exception("Export option controls missing");
        return (controls[0], controls[1], controls[2]);
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30))) throw new TimeoutException("Export dialog test did not finish");
        error?.Throw();
    }

    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
