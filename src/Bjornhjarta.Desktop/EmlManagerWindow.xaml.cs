using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Bjornhjarta.Core.Modding;
using Microsoft.Win32;

namespace Bjornhjarta.Desktop;

public partial class EmlManagerWindow : Window
{
    private readonly EmlInstallationScanner _scanner = new();
    private readonly EmlConfigStore _configStore = new();
    private readonly EmlProxyInstaller _proxyInstaller = new();
    private readonly EmlModPackageManager _modPackageManager = new();
    private readonly EmlManagerPreferencesStore _preferencesStore;
    private readonly ObservableCollection<EmlModPackage> _packages = [];
    private readonly Dictionary<EmlInstallationProfileKind, EmlInstallationScan> _scans = [];
    private readonly Dictionary<string, EmlConfigSettings> _pendingConfigSettings = new(StringComparer.OrdinalIgnoreCase);

    private EmlManagerPreferences _preferences = new();
    private EmlInstallationProfileKind _activeProfile = EmlInstallationProfileKind.Client;
    private EmlInstallationScan? _scan;
    private string? _emmPath;
    private bool _initializing;
    private bool _validationRunning;

    public EmlManagerWindow()
    {
        // Set this BEFORE InitializeComponent. XAML can raise SelectionChanged
        // while the ComboBox items are being constructed; the handler must not
        // touch controls that have not yet been initialized.
        _initializing = true;
        try
        {
            InitializeComponent();
            ModsGrid.ItemsSource = _packages;
            _preferencesStore = new EmlManagerPreferencesStore(PreferencesDirectory);

            // UI smoke tests must never read or modify a user's saved profiles.
            // A normal launch keeps the original persistence behavior.
            if (!App.IsUiSmokeMode)
                LoadPreferences();
            else
                EmmPathText.Text = "UI smoke test (no user settings loaded)";
            _activeProfile = _preferences.LastActiveProfile;
            TargetProfileComboBox.SelectedIndex = _activeProfile == EmlInstallationProfileKind.DedicatedServer ? 1 : 0;
            GameRootTextBox.Text = _preferences.GetRootPath(_activeProfile) ?? string.Empty;
            UpdateActiveProfileUi();
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException("EML Mod Manager window initialization", ex);
            throw;
        }
        finally
        {
            _initializing = false;
        }

        // Scan only after every named XAML control and preference has been initialized.
        if (!App.IsUiSmokeMode)
            RefreshInstallation();
        else
            ValidationStatusText.Text = "Read-only UI smoke test: no installation scanned or modified.";
        UpdateProfileSummaries();
    }

    private static string PreferencesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bjornhjarta", "Settings");

    private void LoadPreferences()
    {
        try
        {
            _preferences = _preferencesStore.Load();
            if (_preferences.LastActiveProfile is not EmlInstallationProfileKind.Client and not EmlInstallationProfileKind.DedicatedServer)
                _preferences = _preferences with { LastActiveProfile = EmlInstallationProfileKind.Client };
            _emmPath = _preferences.EmmPath;
            EmmPathText.Text = string.IsNullOrWhiteSpace(_emmPath)
                ? "emm.exe not selected"
                : File.Exists(_emmPath) ? _emmPath : $"Not found: {_emmPath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _preferences = new EmlManagerPreferences();
            _emmPath = null;
            EmmPathText.Text = "emm.exe not selected";
            ValidationStatusText.Text = $"Could not load EML preferences: {ex.Message}";
        }
    }

