using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Bjornhjarta.Core.Blueprints;

namespace Bjornhjarta.Desktop;

public partial class ModelAssetLibraryWindow : Window
{
    private readonly ModelAssetCatalog _catalog = new();
    private readonly ObservableCollection<ModelAssetRecord> _assets = [];
    private readonly bool _allowAssign;
    private readonly string? _suggestedTypeId;
    private bool _loadingSelection;

    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bjornhjarta");
    private static string PreferencesPath => Path.Combine(AppDataDirectory, "model-asset-library.json");
    private static string DefaultLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bjornhjarta", "ModelAssets");

    internal static string ActiveLibraryRoot => LoadSavedLibraryPath() ?? DefaultLibraryPath;

    public ModelAssetRecord? SelectedAsset { get; private set; }

    public ModelAssetLibraryWindow(bool allowAssign = false, string? initialLibraryPath = null, string? suggestedTypeId = null)
    {
        InitializeComponent();
        _allowAssign = allowAssign;
        _suggestedTypeId = string.IsNullOrWhiteSpace(suggestedTypeId) ? null : suggestedTypeId.Trim();
        AssetsGrid.ItemsSource = _assets;
        LibraryPathTextBox.Text = NormalizeInitialPath(initialLibraryPath) ?? LoadSavedLibraryPath() ?? DefaultLibraryPath;
        AssignButton.IsEnabled = false;
        if (Directory.Exists(LibraryPathTextBox.Text))
            ScanLibrary();
        else
            StatusText.Text = "Create or select a model library folder, then import GLB exports.";
    }

