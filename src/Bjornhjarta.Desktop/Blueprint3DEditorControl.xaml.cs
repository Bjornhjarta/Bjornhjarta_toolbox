using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Bjornhjarta.Core.Blueprints;

namespace Bjornhjarta.Desktop;

/// <summary>
/// Embedded textured GLB editor with interactive element selection and
/// grid-snapped, undoable movements delegated to Blueprint Forge. Only the
/// parent editor changes blueprint data; no game files are modified.
/// </summary>
public partial class Blueprint3DEditorControl : UserControl
{
    private readonly IEnumerable<BlueprintElement> _sourceElements;
    private readonly Action<IReadOnlyList<BlueprintElement>, double, double, double> _moveInEditor;
    private readonly Action<IReadOnlyList<BlueprintElement>> _selectInEditor;
    private readonly Action _undoInEditor;
    private List<BlueprintElement> _elements = [];
    private readonly HashSet<string> _selectedIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GeometryModel3D, string> _geometryOwners = new();
    // Geometry-specific world matrices let us test whether the mouse is over
    // an upward-facing support surface rather than a vertical wall.
    private readonly Dictionary<GeometryModel3D, Matrix3D> _geometryToWorld = new();
    // Store the original translation for fast, non-destructive live dragging.
    // Blueprint coordinates are updated ONLY on mouse release, in one undo step.
    private readonly Dictionary<string, List<(TranslateTransform3D Transform, double X, double Y, double Z)>> _previewTransforms
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModelAssetCatalog _catalog = new();
    private AutomaticModelResolver? _automaticModelResolver;
    private readonly PerspectiveCamera _camera = new();
    private readonly Dictionary<string, GlbSceneLoader.LoadedScene> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rect3D> _pieceWorldBounds = new(StringComparer.OrdinalIgnoreCase);
    private Rect3D _sceneModelBounds = Rect3D.Empty;
    private readonly Dictionary<string, ModelAssetRecord> _assetIndex = new(StringComparer.OrdinalIgnoreCase);
    private string? _scannedLibraryPath;
    private string? _libraryWarning;
    private readonly List<string> _warnings = [];
    private Point3D _target;
    private double _distance = 45;
    private double _azimuth = 0.85;
    private double _elevation = 0.48;
    private Point _lastMouse;
    private Point _mouseDownPoint;
    private bool _orbiting;
    private bool _panning;
    private bool _mouseDragged;
    private string? _dragCandidateId;
    private bool _draggingPieces;
    private bool _draggingHeight;
    private double _dragPlaneHeight;
    private Point3D _dragWorldStart;
    private string _dropSurfaceDescription = "ground";
    private Vector3D _previewOffset;
    private List<BlueprintElement> _dragElements = [];
    private int _renderedMeshes;
    private int _placeholders;
    private int _triangles;
    private const int MaximumPreviewElements = 350;
    private const double GridStep = 4.0;

    public Blueprint3DEditorControl(
        IEnumerable<BlueprintElement> elements,
        Action<IReadOnlyList<BlueprintElement>, double, double, double> moveInEditor,
        Action<IReadOnlyList<BlueprintElement>> selectInEditor,
        Action undoInEditor,
        IEnumerable<BlueprintElement>? initiallySelected = null)
    {
        _sourceElements = elements;
        _moveInEditor = moveInEditor;
        _selectInEditor = selectInEditor;
        _undoInEditor = undoInEditor;
        if (initiallySelected is not null)
            foreach (var item in initiallySelected)
                _selectedIds.Add(item.Id);

        InitializeComponent();
        _camera.FieldOfView = 54;
        _camera.NearPlaneDistance = 0.1;
        _camera.FarPlaneDistance = 100000;
        _camera.UpDirection = new Vector3D(0, 1, 0);
        SceneViewport.Camera = _camera;
        _elements = _sourceElements.ToList();
        FitScene();
        ReloadScene();
        FitScene(); // Now include the actual loaded geometry bounds, not only piece anchors.
    }