    private void SavePreferences()
    {
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ValidationStatusText.Text = $"Could not save local EML preferences: {ex.Message}";
        }
    }

    private void PersistActiveRoot(string rootPath)
    {
        _preferences = _preferences.WithRootPath(_activeProfile, rootPath);
        SavePreferences();
    }

    private void TargetProfileComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || _validationRunning || !IsInitialized || TargetProfileComboBox is null || GameRootTextBox is null)
            return;

        CaptureCurrentConfigEdits();
        _activeProfile = TargetProfileComboBox.SelectedIndex == 1
            ? EmlInstallationProfileKind.DedicatedServer
            : EmlInstallationProfileKind.Client;
        _preferences = _preferences with { LastActiveProfile = _activeProfile };
        SavePreferences();
        GameRootTextBox.Text = _preferences.GetRootPath(_activeProfile) ?? string.Empty;
        UpdateActiveProfileUi();
        RefreshInstallation();
    }

    private void UpdateActiveProfileUi()
    {
        var displayName = GetProfileDisplayName(_activeProfile);
        InstallationDirectoryLabel.Text = $"{displayName.ToUpperInvariant()} INSTALLATION DIRECTORY";
        ActiveProfileDescriptionText.Text = $"Actions apply only to {displayName}. Client and server paths, mods folders, proxy DLLs and eml.json files are kept separate.";
        SaveConfigButton.Content = $"Save {displayName} eml.json (create backup)";
        InstallProxyButton.Content = $"Install / update {displayName} EML proxy…";
        ValidateButton.Content = $"Validate {displayName} mods";
    }

    private static string GetProfileDisplayName(EmlInstallationProfileKind profile) => profile switch
    {
        EmlInstallationProfileKind.Client => "Enshrouded Client",
        EmlInstallationProfileKind.DedicatedServer => "Dedicated Server",
        _ => "Enshrouded"
    };

    private void BrowseGameRoot_Click(object sender, RoutedEventArgs e)
    {
        var isClient = _activeProfile == EmlInstallationProfileKind.Client;
        var executableName = isClient ? "enshrouded.exe" : "enshrouded_server.exe";
        var dialog = new OpenFileDialog
        {
            Title = $"Select the {GetProfileDisplayName(_activeProfile)} executable",
            Filter = $"{GetProfileDisplayName(_activeProfile)} executable|{executableName}|Executable files|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        CaptureCurrentConfigEdits();
        GameRootTextBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        PersistActiveRoot(GameRootTextBox.Text);
        RefreshInstallation();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshInstallation();

    private void ScanBothProfiles_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentConfigEdits();
        RefreshInstallation();
        UpdateProfileSummaries(forceScan: true);
        ValidationStatusText.Text = "Both saved installation profiles were scanned read-only. No game or mod files were changed.";
    }

    private void RefreshInstallation()
    {
        var path = GameRootTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _scan = null;
            _scans.Remove(_activeProfile);
            _packages.Clear();
            LoaderStatusText.Text = "Not detected";
            ModsStatusText.Text = "Not detected";
            ConfigStatusText.Text = "Not detected";
            ScanWarningsText.Text = "Select the correct executable for the active profile, then scan the installation.";
            ValidationStatusText.Text = "No valid installation directory is selected for this profile.";
            ValidateButton.IsEnabled = false;
            UpdateProfileSummaries();
            return;
        }

        try
        {
            var scan = _scanner.Scan(path);
            if (!MatchesActiveProfile(scan))
            {
                _scan = null;
                _scans.Remove(_activeProfile);
                _packages.Clear();
                LoaderStatusText.Text = "Wrong target";
                ModsStatusText.Text = "Not scanned";
                ConfigStatusText.Text = "Not scanned";
                var expected = _activeProfile == EmlInstallationProfileKind.Client ? "enshrouded.exe" : "enshrouded_server.exe";
                var found = scan.HasClientExecutable && scan.HasServerExecutable
                    ? "both client and server executables"
                    : scan.HasClientExecutable ? "enshrouded.exe" : scan.HasServerExecutable ? "enshrouded_server.exe" : "no Enshrouded executable";
                ScanWarningsText.Text = $"This folder contains {found}. The active profile requires {expected}. Switch profiles or select the matching installation.";
                ValidationStatusText.Text = "The selected folder does not match the active installation profile. No files were changed.";
                ValidateButton.IsEnabled = false;
                UpdateProfileSummaries();
                return;
            }

            _scan = scan;
            _scans[_activeProfile] = scan;
            // Scanning must not update preferences. Paths are persisted only
            // when the user explicitly selects a game/server executable.
            _packages.Clear();
            foreach (var package in scan.Mods)
                _packages.Add(package);

            LoaderStatusText.Text = scan.HasEmlProxy
                ? "dbghelp.dll detected"
                : scan.HasAlternativeProxy
                    ? "Alternative proxy found"
                    : "Not installed";
            ModsStatusText.Text = scan.ModsDirectoryExists
                ? $"{scan.Mods.Count(mod => mod.IsEnabled)} active / {scan.Mods.Count(mod => !mod.IsEnabled)} disabled"
                : "Folder missing";
            ConfigStatusText.Text = scan.ConfigExists ? "eml.json found" : "Not created yet";
            ScanWarningsText.Text = scan.Warnings.Count == 0
                ? "No installation scan warnings."
                : string.Join(Environment.NewLine, scan.Warnings.Select(warning => "• " + warning));

            LoadConfigIntoControls();
            ValidationStatusText.Text = $"{GetProfileDisplayName(_activeProfile)} scanned. No game files were changed.";
            ValidateButton.IsEnabled = File.Exists(_emmPath) && scan.LooksLikeEnshroudedInstallation;
            UpdateProfileSummaries();
        }
        catch (Exception ex)
        {
            _scan = null;
            ValidateButton.IsEnabled = false;
            MessageBox.Show(this, ex.Message, "EML scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateProfileSummaries();
        }
    }

    private bool MatchesActiveProfile(EmlInstallationScan scan) => _activeProfile switch
    {
        EmlInstallationProfileKind.Client => scan.HasClientExecutable,
        EmlInstallationProfileKind.DedicatedServer => scan.HasServerExecutable,
        _ => false
    };

    private void UpdateProfileSummaries(bool forceScan = false)
    {
        ClientProfileSummaryText.Text = GetProfileSummary(EmlInstallationProfileKind.Client, forceScan);
        ServerProfileSummaryText.Text = GetProfileSummary(EmlInstallationProfileKind.DedicatedServer, forceScan);
    }

    private string GetProfileSummary(EmlInstallationProfileKind profile, bool forceScan)
    {
        var root = _preferences.GetRootPath(profile);
        if (string.IsNullOrWhiteSpace(root))
            return "Not configured";
        if (!Directory.Exists(root))
            return "Saved path unavailable";

        try
        {
            EmlInstallationScan scan;
            if (!forceScan && _scans.TryGetValue(profile, out var cached) &&
                string.Equals(cached.RootPath, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            {
                scan = cached;
            }
            else
            {
                scan = _scanner.Scan(root);
                if (ProfileMatches(profile, scan))
                    _scans[profile] = scan;
            }

            if (!ProfileMatches(profile, scan))
                return "Executable type mismatch";

            var proxy = scan.HasEmlProxy ? "Proxy detected" : "Proxy missing";
            var config = scan.ConfigExists ? "eml.json found" : "eml.json missing";
            return $"Detected · {proxy} · {scan.Mods.Count(mod => mod.IsEnabled)} active / {scan.Mods.Count(mod => !mod.IsEnabled)} disabled · {config}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Scan unavailable";
        }
    }

    private static bool ProfileMatches(EmlInstallationProfileKind profile, EmlInstallationScan scan) => profile switch
    {
        EmlInstallationProfileKind.Client => scan.HasClientExecutable,
        EmlInstallationProfileKind.DedicatedServer => scan.HasServerExecutable,
        _ => false
    };

    private void CaptureCurrentConfigEdits()
    {
        if (_scan is null)
            return;

        _pendingConfigSettings[_scan.ConfigPath] = new EmlConfigSettings(
            EnableConsoleCheckBox.IsChecked == true,
            UseExportCheckBox.IsChecked == true,
            string.IsNullOrWhiteSpace(ExportDirectoryTextBox.Text) ? "export" : ExportDirectoryTextBox.Text.Trim());
    }

    private void LoadConfigIntoControls()
    {
        if (_scan is null)
            return;

        try
        {
            var settings = _pendingConfigSettings.TryGetValue(_scan.ConfigPath, out var pending)
                ? pending
                : _configStore.Load(_scan.ConfigPath);
            EnableConsoleCheckBox.IsChecked = settings.EnableConsole;
            UseExportCheckBox.IsChecked = settings.UseExportFlag;
            ExportDirectoryTextBox.Text = settings.ExportDirectory;
        }
        catch (Exception ex)
        {
            ValidationStatusText.Text = "Could not read eml.json; it has not been changed.";
            MessageBox.Show(this, ex.Message, "EML configuration warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InstallProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan))
            return;

        if (IsTargetInstallationRunning(_activeProfile, scan.RootPath))
        {
            MessageBox.Show(
                this,
                $"Stop the {GetProfileDisplayName(_activeProfile)} process from this installation before replacing its EML proxy DLL. The other installation may remain running if it is in a different folder.",
                "Target installation is running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select dbghelp.dll from the official EML release archive",
            Filter = "EML proxy DLL (dbghelp.dll)|dbghelp.dll|DLL files|*.dll|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var profileName = GetProfileDisplayName(_activeProfile);
        var confirmation = MessageBox.Show(
            this,
            $"Install the selected dbghelp.dll into this {profileName} directory?\n\n" +
            "Only the active installation will be changed. Any existing dbghelp.dll will be copied into .bjornhjarta\\backups before replacement. " +
            "Only use a DLL obtained from a source you trust.",
            "Install EML proxy",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            var result = _proxyInstaller.Install(scan.RootPath, dialog.FileName);
            MessageBox.Show(
                this,
                $"EML proxy copied to:\n{result.InstalledProxyPath}\n\nMods directory:\n{result.ModsDirectoryPath}" +
                (result.PreviousProxyBackupPath is null ? string.Empty : $"\n\nPrevious DLL backup:\n{result.PreviousProxyBackupPath}"),
                "EML proxy installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            RefreshInstallation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "EML installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSelectedModFolder_Click(object sender, RoutedEventArgs e) => OpenSelectedModFolder();

    private void ModsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelectedModFolder();

    private void OpenSelectedModFolder()
    {
        if (ModsGrid.SelectedItem is not EmlModPackage selected || !Directory.Exists(selected.PackagePath))
            return;

        try
        {
            OpenFolder(selected.PackagePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open mod folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportModZip_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan) || !EnsureStoppedForModChange(scan))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Select one EML mod ZIP (review the contents before installing)",
            Filter = "ZIP archives (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            // Dry run: verifies manifest and archive structure without extracting files.
            var preview = _modPackageManager.Preview(scan.RootPath, dialog.FileName);
            var answer = MessageBox.Show(this,
                $"TARGET PROFILE: {GetProfileDisplayName(_activeProfile)}\n" +
                $"Mod: {preview.Name} ({preview.ModId})\nVersion: {preview.Version}\n" +
                $"Files: {preview.FileCount}\nUncompressed size: {preview.UncompressedBytes:N0} bytes\n\n" +
                $"Target folder: {preview.TargetPath}\n" +
                (preview.IsReplacement
                    ? "Existing mod will be backed up before replacement.\n"
                    : "This mod is not currently installed.\n") +
                "\nOnly install mods from sources you trust. Archive content is not executed now, but EML may run its scripts when the game/server starts.\n\nProceed?",
                "Review EML mod installation",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;

            if (!EnsureStoppedForModChange(scan))
                return;
            var result = _modPackageManager.Install(scan.RootPath, dialog.FileName, preview.ModId, preview.ArchiveSha256);
            RefreshInstallation();
            ValidationStatusText.Text = $"Installed {result.ModId} into the active profile. Restart the game/server for changes to take effect.";
            MessageBox.Show(this,
                $"Mod installed:\n{result.InstalledPath}" +
                (result.BackupPath is null ? "" : $"\n\nPrevious version saved at:\n{result.BackupPath}") +
                "\n\nUse Validate mods to check the EML manifest before launching.",
                "Mod installed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException("Import EML mod ZIP", ex);
            MessageBox.Show(this, ex.Message, "EML mod installation cancelled", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ToggleSelectedMod_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan) || !EnsureStoppedForModChange(scan))
            return;
        if (ModsGrid.SelectedItem is not EmlModPackage selected || string.IsNullOrWhiteSpace(selected.Id))
        {
            MessageBox.Show(this, "Select a mod with a valid manifest ID in the table first.",
                "Select a mod", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var action = selected.IsEnabled ? "Disable" : "Enable";
        var answer = MessageBox.Show(this,
            $"{action} mod '{selected.Name}' ({selected.Id}) for {GetProfileDisplayName(_activeProfile)}?\n\n" +
            (selected.IsEnabled
                ? "The mod folder will be moved outside mods/ to .bjornhjarta/disabled-mods. No mod files will be deleted."
                : "The mod folder will be moved back into mods/. No mod files will be deleted.") +
            "\n\nA game/server restart is required for the change to apply.",
            $"{action} EML mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes || !EnsureStoppedForModChange(scan))
            return;

        try
        {
            var destination = selected.IsEnabled
                ? _modPackageManager.Disable(scan.RootPath, selected.Id)
                : _modPackageManager.Enable(scan.RootPath, selected.Id);
            RefreshInstallation();
            ValidationStatusText.Text = $"{selected.Id} is now {(selected.IsEnabled ? "disabled" : "enabled")} in {GetProfileDisplayName(_activeProfile)}. Restart required.";
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException($"{action} EML mod", ex);
            MessageBox.Show(this, ex.Message, $"Could not {action.ToLowerInvariant()} mod", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool EnsureStoppedForModChange(EmlInstallationScan scan)
    {
        if (_validationRunning || IsTargetInstallationRunning(_activeProfile, scan.RootPath))
        {
            MessageBox.Show(this,
                "Stop the target Enshrouded game/server and wait for EMM validation to finish before changing mods. Other installations in separate directories may remain running.",
                "Active installation must be stopped", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void CreateModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan))
            return;

        try
        {
            Directory.CreateDirectory(scan.ModsPath);
            RefreshInstallation();
            ValidationStatusText.Text = $"Created or confirmed the {GetProfileDisplayName(_activeProfile)} mods directory: {scan.ModsPath}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not create mods folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan))
            return;

        try
        {
            Directory.CreateDirectory(scan.ModsPath);
            OpenFolder(scan.ModsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open mods folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan))
            return;

        try
        {
            var exportDirectory = string.IsNullOrWhiteSpace(ExportDirectoryTextBox.Text)
                ? "export"
                : ExportDirectoryTextBox.Text.Trim();
            if (Path.IsPathRooted(exportDirectory))
            {
                MessageBox.Show(this, "The EML export directory must be relative to the active Enshrouded installation directory.", "Invalid export directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var combinedExportPath = Path.GetFullPath(Path.Combine(scan.RootPath, exportDirectory));
            var relativeExportPath = Path.GetRelativePath(scan.RootPath, combinedExportPath);
            if (relativeExportPath == ".." || relativeExportPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "The export directory cannot point outside the active Enshrouded installation directory.", "Invalid export directory", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = new EmlConfigSettings(
                EnableConsoleCheckBox.IsChecked == true,
                UseExportCheckBox.IsChecked == true,
                exportDirectory);
            if (settings.UseExportFlag)
                Directory.CreateDirectory(combinedExportPath);
            var backup = _configStore.Save(scan.ConfigPath, settings, scan.RootPath);
            _pendingConfigSettings.Remove(scan.ConfigPath);
            ConfigStatusText.Text = "eml.json saved";
            ValidationStatusText.Text = (backup is null
                ? $"Created {GetProfileDisplayName(_activeProfile)} eml.json. No previous file existed to back up."
                : $"Saved {GetProfileDisplayName(_activeProfile)} eml.json. Previous configuration backed up to: {backup}") + " EML reads this configuration on a subsequent launch.";
            UpdateProfileSummaries();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save eml.json", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SelectEmm_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select emm.exe from the EML release archive (shared by Client and Server)",
            Filter = "Enshrouded Mod Manager CLI (emm.exe)|emm.exe|Executable files|*.exe|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!string.Equals(Path.GetFileName(dialog.FileName), "emm.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Please select the EML command-line executable named emm.exe.", "Invalid EML tool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _emmPath = Path.GetFullPath(dialog.FileName);
        _preferences = _preferences with { EmmPath = _emmPath };
        EmmPathText.Text = _emmPath;
        SavePreferences();
        ValidateButton.IsEnabled = _scan?.LooksLikeEnshroudedInstallation == true && File.Exists(_emmPath);
    }

    private void CreateModTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan))
            return;
        if (string.IsNullOrWhiteSpace(_emmPath) || !File.Exists(_emmPath))
        {
            MessageBox.Show(this, "Select the EML emm.exe command-line tool first. It is shared by the Client and Dedicated Server profiles.", "EMM CLI required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Open EMM's interactive mod-template creator for the {GetProfileDisplayName(_activeProfile)}? It will create a new mod folder under this profile's mods directory.",
            "Create EML mod template",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _emmPath,
                WorkingDirectory = Path.GetDirectoryName(_emmPath) ?? scan.RootPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            startInfo.ArgumentList.Add("create");
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(scan.RootPath);
            Process.Start(startInfo);
            ValidationStatusText.Text = $"EMM template creator opened for {GetProfileDisplayName(_activeProfile)}. Complete its prompts, then scan this profile again.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not start EMM template creator", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ValidateMods_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetInstallation(out var scan) || string.IsNullOrWhiteSpace(_emmPath) || !File.Exists(_emmPath))
        {
            if (string.IsNullOrWhiteSpace(_emmPath) || !File.Exists(_emmPath))
                MessageBox.Show(this, "Select a valid emm.exe before running validation.", "EMM CLI required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _validationRunning = true;
        ValidateButton.IsEnabled = false;
        TargetProfileComboBox.IsEnabled = false;
        ValidationStatusText.Text = $"Validating {GetProfileDisplayName(_activeProfile)} mods…";
        ValidationOutputTextBox.Text = $"> emm.exe run -g \"{scan.RootPath}\"\r\n(Validation-only: no --patch or --export flags)\r\n\r\n";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _emmPath,
                WorkingDirectory = Path.GetDirectoryName(_emmPath) ?? scan.RootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add(scan.RootPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new InvalidOperationException("Windows could not start emm.exe.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await stdoutTask;
            var error = await stderrTask;

            ValidationOutputTextBox.Text += string.IsNullOrWhiteSpace(output) ? "(No standard output)\r\n" : output;
            if (!string.IsNullOrWhiteSpace(error))
                ValidationOutputTextBox.Text += "\r\n[stderr]\r\n" + error;

            ValidationStatusText.Text = process.ExitCode == 0
                ? $"{GetProfileDisplayName(_activeProfile)} validation completed (exit code 0). Review the output above."
                : $"{GetProfileDisplayName(_activeProfile)} validation exited with code {process.ExitCode}. Review the output above.";
        }
        catch (Exception ex)
        {
            ValidationOutputTextBox.Text += "\r\nERROR: " + ex.Message;
            ValidationStatusText.Text = "Could not run EML validation.";
        }
        finally
        {
            _validationRunning = false;
            TargetProfileComboBox.IsEnabled = true;
            ValidateButton.IsEnabled = _scan?.LooksLikeEnshroudedInstallation == true && File.Exists(_emmPath);
        }
    }

    private bool TryGetInstallation(out EmlInstallationScan scan)
    {
        var selectedRoot = GameRootTextBox.Text.Trim();
        if (_scan is not null && MatchesActiveProfile(_scan) &&
            Directory.Exists(selectedRoot) &&
            string.Equals(Path.GetFullPath(selectedRoot), _scan.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            scan = _scan;
            return true;
        }

        MessageBox.Show(
            this,
            $"Select a directory containing {(_activeProfile == EmlInstallationProfileKind.Client ? "enshrouded.exe" : "enshrouded_server.exe")} for the active profile and scan it first.",
            "Enshrouded installation required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        scan = null!;
        return false;
    }

    private static bool IsTargetInstallationRunning(EmlInstallationProfileKind profile, string installationRoot)
    {
        var processName = profile == EmlInstallationProfileKind.Client ? "enshrouded" : "enshrouded_server";
        var expectedExecutable = profile == EmlInstallationProfileKind.Client ? "enshrouded.exe" : "enshrouded_server.exe";
        var expectedPath = Path.GetFullPath(Path.Combine(installationRoot, expectedExecutable));

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    var runningPath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(runningPath))
                        return true;

                    if (string.Equals(Path.GetFullPath(runningPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // If Windows will not reveal the executable path, fail closed and
                    // avoid replacing a proxy while a same-type process may be using it.
                    return true;
                }
            }
        }

        return false;
    }

    private static void OpenFolder(string path)
    {
        var startInfo = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        Process.Start(startInfo);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
