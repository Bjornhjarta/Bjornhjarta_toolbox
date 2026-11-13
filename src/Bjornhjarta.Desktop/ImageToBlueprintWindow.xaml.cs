using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Bjornhjarta.Core.Blueprints;
using Microsoft.Win32;

namespace Bjornhjarta.Desktop;

public partial class ImageToBlueprintWindow : Window
{
    public sealed class PaletteOption
    {
        public required PlaceableCatalogEntry Entry { get; init; }
        private bool _selected;
        public Action? OnChanged { get; init; }
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnChanged?.Invoke();
            }
        }
        public string Detail => $"{Entry.Category} · {Entry.TypeId} · {Entry.StatusLabel}";
    }

    private string? _filePath;
    private byte[]? _grayscale;
    private int _imageWidth, _imageHeight;
    private readonly double _originX, _originY, _originZ;
    private ImageBlueprintGenerator.Result? _preview;
    private ImageBlueprintGenerator.Options? _lastOptions;
    private readonly Dictionary<(int X, int Z, int Side), ImageBlueprintGenerator.OpeningKind> _openingOverrides = new();
    private readonly HashSet<(int X, int Z, int Side)> _addedWalls = [];
    private readonly HashSet<(int X, int Z, int Side)> _removedWalls = [];
    private const double PreviewCell = 16;
    public ObservableCollection<PaletteOption> PaletteEntries { get; } = [];
    public IReadOnlyList<BlueprintElement> GeneratedPieces => _preview?.Elements ?? [];

    public ImageToBlueprintWindow(double originX, double originY, double originZ)
    {
        _originX = originX;
        _originY = originY;
        _originZ = originZ;
        InitializeComponent();
        DataContext = this;
        AnchorText.Text = $"Placement origin: ({originX:0}, {originY:0}, {originZ:0}) m (selected piece, or world origin)";
        LoadPalette();
        ModeCombo.SelectionChanged += (_, _) => InvalidatePreview();
        WidthBox.TextChanged += (_, _) => InvalidatePreview();
        DepthBox.TextChanged += (_, _) => InvalidatePreview();
        LayersBox.TextChanged += (_, _) => InvalidatePreview();
        AutoSizeCheck.Checked += (_, _) => UpdateSizingControls();
        AutoSizeCheck.Unchecked += (_, _) => UpdateSizingControls();
        ReferenceWidthBox.TextChanged += (_, _) => InvalidatePreview();
        PixelsPerCellBox.TextChanged += (_, _) => InvalidatePreview();
        UpdateSizingControls();
    }

    private void UpdateSizingControls()
    {
        bool auto = AutoSizeCheck.IsChecked == true;
        WidthBox.IsEnabled = !auto;
        DepthBox.IsEnabled = !auto;
        ReferenceWidthBox.IsEnabled = auto;
        PixelsPerCellBox.IsEnabled = auto;
        InvalidatePreview();
    }

    private void LoadPalette()
    {
        try
        {
            var defaults = PlaceablesCatalogStore.ExampleEntries();
            var custom = new PlaceablesCatalogStore().Load();
            var choices = custom.Concat(defaults)
                .Where(x => x.Category is "Foundations" or "Walls" or "Roofs" or "Doors & Windows")
                .GroupBy(x => x.TypeId, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Category == "Foundations" ? 0 : x.Category == "Walls" ? 1 : x.Category == "Roofs" ? 2 : 3)
                .ThenBy(x => x.Name)
                .ToArray();
            foreach (var piece in choices)
                PaletteEntries.Add(new PaletteOption { Entry = piece, OnChanged = InvalidatePreview });
            foreach (var category in new[] { "Foundations", "Walls", "Roofs" })
            {
                var preferred = PaletteEntries.FirstOrDefault(x => x.Entry.Category == category && !x.Entry.IsExample)
                    ?? PaletteEntries.FirstOrDefault(x => x.Entry.Category == category);
                if (preferred is not null) preferred.Selected = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            // A corrupted optional user catalog must not prevent image import.
            PaletteEntries.Clear();
            foreach (var piece in PlaceablesCatalogStore.ExampleEntries().Where(x => x.Category is "Foundations" or "Walls" or "Roofs" or "Doors & Windows"))
                PaletteEntries.Add(new PaletteOption { Entry = piece, OnChanged = InvalidatePreview, Selected = piece.TypeId is "example.foundation" or "example.wall" or "example.roof" });
            PreviewText.Text = $"Custom catalog unavailable ({ex.Message}). Showing clearly marked examples only.";
        }
    }

    private void LoadImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a local 2D plan or 3D reference image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 15L * 1024 * 1024)
                throw new InvalidDataException("Images larger than 15 MB are not supported in the preview.");
            BitmapImage bitmap;
            using (var stream = File.OpenRead(dialog.FileName))
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 480;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            if ((long)converted.PixelWidth * converted.PixelHeight > 1_500_000)
                throw new InvalidDataException("Decoded image is too large for tracing.");
            var stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            var gray = new byte[checked(converted.PixelWidth * converted.PixelHeight)];
            for (var i = 0; i < gray.Length; i++)
            {
                var p = i * 4;
                var alpha = pixels[p + 3];
                var luminance = (54 * pixels[p + 2] + 183 * pixels[p + 1] + 19 * pixels[p]) / 256;
                gray[i] = (byte)((luminance * alpha + 255 * (255 - alpha)) / 255);
            }
            _openingOverrides.Clear();
            _addedWalls.Clear();
            _removedWalls.Clear();
            _filePath = dialog.FileName;
            _imageWidth = converted.PixelWidth;
            _imageHeight = converted.PixelHeight;
            _grayscale = gray;
            ReferenceImage.Source = bitmap;
            FileText.Text = System.IO.Path.GetFileName(_filePath) + $" · {_imageWidth} × {_imageHeight} decoded pixels";
            InvalidatePreview();
            PreviewText.Text = "Image loaded. Auto sizing uses image content, not the current editor grid. Generate a preview; review scale and shape before inserting.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        {
            MessageBox.Show(this, $"Cannot read the image: {ex.Message}", "Image import", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_grayscale is null)
        {
            MessageBox.Show(this, "Load a reference image first.", "Image to Blueprint", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!int.TryParse(WidthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var width) ||
            !int.TryParse(DepthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var depth) ||
            !int.TryParse(LayersBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var layers))
        {
            MessageBox.Show(this, "Enter whole numbers for wall layers (and width/depth in manual mode).", "Invalid dimensions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        double referenceWidth = 0;
        if (!string.IsNullOrWhiteSpace(ReferenceWidthBox.Text) &&
            (!double.TryParse(ReferenceWidthBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out referenceWidth) || referenceWidth <= 0))
        {
            MessageBox.Show(this, "Known real width must be a positive number of metres, or left blank.", "Invalid reference size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PixelsPerCellBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int pixelsPerCell))
        {
            MessageBox.Show(this, "Enter a whole number for pixels per 4m grid cell.", "Invalid scale", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var options = new ImageBlueprintGenerator.Options
            {
                WidthCells = width, DepthCells = depth, WallLayers = layers,
                AutoSizeFromImage = AutoSizeCheck.IsChecked == true,
                ReferenceWidthMetres = referenceWidth, ApproxPixelsPerCell = pixelsPerCell,
                TraceFloorPlan = ModeCombo.SelectedIndex == 0,
                OriginX = _originX, OriginY = _originY, OriginZ = _originZ,
                AllowedPieces = PaletteEntries.Where(x => x.Selected).Select(x => x.Entry).ToArray(),
                Openings = _openingOverrides.Select(p => new ImageBlueprintGenerator.OpeningSelection(
                    p.Key.X, p.Key.Z, p.Key.Side, p.Value)).ToArray(),
                AddedInteriorWalls = WallEdits(_addedWalls),
                RemovedInteriorWalls = WallEdits(_removedWalls)
            };
            var generated = ImageBlueprintGenerator.Generate(_grayscale, _imageWidth, _imageHeight, options);
            // Assign auto-detected dimensions BEFORE saving the preview because
            // TextChanged invalidates it.
            if (options.AutoSizeFromImage)
            {
                var autoWidth = generated.WidthCells.ToString(CultureInfo.CurrentCulture);
                var autoDepth = generated.DepthCells.ToString(CultureInfo.CurrentCulture);
                if (WidthBox.Text != autoWidth) WidthBox.Text = autoWidth;
                if (DepthBox.Text != autoDepth) DepthBox.Text = autoDepth;
            }
            _lastOptions = options;
            ApplyPreview(generated);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Could not generate blueprint", MessageBoxButton.OK, MessageBoxImage.Warning);
            InvalidatePreview();
        }
    }

    private static ImageBlueprintGenerator.PlanWallEdge[] WallEdits(IEnumerable<(int X, int Z, int Side)> source) =>
        source.Select(k => new ImageBlueprintGenerator.PlanWallEdge(k.X, k.Z, k.Side, true)).ToArray();

    private void ApplyPreview(ImageBlueprintGenerator.Result generated)
    {
        _preview = generated;
        DrawFootprint(generated);
        PreviewText.Text = $"{generated.Elements.Count} elements • {generated.Foundations} foundations • {generated.Walls} wall pieces • {generated.InteriorWalls} interior seams • {generated.Doors} doors • {generated.Windows} windows • {generated.Roofs} roof pieces.\n{generated.Explanation}\n\nThe yellow/amber segments need review. Toggle interior walls to correct missed or spurious partitions before inserting.";
        InsertButton.IsEnabled = true;
    }

    private void FootprintCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_preview is null || _grayscale is null || _lastOptions is null || !_lastOptions.TraceFloorPlan)
        {
            PreviewText.Text = "Generate a 2D floor plan preview before painting openings.";
            return;
        }
        var pos = e.GetPosition(FootprintCanvas);
        if (OpeningToolCombo.SelectedIndex == 3)
        {
            ToggleInteriorWall(pos);
            e.Handled = true;
            return;
        }
        // Hit-test ONLY wall segments drawn in the current preview. This lets
        // users place doors and windows on detected interior partitions as well
        // as perimeter edges, but never on empty foundation cell boundaries.
        const double EdgeHitTolerance = 5.0;
        var candidateEdges = _preview.WallEdges
            .Where(edge => Math.Abs(edge.X * PreviewCell - pos.X) <= PreviewCell * 2 &&
                           Math.Abs(edge.Z * PreviewCell - pos.Y) <= PreviewCell * 2)
            .Select(edge =>
            {
                double x1 = (edge.Side == 0 ? edge.X + 1 : edge.X) * PreviewCell;
                double y1 = (edge.Side == 2 ? edge.Z + 1 : edge.Z) * PreviewCell;
                double x2 = x1 + (edge.Side >= 2 ? PreviewCell : 0);
                double y2 = y1 + (edge.Side <= 1 ? PreviewCell : 0);
                double dx = x2 - x1, dy = y2 - y1;
                double t = Math.Clamp(((pos.X - x1) * dx + (pos.Y - y1) * dy) /
                                      (dx * dx + dy * dy), 0, 1);
                double distance = Math.Sqrt(Math.Pow(pos.X - (x1 + t * dx), 2) +
                                            Math.Pow(pos.Y - (y1 + t * dy), 2));
                return (Edge: edge, Distance: distance);
            })
            .Where(hit => hit.Distance <= EdgeHitTolerance)
            .OrderBy(hit => hit.Distance)
            .ThenBy(hit => hit.Edge.Interior ? 1 : 0)
            .ToArray();
        if (candidateEdges.Length == 0)
        {
            PreviewText.Text = "Click directly on a visible exterior or interior wall line (within 5 px) to paint an opening. Other clicks do nothing.";
            return;
        }
        var selectedEdge = candidateEdges[0].Edge;
        var x = selectedEdge.X;
        var z = selectedEdge.Z;
        var side = selectedEdge.Side;
        var key = (x, z, side);
        bool hadOld = _openingOverrides.TryGetValue(key, out var old);
        if (OpeningToolCombo.SelectedIndex == 2)
            _openingOverrides.Remove(key);
        else
            _openingOverrides[key] = OpeningToolCombo.SelectedIndex == 1
                ? ImageBlueprintGenerator.OpeningKind.Window
                : ImageBlueprintGenerator.OpeningKind.Door;
        try
        {
            _lastOptions.Openings = _openingOverrides.Select(p => new ImageBlueprintGenerator.OpeningSelection(
                p.Key.X, p.Key.Z, p.Key.Side, p.Value)).ToArray();
            ApplyPreview(ImageBlueprintGenerator.Generate(_grayscale, _imageWidth, _imageHeight, _lastOptions));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            if (hadOld) _openingOverrides[key] = old; else _openingOverrides.Remove(key);
            _lastOptions.Openings = _openingOverrides.Select(p => new ImageBlueprintGenerator.OpeningSelection(
                p.Key.X, p.Key.Z, p.Key.Side, p.Value)).ToArray();
            MessageBox.Show(this, ex.Message, "Opening placement", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        e.Handled = true;
    }

    // A deliberate user click can add a missed interior wall or suppress a
    // spurious one. Only shared edges of occupied cells are eligible.
    private void ToggleInteriorWall(System.Windows.Point pos)
    {
        if (_preview is null || _lastOptions is null || _grayscale is null) return;
        const double maxDistance = 5;
        var footprint = _preview.Footprint;
        int width = footprint.GetLength(0), depth = footprint.GetLength(1);
        var candidates = new List<(int X, int Z, int Side, double Distance)>();
        int approxX = (int)(pos.X / PreviewCell), approxZ = (int)(pos.Y / PreviewCell);
        for (int z = Math.Max(0, approxZ - 1); z <= Math.Min(depth - 1, approxZ + 1); z++)
        for (int x = Math.Max(0, approxX - 1); x <= Math.Min(width - 1, approxX + 1); x++)
        {
            if (!footprint[x, z]) continue;
            if (x + 1 < width && footprint[x + 1, z])
                candidates.Add((x, z, 0, Math.Sqrt(Math.Pow(pos.X - (x + 1) * PreviewCell, 2) +
                    Math.Pow(Math.Clamp(pos.Y, z * PreviewCell, (z + 1) * PreviewCell) - pos.Y, 2))));
            if (z + 1 < depth && footprint[x, z + 1])
                candidates.Add((x, z, 2, Math.Sqrt(Math.Pow(Math.Clamp(pos.X, x * PreviewCell, (x + 1) * PreviewCell) - pos.X, 2) +
                    Math.Pow(pos.Y - (z + 1) * PreviewCell, 2))));
        }
        var target = candidates.Where(c => c.Distance <= maxDistance).OrderBy(c => c.Distance).FirstOrDefault();
        if (target.Distance > maxDistance || candidates.Count == 0 || !candidates.Any(c => c.Distance <= maxDistance))
        {
            PreviewText.Text = "Click close to a seam shared by two occupied floor cells to add/remove a partition.";
            return;
        }
        var key = (target.X, target.Z, target.Side);
        var autoDetected = _preview.AutoDetectedInteriorWalls.Any(w => w.X == key.X && w.Z == key.Z && w.Side == key.Side);
        var hadAdded = _addedWalls.Contains(key);
        var hadRemoved = _removedWalls.Contains(key);
        var hadOpening = _openingOverrides.TryGetValue(key, out var existingOpening);
        if (hadAdded) _addedWalls.Remove(key);
        else if (hadRemoved) _removedWalls.Remove(key);
        else if (autoDetected) _removedWalls.Add(key);
        else _addedWalls.Add(key);

        // Removing a wall also removes its door/window annotation. This is a
        // change to the preview only; the blueprint remains untouched.
        if (hadAdded || (!hadRemoved && autoDetected)) _openingOverrides.Remove(key);
        _lastOptions.AddedInteriorWalls = WallEdits(_addedWalls);
        _lastOptions.RemovedInteriorWalls = WallEdits(_removedWalls);
        _lastOptions.Openings = _openingOverrides.Select(p => new ImageBlueprintGenerator.OpeningSelection(
            p.Key.X, p.Key.Z, p.Key.Side, p.Value)).ToArray();
        try
        {
            ApplyPreview(ImageBlueprintGenerator.Generate(_grayscale, _imageWidth, _imageHeight, _lastOptions));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            if (hadAdded) _addedWalls.Add(key); else _addedWalls.Remove(key);
            if (hadRemoved) _removedWalls.Add(key); else _removedWalls.Remove(key);
            if (hadOpening) _openingOverrides[key] = existingOpening; else _openingOverrides.Remove(key);
            _lastOptions.AddedInteriorWalls = WallEdits(_addedWalls);
            _lastOptions.RemovedInteriorWalls = WallEdits(_removedWalls);
            _lastOptions.Openings = _openingOverrides.Select(p => new ImageBlueprintGenerator.OpeningSelection(
                p.Key.X, p.Key.Z, p.Key.Side, p.Value)).ToArray();
            MessageBox.Show(this, ex.Message, "Interior wall edit", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Line MakeEdgeLine(int x, int z, int side, Brush brush, double thickness)
    {
        double x1 = (side == 0 ? x + 1 : x) * PreviewCell;
        double y1 = (side == 2 ? z + 1 : z) * PreviewCell;
        var line = new Line
        {
            X1 = x1, Y1 = y1,
            X2 = x1 + (side >= 2 ? PreviewCell : 0),
            Y2 = y1 + (side <= 1 ? PreviewCell : 0),
            Stroke = brush, StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        return line;
    }

    private void DrawFootprint(ImageBlueprintGenerator.Result generated)
    {
        FootprintCanvas.Children.Clear();
        var footprint = generated.Footprint;
        var width = footprint.GetLength(0);
        var depth = footprint.GetLength(1);
        const double cell = PreviewCell;
        FootprintCanvas.Width = width * cell;
        FootprintCanvas.Height = depth * cell;
        for (var z = 0; z < depth; z++)
        for (var x = 0; x < width; x++)
        {
            var inside = footprint[x, z];
            var edge = inside && (x == 0 || x == width - 1 || z == 0 || z == depth - 1 ||
                !footprint[Math.Max(0, x - 1), z] || !footprint[Math.Min(width - 1, x + 1), z] ||
                !footprint[x, Math.Max(0, z - 1)] || !footprint[x, Math.Min(depth - 1, z + 1)]);
            var rect = new Rectangle
            {
                Width = cell, Height = cell,
                Fill = inside ? new SolidColorBrush(Color.FromRgb(46, 97, 54)) : new SolidColorBrush(Color.FromRgb(19, 29, 21)),
                Stroke = edge ? new SolidColorBrush(Color.FromRgb(230, 197, 129)) : new SolidColorBrush(Color.FromRgb(54, 70, 56)),
                StrokeThickness = edge ? 1.6 : 0.5
            };
            Canvas.SetLeft(rect, x * cell);
            Canvas.SetTop(rect, z * cell);
            FootprintCanvas.Children.Add(rect);
        }
        var normalBrush = new SolidColorBrush(Color.FromRgb(235, 224, 199));
        var reviewBrush = new SolidColorBrush(Color.FromRgb(240, 177, 78));
        var manualBrush = new SolidColorBrush(Color.FromRgb(83, 207, 186));
        var removedBrush = new SolidColorBrush(Color.FromRgb(173, 88, 89));
        var evidence = generated.InteriorWallEvidence.ToDictionary(e => (e.Edge.X, e.Edge.Z, e.Edge.Side));
        foreach (var edge in generated.WallEdges)
        {
            Brush brush = normalBrush;
            if (edge.Interior && evidence.TryGetValue((edge.X, edge.Z, edge.Side), out var proof))
                brush = proof.UserAdded ? manualBrush : proof.NeedsReview ? reviewBrush : normalBrush;
            FootprintCanvas.Children.Add(MakeEdgeLine(edge.X, edge.Z, edge.Side, brush,
                edge.Interior ? 2.3 : 2.7));
        }
        foreach (var removed in generated.RemovedInteriorWalls)
        {
            var line = MakeEdgeLine(removed.X, removed.Z, removed.Side, removedBrush, 2.5);
            line.StrokeDashArray = new DoubleCollection { 2, 2 };
            FootprintCanvas.Children.Add(line);
        }
        foreach (var opening in _openingOverrides)
        {
            var brush = opening.Value == ImageBlueprintGenerator.OpeningKind.Door
                ? new SolidColorBrush(Color.FromRgb(227, 70, 74))
                : new SolidColorBrush(Color.FromRgb(88, 187, 255));
            FootprintCanvas.Children.Add(MakeEdgeLine(opening.Key.X, opening.Key.Z,
                opening.Key.Side, brush, 5));
        }
    }

    private void InvalidatePreview()
    {
        _preview = null;
        _lastOptions = null;
        _openingOverrides.Clear();
        _addedWalls.Clear();
        _removedWalls.Clear();
        InsertButton.IsEnabled = false;
        FootprintCanvas.Children.Clear();
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        if (_preview.Elements.Count > 350)
        {
            var response = MessageBox.Show(this,
                $"This preview contains {_preview.Elements.Count} pieces. The current 3D editor displays at most 350 at once. Insert anyway?",
                "Large blueprint", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
