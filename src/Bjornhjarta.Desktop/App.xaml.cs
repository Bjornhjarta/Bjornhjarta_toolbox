using System.Windows;
using System.Windows.Threading;

namespace Bjornhjarta.Desktop;

public partial class App : Application
{
    internal static bool IsUiSmokeMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        IsUiSmokeMode = e.Args.Any(arg => string.Equals(arg, "--ui-smoke", StringComparison.OrdinalIgnoreCase));
        AppDiagnostics.WriteEvent("Bjornhjarta 1.0.0-rc.2 started" + (IsUiSmokeMode ? " (UI smoke test)" : ""));

        // Use the SAME WPF StartupUri for the automated smoke test as for normal startup.
        // RC2 used to clear StartupUri and manually construct/show a second startup path.
        // A separate startup path is fragile and can throw before the five UI checks run.
        // WPF creates the dashboard after OnStartup returns; ApplicationIdle runs afterward.
        base.OnStartup(e);
        if (!IsUiSmokeMode)
            return;

        AppDiagnostics.WriteEvent("UI smoke: allowing normal WPF StartupUri to create dashboard");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (this.MainWindow is not Bjornhjarta.Desktop.MainWindow dashboard)
                {
                    throw new InvalidOperationException(
                        "WPF StartupUri did not create a dashboard MainWindow. Actual main window: " +
                        (this.MainWindow?.GetType().FullName ?? "(null)"));
                }

                AppDiagnostics.WriteEvent("UI smoke: dashboard created; starting window checks");
                DesktopUiSmoke.Run(this, dashboard);
            }
            catch (Exception ex)
            {
                AppDiagnostics.WriteException("UI smoke startup or test runner", ex);
                DesktopUiSmoke.WriteFatal(ex);
                Shutdown(3);
            }
        }), DispatcherPriority.ApplicationIdle);
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception error)
            AppDiagnostics.WriteException("Uncaught application exception", error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        AppDiagnostics.WriteException("Unobserved background task exception", e.Exception);

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppDiagnostics.WriteException("Unhandled WPF UI exception", e.Exception);
        if (IsUiSmokeMode)
        {
            DesktopUiSmoke.WriteFatal(e.Exception);
            e.Handled = true;
            Current.Shutdown(3);
            return;
        }
        try
        {
            MessageBox.Show(
                "An unexpected error occurred. Details were saved to:\n" + AppDiagnostics.LogPath +
                "\n\nThe application may need to be restarted. No game files were intentionally changed.",
                "Bjornhjarta – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* Allow the original failure to propagate. */ }
        // Do not silently resume after a potentially corrupt UI state.
        e.Handled = false;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppDiagnostics.WriteEvent("Bjornhjarta exiting");
        DispatcherUnhandledException -= OnUiException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }
}
