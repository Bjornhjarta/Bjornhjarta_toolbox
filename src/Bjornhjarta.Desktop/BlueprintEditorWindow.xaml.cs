using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Bjornhjarta.Core.Blueprints;

namespace Bjornhjarta.Desktop;

public partial class BlueprintEditorWindow : Window
{
    private enum GridViewMode
    {
        TwoDTopDown,
        ThreeDTextured
    }
    private readonly BlueprintJsonStore _store = new();
    private readonly ObservableCollection<BlueprintElement> _elements = [];
    private readonly ObservableCollection<PlaceableCatalogEntry> _visiblePlaceables = [];
    private readonly PlaceablesCatalogStore _placeableCatalog = new();
    private IReadOnlyList<PlaceableCatalogEntry> _placeableEntries = [];
    private readonly HashSet<BlueprintElement> _selectedElements = new();
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();

    private string? _currentPath;
    private double _pixelsPerCell = 48;
    private double _viewCenterXMetres;
    private double _viewCenterYMetres;
    private double _viewCenterZMetres;
    private GridViewMode _gridViewMode = GridViewMode.TwoDTopDown;
    private bool _syncingSelection;

    private bool _isDraggingElements;
    private bool _isAreaSelecting;
    private bool _isPanning;
    private MouseButton? _panButton;
    private Blueprint3DEditorControl? _threeDEditor;
    private bool _pending3DRefresh;
    private bool _fitAfter3DRefresh;
    private bool _handling3DCallback;
    private bool _dragHasMoved;
    private bool _dragElevationMode;
    private Point _dragStartPoint;
    private Point _areaStartPoint;
    private Point _panStartPoint;
    private double _panStartCenterX;
    private double _panStartCenterZ;
    private string? _dragSnapshot;
    private Dictionary<BlueprintElement, (double X, double Y, double Z)> _dragStartPositions = new();
    private HashSet<BlueprintElement> _areaBaseSelection = new();
    private bool _areaSelectionIsAdditive;
    private Rectangle? _selectionRectangle;

    private List<ClipboardElement>? _groupClipboard;
    private double _clipboardMinX;
    private double _clipboardMinY;
    private double _clipboardMinZ;
    private double _clipboardWidthMetres;
    private int _pasteCount;

    private const double MetresPerCell = 4.0;
    private const double MinimumZoom = 5;
    private const double MaximumZoom = 120;
    private const double IsometricXScale = 0.8660254037844386; // cos(30°)
    private const double IsometricYScale = 0.5;                // sin(30°)
    private const double IsometricElevationScale = 1.0;

    public BlueprintEditorWindow()
    {
        InitializeComponent();
        ElementsGrid.ItemsSource = _elements;
        PlaceablesGrid.ItemsSource = _visiblePlaceables;
        LoadPlaceablesLibrary();
        GridModeComboBox.SelectionChanged += GridModeComboBox_SelectionChanged;
        GridModeComboBox.SelectedIndex = 0;
        StartNewDocument();
        Loaded += (_, _) => RenderGrid();
    }

    private BlueprintDocument CreateDocument() => new()
    {
        Metadata = new BlueprintMetadata
        {
            Name = string.IsNullOrWhiteSpace(BlueprintNameBox.Text) ? "Untitled Blueprint" : BlueprintNameBox.Text.Trim(),
            ToolkitVersion = "1.0.0-rc.2"
        },
        Elements = _elements.ToList()
    };