    private void ReloadScene(bool reloadAssets = false)
    {
        _elements = _sourceElements.ToList();
        _warnings.Clear();
        _geometryOwners.Clear();
        _geometryToWorld.Clear();
        _previewTransforms.Clear();
        _pieceWorldBounds.Clear();
        _sceneModelBounds = Rect3D.Empty;
        if (reloadAssets)
            _loaded.Clear();
        _renderedMeshes = _placeholders = _triangles = 0;

        // Asset inventory can be large. Scan once per path/reload, not on every
        // click or nudge; the explicit Reload library button invalidates the cache.
        var libraryRoot = ModelAssetLibraryWindow.ActiveLibraryRoot;
        if (reloadAssets || !string.Equals(_scannedLibraryPath, libraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            _assetIndex.Clear();
            _automaticModelResolver = null;
            _libraryWarning = null;
            _scannedLibraryPath = libraryRoot;
            try
            {
                if (Directory.Exists(libraryRoot))
                {
                    var records = _catalog.Scan(libraryRoot);
                    _automaticModelResolver = new AutomaticModelResolver(records);
                    foreach (var asset in records)
                    {
                        if (asset.IsValid && !string.IsNullOrWhiteSpace(asset.AssetId))
                            _assetIndex[asset.AssetId] = asset;
                    }
                }
                else _libraryWarning = $"Model library not found: {libraryRoot}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                _libraryWarning = "Model library scan failed: " + ex.Message;
            }
        }
        if (_libraryWarning is not null)
            _warnings.Add(_libraryWarning);

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(105, 110, 101)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 247, 226), new Vector3D(-0.5, -1, -0.7)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(100, 135, 105), new Vector3D(0.8, -0.4, 0.6)));
        AddGroundGrid(scene);

        foreach (var item in _elements.Take(MaximumPreviewElements))
        {
            Model3DGroup model;
            // Auto-resolve unassigned pieces directly in the preview. This does
            // not mutate the blueprint; the editor's Auto-map button persists it.
            ModelAssetRecord? asset = null;
            if (!string.IsNullOrWhiteSpace(item.ModelAssetId))
                _assetIndex.TryGetValue(item.ModelAssetId, out asset);
            else
                _automaticModelResolver?.TryResolve(item.TypeId, out asset);
            if (asset is not null &&
                asset.Format.Equals("GLB", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!_loaded.TryGetValue(asset.AssetId, out var loaded))
                    {
                        loaded = GlbSceneLoader.Load(asset.ModelFilePath);
                        _loaded[asset.AssetId] = loaded;
                    }
                    model = loaded.Model.Clone();
                    _renderedMeshes++;
                    _triangles += loaded.TriangleCount;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    _warnings.Add($"{asset.DisplayName}: {ex.Message}");
                    model = MakePlaceholder();
                    _placeholders++;
                }
            }
            else
            {
                model = MakePlaceholder();
                _placeholders++;
                if (!string.IsNullOrWhiteSpace(item.ModelAssetId))
                    _warnings.Add($"No supported GLB found for {item.ModelAssetName ?? item.TypeId} ({item.ModelAssetId}).");
                else if (_automaticModelResolver?.IsAmbiguous(item.TypeId) == true)
                    _warnings.Add($"Ambiguous model mapping for {item.TypeId}: choose one model in Model Asset Library.");
            }

            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), item.Rotation.Pitch)));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), item.Rotation.Roll)));
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), item.Rotation.Yaw)));
            var placement = new TranslateTransform3D(item.Position.X, item.Position.Y, item.Position.Z);
            transform.Children.Add(placement);
            RegisterPreviewTransform(item.Id, placement);
            model.Transform = transform;
            var bounds = model.Bounds;
            if (!bounds.IsEmpty && double.IsFinite(bounds.SizeX) && double.IsFinite(bounds.SizeY) &&
                double.IsFinite(bounds.SizeZ) && double.IsFinite(bounds.X) &&
                double.IsFinite(bounds.Y) && double.IsFinite(bounds.Z))
            {
                _pieceWorldBounds[item.Id] = bounds;
                if (_sceneModelBounds.IsEmpty)
                    _sceneModelBounds = bounds;
                else
                    _sceneModelBounds.Union(bounds);
            }
            RegisterGeometry(model, item.Id, Matrix3D.Identity);
            scene.Children.Add(model);
            if (_selectedIds.Contains(item.Id))
                AddSelectionFrame(scene, item);
        }

        SceneViewport.Children.Clear();
        SceneViewport.Children.Add(new ModelVisual3D { Content = scene });
        SceneStatusText.Text = $"{_renderedMeshes} textured model(s) • {_placeholders} placeholder(s) • {_triangles:N0} triangles • {_elements.Count} blueprint element(s)";
        if (_elements.Count > MaximumPreviewElements)
            _warnings.Add($"Preview limit: only the first {MaximumPreviewElements} of {_elements.Count} elements are shown.");
        SceneWarningsText.Text = _warnings.Count == 0
            ? "Left-drag pieces onto the ground or another piece's top face to change floors automatically. Alt+drag sets height manually. Right-drag pans."
            : string.Join("  |  ", _warnings.Distinct().Take(5)) + (_warnings.Count > 5 ? $"  (+{_warnings.Count - 5} more warnings)" : "");
        ApplyDragPreview(_previewOffset);
        UpdateSelectedUi();
        UpdateCamera();
    }

    private void RegisterPreviewTransform(string id, TranslateTransform3D transform)
    {
        if (!_previewTransforms.TryGetValue(id, out var transforms))
            _previewTransforms[id] = transforms = [];
        transforms.Add((transform, transform.OffsetX, transform.OffsetY, transform.OffsetZ));
    }

    private void ApplyDragPreview(Vector3D offset)
    {
        _previewOffset = offset;
        foreach (var item in _dragElements)
        {
            if (!_previewTransforms.TryGetValue(item.Id, out var transforms)) continue;
            foreach (var (transform, x, y, z) in transforms)
            {
                transform.OffsetX = x + offset.X;
                transform.OffsetY = y + offset.Y;
                transform.OffsetZ = z + offset.Z;
            }
        }
        if (_draggingPieces && _dragElements.Count > 0)
            SelectedPieceText.Text = $"Drag n' drop  |  {_dragElements.Count} selected  |  X {offset.X:+0;-0;0}  Y {offset.Y:+0;-0;0}  Z {offset.Z:+0;-0;0} m  |  {_dropSurfaceDescription}  |  Release to place";
    }

    private void RegisterGeometry(Model3D model, string elementId, Matrix3D parentToWorld)
    {
        // WPF uses row-vector matrices: a child's local transformation comes
        // before its parent. Apply the same transform order used for rendering.
        var toWorld = model.Transform.Value;
        toWorld.Append(parentToWorld);
        if (model is GeometryModel3D geometry)
        {
            _geometryOwners[geometry] = elementId;
            _geometryToWorld[geometry] = toWorld;
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
                RegisterGeometry(child, elementId, toWorld);
        }
    }

    private void AddSelectionFrame(Model3DGroup scene, BlueprintElement item)
    {
        // Use the real rendered geometry extents when present. Unknown assets
        // are represented by the explicit 4m placeholder instead.
        var b = _pieceWorldBounds.TryGetValue(item.Id, out var measured)
            ? measured : new Rect3D(item.Position.X - 2, item.Position.Y, item.Position.Z - 2, 4, 4, 4);
        var material = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(144, 244, 72)));
        var x0 = b.X - 0.04; var x1 = b.X + Math.Max(b.SizeX, 0.08) + 0.04;
        var z0 = b.Z - 0.04; var z1 = b.Z + Math.Max(b.SizeZ, 0.08) + 0.04;
        var frame = new Model3DGroup();
        foreach (var h in new[] { b.Y + 0.04, b.Y + Math.Max(b.SizeY, 0.08) + 0.08 })
        {
            var lineWidth = Math.Max(0.018, Math.Min(0.06, Math.Min(x1 - x0, z1 - z0) / 16));
            frame.Children.Add(Quad(new Point3D(x0, h, z0), new Point3D(x1, h, z0),
                new Point3D(x1, h, z0 + lineWidth), new Point3D(x0, h, z0 + lineWidth), material));
            frame.Children.Add(Quad(new Point3D(x0, h, z1 - lineWidth), new Point3D(x1, h, z1 - lineWidth),
                new Point3D(x1, h, z1), new Point3D(x0, h, z1), material));
            frame.Children.Add(Quad(new Point3D(x0, h, z0), new Point3D(x0 + lineWidth, h, z0),
                new Point3D(x0 + lineWidth, h, z1), new Point3D(x0, h, z1), material));
            frame.Children.Add(Quad(new Point3D(x1 - lineWidth, h, z0), new Point3D(x1, h, z0),
                new Point3D(x1, h, z1), new Point3D(x1 - lineWidth, h, z1), material));
        }
        var offset = new TranslateTransform3D();
        frame.Transform = offset;
        RegisterPreviewTransform(item.Id, offset);
        scene.Children.Add(frame);
    }

    private void UpdateSelectedUi()
    {
        var selected = _elements.Where(x => _selectedIds.Contains(x.Id)).ToList();
        if (selected.Count == 0)
            SelectedPieceText.Text = "No pieces selected • Click a model or placeholder to select";
        else if (selected.Count == 1)
        {
            var item = selected[0];
            var dimensions = _pieceWorldBounds.TryGetValue(item.Id, out var b)
                ? $"  |  Rendered bounds: {b.SizeX:0.##} × {b.SizeY:0.##} × {b.SizeZ:0.##} GLB units"
                : "";
            SelectedPieceText.Text = $"Selected: {item.TypeId}  |  X {item.Position.X:0.##}  Y {item.Position.Y:0.##}  Z {item.Position.Z:0.##}" + dimensions;
        }
        else
            SelectedPieceText.Text = $"{selected.Count} pieces selected • Movements keep relative positions";
    }

    private void AddGroundGrid(Model3DGroup scene)
    {
        var minX = _elements.Count == 0 ? -16 : _elements.Min(x => x.Position.X) - 12;
        var maxX = _elements.Count == 0 ? 16 : _elements.Max(x => x.Position.X) + 12;
        var minZ = _elements.Count == 0 ? -16 : _elements.Min(x => x.Position.Z) - 12;
        var maxZ = _elements.Count == 0 ? 16 : _elements.Max(x => x.Position.Z) + 12;
        var left = Math.Floor(minX / 4) * 4;
        var right = Math.Ceiling(maxX / 4) * 4;
        var near = Math.Floor(minZ / 4) * 4;
        var far = Math.Ceiling(maxZ / 4) * 4;
        const double maxSpan = 240.0;
        var centerX = (left + right) / 2;
        var centerZ = (near + far) / 2;
        left = Math.Max(left, centerX - maxSpan / 2);
        right = Math.Min(right, centerX + maxSpan / 2);
        near = Math.Max(near, centerZ - maxSpan / 2);
        far = Math.Min(far, centerZ + maxSpan / 2);
        var gridBrush = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(47, 66, 49)));
        var axisBrush = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(105, 181, 70)));
        for (var x = Math.Ceiling(left / 4) * 4; x <= right; x += 4)
            scene.Children.Add(Quad(new Point3D(x - 0.025, -0.045, near), new Point3D(x + 0.025, -0.045, near),
                new Point3D(x + 0.025, -0.045, far), new Point3D(x - 0.025, -0.045, far), Math.Abs(x) < 0.01 ? axisBrush : gridBrush));
        for (var z = Math.Ceiling(near / 4) * 4; z <= far; z += 4)
            scene.Children.Add(Quad(new Point3D(left, -0.045, z - 0.025), new Point3D(right, -0.045, z - 0.025),
                new Point3D(right, -0.045, z + 0.025), new Point3D(left, -0.045, z + 0.025), Math.Abs(z) < 0.01 ? axisBrush : gridBrush));
    }

    private static GeometryModel3D Quad(Point3D a, Point3D b, Point3D c, Point3D d, Material material)
    {
        var mesh = new MeshGeometry3D();
        mesh.Positions.Add(a); mesh.Positions.Add(b); mesh.Positions.Add(c); mesh.Positions.Add(d);
        foreach (var index in new[] { 0, 1, 2, 0, 2, 3 }) mesh.TriangleIndices.Add(index);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    private static Model3DGroup MakePlaceholder()
    {
        // Deliberately artificial 4m block – NOT an inferred Enshrouded model.
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(98, 119, 78)));
        var group = new Model3DGroup();
        var b = new Point3D[]
        {
            new(-2, 0, -2), new(2, 0, -2), new(2, 0, 2), new(-2, 0, 2),
            new(-2, 4, -2), new(2, 4, -2), new(2, 4, 2), new(-2, 4, 2)
        };
        foreach (var (i, j, k, l) in new[] { (0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (3, 2, 6, 7), (1, 5, 6, 2), (0, 3, 7, 4) })
            group.Children.Add(Quad(b[i], b[j], b[k], b[l], material));
        return group;
    }

    private void FitScene()
    {
        _elements = _sourceElements.ToList();
        if (_elements.Count == 0) { _target = new Point3D(0, 0, 0); _distance = 36; UpdateCamera(); return; }
        var minX = _sceneModelBounds.IsEmpty ? _elements.Min(x => x.Position.X) : _sceneModelBounds.X;
        var maxX = _sceneModelBounds.IsEmpty ? _elements.Max(x => x.Position.X) : _sceneModelBounds.X + _sceneModelBounds.SizeX;
        var minY = _sceneModelBounds.IsEmpty ? _elements.Min(x => x.Position.Y) : _sceneModelBounds.Y;
        var maxY = _sceneModelBounds.IsEmpty ? _elements.Max(x => x.Position.Y) : _sceneModelBounds.Y + _sceneModelBounds.SizeY;
        var minZ = _sceneModelBounds.IsEmpty ? _elements.Min(x => x.Position.Z) : _sceneModelBounds.Z;
        var maxZ = _sceneModelBounds.IsEmpty ? _elements.Max(x => x.Position.Z) : _sceneModelBounds.Z + _sceneModelBounds.SizeZ;
        _target = new Point3D((minX + maxX) / 2, (minY + maxY) / 2 + 2, (minZ + maxZ) / 2);
        var diameter = Math.Sqrt(Math.Pow(maxX - minX + 8, 2) + Math.Pow(maxY - minY + 8, 2) + Math.Pow(maxZ - minZ + 8, 2));
        _distance = Math.Clamp(diameter * 1.7, 20, 20000);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var c = Math.Cos(_elevation);
        var p = new Point3D(_target.X + _distance * c * Math.Cos(_azimuth),
            _target.Y + _distance * Math.Sin(_elevation),
            _target.Z + _distance * c * Math.Sin(_azimuth));
        _camera.Position = p;
        _camera.LookDirection = _target - p;
        _camera.UpDirection = new Vector3D(0, 1, 0);
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right)) return;
        if (_orbiting || _panning || _dragCandidateId is not null) return;
        SceneSurface.Focus();
        _lastMouse = e.GetPosition(SceneSurface);
        _mouseDownPoint = _lastMouse;
        _mouseDragged = false;
        _previewOffset = new Vector3D();
        if (e.ChangedButton == MouseButton.Left)
        {
            // A hit starts a potential drag. Empty-space left drag orbits instead.
            _dragCandidateId = HitPieceId(e.GetPosition(SceneViewport));
            _orbiting = _dragCandidateId is null;
            _draggingHeight = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        }
        else
            _panning = true;
        SceneSurface.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_orbiting && !_panning && _dragCandidateId is null) return;
        var point = e.GetPosition(SceneSurface);
        var delta = point - _lastMouse;
        _lastMouse = point;
        if ((point - _mouseDownPoint).Length < 4 && !_mouseDragged) return;
        _mouseDragged = true;
        if (_dragCandidateId is not null)
        {
            if (!_draggingPieces && !StartPieceDrag()) return;
            var offset = new Vector3D();
            if (_draggingHeight)
            {
                // Explicit precision fallback for floating pieces and arbitrary elevations.
                var viewportWidth = Math.Max(1, SceneViewport.ActualWidth);
                var unitsPerPixel = 2 * _distance * Math.Tan(_camera.FieldOfView * Math.PI / 360) / viewportWidth;
                offset.Y = SnapToGrid((_mouseDownPoint.Y - point.Y) * unitsPerPixel);
                _dropSurfaceDescription = "manual height";
            }
            else
            {
                var cursor = e.GetPosition(SceneViewport);
                // The original implementation locked the cursor ray to the
                // grabbed piece's Y. Instead find a visible upward-facing piece
                // underneath the cursor, or use the ground plane (Y=0).
                var onPiece = TryFindDropSurface(cursor, out var targetSurfaceY);
                var targetY = SnapToGrid(targetSurfaceY);
                if (!TryScreenToHorizontalPlane(cursor, targetY, out var world))
                    return;
                offset.X = SnapToGrid(world.X - _dragWorldStart.X);
                offset.Y = SnapToGrid(targetY - _dragPlaneHeight);
                offset.Z = SnapToGrid(world.Z - _dragWorldStart.Z);
                _dropSurfaceDescription = $"{(onPiece ? "top surface" : "ground")} Y {targetY:0.##} m";
            }
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || !double.IsFinite(offset.Z) ||
                Math.Abs(offset.X) > 1_000_000 || Math.Abs(offset.Y) > 1_000_000 || Math.Abs(offset.Z) > 1_000_000)
                return;
            if (offset != _previewOffset)
                ApplyDragPreview(offset);
            e.Handled = true;
            return;
        }
        if (_orbiting)
        {
            _azimuth -= delta.X * 0.008;
            _elevation = Math.Clamp(_elevation + delta.Y * 0.008, -1.45, 1.45);
        }
        else
        {
            var look = _camera.LookDirection;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 1, 0));
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            var units = _distance * 0.0013;
            _target -= (right * delta.X + up * -delta.Y) * units;
        }
        UpdateCamera();
        e.Handled = true;
    }

    private bool StartPieceDrag()
    {
        var candidate = _elements.FirstOrDefault(x => x.Id.Equals(_dragCandidateId, StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return false;
        _dragPlaneHeight = candidate.Position.Y;
        if (!_selectedIds.Contains(candidate.Id))
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None)
                _selectedIds.Clear();
            _selectedIds.Add(candidate.Id);
        }
        _dragElements = _elements.Where(x => _selectedIds.Contains(x.Id)).ToList();
        _draggingPieces = true;
        _selectInEditor(_dragElements);
        ReloadScene(); // Add selected outlines and register their preview transforms.
        if (!_draggingHeight)
        {
            // Record the initial cursor position on its actual support plane,
            // not the selected piece's height. This prevents an X/Z jump when
            // dragging a piece off a stack and onto the ground (or vice versa).
            TryFindDropSurface(_mouseDownPoint, out var startSurfaceY);
            if (!TryScreenToHorizontalPlane(_mouseDownPoint, SnapToGrid(startSurfaceY), out _dragWorldStart))
            {
                SceneWarningsText.Text = "The ground is not visible from this camera angle. Tilt the camera down and retry.";
                _draggingPieces = false;
                _dragElements.Clear();
                ReloadScene();
                return false;
            }
        }
        _dropSurfaceDescription = _draggingHeight ? "manual height" : "surface snapping";
        SceneSurface.Cursor = Cursors.SizeAll;
        return true;
    }

    private static double SnapToGrid(double offset) =>
        Math.Round(offset / GridStep, MidpointRounding.AwayFromZero) * GridStep;

    private bool TryScreenToHorizontalPlane(Point screen, double planeY, out Point3D world)
    {
        world = default;
        var width = SceneViewport.ActualWidth;
        var height = SceneViewport.ActualHeight;
        if (width <= 0 || height <= 0) return false;
        var forward = _camera.LookDirection;
        forward.Normalize();
        var right = Vector3D.CrossProduct(forward, _camera.UpDirection);
        if (right.LengthSquared < 1e-12) return false;
        right.Normalize();
        var up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        // WPF's PerspectiveCamera.FieldOfView is horizontal, not vertical.
        var halfWidth = Math.Tan(_camera.FieldOfView * Math.PI / 360);
        var halfHeight = halfWidth * (height / width);
        var dir = forward + right * ((2 * screen.X / width - 1) * halfWidth)
                    + up * ((1 - 2 * screen.Y / height) * halfHeight);
        if (Math.Abs(dir.Y) < 1e-7) return false;
        var distance = (planeY - _camera.Position.Y) / dir.Y;
        if (distance <= 0 || !double.IsFinite(distance)) return false;
        world = _camera.Position + dir * distance;
        return double.IsFinite(world.X) && double.IsFinite(world.Y) && double.IsFinite(world.Z);
    }

    /// <summary>
    /// Find the highest visible *supporting* surface underneath the cursor.
    /// Selected/dragged geometry is intentionally ignored so the piece cannot
    /// land on itself or on another piece in its moving group. The scene's grid
    /// has no geometry ownership and does not compete with a building surface.
    /// With no support hit, the placement plane is ground level (Y=0).
    /// </summary>
    private bool TryFindDropSurface(Point screen, out double surfaceY)
    {
        var pickedY = 0.0;
        var found = false;
        VisualTreeHelper.HitTest(SceneViewport, null, result =>
        {
            if (result is not RayMeshGeometry3DHitTestResult ray ||
                ray.ModelHit is not GeometryModel3D geometry ||
                !_geometryOwners.TryGetValue(geometry, out var id) ||
                _dragElements.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                !_geometryToWorld.TryGetValue(geometry, out var toWorld))
                return HitTestResultBehavior.Continue;

            var mesh = ray.MeshHit;
            if (ray.VertexIndex1 < 0 || ray.VertexIndex2 < 0 || ray.VertexIndex3 < 0 ||
                ray.VertexIndex1 >= mesh.Positions.Count ||
                ray.VertexIndex2 >= mesh.Positions.Count ||
                ray.VertexIndex3 >= mesh.Positions.Count)
                return HitTestResultBehavior.Continue;

            var a = toWorld.Transform(mesh.Positions[ray.VertexIndex1]);
            var b = toWorld.Transform(mesh.Positions[ray.VertexIndex2]);
            var c = toWorld.Transform(mesh.Positions[ray.VertexIndex3]);
            var normal = Vector3D.CrossProduct(b - a, c - a);
            // Side faces must not become placement surfaces. Some imported
            // GLB models have reversed winding, so accept either orientation
            // for near-horizontal surfaces (but still require a visible hit).
            if (normal.LengthSquared < 1e-12 ||
                Math.Abs(normal.Y) < 0.65 * normal.Length)
                return HitTestResultBehavior.Continue;

            pickedY = ray.PointHit.Y;
            found = double.IsFinite(pickedY);
            return found ? HitTestResultBehavior.Stop : HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(screen));
        surfaceY = found ? pickedY : 0.0;
        return found;
    }

    private string? HitPieceId(Point point)
    {
        string? hitId = null;
        VisualTreeHelper.HitTest(SceneViewport, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult ray &&
                ray.ModelHit is GeometryModel3D mesh &&
                _geometryOwners.TryGetValue(mesh, out var id))
            {
                hitId = id;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        return hitId;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _dragCandidateId is not null)
        {
            var wasDragging = _draggingPieces;
            var selectedToMove = _dragElements.ToArray();
            var offset = _previewOffset;
            _dragCandidateId = null;
            _draggingPieces = false;
            _draggingHeight = false;
            _dragElements.Clear();
            _previewOffset = new Vector3D();
            SceneSurface.Cursor = Cursors.Arrow;
            SceneSurface.ReleaseMouseCapture();
            if (wasDragging)
            {
                try
                {
                    // Commit exactly once. The parent editor pushes one undo snapshot.
                    if (offset.LengthSquared > 0)
                        _moveInEditor(selectedToMove, offset.X, offset.Y, offset.Z);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    AppDiagnostics.WriteException("Drag-and-drop movement in 3D", ex);
                    MessageBox.Show(Window.GetWindow(this), ex.Message, "3D drag failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally { ReloadScene(); }
            }
            else if (!_mouseDragged)
                SelectAtScreenPoint(e.GetPosition(SceneViewport), Keyboard.Modifiers);
            else
                ReloadScene(); // Aborted drag: discard preview and leave blueprint untouched.
            e.Handled = true;
            return;
        }
        var wasClick = e.ChangedButton == MouseButton.Left && _orbiting && !_mouseDragged;
        if (e.ChangedButton == MouseButton.Left) _orbiting = false;
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) _panning = false;
        if (!_orbiting && !_panning) SceneSurface.ReleaseMouseCapture();
        if (wasClick) SelectAtScreenPoint(e.GetPosition(SceneViewport), Keyboard.Modifiers);
        e.Handled = true;
    }

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _orbiting = false;
        _panning = false;
        _dragCandidateId = null;
        _draggingHeight = false;
        _draggingPieces = false;
        _dragElements.Clear();
        SceneSurface.Cursor = Cursors.Arrow;
        if (_previewOffset.LengthSquared > 0)
        {
            _previewOffset = new Vector3D();
            ReloadScene(); // Lost capture cancels, not commits, a drag.
        }
    }

    private void SelectAtScreenPoint(Point point, ModifierKeys modifiers)
    {
        var hitId = HitPieceId(point);
        var additive = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != ModifierKeys.None;
        if (hitId is null)
        {
            if (!additive) _selectedIds.Clear();
        }
        else if (additive)
        {
            if (!_selectedIds.Add(hitId)) _selectedIds.Remove(hitId);
        }
        else
        {
            _selectedIds.Clear();
            _selectedIds.Add(hitId);
        }
        var selected = _elements.Where(item => _selectedIds.Contains(item.Id)).ToList();
        _selectInEditor(selected);
        ReloadScene();
    }

    private void CancelActiveDrag()
    {
        _dragCandidateId = null;
        _draggingPieces = false;
        _draggingHeight = false;
        _dragElements.Clear();
        _previewOffset = new Vector3D();
        _orbiting = false;
        _panning = false;
        SceneSurface.Cursor = Cursors.Arrow;
        SceneSurface.ReleaseMouseCapture();
        ReloadScene();
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            if (_dragCandidateId is not null)
            {
                CancelActiveDrag(); // Do not undo an earlier completed operation while previewing a drag.
                e.Handled = true;
                return;
            }
            _undoInEditor();
            ReloadScene();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_dragCandidateId is not null)
            {
                CancelActiveDrag(); // Escape aborts the pending drop without saving it.
                e.Handled = true;
                return;
            }
            _selectedIds.Clear();
            _selectInEditor([]);
            ReloadScene();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var direction = e.Key switch
        {
            Key.Right => "X+", Key.Left => "X-", Key.Up => "Z-", Key.Down => "Z+",
            Key.PageUp => "Y+", Key.PageDown => "Y-", _ => null
        };
        if (direction is null) return;
        var (x, y, z) = direction switch
        {
            "X+" => (GridStep, 0.0, 0.0), "X-" => (-GridStep, 0.0, 0.0),
            "Y+" => (0.0, GridStep, 0.0), "Y-" => (0.0, -GridStep, 0.0),
            "Z+" => (0.0, 0.0, GridStep), "Z-" => (0.0, 0.0, -GridStep),
            _ => (0.0, 0.0, 0.0)
        };
        var selected = _elements.Where(item => _selectedIds.Contains(item.Id)).ToList();
        if (selected.Count > 0)
        {
            try { _moveInEditor(selected, x, y, z); ReloadScene(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                AppDiagnostics.WriteException("Keyboard movement in 3D", ex);
                MessageBox.Show(Window.GetWindow(this), ex.Message, "3D move failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        e.Handled = true;
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Zoom(e.Delta > 0 ? 0.86 : 1.16);
        e.Handled = true;
    }

    private void Zoom(double multiplier)
    {
        _distance = Math.Clamp(_distance * multiplier, 2, 30000);
        UpdateCamera();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try { ReloadScene(reloadAssets: true); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            AppDiagnostics.WriteException("Reloading 3D preview", ex);
            SceneWarningsText.Text = "Unable to reload 3D scene: " + ex.Message +
                "  |  Log: " + AppDiagnostics.LogPath;
        }
    }
    public void FitToScene() => FitScene();
    public void ZoomIn() => Zoom(0.8);
    public void ZoomOut() => Zoom(1.25);

    public void RefreshFromEditor(IEnumerable<BlueprintElement> selection, bool reloadAssets = false)
    {
        _selectedIds.Clear();
        foreach (var item in selection)
            _selectedIds.Add(item.Id);
        ReloadScene(reloadAssets);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitScene();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(0.8);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
}