    private static string? NormalizeInitialPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? LoadSavedLibraryPath()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(PreferencesPath));
            if (document.RootElement.TryGetProperty("libraryRoot", out var value) && value.ValueKind == JsonValueKind.String)
                return NormalizeInitialPath(value.GetString());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged preference is non-critical; the user can choose the folder again.
        }
        return null;
    }

    private static void SaveLibraryPreference(string root)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var temporary = PreferencesPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { libraryRoot = root }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PreferencesPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Bjornhjarta model asset library",
            InitialDirectory = Directory.Exists(LibraryPathTextBox.Text) ? LibraryPathTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
        {
            LibraryPathTextBox.Text = dialog.FolderName;
            ScanLibrary();
        }
    }

    private void ImportGlb_Click(object sender, RoutedEventArgs e)
    {
        var root = GetLibraryRoot(create: true);
        if (root is null)
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Import one or more textured GLB model exports",
            Filter = "glTF Binary model (*.glb)|*.glb",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var imported = 0;
        string? lastImported = null;
        try
        {
            foreach (var source in dialog.FileNames)
            {
                var sourceInfo = new FileInfo(source);
                if (!sourceInfo.Exists || sourceInfo.Length < 20)
                    throw new InvalidDataException($"The selected GLB file is missing or too small to be valid: {source}");
                if (sourceInfo.Length > ModelAssetCatalog.MaximumModelFileBytes)
                    throw new InvalidDataException($"The selected GLB exceeds the supported {ModelAssetCatalog.MaximumModelFileBytes / (1024L * 1024L)} MB file size limit: {source}");

                var destination = Path.Combine(root, Path.GetFileName(source));
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    continue;

                if (File.Exists(destination))
                {
                    var stem = Path.GetFileNameWithoutExtension(destination);
                    var extension = Path.GetExtension(destination);
                    var suffix = Guid.NewGuid().ToString("N")[..6];
                    destination = Path.Combine(root, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}_{suffix}{extension}");
                }
                File.Copy(source, destination, overwrite: false);
                CopySourceManifest(source, destination);
                lastImported = destination;
                imported++;
            }

            ScanLibrary(lastImported);
            StatusText.Text = imported == 0
                ? "GLB is already in this library. Select it and use Quick assign."
                : $"Imported {imported} GLB file(s). Select the model, then choose Quick assign for 3D preview.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "Model import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => ScanLibrary();

    private static void CopySourceManifest(string sourceModelPath, string destinationModelPath)
    {
        var sourceManifestPath = ModelAssetCatalog.GetManifestPath(sourceModelPath);
        if (!File.Exists(sourceManifestPath))
            return;

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
            var manifest = JsonSerializer.Deserialize<ModelAssetManifest>(File.ReadAllText(sourceManifestPath), options);
            if (manifest is null || manifest.SchemaVersion != 1)
                return;

            manifest.AssetId = Guid.NewGuid().ToString("N");
            manifest.ModelFile = Path.GetFileName(destinationModelPath);
            var destinationManifestPath = ModelAssetCatalog.GetManifestPath(destinationModelPath);
            File.WriteAllText(destinationManifestPath, JsonSerializer.Serialize(manifest, options));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // The model itself remains imported; the user can create metadata in the editor.
        }
    }

    private string? GetLibraryRoot(bool create)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LibraryPathTextBox.Text))
                throw new InvalidOperationException("Choose a model library folder first.");
            var root = Path.GetFullPath(LibraryPathTextBox.Text.Trim());
            if (File.Exists(Path.Combine(root, "enshrouded.exe")) || File.Exists(Path.Combine(root, "enshrouded_server.exe")))
                throw new InvalidOperationException("Choose a separate user asset library folder, not the Enshrouded Client or Dedicated Server installation.");
            if (create)
                Directory.CreateDirectory(root);
            else if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("The selected model library folder does not exist.");
            SaveLibraryPreference(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "Model library folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private void ScanLibrary(string? selectModelPath = null)
    {
        var root = GetLibraryRoot(create: true);
        if (root is null)
            return;

        try
        {
            var results = _catalog.Scan(root);
            _assets.Clear();
            foreach (var entry in results)
                _assets.Add(entry);

            LibrarySummaryText.Text = $"{_assets.Count} model file(s)  •  {_assets.Count(item => item.Status == "Ready")} ready  •  {_assets.Count(item => item.Status != "Ready")} need attention";
            StatusText.Text = results.Count == 0
                ? "No GLB/glTF models found. Export a selected Enshrouded model from Blender, then import its GLB file here."
                : "Scan completed. No game files were changed.";

            var match = !string.IsNullOrWhiteSpace(selectModelPath)
                ? _assets.FirstOrDefault(item => string.Equals(item.ModelFilePath, selectModelPath, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match is not null)
                AssetsGrid.SelectedItem = match;
            else if (_assets.Count > 0)
                AssetsGrid.SelectedIndex = 0;
            else
                ClearDetails();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            MessageBox.Show(this, ex.Message, "Model library scan failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AssetsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSelection)
            return;

        if (AssetsGrid.SelectedItem is not ModelAssetRecord record)
        {
            ClearDetails();
            return;
        }

        _loadingSelection = true;
        try
        {
            SelectedAssetText.Text = record.DisplayName;
            DisplayNameTextBox.Text = record.DisplayName;
            TypeIdTextBox.Text = string.IsNullOrWhiteSpace(record.TypeId) ? (_suggestedTypeId ?? "") : record.TypeId;
            SourceGuidTextBox.Text = record.SourceModelGuid ?? "";
            NotesTextBox.Text = record.Notes ?? "";
            MeshNamesText.Text = $"{record.MeshCount} mesh(es) • {record.MaterialCount} material(s) • {record.TextureCount} texture(s)\n" +
                                 (string.IsNullOrWhiteSpace(record.MeshNames) ? "No named meshes" : record.MeshNames);
            ImageStorageText.Text = $"Images: {record.ImageCount} total · {record.EmbeddedImageCount} embedded · {record.ExternalImageCount} external";
            MaterialChannelsText.Text = record.MaterialChannels;
            PreviewCompatibilityText.Text = record.PreviewStatus + " — " + record.PreviewNotes;
            GeometryDimensionsText.Text = "Not measured yet. Click Inspect GLB dimensions.";
            InspectGeometryButton.IsEnabled = record.IsModelValid && record.Format.Equals("GLB", StringComparison.OrdinalIgnoreCase);
            TexturePackingText.Text = BuildTexturePackingSummary(record);
            WarningText.Text = record.Warning ?? (record.HasManifest
                ? "Asset manifest found. Model assignment does not change the selected blueprint Type ID."
                : _suggestedTypeId is null
                    ? "No mapping manifest yet. Select blueprint pieces with the same Type ID or save a mapping manually."
                    : $"Quick assign can create a local mapping using the selected piece's Type ID: {_suggestedTypeId}.");
            var canQuickMap = _suggestedTypeId is not null && record.IsModelValid &&
                              (!record.HasManifest || record.Status == "Needs Type ID");
            AssignButton.IsEnabled = _allowAssign && (record.IsValid || canQuickMap);
        }
        finally
        {
            _loadingSelection = false;
        }
    }


    private static string BuildTexturePackingSummary(ModelAssetRecord record)
    {
        if (record.TexturePackingConverted == true)
            return $"Converted: {record.SourceTexturePacking ?? "source packing"} → {record.GltfTexturePacking ?? "glTF ORM"}";
        if (!string.IsNullOrWhiteSpace(record.SourceTexturePacking))
            return $"Source: {record.SourceTexturePacking}. Conversion: {(record.TexturePackingConverted == false ? "not performed" : "not confirmed")}.";
        return "Unknown. Review the source material nodes before relying on PBR appearance.";
    }

    private void ClearDetails()
    {
        _loadingSelection = true;
        try
        {
            SelectedAssetText.Text = "Select a model from the library.";
            DisplayNameTextBox.Text = TypeIdTextBox.Text = SourceGuidTextBox.Text = NotesTextBox.Text = "";
            MeshNamesText.Text = "—";
            ImageStorageText.Text = "Images: —";
            MaterialChannelsText.Text = "—";
            TexturePackingText.Text = "Not recorded";
            PreviewCompatibilityText.Text = "Not inspected";
            GeometryDimensionsText.Text = "Select a GLB model.";
            InspectGeometryButton.IsEnabled = false;
            WarningText.Text = "";
            AssignButton.IsEnabled = false;
        }
        finally
        {
            _loadingSelection = false;
        }
    }

    private void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not ModelAssetRecord selected)
        {
            MessageBox.Show(this, "Select a valid GLB/glTF model first.", "No model selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!selected.IsModelValid)
        {
            MessageBox.Show(this, selected.Warning ?? "The model file is not a valid glTF 2.0 asset.", "Model cannot be mapped", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var root = GetLibraryRoot(create: false);
            if (root is null)
                return;
            _catalog.SaveMetadata(root, selected,
                string.IsNullOrWhiteSpace(DisplayNameTextBox.Text) ? Path.GetFileNameWithoutExtension(selected.ModelFilePath) : DisplayNameTextBox.Text,
                TypeIdTextBox.Text,
                SourceGuidTextBox.Text,
                NotesTextBox.Text);
            ScanLibrary(selected.ModelFilePath);
            StatusText.Text = string.IsNullOrWhiteSpace(TypeIdTextBox.Text)
                ? "Model details saved without a Type ID. Assign a verified or user-curated Type ID to enable automatic reuse."
                : "Model mapping saved. Matching blueprint pieces can now reuse this asset automatically.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            MessageBox.Show(this, ex.Message, "Could not save asset mapping", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowAssign || AssetsGrid.SelectedItem is not ModelAssetRecord record || !record.IsModelValid)
            return;

        try
        {
            if (!record.IsValid)
            {
                // The test GLB needs no manually typed Type ID. A missing sidecar can
                // be created from the (single, consistent) Type ID already selected
                // in Blueprint Forge. Ambiguous selections are not auto-mapped.
                if (_suggestedTypeId is null || (record.HasManifest && record.Status != "Needs Type ID"))
                    throw new InvalidOperationException("Select pieces with one shared Type ID, or save a valid manual mapping first.");
                var libraryRoot = GetLibraryRoot(create: false);
                if (libraryRoot is null) return;
                record = _catalog.SaveMetadata(libraryRoot, record,
                    string.IsNullOrWhiteSpace(DisplayNameTextBox.Text) ? record.DisplayName : DisplayNameTextBox.Text,
                    _suggestedTypeId, SourceGuidTextBox.Text, NotesTextBox.Text);
                if (!record.IsValid)
                    throw new InvalidDataException(record.Warning ?? "The saved model mapping is not valid.");
            }

            SelectedAsset = record;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        {
            MessageBox.Show(this, ex.Message, "Could not assign model", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AssetsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AssignButton.IsEnabled)
            Assign_Click(sender, new RoutedEventArgs());
    }

    private void InspectGeometry_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not ModelAssetRecord asset ||
            !asset.Format.Equals("GLB", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            // The same importer is used by the 3D editor. Bounds include glTF
            // scene-node transforms; do not confuse them with game block sizes.
            var loaded = GlbSceneLoader.Load(asset.ModelFilePath);
            var bounds = loaded.Model.Bounds;
            if (bounds.IsEmpty ||
                !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Z) ||
                !double.IsFinite(bounds.SizeX) || !double.IsFinite(bounds.SizeY) || !double.IsFinite(bounds.SizeZ))
                throw new InvalidDataException("No finite geometry bounds were produced by the GLB renderer.");

            GeometryDimensionsText.Text =
                $"Width X: {bounds.SizeX:0.###}  |  Height Y: {bounds.SizeY:0.###}  |  Depth Z: {bounds.SizeZ:0.###} GLB units" +
                $"\nOrigin-relative minimum: X {bounds.X:0.###}, Y {bounds.Y:0.###}, Z {bounds.Z:0.###}" +
                $"\n{loaded.TriangleCount:N0} preview triangles • {loaded.TexturedPrimitives} textured material use(s).";
            if (bounds.SizeX > 1000 || bounds.SizeY > 1000 || bounds.SizeZ > 1000 ||
                (bounds.SizeX < 0.001 && bounds.SizeY < 0.001 && bounds.SizeZ < 0.001))
                GeometryDimensionsText.Text += "\nReview the export's scale before placing the model.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            AppDiagnostics.WriteException("Inspecting GLB geometry", ex);
            GeometryDimensionsText.Text = "Cannot inspect this model with the WPF GLB importer: " + ex.Message;
        }
    }

    private void ShowFile_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsGrid.SelectedItem is not ModelAssetRecord record)
            return;
        if (!File.Exists(record.ModelFilePath))
        {
            MessageBox.Show(this, "The model file no longer exists at the indexed path.", "Model file missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{record.ModelFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "Could not open file location", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
