using System.Windows;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Bjornhjarta.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SetDisplayedVersion();
    }

    // Keep window captions and sidebar version in sync with the compiled assembly.
    // This avoids shipping a new EXE with an older hard-coded UI version.
    private void SetDisplayedVersion()
    {
        var informational = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "development";
        var version = informational.Split('+')[0];
        var readableVersion = version.Replace("-rc.", " RC", StringComparison.OrdinalIgnoreCase);
        var channel = version.Contains("-rc.", StringComparison.OrdinalIgnoreCase)
            ? "RELEASE CANDIDATE"
            : version.Contains('-') ? "PREVIEW BUILD" : "STABLE RELEASE";
        Title = $"Bjornhjarta - Enshrouded ToolBox v{readableVersion}";
        SidebarVersionText.Text = $"VERSION {readableVersion.ToUpperInvariant()}  •  {channel}";
    }

    // Windows 10/11: request a dark native title bar, while keeping normal
    // Windows window controls, resizing, accessibility and snap layouts.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle != IntPtr.Zero)
            {
                int enabled = 1;
                // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on supported Windows versions;
                // 19 covers older Windows 10 builds. Failure leaves the native default.
                if (DwmSetWindowAttribute(windowHandle, 20, ref enabled, sizeof(int)) != 0)
                    _ = DwmSetWindowAttribute(windowHandle, 19, ref enabled, sizeof(int));

                // Windows 11 also supports explicit COLORREF titlebar colors,
                // so it matches our dark UI even if the OS uses a light theme.
                // COLORREF is 0x00BBGGRR (not 0x00RRGGBB).
                int captionColor = 0x00101410; // #101410
                int captionTextColor = 0x00E2EDF1; // #F1EDE2
                int borderColor = 0x002E3A30; // #303A2E
                _ = DwmSetWindowAttribute(windowHandle, 35, ref captionColor, sizeof(int));
                _ = DwmSetWindowAttribute(windowHandle, 36, ref captionTextColor, sizeof(int));
                _ = DwmSetWindowAttribute(windowHandle, 34, ref borderColor, sizeof(int));
            }
        }
        catch (DllNotFoundException) { /* cosmetic setting is optional */ }
        catch (EntryPointNotFoundException) { /* unsupported Windows build */ }
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int valueSize);

    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        PageTitle.Text = "THE GREAT HALL";
        PageSubtitle.Text = "Your tools. Your world. Your saga.";
    }

    private void Blueprint_Click(object sender, RoutedEventArgs e)
    {
        PageTitle.Text = "BLUEPRINT FORGE";
        PageSubtitle.Text = "Create, edit, validate and save offline blueprint documents.";
        try
        {
            var editor = new BlueprintEditorWindow { Owner = this };
            editor.Show();
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException("Opening Blueprint Forge", ex);
            MessageBox.Show(
                this,
                "Bjornhjarta could not open Blueprint Forge. The dashboard will remain open.\n\n" +
                ex.Message + "\n\nDiagnostic log: " + AppDiagnostics.LogPath,
                "Blueprint Forge could not open",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ModManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PageTitle.Text = "EML MOD MANAGER";
            PageSubtitle.Text = "Manage the Enshrouded Mod Loader installation and validate mod packages.";
            var manager = new EmlManagerWindow { Owner = this };
            manager.ShowDialog();
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException("Opening EML Mod Manager", ex);
            MessageBox.Show(
                this,
                "Bjornhjarta could not open the EML Mod Manager. The main application will remain open.\n\n" +
                ex.Message + "\n\nDiagnostic log: " + AppDiagnostics.LogPath,
                "EML Mod Manager could not open",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "This module is planned but not implemented in this prototype.",
            "Coming in a later build",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