    private void StartNewDocument()
    {
        _undo.Clear();
        _redo.Clear();
        _elements.Clear();
        _selectedElements.Clear();
        BlueprintNameBox.Text = "Untitled Blueprint";
        _currentPath = null;
        _viewCenterXMetres = 0;
        _viewCenterYMetres = 0;
        _viewCenterZMetres = 0;
        _pixelsPerCell = 48;
        _groupClipboard = null;
        _pasteCount = 0;
        UpdateSelectionDisplay();
        RenderGrid();
        UpdateStatus();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (_elements.Count > 0 && MessageBox.Show("Create a new blueprint? Unsaved changes will be lost.", "New blueprint", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        StartNewDocument();
    }

    private void LoadPlaceablesLibrary()
    {
        try
        {
            _placeableEntries = _placeableCatalog.Load()
                .Concat(PlaceablesCatalogStore.ExampleEntries())
                .GroupBy(p => p.TypeId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            _placeableEntries = PlaceablesCatalogStore.ExampleEntries();
            UpdateStatus($"Custom placeables catalog could not be loaded: {ex.Message}");
        }

        var previouslySelected = (PlaceableCategoryCombo.SelectedItem as string) ?? "All categories";
        PlaceableCategoryCombo.Items.Clear();
        PlaceableCategoryCombo.Items.Add("All categories");
        foreach (var category in _placeableEntries.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            PlaceableCategoryCombo.Items.Add(category);
        PlaceableCategoryCombo.SelectedItem = PlaceableCategoryCombo.Items.Contains(previouslySelected)
            ? previouslySelected : "All categories";
        FilterPlaceables();
    }

    private void PlaceablesFilter_Changed(object sender, EventArgs e) => FilterPlaceables();

    private void FilterPlaceables()
    {
        if (PlaceablesGrid is null || PlaceableCategoryCombo is null || PlaceableSearchBox is null)
            return;
        var search = PlaceableSearchBox.Text.Trim();
        var category = (PlaceableCategoryCombo.SelectedItem as string) ?? "All categories";
        _visiblePlaceables.Clear();
        foreach (var item in _placeableEntries
                     .Where(x => category == "All categories" || category.Equals(x.Category, StringComparison.OrdinalIgnoreCase))
                     .Where(x => string.IsNullOrEmpty(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                          x.TypeId.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(x => x.Category).ThenBy(x => x.Name))
            _visiblePlaceables.Add(item);
    }

    private void PlaceablesAdd_Click(object sender, RoutedEventArgs e)
    {
        var selected = PlaceablesGrid.SelectedItems.OfType<PlaceableCatalogEntry>().ToList();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more entries in the PLACEABLES tab first.");
            return;
        }
        var anchor = GetSelectedElements().FirstOrDefault()?.Position;
        PushUndo();
        var added = new List<BlueprintElement>();
        for (var i = 0; i < selected.Count; i++)
        {
            var item = new BlueprintElement
            {
                TypeId = selected[i].TypeId,
                Position = new BlueprintVector3
                {
                    X = (anchor?.X ?? 0) + 4 * i,
                    Y = anchor?.Y ?? 0,
                    Z = anchor?.Z ?? 0
                }
            };
            added.Add(item);
            _elements.Add(item);
        }
        AutoMapElements(added);
        SidebarTabs.SelectedIndex = 1;
        SetSelection(added);
        UpdateStatus($"Added {added.Count} selected placeable(s). Confirm Type IDs and adjust placement before saving.");
    }

    private void PlaceablesImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Placeable catalog JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = _placeableCatalog.ReadFile(dialog.FileName);
            var existing = _placeableCatalog.Load();
            var combined = existing.Concat(imported)
                .GroupBy(x => x.TypeId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last()).ToList();
            _placeableCatalog.Save(combined);
            LoadPlaceablesLibrary();
            UpdateStatus($"Imported {imported.Count} local catalog entries. Game IDs are user-supplied, not automatically verified.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            MessageBox.Show(this, ex.Message, "Catalog import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        var item = new BlueprintElement
        {
            TypeId = "example.foundation",
            Position = new BlueprintVector3(),
            Rotation = new BlueprintRotation()
        };
        _elements.Add(item);
        SetSelection([item]);
        UpdateStatus("Element added.");
    }

    private void ImageToBlueprint_Click(object sender, RoutedEventArgs e)
    {
        var anchor = GetSelectedElements().FirstOrDefault()?.Position;
        var generator = new ImageToBlueprintWindow(anchor?.X ?? 0, anchor?.Y ?? 0, anchor?.Z ?? 0)
        {
            Owner = this
        };
        if (generator.ShowDialog() != true || generator.GeneratedPieces.Count == 0)
            return;

        // Keep the imported structure editable and let the entire insertion be
        // reversed with one Ctrl+Z. No game/world data is modified.
        PushUndo();
        // Auto-link local models only when exactly one valid asset uses that Type ID.
        // Image-to-Blueprint and manual Placeables use the same mapping policy.
        AutoMapElements(generator.GeneratedPieces);
        var startIndex = _elements.Count;
        foreach (var element in generator.GeneratedPieces)
            _elements.Add(element);
        SidebarTabs.SelectedIndex = 1;
        SetSelection(_elements.Skip(startIndex).Take(1));
        ElementsGrid.Items.Refresh();
        // The drawing defines the grid extent, not the editor's old viewport.
        // Auto fit the newly generated blueprint on either editor surface.
        if (_gridViewMode == GridViewMode.ThreeDTextured)
        {
            // Fit after the pending scene refresh has rebuilt geometry.
            _fitAfter3DRefresh = true;
        }
        else
        {
            CenterGridToContent();
        }
        RenderGrid();
        UpdateStatus($"Inserted {generator.GeneratedPieces.Count} approximate image-derived piece(s). Review the structure before saving. Ctrl+Z to undo.");
    }

    private void AddSamples_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        var samples = new[]
        {
            new BlueprintElement { TypeId = "example.foundation", Position = new BlueprintVector3 { X = 0, Y = 0, Z = 0 } },
            new BlueprintElement { TypeId = "example.wall", Position = new BlueprintVector3 { X = 0, Y = 0, Z = 4 } },
            new BlueprintElement { TypeId = "example.wall", Position = new BlueprintVector3 { X = 4, Y = 0, Z = 4 }, Rotation = new BlueprintRotation { Yaw = 90 } },
            new BlueprintElement { TypeId = "example.floor", Position = new BlueprintVector3 { X = 0, Y = 4, Z = 0 } },
            new BlueprintElement { TypeId = "example.roof", Position = new BlueprintVector3 { X = 0, Y = 8, Z = 0 } }
        };

        foreach (var sample in samples)
            _elements.Add(sample);

        SetSelection([samples[^1]]);
        UpdateStatus("Sample set added. Select multiple pieces or drag a rectangle to select a group.");
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelectedElements();

    private void RemoveSelectedElements()
    {
        var selected = GetSelectedElements();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more elements to remove.");
            return;
        }

        PushUndo();
        foreach (var item in selected)
            _elements.Remove(item);

        SetSelection([]);
        UpdateStatus($"Removed {selected.Count} selected element(s).");
    }

    private void ElementsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ElementsGrid.Items.Refresh();
            RenderGrid();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ElementsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;

        _selectedElements.Clear();
        foreach (var item in ElementsGrid.SelectedItems.OfType<BlueprintElement>())
            _selectedElements.Add(item);

        UpdateSelectionDisplay();
        RenderGrid();
    }

    private void SetSelection(IEnumerable<BlueprintElement> items)
    {
        var validItems = items.Where(_elements.Contains).Distinct().ToArray();
        _syncingSelection = true;
        try
        {
            _selectedElements.Clear();
            ElementsGrid.SelectedItems.Clear();

            foreach (var item in validItems)
            {
                _selectedElements.Add(item);
                ElementsGrid.SelectedItems.Add(item);
            }

            if (validItems.Length > 0)
                ElementsGrid.ScrollIntoView(validItems[0]);
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateSelectionDisplay();
        RenderGrid();
    }

    private List<BlueprintElement> GetSelectedElements() =>
        _elements.Where(_selectedElements.Contains).ToList();

    private void UpdateSelectionDisplay()
    {
        var selected = GetSelectedElements();
        if (selected.Count == 1)
        {
            ShowInspector(selected[0]);
        }
        else if (selected.Count > 1)
        {
            SelectedElementText.Text = $"{selected.Count} elements selected";
            ClearInspector();
        }
        else
        {
            SelectedElementText.Text = "Select an element from the list or grid.";
            ClearInspector();
        }
    }

    private void ShowInspector(BlueprintElement item)
    {
        SelectedElementText.Text = item.TypeId;
        PosXBox.Text = Format(item.Position.X);
        PosYBox.Text = Format(item.Position.Y);
        PosZBox.Text = Format(item.Position.Z);
        PitchBox.Text = Format(item.Rotation.Pitch);
        YawBox.Text = Format(item.Rotation.Yaw);
        RollBox.Text = Format(item.Rotation.Roll);
        VariantBox.Text = item.Variant ?? "";
        ModelAssetText.Text = string.IsNullOrWhiteSpace(item.ModelAssetName) ? "No model asset assigned." : $"{item.ModelAssetName}  •  {item.ModelAssetId}";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyInspectorIfPossible(true))
            return;

        ElementsGrid.Items.Refresh();
        RenderGrid();
        UpdateStatus("Inspector changes applied.");
    }

    private bool ApplyInspectorIfPossible(bool showErrors = false)
    {
        var selected = GetSelectedElements();
        if (selected.Count != 1)
            return true;

        var item = selected[0];
        if (!TryNumber(PosXBox.Text, out var x) || !TryNumber(PosYBox.Text, out var y) || !TryNumber(PosZBox.Text, out var z) ||
            !TryNumber(PitchBox.Text, out var pitch) || !TryNumber(YawBox.Text, out var yaw) || !TryNumber(RollBox.Text, out var roll))
        {
            if (showErrors)
                MessageBox.Show("Enter valid numbers in all position and rotation fields.", "Invalid values", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var variant = string.IsNullOrWhiteSpace(VariantBox.Text) ? null : VariantBox.Text.Trim();
        var changed = item.Position.X != x || item.Position.Y != y || item.Position.Z != z ||
                      item.Rotation.Pitch != pitch || item.Rotation.Yaw != yaw || item.Rotation.Roll != roll || item.Variant != variant;

        if (changed)
            PushUndo();

        item.Position.X = x;
        item.Position.Y = y;
        item.Position.Z = z;
        item.Rotation.Pitch = pitch;
        item.Rotation.Yaw = yaw;
        item.Rotation.Roll = roll;
        item.Variant = variant;
        return true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyInspectorIfPossible(true))
            return;

        ElementsGrid.Items.Refresh();
        RenderGrid();
        var path = _currentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Bjornhjarta Blueprint",
                Filter = "Bjornhjarta Blueprint (*.json)|*.json",
                FileName = $"{SafeFileName(BlueprintNameBox.Text)}.json"
            };
            if (dialog.ShowDialog() != true)
                return;
            path = dialog.FileName;
        }

        try
        {
            var document = CreateDocument();
            var errors = Validate(document);
            if (errors.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, errors), "Blueprint validation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await _store.SaveAsync(document, path);
            _currentPath = path;
            UpdateStatus($"Saved: {System.IO.Path.GetFileName(path)}");
            MessageBox.Show($"Blueprint saved successfully.\nElements: {document.Elements.Count}", "Blueprint saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Bjornhjarta Blueprint",
            Filter = "Bjornhjarta Blueprint (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await LoadBlueprintFromFileAsync(dialog.FileName, recoveryMode: false);
    }

    private async void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Recover a saved Bjornhjarta blueprint backup",
            Filter = "Blueprint backup (*.bak)|*.bak|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        await LoadBlueprintFromFileAsync(dialog.FileName, recoveryMode: true);
    }

    private async Task LoadBlueprintFromFileAsync(string fileName, bool recoveryMode)
    {
        try
        {
            var document = await _store.LoadAsync(fileName);
            var errors = Validate(document);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, errors),
                    "Blueprint validation failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_elements.Count > 0 && MessageBox.Show(this,
                "Open this blueprint? Unsaved changes to the current document will be lost.",
                "Open blueprint", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _undo.Clear();
            _redo.Clear();
            _elements.Clear();
            foreach (var element in document.Elements)
                _elements.Add(element);

            var openingMapping = AutoMapElements(_elements);
            BlueprintNameBox.Text = document.Metadata.Name;
            // A recovered .bak must never be silently overwritten as a working file.
            // The next Save prompts the user for an explicit new path.
            _currentPath = recoveryMode ? null : fileName;
            _groupClipboard = null;
            _pasteCount = 0;
            if (_elements.FirstOrDefault() is { } first)
                SetSelection([first]);
            else
                SetSelection([]);

            CenterGridToContent();
            UpdateStatus((recoveryMode ? "Recovered backup (unsaved): " : "Loaded: ") +
                System.IO.Path.GetFileName(fileName) +
                (openingMapping?.Assigned > 0 ?
                    $" • {openingMapping.Assigned} local models auto-linked (save blueprint to persist)" : ""));
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteException(recoveryMode ? "Recovering blueprint backup" : "Opening blueprint", ex);
            MessageBox.Show(this, ex.Message, recoveryMode ? "Backup recovery failed" : "Open error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyInspectorIfPossible(true))
            return;

        ElementsGrid.Items.Refresh();
        RenderGrid();
        var errors = Validate(CreateDocument());
        MessageBox.Show(errors.Count == 0 ? $"Blueprint is valid.\n\nElements: {_elements.Count}" : string.Join(Environment.NewLine, errors),
            errors.Count == 0 ? "Validation passed" : "Validation failed", MessageBoxButton.OK,
            errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        UpdateStatus(errors.Count == 0 ? "Validation passed." : $"Validation found {errors.Count} issue(s).");
    }

    private List<string> Validate(BlueprintDocument document)
    {
        var errors = new List<string>();
        if (document.SchemaVersion != 1)
            errors.Add($"Unsupported schema version: {document.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(document.Metadata.Name))
            errors.Add("Blueprint name is required.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < document.Elements.Count; i++)
        {
            var item = document.Elements[i];
            var label = $"Element {i + 1}";
            if (string.IsNullOrWhiteSpace(item.Id))
                errors.Add($"{label}: ID is missing.");
            else if (!ids.Add(item.Id))
                errors.Add($"{label}: duplicate ID '{item.Id}'.");

            if (string.IsNullOrWhiteSpace(item.TypeId))
                errors.Add($"{label}: Type ID is required.");
            if (!Finite(item.Position.X) || !Finite(item.Position.Y) || !Finite(item.Position.Z))
                errors.Add($"{label}: position must contain finite numbers.");
            if (!Finite(item.Rotation.Pitch) || !Finite(item.Rotation.Yaw) || !Finite(item.Rotation.Roll))
                errors.Add($"{label}: rotation must contain finite numbers.");
        }

        return errors;
    }

    // X is left/right, Z is depth, and Y is elevation. The 3D mode uses an
    // isometric projection of the same coordinate data; it does not invent meshes.
    private Point WorldToCanvas(double x, double z) => WorldToCanvas(x, 0, z);

    private Point WorldToCanvas(double x, double y, double z)
    {
        var center = new Point(BlueprintCanvas.ActualWidth / 2, BlueprintCanvas.ActualHeight / 2);
        var relativeX = (x - _viewCenterXMetres) / MetresPerCell;
        var relativeZ = (z - _viewCenterZMetres) / MetresPerCell;

        if (_gridViewMode == GridViewMode.TwoDTopDown)
        {
            return new Point(
                center.X + relativeX * _pixelsPerCell,
                center.Y - relativeZ * _pixelsPerCell);
        }

        var relativeY = (y - _viewCenterYMetres) / MetresPerCell;
        return new Point(
            center.X + (relativeX - relativeZ) * _pixelsPerCell * IsometricXScale,
            center.Y + (relativeX + relativeZ) * _pixelsPerCell * IsometricYScale
                    - relativeY * _pixelsPerCell * IsometricElevationScale);
    }

    // Returns the X/Z coordinate under a screen point on the ground plane.
    // This is used for cursor-anchored zoom and ground-plane navigation.
    private Point CanvasToWorldContinuous(Point point)
    {
        var center = new Point(BlueprintCanvas.ActualWidth / 2, BlueprintCanvas.ActualHeight / 2);
        if (_gridViewMode == GridViewMode.TwoDTopDown)
        {
            return new Point(
                _viewCenterXMetres + ((point.X - center.X) / _pixelsPerCell) * MetresPerCell,
                _viewCenterZMetres - ((point.Y - center.Y) / _pixelsPerCell) * MetresPerCell);
        }

        var projectedDifference = (point.X - center.X) / (_pixelsPerCell * IsometricXScale);
        var projectedSum = (point.Y - center.Y - (_viewCenterYMetres / MetresPerCell) * _pixelsPerCell)
                           / (_pixelsPerCell * IsometricYScale);
        var relativeX = (projectedDifference + projectedSum) / 2.0;
        var relativeZ = (projectedSum - projectedDifference) / 2.0;
        return new Point(
            _viewCenterXMetres + relativeX * MetresPerCell,
            _viewCenterZMetres + relativeZ * MetresPerCell);
    }

    // Convert a screen-space drag into movement on the X/Z ground plane.
    private Point ScreenDeltaToWorldDelta(Vector delta)
    {
        if (_gridViewMode == GridViewMode.TwoDTopDown)
        {
            return new Point(
                (delta.X / _pixelsPerCell) * MetresPerCell,
                (-delta.Y / _pixelsPerCell) * MetresPerCell);
        }

        var projectedDifference = delta.X / (_pixelsPerCell * IsometricXScale);
        var projectedSum = delta.Y / (_pixelsPerCell * IsometricYScale);
        var relativeX = (projectedDifference + projectedSum) / 2.0;
        var relativeZ = (projectedSum - projectedDifference) / 2.0;
        return new Point(relativeX * MetresPerCell, relativeZ * MetresPerCell);
    }

    private void GridModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridModeComboBox.SelectedIndex == 1)
        {
            try
            {
                // Embed the actual textured editor. Do not open a second Window.
                if (_threeDEditor is null)
                {
                    _threeDEditor = new Blueprint3DEditorControl(
                        _elements, MoveElementsFrom3D, SelectElementsFrom3D, UndoFrom3D, GetSelectedElements());
                    ThreeDEditorHost.Content = _threeDEditor;
                }
                else
                    _threeDEditor.RefreshFromEditor(GetSelectedElements());

                _gridViewMode = GridViewMode.ThreeDTextured;
                TwoDEditorSurface.Visibility = Visibility.Collapsed;
                ThreeDEditorSurface.Visibility = Visibility.Visible;
                GridHeadingText.Text = "3D EDITOR";
                EditorHelpText.Text = "3D Editor: left-click selects • Left-drag a piece to place on the 4 m grid • Alt+left-drag a piece adjusts height • Left-drag empty space orbits • Right-drag pans • Ctrl+Z undoes.";
                UpdateStatus("3D Editor enabled. Drag and drop a piece with the left mouse; right-drag pans.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                AppDiagnostics.WriteException("Opening embedded 3D Editor", ex);
                GridModeComboBox.SelectedIndex = 0;
                MessageBox.Show(this, "Could not load the 3D Editor: " + ex.Message +
                    "\n\nDiagnostic log: " + AppDiagnostics.LogPath,
                    "3D Editor error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            _gridViewMode = GridViewMode.TwoDTopDown;
            TwoDEditorSurface.Visibility = Visibility.Visible;
            ThreeDEditorSurface.Visibility = Visibility.Collapsed;
            GridHeadingText.Text = "2D BUILD GRID";
            EditorHelpText.Text = "2D Editor: X/Z top-down grid • Left-drag markers to move • Drag empty space to select • Right-drag or middle-drag to pan • Mouse wheel to zoom • 1 cell = 4 m.";
            UpdateStatus("2D Editor enabled. Right-click and drag to pan.");
        }

        RenderGrid();
    }

    private void RenderGrid()
    {
        if (BlueprintCanvas is null)
            return;

        UpdateDimensions();
        if (_gridViewMode == GridViewMode.ThreeDTextured)
        {
            // Coalesce multiple synchronous editor changes into one 3D refresh.
            if (_threeDEditor is not null && !_pending3DRefresh && !_handling3DCallback)
            {
                _pending3DRefresh = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _pending3DRefresh = false;
                    if (_gridViewMode != GridViewMode.ThreeDTextured || _threeDEditor is null)
                        return;
                    try
                    {
                        _threeDEditor.RefreshFromEditor(GetSelectedElements());
                        if (_fitAfter3DRefresh)
                        {
                            _fitAfter3DRefresh = false;
                            _threeDEditor.FitToScene();
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        AppDiagnostics.WriteException("Refreshing embedded 3D Editor", ex);
                        UpdateStatus("3D Editor refresh failed; see diagnostic log.");
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            return;
        }
        if (BlueprintCanvas.ActualWidth < 2 || BlueprintCanvas.ActualHeight < 2)
            return;

        BlueprintCanvas.Children.Clear();
        var width = BlueprintCanvas.ActualWidth;
        var height = BlueprintCanvas.ActualHeight;

        if (_gridViewMode == GridViewMode.TwoDTopDown)
            RenderTopDownGrid(width, height);
        else
            RenderIsometricGrid(width, height);

        foreach (var item in _elements)
        {
            var position = WorldToCanvas(item.Position.X, item.Position.Y, item.Position.Z);
            var selected = _selectedElements.Contains(item);
            var marker = new Border
            {
                Width = _gridViewMode == GridViewMode.ThreeDTextured ? 24 : 28,
                Height = _gridViewMode == GridViewMode.ThreeDTextured ? 24 : 28,
                CornerRadius = new CornerRadius(4),
                Background = selected
                    ? new SolidColorBrush(Color.FromRgb(104, 220, 65))
                    : new SolidColorBrush(Color.FromRgb(48, 91, 50)),
                BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(120, 170, 100)),
                BorderThickness = new Thickness(selected ? 2.5 : 1.5),
                Tag = item,
                ToolTip = $"{item.TypeId}\nX: {Format(item.Position.X)} m  Y: {Format(item.Position.Y)} m  Z: {Format(item.Position.Z)}"
            };
            if (_gridViewMode == GridViewMode.ThreeDTextured)
            {
                marker.RenderTransformOrigin = new Point(0.5, 0.5);
                marker.RenderTransform = new RotateTransform(45);
            }

            var markerLabel = new TextBlock
            {
                Text = ShortLabel(item.TypeId),
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            if (_gridViewMode == GridViewMode.ThreeDTextured)
            {
                markerLabel.RenderTransformOrigin = new Point(0.5, 0.5);
                markerLabel.RenderTransform = new RotateTransform(-45);
            }
            marker.Child = markerLabel;
            marker.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
            Canvas.SetLeft(marker, position.X - marker.Width / 2);
            Canvas.SetTop(marker, position.Y - marker.Height / 2);
            Panel.SetZIndex(marker, 10 + (int)Math.Clamp(item.Position.Y / MetresPerCell, -100, 100));
            BlueprintCanvas.Children.Add(marker);
        }

        var modeLabel = _gridViewMode == GridViewMode.TwoDTopDown ? "2D TOP-DOWN" : "3D ISOMETRIC";
        AddCanvasText(
            $"{modeLabel}  |  GRID: 4m  |  ZOOM: {_pixelsPerCell:0}px/cell  |  ELEMENTS: {_elements.Count}  |  SELECTED: {_selectedElements.Count}",
            10, 10, new SolidColorBrush(Color.FromRgb(112, 210, 75)));
    }

    private void RenderTopDownGrid(double width, double height)
    {
        var zero = WorldToCanvas(0, 0);
        var startX = PositiveModulo(zero.X, _pixelsPerCell);
        var startY = PositiveModulo(zero.Y, _pixelsPerCell);

        for (double x = startX; x < width; x += _pixelsPerCell)
            AddGridLine(new Point(x, 0), new Point(x, height));
        for (double y = startY; y < height; y += _pixelsPerCell)
            AddGridLine(new Point(0, y), new Point(width, y));

        if (zero.Y >= 0 && zero.Y <= height)
            AddAxis(new Point(0, zero.Y), new Point(width, zero.Y));
        if (zero.X >= 0 && zero.X <= width)
            AddAxis(new Point(zero.X, 0), new Point(zero.X, height));

        if (zero.X >= 0 && zero.X < width && zero.Y >= 0 && zero.Y < height)
            AddCanvasText("0,0", zero.X + 7, zero.Y + 6, Brushes.WhiteSmoke);
    }

    private void RenderIsometricGrid(double width, double height)
    {
        // Draw a clipped isometric ground-plane grid. Its axes represent the same
        // 4 m X/Z coordinates as the top-down mode.
        var radius = (int)Math.Clamp(Math.Ceiling(Math.Max(width, height) / Math.Max(12, _pixelsPerCell * 0.72)) + 6, 8, 80);
        var centerXCell = (int)Math.Floor(_viewCenterXMetres / MetresPerCell);
        var centerZCell = (int)Math.Floor(_viewCenterZMetres / MetresPerCell);
        var minXCell = centerXCell - radius;
        var maxXCell = centerXCell + radius;
        var minZCell = centerZCell - radius;
        var maxZCell = centerZCell + radius;

        for (var xCell = minXCell; xCell <= maxXCell; xCell++)
        {
            var x = xCell * MetresPerCell;
            AddGridLine(WorldToCanvas(x, 0, minZCell * MetresPerCell),
                        WorldToCanvas(x, 0, maxZCell * MetresPerCell));
        }

        for (var zCell = minZCell; zCell <= maxZCell; zCell++)
        {
            var z = zCell * MetresPerCell;
            AddGridLine(WorldToCanvas(minXCell * MetresPerCell, 0, z),
                        WorldToCanvas(maxXCell * MetresPerCell, 0, z));
        }

        // Add lighter horizontal grid slices at the distinct occupied elevation
        // levels. This makes the view a spatial X/Y/Z grid rather than only a
        // tilted floor plane. The number of slices is capped for large blueprints.
        if (_elements.Count > 0)
        {
            var occupiedLevels = _elements
                .Select(item => (int)Math.Round(item.Position.Y / MetresPerCell, MidpointRounding.AwayFromZero))
                .Where(level => level != 0)
                .Distinct()
                .OrderBy(level => level)
                .ToArray();

            const int maximumElevationSlices = 10;
            var sliceStride = Math.Max(1, (int)Math.Ceiling(occupiedLevels.Length / (double)maximumElevationSlices));
            var minElementXCell = (int)Math.Floor(_elements.Min(item => item.Position.X) / MetresPerCell) - 1;
            var maxElementXCell = (int)Math.Ceiling(_elements.Max(item => item.Position.X) / MetresPerCell) + 1;
            var minElementZCell = (int)Math.Floor(_elements.Min(item => item.Position.Z) / MetresPerCell) - 1;
            var maxElementZCell = (int)Math.Ceiling(_elements.Max(item => item.Position.Z) / MetresPerCell) + 1;
            var xStride = Math.Max(1, (int)Math.Ceiling((maxElementXCell - minElementXCell) / 60.0));
            var zStride = Math.Max(1, (int)Math.Ceiling((maxElementZCell - minElementZCell) / 60.0));

            for (var levelIndex = 0; levelIndex < occupiedLevels.Length; levelIndex += sliceStride)
            {
                var y = occupiedLevels[levelIndex] * MetresPerCell;
                for (var xCell = minElementXCell; xCell <= maxElementXCell; xCell += xStride)
                {
                    AddElevationGridLine(
                        WorldToCanvas(xCell * MetresPerCell, y, minElementZCell * MetresPerCell),
                        WorldToCanvas(xCell * MetresPerCell, y, maxElementZCell * MetresPerCell));
                }

                for (var zCell = minElementZCell; zCell <= maxElementZCell; zCell += zStride)
                {
                    AddElevationGridLine(
                        WorldToCanvas(minElementXCell * MetresPerCell, y, zCell * MetresPerCell),
                        WorldToCanvas(maxElementXCell * MetresPerCell, y, zCell * MetresPerCell));
                }
            }
        }

        // Highlight the world X/Z axes on the ground plane.
        AddAxis(WorldToCanvas(minXCell * MetresPerCell, 0, 0),
                WorldToCanvas(maxXCell * MetresPerCell, 0, 0));
        AddAxis(WorldToCanvas(0, 0, minZCell * MetresPerCell),
                WorldToCanvas(0, 0, maxZCell * MetresPerCell));

        // Show a vertical Y axis and per-element elevation guides so stacked floors,
        // walls and roofs remain understandable in the isometric coordinate view.
        var minY = Math.Min(0, _elements.Count == 0 ? 0 : _elements.Min(item => item.Position.Y));
        var maxY = Math.Max(MetresPerCell, _elements.Count == 0 ? MetresPerCell : _elements.Max(item => item.Position.Y));
        var yPadding = Math.Max(MetresPerCell, radius * MetresPerCell * 0.2);
        AddAxis(WorldToCanvas(0, minY - yPadding, 0), WorldToCanvas(0, maxY + yPadding, 0));

        foreach (var item in _elements)
        {
            if (Math.Abs(item.Position.Y) < 0.001)
                continue;
            AddGuideLine(
                WorldToCanvas(item.Position.X, 0, item.Position.Z),
                WorldToCanvas(item.Position.X, item.Position.Y, item.Position.Z));
        }

        var origin = WorldToCanvas(0, 0, 0);
        if (origin.X >= -40 && origin.X <= width + 40 && origin.Y >= -40 && origin.Y <= height + 40)
            AddCanvasText("X/Z ORIGIN", origin.X + 8, origin.Y + 8, Brushes.WhiteSmoke);
        var yLabel = WorldToCanvas(0, maxY + yPadding, 0);
        AddCanvasText("Y", yLabel.X + 5, yLabel.Y - 4, Brushes.WhiteSmoke);
    }

    private void AddElevationGridLine(Point start, Point end)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(45, 105, 155, 100)),
            StrokeThickness = 0.8,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(line, 1);
        BlueprintCanvas.Children.Add(line);
    }

    private void AddGuideLine(Point start, Point end)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = new SolidColorBrush(Color.FromArgb(150, 115, 175, 105)),
            StrokeThickness = 1.25,
            StrokeDashArray = new DoubleCollection { 2, 3 },
            IsHitTestVisible = false
        };
        Panel.SetZIndex(line, 2);
        BlueprintCanvas.Children.Add(line);
    }

    private static double PositiveModulo(double value, double divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static double SnapToGrid(double value) =>
        Math.Round(value / MetresPerCell, MidpointRounding.AwayFromZero) * MetresPerCell;

    private void AddGridLine(Point start, Point end)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = new SolidColorBrush(Color.FromRgb(35, 49, 37)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        BlueprintCanvas.Children.Add(line);
    }

    private void AddAxis(Point start, Point end)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = new SolidColorBrush(Color.FromRgb(80, 135, 65)),
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        Panel.SetZIndex(line, 1);
        BlueprintCanvas.Children.Add(line);
    }

    private void AddCanvasText(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 10,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        Panel.SetZIndex(label, 20);
        BlueprintCanvas.Children.Add(label);
    }

    private static string ShortLabel(string typeId)
    {
        var part = typeId.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "?";
        return part.Length <= 3 ? part.ToUpperInvariant() : part[..3].ToUpperInvariant();
    }

    private void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border marker || marker.Tag is not BlueprintElement item)
            return;

        if (Keyboard.IsKeyDown(Key.Space))
        {
            BeginPan(e.GetPosition(BlueprintCanvas), MouseButton.Left);
            e.Handled = true;
            return;
        }

        var control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var selection = GetSelectedElements();

        if (control)
        {
            if (_selectedElements.Contains(item))
            {
                selection.Remove(item);
                SetSelection(selection);
                UpdateStatus("Element removed from selection.");
                e.Handled = true;
                return;
            }

            selection.Add(item);
            SetSelection(selection);
        }
        else if (shift)
        {
            selection.Add(item);
            SetSelection(selection);
        }
        else if (!_selectedElements.Contains(item))
        {
            SetSelection([item]);
        }

        if (!_selectedElements.Contains(item))
        {
            e.Handled = true;
            return;
        }

        BeginGroupDrag(e.GetPosition(BlueprintCanvas));
        e.Handled = true;
    }

    private void BeginGroupDrag(Point startPoint)
    {
        var selected = GetSelectedElements();
        if (selected.Count == 0)
            return;

        _dragSnapshot = CaptureSnapshot();
        _dragStartPoint = startPoint;
        _dragHasMoved = false;
        _dragElevationMode = _gridViewMode == GridViewMode.ThreeDTextured &&
            (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        _dragStartPositions = selected.ToDictionary(
            item => item,
            item => (item.Position.X, item.Position.Y, item.Position.Z));
        _isDraggingElements = true;
        BlueprintCanvas.CaptureMouse();
    }

    private void BlueprintCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.Space))
        {
            BeginPan(e.GetPosition(BlueprintCanvas), MouseButton.Left);
            e.Handled = true;
            return;
        }

        if (e.OriginalSource != BlueprintCanvas)
            return;

        _areaStartPoint = e.GetPosition(BlueprintCanvas);
        _areaSelectionIsAdditive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        _areaBaseSelection = _areaSelectionIsAdditive
            ? GetSelectedElements().ToHashSet()
            : new HashSet<BlueprintElement>();
        _isAreaSelecting = true;
        _selectionRectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(150, 225, 105)),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(45, 120, 200, 95)),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_selectionRectangle, 30);
        BlueprintCanvas.Children.Add(_selectionRectangle);
        UpdateSelectionRectangle(e.GetPosition(BlueprintCanvas));
        BlueprintCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void BlueprintCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right) ||
            _isPanning || _isAreaSelecting || _isDraggingElements)
            return;

        BeginPan(e.GetPosition(BlueprintCanvas), e.ChangedButton);
        e.Handled = true;
    }

    private void BeginPan(Point startPoint, MouseButton button)
    {
        if (_isPanning) return;
        _isPanning = true;
        _panButton = button;
        _panStartPoint = startPoint;
        _panStartCenterX = _viewCenterXMetres;
        _panStartCenterZ = _viewCenterZMetres;
        BlueprintCanvas.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void BlueprintCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pointer = e.GetPosition(BlueprintCanvas);

        if (_isPanning)
        {
            var worldDelta = ScreenDeltaToWorldDelta(pointer - _panStartPoint);
            _viewCenterXMetres = _panStartCenterX - worldDelta.X;
            _viewCenterZMetres = _panStartCenterZ - worldDelta.Y;
            RenderGrid();
            return;
        }

        if (_isAreaSelecting)
        {
            UpdateSelectionRectangle(pointer);
            return;
        }

        if (!_isDraggingElements || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = pointer - _dragStartPoint;
        var moveX = 0.0;
        var moveY = 0.0;
        var moveZ = 0.0;

        if (_dragElevationMode)
        {
            // Quantize the drag delta itself, not the group's origin. This prevents
            // an off-grid group from jumping as soon as a drag begins.
            var deltaYCells = (int)Math.Round(-delta.Y / _pixelsPerCell, MidpointRounding.AwayFromZero);
            moveY = deltaYCells * MetresPerCell;
        }
        else
        {
            var worldDelta = ScreenDeltaToWorldDelta(delta);
            var deltaXCells = (int)Math.Round(worldDelta.X / MetresPerCell, MidpointRounding.AwayFromZero);
            var deltaZCells = (int)Math.Round(worldDelta.Y / MetresPerCell, MidpointRounding.AwayFromZero);
            moveX = deltaXCells * MetresPerCell;
            moveZ = deltaZCells * MetresPerCell;
        }

        foreach (var (item, start) in _dragStartPositions)
        {
            item.Position.X = start.X + moveX;
            item.Position.Y = start.Y + moveY;
            item.Position.Z = start.Z + moveZ;
        }

        _dragHasMoved = moveX != 0 || moveY != 0 || moveZ != 0;
        UpdateSelectionDisplay();
        ElementsGrid.Items.Refresh();
        RenderGrid();
    }

    private void UpdateSelectionRectangle(Point current)
    {
        if (_selectionRectangle is null)
            return;

        var left = Math.Min(_areaStartPoint.X, current.X);
        var top = Math.Min(_areaStartPoint.Y, current.Y);
        _selectionRectangle.Width = Math.Abs(current.X - _areaStartPoint.X);
        _selectionRectangle.Height = Math.Abs(current.Y - _areaStartPoint.Y);
        Canvas.SetLeft(_selectionRectangle, left);
        Canvas.SetTop(_selectionRectangle, top);
    }

    private void BlueprintCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPanning && _panButton == MouseButton.Left)
        {
            FinishPan();
            e.Handled = true;
            return;
        }

        if (_isAreaSelecting)
        {
            FinishAreaSelection(e.GetPosition(BlueprintCanvas));
            e.Handled = true;
            return;
        }

        if (_isDraggingElements)
        {
            FinishGroupDrag();
            e.Handled = true;
        }
    }

    private void BlueprintCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.ChangedButton is MouseButton.Middle or MouseButton.Right) &&
            _isPanning && _panButton == e.ChangedButton)
        {
            FinishPan();
            e.Handled = true;
        }
    }

    private void FinishPan()
    {
        _isPanning = false;
        _panButton = null;
        Cursor = Cursors.Arrow;
        BlueprintCanvas.ReleaseMouseCapture();
    }

    private void BlueprintCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // A window switch or another control capturing the mouse must not leave
        // a right-drag pan active indefinitely.
        _isPanning = false;
        _panButton = null;
        Cursor = Cursors.Arrow;
    }

    private void FinishAreaSelection(Point endPoint)
    {
        var rectangle = new Rect(_areaStartPoint, endPoint);
        var didDrag = rectangle.Width >= 4 || rectangle.Height >= 4;
        var selected = _areaBaseSelection.ToHashSet();

        if (didDrag)
        {
            foreach (var item in _elements)
            {
                var markerCenter = WorldToCanvas(item.Position.X, item.Position.Y, item.Position.Z);
                if (rectangle.Contains(markerCenter))
                    selected.Add(item);
            }
        }
        else if (!_areaSelectionIsAdditive)
        {
            selected.Clear();
        }

        _isAreaSelecting = false;
        if (_selectionRectangle is not null)
            BlueprintCanvas.Children.Remove(_selectionRectangle);
        _selectionRectangle = null;
        _areaBaseSelection.Clear();
        BlueprintCanvas.ReleaseMouseCapture();
        SetSelection(selected);
        UpdateStatus(didDrag ? $"Rectangle selection: {_selectedElements.Count} element(s)." : "Selection cleared.");
    }

    private void FinishGroupDrag()
    {
        _isDraggingElements = false;
        BlueprintCanvas.ReleaseMouseCapture();

        if (_dragHasMoved && _dragSnapshot is not null)
        {
            PushUndoSnapshot(_dragSnapshot);
            UpdateStatus($"Moved {_dragStartPositions.Count} element(s) as a group. Ctrl+Z to undo.");
        }

        _dragSnapshot = null;
        _dragStartPositions.Clear();
        _dragHasMoved = false;
        ElementsGrid.Items.Refresh();
        RenderGrid();
    }

    private void BlueprintCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var step = e.Delta > 0 ? 8 : -8;
        ZoomAt(e.GetPosition(BlueprintCanvas), _pixelsPerCell + step);
        e.Handled = true;
    }

    private void ZoomAt(Point anchor, double requestedZoom)
    {
        var worldAtAnchor = CanvasToWorldContinuous(anchor);
        _pixelsPerCell = Math.Clamp(requestedZoom, MinimumZoom, MaximumZoom);
        var worldAtAnchorAfterZoom = CanvasToWorldContinuous(anchor);
        _viewCenterXMetres += worldAtAnchor.X - worldAtAnchorAfterZoom.X;
        _viewCenterZMetres += worldAtAnchor.Y - worldAtAnchorAfterZoom.Y;
        RenderGrid();
    }

    private void BlueprintCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderGrid();

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (_gridViewMode == GridViewMode.ThreeDTextured) _threeDEditor?.ZoomIn();
        else ZoomAt(new Point(BlueprintCanvas.ActualWidth / 2, BlueprintCanvas.ActualHeight / 2), _pixelsPerCell + 8);
    }
    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (_gridViewMode == GridViewMode.ThreeDTextured) _threeDEditor?.ZoomOut();
        else ZoomAt(new Point(BlueprintCanvas.ActualWidth / 2, BlueprintCanvas.ActualHeight / 2), _pixelsPerCell - 8);
    }
    private void CenterGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_gridViewMode == GridViewMode.ThreeDTextured) _threeDEditor?.FitToScene();
        else CenterGridToContent();
    }

    private void CenterGridToContent()
    {
        if (_elements.Count == 0)
        {
            _viewCenterXMetres = 0;
            _viewCenterYMetres = 0;
            _viewCenterZMetres = 0;
            _pixelsPerCell = 48;
            RenderGrid();
            return;
        }

        var minX = _elements.Min(item => item.Position.X);
        var maxX = _elements.Max(item => item.Position.X);
        var minY = _elements.Min(item => item.Position.Y);
        var maxY = _elements.Max(item => item.Position.Y);
        var minZ = _elements.Min(item => item.Position.Z);
        var maxZ = _elements.Max(item => item.Position.Z);
        _viewCenterXMetres = (minX + maxX) / 2;
        _viewCenterYMetres = _gridViewMode == GridViewMode.ThreeDTextured ? (minY + maxY) / 2 : 0;
        _viewCenterZMetres = (minZ + maxZ) / 2;

        var rangeXCells = Math.Max(1, (maxX - minX) / MetresPerCell + 1);
        var rangeZCells = Math.Max(1, (maxZ - minZ) / MetresPerCell + 1);
        var rangeYCells = Math.Max(1, (maxY - minY) / MetresPerCell + 1);
        var availableWidth = Math.Max(100, BlueprintCanvas.ActualWidth - 120);
        var availableHeight = Math.Max(100, BlueprintCanvas.ActualHeight - 120);

        double projectedWidthCells;
        double projectedHeightCells;
        if (_gridViewMode == GridViewMode.ThreeDTextured)
        {
            projectedWidthCells = (rangeXCells + rangeZCells) * IsometricXScale + 2;
            projectedHeightCells = (rangeXCells + rangeZCells) * IsometricYScale
                                   + rangeYCells * IsometricElevationScale + 2;
        }
        else
        {
            projectedWidthCells = rangeXCells;
            projectedHeightCells = rangeZCells;
        }

        var fitZoom = Math.Min(availableWidth / projectedWidthCells, availableHeight / projectedHeightCells);
        _pixelsPerCell = Math.Clamp(fitZoom, MinimumZoom, MaximumZoom);
        RenderGrid();
        UpdateStatus(_gridViewMode == GridViewMode.ThreeDTextured
            ? "3D isometric grid centered and fitted to blueprint."
            : "2D grid centered and fitted to blueprint.");
    }

    private void UpdateDimensions()
    {
        if (DimensionsText is null)
            return;

        if (_elements.Count == 0)
        {
            DimensionsText.Text = "Estimated footprint: —  |  Vertical anchor span: —";
            return;
        }

        var minX = _elements.Min(item => item.Position.X);
        var maxX = _elements.Max(item => item.Position.X);
        var minY = _elements.Min(item => item.Position.Y);
        var maxY = _elements.Max(item => item.Position.Y);
        var minZ = _elements.Min(item => item.Position.Z);
        var maxZ = _elements.Max(item => item.Position.Z);

        // X/Z footprint assumes one 4 m cell per occupied anchor. Y is only the coordinate span.
        var widthCells = (maxX - minX) / MetresPerCell + 1;
        var depthCells = (maxZ - minZ) / MetresPerCell + 1;
        var widthMetres = widthCells * MetresPerCell;
        var depthMetres = depthCells * MetresPerCell;
        var heightSpan = maxY - minY;

        DimensionsText.Text =
            $"Estimated footprint: {widthCells:0.##} × {depthCells:0.##} cells ({widthMetres:0.##} × {depthMetres:0.##} m)  |  Vertical anchor span: {heightSpan / MetresPerCell:0.##} cells ({heightSpan:0.##} m)";
    }

    private void CopyGroup_Click(object sender, RoutedEventArgs e) => CopySelectedGroup();
    private void PasteGroup_Click(object sender, RoutedEventArgs e) => PasteGroup();

    private void CopySelectedGroup()
    {
        var selected = GetSelectedElements();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more elements before copying.");
            return;
        }

        _clipboardMinX = selected.Min(item => item.Position.X);
        _clipboardMinY = selected.Min(item => item.Position.Y);
        _clipboardMinZ = selected.Min(item => item.Position.Z);
        var maxX = selected.Max(item => item.Position.X);
        _clipboardWidthMetres = Math.Max(MetresPerCell, maxX - _clipboardMinX + MetresPerCell);
        _groupClipboard = selected.Select(item => new ClipboardElement(
            item.TypeId,
            item.Position.X - _clipboardMinX,
            item.Position.Y - _clipboardMinY,
            item.Position.Z - _clipboardMinZ,
            item.Rotation.Pitch,
            item.Rotation.Yaw,
            item.Rotation.Roll,
            item.Variant,
            item.ModelAssetId,
            item.ModelAssetName)).ToList();
        _pasteCount = 0;
        UpdateStatus($"Copied {selected.Count} element(s). Use Ctrl+V to paste the group.");
    }

    private void PasteGroup()
    {
        if (_groupClipboard is null || _groupClipboard.Count == 0)
        {
            UpdateStatus("Clipboard is empty. Select and copy a group first.");
            return;
        }

        PushUndo();
        var offsetX = _clipboardMinX + (++_pasteCount * _clipboardWidthMetres);
        var pasted = new List<BlueprintElement>(_groupClipboard.Count);
        foreach (var source in _groupClipboard)
        {
            var item = new BlueprintElement
            {
                TypeId = source.TypeId,
                Position = new BlueprintVector3
                {
                    X = offsetX + source.RelativeX,
                    Y = _clipboardMinY + source.RelativeY,
                    Z = _clipboardMinZ + source.RelativeZ
                },
                Rotation = new BlueprintRotation
                {
                    Pitch = source.Pitch,
                    Yaw = source.Yaw,
                    Roll = source.Roll
                },
                Variant = source.Variant,
                ModelAssetId = source.ModelAssetId,
                ModelAssetName = source.ModelAssetName
            };
            pasted.Add(item);
            _elements.Add(item);
        }

        SetSelection(pasted);
        UpdateStatus($"Pasted {pasted.Count} element(s) as a group. Drag the selected group to reposition it.");
    }

    private sealed record ClipboardElement(
        string TypeId,
        double RelativeX,
        double RelativeY,
        double RelativeZ,
        double Pitch,
        double Yaw,
        double Roll,
        string? Variant,
        string? ModelAssetId,
        string? ModelAssetName);

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelectedGroup();

    private void DuplicateSelectedGroup()
    {
        var selected = GetSelectedElements();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more elements to duplicate.");
            return;
        }

        PushUndo();
        var minX = selected.Min(item => item.Position.X);
        var maxX = selected.Max(item => item.Position.X);
        var offsetX = maxX - minX + MetresPerCell;
        var copies = selected.Select(item => CloneElement(item, offsetX, 0, 0)).ToList();
        foreach (var copy in copies)
            _elements.Add(copy);

        SetSelection(copies);
        UpdateStatus($"Duplicated {copies.Count} element(s) one group-width east.");
    }

    private static BlueprintElement CloneElement(BlueprintElement source, double offsetX, double offsetY, double offsetZ) => new()
    {
        TypeId = source.TypeId,
        Position = new BlueprintVector3
        {
            X = source.Position.X + offsetX,
            Y = source.Position.Y + offsetY,
            Z = source.Position.Z + offsetZ
        },
        Rotation = new BlueprintRotation
        {
            Pitch = source.Rotation.Pitch,
            Yaw = source.Rotation.Yaw,
            Roll = source.Rotation.Roll
        },
        Variant = source.Variant,
        ModelAssetId = source.ModelAssetId,
        ModelAssetName = source.ModelAssetName
    };

    private void Rotate90_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedElements();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more elements to rotate.");
            return;
        }

        PushUndo();
        foreach (var item in selected)
            item.Rotation.Yaw = (item.Rotation.Yaw + 90) % 360;

        UpdateSelectionDisplay();
        ElementsGrid.Items.Refresh();
        RenderGrid();
        UpdateStatus($"Rotated {selected.Count} selected element(s) 90° around the Y axis.");
    }

    private void MoveElementsFrom3D(IReadOnlyList<BlueprintElement> requested, double offsetX, double offsetY, double offsetZ)
    {
        var ids = requested.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracked = _elements.Where(item => ids.Contains(item.Id)).ToList();
        if (tracked.Count == 0) return;
        if (tracked.Any(item => !double.IsFinite(item.Position.X + offsetX) ||
                                !double.IsFinite(item.Position.Y + offsetY) ||
                                !double.IsFinite(item.Position.Z + offsetZ)))
            throw new InvalidOperationException("Moving these elements would exceed the supported coordinate range.");
        _handling3DCallback = true;
        try
        {
            PushUndo();
            foreach (var item in tracked)
            {
                item.Position.X += offsetX;
                item.Position.Y += offsetY;
                item.Position.Z += offsetZ;
            }
            SetSelection(tracked);
            ElementsGrid.Items.Refresh();
            UpdateStatus($"Moved {tracked.Count} piece(s) from 3D by ({offsetX:0}, {offsetY:0}, {offsetZ:0}) m. Undo is available.");
        }
        finally { _handling3DCallback = false; }
    }

    private void SelectElementsFrom3D(IReadOnlyList<BlueprintElement> selected)
    {
        _handling3DCallback = true;
        try { SetSelection(selected); }
        finally { _handling3DCallback = false; }
    }

    private void UndoFrom3D()
    {
        _handling3DCallback = true;
        try { Undo(); }
        finally { _handling3DCallback = false; }
    }

    private AutomaticModelResolver? LoadLocalModelResolver()
    {
        var root = ModelAssetLibraryWindow.ActiveLibraryRoot;
        if (!Directory.Exists(root)) return null;
        return new AutomaticModelResolver(new ModelAssetCatalog().Scan(root));
    }

    private ModelMappingReport? AutoMapElements(IEnumerable<BlueprintElement> elements)
    {
        try { return LoadLocalModelResolver()?.AssignMissing(elements); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        {
            UpdateStatus($"Model library unavailable: {ex.Message}");
            return null;
        }
    }

    private void AutoMapModels_Click(object sender, RoutedEventArgs e)
    {
        // Save a single undo checkpoint only if any changes actually occur.
        var snapshot = CaptureSnapshot();
        var result = AutoMapElements(_elements);
        if (result is null)
        {
            UpdateStatus("No local model library found. Choose the library folder under Model Asset Library first.");
            return;
        }
        if (result.Assigned > 0)
        {
            PushUndoSnapshot(snapshot);
        }
        ElementsGrid.Items.Refresh();
        UpdateSelectionDisplay();
        RenderGrid();
        _threeDEditor?.RefreshFromEditor(GetSelectedElements(), reloadAssets: true);
        UpdateStatus($"Model mapping: {result.Summary}. Type IDs and manual assignments unchanged.");
    }

    private void AssetLibrary_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedElements();
        var commonTypeId = selected.Count > 0 && selected.All(item =>
            string.Equals(item.TypeId, selected[0].TypeId, StringComparison.OrdinalIgnoreCase))
            ? selected[0].TypeId : null;
        var library = new ModelAssetLibraryWindow(allowAssign: selected.Count > 0, suggestedTypeId: commonTypeId) { Owner = this };
        var accepted = library.ShowDialog() == true;
        // A user can save a Type ID mapping without quick-assigning it. Refresh
        // the preview's GLB index after the library window closes either way.
        _threeDEditor?.RefreshFromEditor(GetSelectedElements(), reloadAssets: true);
        if (!accepted || library.SelectedAsset is not { } asset)
            return;

        selected = GetSelectedElements();
        if (selected.Count == 0)
        {
            UpdateStatus("Select one or more blueprint elements before assigning a model asset.");
            return;
        }

        var differingTypeIds = selected.Any(item => !string.Equals(item.TypeId, asset.TypeId, StringComparison.OrdinalIgnoreCase));
        if (differingTypeIds && MessageBox.Show(
                this,
                $"This model's catalog Type ID is '{asset.TypeId}', but the selected piece(s) use different Type IDs. Link it for LOCAL 3D PREVIEW only, without changing any piece Type IDs? This does not verify an Enshrouded game model mapping.",
                "Confirm preview-only model assignment",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        PushUndo();
        foreach (var item in selected)
        {
            // Assign only the local asset reference; never replace a real game Type ID.
            item.ModelAssetId = asset.AssetId;
            item.ModelAssetName = asset.DisplayName;
        }

        ElementsGrid.Items.Refresh();
        UpdateSelectionDisplay();
        RenderGrid();
        UpdateStatus($"Linked '{asset.DisplayName}' to {selected.Count} blueprint piece(s) for 3D preview. Type IDs preserved. Save the blueprint to keep the links.");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let focused text fields retain their normal text-editing shortcuts.
        if (Keyboard.FocusedElement is TextBoxBase)
            return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            DuplicateSelectedGroup();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            CopySelectedGroup();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            PasteGroup();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
        {
            SetSelection(_elements);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            RemoveSelectedElements();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_isAreaSelecting)
            {
                _isAreaSelecting = false;
                if (_selectionRectangle is not null)
                    BlueprintCanvas.Children.Remove(_selectionRectangle);
                _selectionRectangle = null;
                _areaBaseSelection.Clear();
                BlueprintCanvas.ReleaseMouseCapture();
            }
            SetSelection([]);
            UpdateStatus("Selection cleared.");
            e.Handled = true;
        }
    }

    private void PushUndo() => PushUndoSnapshot(CaptureSnapshot());

    private void PushUndoSnapshot(string snapshot)
    {
        _undo.Push(snapshot);
        if (_undo.Count > 100)
        {
            var keep = _undo.Take(100).Reverse().ToArray();
            _undo.Clear();
            foreach (var entry in keep)
                _undo.Push(entry);
        }
        _redo.Clear();
    }

    private string CaptureSnapshot() => JsonSerializer.Serialize(CreateDocument());

    private void RestoreSnapshot(string snapshot)
    {
        var document = JsonSerializer.Deserialize<BlueprintDocument>(snapshot);
        if (document is null)
            return;

        _elements.Clear();
        foreach (var element in document.Elements)
            _elements.Add(element);
        BlueprintNameBox.Text = document.Metadata.Name;
        SetSelection(_elements.Take(1));
        ElementsGrid.Items.Refresh();
        RenderGrid();
        UpdateStatus($"History restored • {_elements.Count} element(s)");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            UpdateStatus("Nothing to undo.");
            return;
        }

        _redo.Push(CaptureSnapshot());
        RestoreSnapshot(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0)
        {
            UpdateStatus("Nothing to redo.");
            return;
        }

        _undo.Push(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
    }

    private void ClearInspector()
    {
        PosXBox.Text = PosYBox.Text = PosZBox.Text = "0";
        PitchBox.Text = YawBox.Text = RollBox.Text = "0";
        VariantBox.Text = "";
        ModelAssetText.Text = "No model asset assigned.";
    }

    private void UpdateStatus(string? message = null) =>
        StatusText.Text = message ?? $"Ready • {_elements.Count} element(s) • {_selectedElements.Count} selected";

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && Finite(value);
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string SafeFileName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "NewBlueprint" : value.Trim();
        foreach (var character in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(character, '_');
        return name;
    }
}
