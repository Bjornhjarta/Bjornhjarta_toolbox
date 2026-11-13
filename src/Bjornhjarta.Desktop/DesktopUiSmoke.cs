using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Bjornhjarta.Desktop;

/// <summary>
/// Read-only, in-process WPF smoke test for an EXTRACTED portable EXE.
/// This tests window creation/layout, not buttons, game files or mod operations.
/// Called only with --ui-smoke. All UI work stays on the WPF dispatcher thread.
/// </summary>
internal static class DesktopUiSmoke
{
    internal static string ReportPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bjornhjarta", "Logs", "ui-smoke-rc2.json");

    private sealed record Check(string Name, bool Passed, string Detail);

    internal static void Run(Application app, MainWindow dashboard)
    {
        var checks = new List<Check>();
        try
        {
            Verify("Dashboard visible and laid out", () =>
            {
                dashboard.UpdateLayout();
                Require(dashboard.IsVisible && dashboard.ActualWidth > 100 && dashboard.ActualHeight > 100,
                    "Dashboard did not produce a visible layout.");
                Require(dashboard.Title.Contains("1.0.0 RC2", StringComparison.Ordinal),
                    "Unexpected EXE/window version: " + dashboard.Title);
                Require(dashboard.SidebarVersionText.Text.Contains("RC2", StringComparison.Ordinal),
                    "Sidebar version disagrees with the compiled application.");
                Require(dashboard.PageTitle.Text == "THE GREAT HALL", "Dashboard title is missing.");
            }, checks);

            VerifyWindow("Blueprint Forge window opens", () => new BlueprintEditorWindow { Owner = dashboard }, checks);
            VerifyWindow("Image-to-Blueprint window opens", () =>
                new ImageToBlueprintWindow(0, 0, 0) { Owner = dashboard }, checks);
            VerifyWindow("Model Asset Library window opens", () =>
                new ModelAssetLibraryWindow(initialLibraryPath: Path.Combine(
                    Path.GetTempPath(), "Bjornhjarta-UI-SMOKE-" + Guid.NewGuid().ToString("N")))
                { Owner = dashboard }, checks);
            // EML constructor intentionally skips saved profiles/scans in UI smoke mode.
            VerifyWindow("EML Mod Manager window opens without using saved profiles", () =>
                new EmlManagerWindow { Owner = dashboard }, checks);
        }
        catch (Exception ex)
        {
            checks.Add(new Check("UI smoke runner", false, ex.ToString()));
            AppDiagnostics.WriteException("UI smoke runner", ex);
        }
        finally
        {
            bool pass = checks.Count == 5 && checks.All(check => check.Passed);
            WriteReport(checks, pass);
            app.Shutdown(pass ? 0 : 2);
        }
    }

    private static void Verify(string name, Action action, List<Check> checks)
    {
        try
        {
            action();
            checks.Add(new Check(name, true, "Window/layout check passed."));
        }
        catch (Exception ex)
        {
            checks.Add(new Check(name, false, ex.ToString()));
            AppDiagnostics.WriteException("UI smoke: " + name, ex);
        }
    }

    private static void VerifyWindow(string name, Func<Window> create, List<Check> checks)
    {
        Window? window = null;
        try
        {
            window = create();
            window.Show();
            window.UpdateLayout();
            Require(window.IsVisible && window.ActualWidth > 100 && window.ActualHeight > 100,
                "Window could not be shown or measured: " + name);
            checks.Add(new Check(name, true, "Window constructor, Loaded event and layout passed."));
        }
        catch (Exception ex)
        {
            checks.Add(new Check(name, false, ex.ToString()));
            AppDiagnostics.WriteException("UI smoke: " + name, ex);
        }
        finally
        {
            if (window is not null)
            {
                try { window.Close(); }
                catch (Exception ex) { AppDiagnostics.WriteException("UI smoke window cleanup", ex); }
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void WriteFatal(Exception ex) => WriteReport(
        new List<Check> { new("WPF startup", false, ex.ToString()) }, false);

    private static void WriteReport(List<Check> checks, bool pass)
    {
        try
        {
            var path = ReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var report = new
            {
                version = "1.0.0-rc.2",
                passed = pass,
                checkedAtUtc = DateTimeOffset.UtcNow,
                interactiveAcceptanceCompleted = false,
                noGameFilesModifiedByTest = true,
                checks
            };
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            AppDiagnostics.WriteEvent("WPF UI smoke test " + (pass ? "PASS" : "FAIL") + ": " + path);
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException("Saving UI smoke report", ex);
            // Even if a disk issue blocks the report, launch script fails closed
            // because the expected report is absent or still contains stale data.
        }
    }
}
