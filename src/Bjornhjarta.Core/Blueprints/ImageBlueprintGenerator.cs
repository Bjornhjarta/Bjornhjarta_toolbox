namespace Bjornhjarta.Core.Blueprints;

/// <summary>
/// Local, deterministic raster tracing. This is NOT a 3D reconstruction AI:
/// a single photo cannot reveal hidden walls, true world scale or materials.
/// Never silently substitute a rectangular building when shape extraction fails.
/// </summary>
public static class ImageBlueprintGenerator
{
    public const double GridMetres = 4;
    private const int MaxCellsPerSide = 160;
    private const int MaxPieces = 10000;

    // 2D plan perimeter annotations are editable guesses, not image-recognition claims.
    public enum OpeningKind { Door, Window }
    public readonly record struct OpeningSelection(int X, int Z, int Side, OpeningKind Kind);
    public readonly record struct PlanWallEdge(int X, int Z, int Side, bool Interior);
    /// <summary>Evidence for an interior wall based on seven pixel samples. An estimate is not a verified game wall.</summary>
    public readonly record struct InteriorWallEvidence(PlanWallEdge Edge, int InkSamples, bool UserAdded)
    {
        public bool NeedsReview => !UserAdded && InkSamples < 7;
    }

    public sealed class Options
    {
        public int WidthCells { get; set; } = 8;
        public int DepthCells { get; set; } = 8;
        public int WallLayers { get; set; } = 2;
        public bool TraceFloorPlan { get; set; } = true;
        public bool AutoSizeFromImage { get; set; } = true;
        /// <summary>0 means unknown; a positive value is a USER-SUPPLIED physical width.</summary>
        public double ReferenceWidthMetres { get; set; }
        /// <summary>Fallback scale when the drawing has no stated dimensions.</summary>
        public int ApproxPixelsPerCell { get; set; } = 24;
        public bool TraceInteriorWalls { get; set; } = true;
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double OriginZ { get; set; }
        public IReadOnlyList<PlaceableCatalogEntry> AllowedPieces { get; set; } = [];
        /// <summary>User-marked openings on detected exterior OR interior wall edges: 0 right, 1 left, 2 bottom, 3 top.</summary>
        public IReadOnlyList<OpeningSelection> Openings { get; set; } = [];
        /// <summary>Explicit user edits; only interior cell seams (Side=0 or Side=2) are allowed.</summary>
        public IReadOnlyList<PlanWallEdge> AddedInteriorWalls { get; set; } = [];
        public IReadOnlyList<PlanWallEdge> RemovedInteriorWalls { get; set; } = [];
    }

    public sealed class Result
    {
        public required bool[,] Footprint { get; init; }
        public required IReadOnlyList<BlueprintElement> Elements { get; init; }
        public required string Explanation { get; init; }
        public required int WidthCells { get; init; }
        public required int DepthCells { get; init; }
        public int Foundations { get; init; }
        public int Walls { get; init; }
        public int InteriorWalls { get; init; }
        public int Roofs { get; init; }
        public int Doors { get; init; }
        public int Windows { get; init; }
        public IReadOnlyList<PlanWallEdge> WallEdges { get; init; } = [];
        public IReadOnlyList<InteriorWallEvidence> InteriorWallEvidence { get; init; } = [];
        public IReadOnlyList<PlanWallEdge> AutoDetectedInteriorWalls { get; init; } = [];
        public IReadOnlyList<PlanWallEdge> RemovedInteriorWalls { get; init; } = [];
    }

    private readonly record struct ImageBounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left + 1;
        public int Height => Bottom - Top + 1;
    }

    /// <param name="grayscale">Row-major, 0 = black, 255 = white; preprocessed from an image locally.</param>
    public static Result Generate(byte[] grayscale, int sourceWidth, int sourceHeight, Options settings)
    {
        ArgumentNullException.ThrowIfNull(grayscale);
        ArgumentNullException.ThrowIfNull(settings);
        if (sourceWidth < 1 || sourceHeight < 1 || (long)sourceWidth * sourceHeight > grayscale.LongLength)
            throw new ArgumentException("Invalid source image dimensions.");
        if (settings.WallLayers is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(settings), "Use 1–12 wall layers.");
        if (!double.IsFinite(settings.ReferenceWidthMetres) || settings.ReferenceWidthMetres < 0 || settings.ReferenceWidthMetres > MaxCellsPerSide * GridMetres ||
            !double.IsFinite(settings.OriginX) || !double.IsFinite(settings.OriginY) || !double.IsFinite(settings.OriginZ))
            throw new ArgumentException("Dimensions and origin must contain valid finite coordinates.");
        if (settings.ApproxPixelsPerCell is < 4 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings), "Approximate pixels per cell must be 4–100.");

        var allowed = settings.AllowedPieces.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.TypeId)).ToArray();
        var foundations = allowed.Where(p => p.Category.Equals("Foundations", StringComparison.OrdinalIgnoreCase)).ToArray();
        var walls = allowed.Where(p => p.Category.Equals("Walls", StringComparison.OrdinalIgnoreCase)).ToArray();
        var roofs = allowed.Where(p => p.Category.Equals("Roofs", StringComparison.OrdinalIgnoreCase)).ToArray();
        // Deliberately use a human-readable catalog label rather than inventing any game Type ID.
        // Users can import their own Door/Window placeables into the catalog.
        var doorPieces = allowed.Where(p => p.Category.Equals("Doors & Windows", StringComparison.OrdinalIgnoreCase) &&
            (p.Name.Contains("door", StringComparison.OrdinalIgnoreCase) || p.TypeId.Contains("door", StringComparison.OrdinalIgnoreCase))).ToArray();
        var windowPieces = allowed.Where(p => p.Category.Equals("Doors & Windows", StringComparison.OrdinalIgnoreCase) &&
            (p.Name.Contains("window", StringComparison.OrdinalIgnoreCase) || p.TypeId.Contains("window", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (foundations.Length + walls.Length + roofs.Length == 0)
            throw new InvalidOperationException("Select at least one Foundation, Wall or Roof from the palette.");

        ImageBounds bounds;
        bool lightInk = false;
        bool[,] footprint;
        int[]? facadeHeights = null;
        int cellsX, cellsZ;
        string explanation;

        if (settings.TraceFloorPlan)
        {
            if (!TryGetInkBounds(grayscale, sourceWidth, sourceHeight, out bounds, out lightInk))
                throw new InvalidOperationException("No high-contrast plan lines were detected. Use a clear top-down plan with dark lines on white (or light on black).");
            (cellsX, cellsZ) = ComputeDimensions(settings, bounds, true);
            footprint = TracePlan(grayscale, sourceWidth, sourceHeight, bounds, cellsX, cellsZ, lightInk);
            explanation = "2D: followed the detected closed outline. Internal wall strokes are approximated when aligned with grid boundaries; check rooms, doorways and roof shape.";
        }
        else
        {
            if (!TryGetPhotoSubjectBounds(grayscale, sourceWidth, sourceHeight, out bounds))
                throw new InvalidOperationException("No distinct building silhouette was detected in this photo. Try a clearer photo with a contrasting background, or use a top-down 2D plan.");
            (cellsX, cellsZ) = ComputeDimensions(settings, bounds, false);
            footprint = RectangularFootprint(cellsX, cellsZ); // depth is not observable from one facade photograph
            facadeHeights = ExtractFacadeHeights(grayscale, sourceWidth, sourceHeight, bounds, cellsX, settings.WallLayers);
            explanation = "PHOTO ESTIMATE: front-facing silhouette height was traced by column, creating a stepped facade rather than a uniform box. The unknown depth is estimated (or supplied manually). Hidden surfaces, real 3D shape, roof pitch and accurate scale cannot be reconstructed from one photo.";
        }

        // Detect interior wall edges before validating painted openings. A painted
        // door/window may replace ONLY an actual traced wall, never an arbitrary
        // seam between adjacent foundation cells. Store edges in one canonical
        // orientation: side 0 (right) or side 2 (bottom).
        var detected = new Dictionary<(int X, int Z, int Side), int>();
        if (settings.TraceFloorPlan && settings.TraceInteriorWalls && walls.Length > 0)
        {
            for (int z = 0; z < cellsZ; z++)
            for (int x = 0; x < cellsX; x++)
            {
                if (!footprint[x, z]) continue;
                if (x + 1 < cellsX && footprint[x + 1, z])
                {
                    var score = CountWallStrokeSamples(grayscale, sourceWidth, bounds, lightInk,
                        (x + 1.0) / cellsX, (z + 0.15) / cellsZ, (z + 0.85) / cellsZ,
                        vertical: true, tolerance: Math.Clamp((int)Math.Round(bounds.Width / (double)cellsX * 0.40), 2, 10));
                    if (score >= 6) detected[(x, z, 0)] = score;
                }
                if (z + 1 < cellsZ && footprint[x, z + 1])
                {
                    var score = CountWallStrokeSamples(grayscale, sourceWidth, bounds, lightInk,
                        (z + 1.0) / cellsZ, (x + 0.15) / cellsX, (x + 0.85) / cellsX,
                        vertical: false, tolerance: Math.Clamp((int)Math.Round(bounds.Height / (double)cellsZ * 0.40), 2, 10));
                    if (score >= 6) detected[(x, z, 2)] = score;
                }
            }
        }

        // Manual corrections operate only on genuine seams between two occupied
        // footprint cells. They are never allowed to create detached walls or
        // silently override outer boundary walls.
        if (!settings.TraceFloorPlan && ((settings.AddedInteriorWalls?.Count ?? 0) > 0 || (settings.RemovedInteriorWalls?.Count ?? 0) > 0))
            throw new InvalidOperationException("Manual wall corrections are only available for 2D plans.");
        var added = ValidateInteriorEdits(settings.AddedInteriorWalls, footprint, cellsX, cellsZ, "added");
        var removed = ValidateInteriorEdits(settings.RemovedInteriorWalls, footprint, cellsX, cellsZ, "removed");
        if (added.Overlaps(removed))
            throw new InvalidOperationException("The same interior wall cannot be added and removed at once.");
        if (added.Any(key => detected.ContainsKey(key)))
            throw new InvalidOperationException("A manually added wall already exists in the traced plan. Remove it from manual additions.");
        // Removing a non-existent wall must fail; do not silently apply stale
        // corrections after changing the image/scale.
        if (removed.Any(key => !detected.ContainsKey(key)))
            throw new InvalidOperationException("A removed wall no longer exists in the traced image. Regenerate and review your edits.");
        if ((added.Count > 0 || removed.Count > 0) && walls.Length == 0)
            throw new InvalidOperationException("Select a Wall placeable before editing interior partitions.");
        var interiorEdges = detected.Keys.ToHashSet();
        interiorEdges.ExceptWith(removed);
        interiorEdges.UnionWith(added);

        var openings = new Dictionary<(int X, int Z, int Side), OpeningKind>();
        foreach (var item in settings.Openings ?? [])
        {
            if (!settings.TraceFloorPlan)
                throw new InvalidOperationException("Door/window placement from the plan preview is only available for 2D drawings.");
            if (item.X < 0 || item.X >= cellsX || item.Z < 0 || item.Z >= cellsZ || item.Side is < 0 or > 3 ||
                !footprint[item.X, item.Z])
                throw new InvalidOperationException("An opening is outside the detected 2D building footprint.");
            int nx = item.X + (item.Side == 0 ? 1 : item.Side == 1 ? -1 : 0);
            int nz = item.Z + (item.Side == 2 ? 1 : item.Side == 3 ? -1 : 0);
            bool isInteriorNeighbour = nx >= 0 && nx < cellsX && nz >= 0 && nz < cellsZ && footprint[nx, nz];
            var canonicalInterior = item.Side switch
            {
                1 => (item.X - 1, item.Z, 0),
                3 => (item.X, item.Z - 1, 2),
                _ => (item.X, item.Z, item.Side)
            };
            // Reject misplaced marks and both noncanonical and unsupported seams.
            // Canonicalizing here also catches two marks on opposite sides of the
            // same interior wall (without placing two overlapping doors).
            var key = isInteriorNeighbour ? canonicalInterior : (item.X, item.Z, item.Side);
            if (isInteriorNeighbour && !interiorEdges.Contains(key))
                throw new InvalidOperationException("The marked opening must be on a detected interior or exterior wall, not an empty cell seam.");
            if (!openings.TryAdd(key, item.Kind))
                throw new InvalidOperationException("Two openings cannot occupy the same exterior wall edge.");
        }
        // A door or window substitutes for a generated wall piece. Never silently
        // emit zero openings when the user has deselected Walls from the palette.
        if (openings.Count > 0 && walls.Length == 0)
            throw new InvalidOperationException("Select a Wall placeable before adding doors or windows: openings replace a wall segment.");
        if (openings.Values.Contains(OpeningKind.Door) && doorPieces.Length == 0)
            throw new InvalidOperationException("Select a Door from the allowed placeables before painting door openings.");
        if (openings.Values.Contains(OpeningKind.Window) && windowPieces.Length == 0)
            throw new InvalidOperationException("Select a Window from the allowed placeables before painting window openings.");

        var pieces = new List<BlueprintElement>();
        var wallEdges = new List<PlanWallEdge>();
        int fc = 0, wc = 0, rc = 0, interior = 0, dc = 0, windowCount = 0;
        int[] dx = [1, -1, 0, 0];
        int[] dz = [0, 0, 1, -1];
        void AddPiece(PlaceableCatalogEntry piece, int x, int y, int z, double yaw = 0)
        {
            if (pieces.Count >= MaxPieces)
                throw new InvalidOperationException($"Image needs more than {MaxPieces:N0} pieces. Increase pixels per cell, specify a smaller reference width, or select fewer categories to keep the editor responsive.");
            pieces.Add(new BlueprintElement
            {
                TypeId = piece.TypeId,
                Position = new BlueprintVector3
                {
                    X = settings.OriginX + x * GridMetres,
                    Y = settings.OriginY + y * GridMetres,
                    Z = settings.OriginZ + z * GridMetres
                },
                Rotation = new BlueprintRotation { Yaw = yaw }
            });
        }

        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
        {
            if (!footprint[x, z]) continue;
            if (foundations.Length > 0) AddPiece(Pick(foundations, fc++), x, 0, z);
            int layers = facadeHeights is null ? settings.WallLayers : facadeHeights[x];
            if (walls.Length > 0)
            {
                for (int side = 0; side < 4; side++)
                {
                    var nx = x + dx[side]; var nz = z + dz[side];
                    if (nx >= 0 && nx < cellsX && nz >= 0 && nz < cellsZ && footprint[nx, nz]) continue;
                    wallEdges.Add(new PlanWallEdge(x, z, side, false));
                    bool hasOpening = openings.TryGetValue((x, z, side), out var kind);
                    int openingLayer = kind == OpeningKind.Window ? Math.Max(0, layers / 2) : 0;
                    for (int y = 0; y < layers; y++)
                    {
                        if (hasOpening && y == openingLayer && kind == OpeningKind.Door)
                            AddPiece(Pick(doorPieces, dc++), x, y, z, side < 2 ? 90 : 0);
                        else if (hasOpening && y == openingLayer && kind == OpeningKind.Window)
                            AddPiece(Pick(windowPieces, windowCount++), x, y, z, side < 2 ? 90 : 0);
                        else
                            AddPiece(Pick(walls, wc++), x, y, z, side < 2 ? 90 : 0);
                    }
                }
            }
            if (roofs.Length > 0) AddPiece(Pick(roofs, rc++), x, layers, z);
        }

        // Interior boundaries are traced once, then reused for the visible plan
        // and generation. This keeps mouse hit-testing and resulting models aligned.
        foreach (var edge in interiorEdges.OrderBy(e => e.Z).ThenBy(e => e.X).ThenBy(e => e.Side))
        {
            wallEdges.Add(new PlanWallEdge(edge.X, edge.Z, edge.Side, true));
            interior++;
            bool hasOpening = openings.TryGetValue(edge, out var kind);
            int openingLayer = kind == OpeningKind.Window ? settings.WallLayers / 2 : 0;
            for (int y = 0; y < settings.WallLayers; y++)
            {
                var yaw = edge.Side == 0 ? 90 : 0;
                if (hasOpening && y == openingLayer && kind == OpeningKind.Door)
                    AddPiece(Pick(doorPieces, dc++), edge.X, y, edge.Z, yaw);
                else if (hasOpening && y == openingLayer && kind == OpeningKind.Window)
                    AddPiece(Pick(windowPieces, windowCount++), edge.X, y, edge.Z, yaw);
                else
                    AddPiece(Pick(walls, wc++), edge.X, y, edge.Z, yaw);
            }
        }
        if (pieces.Count == 0)
            throw new InvalidOperationException("No building pieces could be extracted from this reference.");

        var scale = settings.AutoSizeFromImage
            ? settings.ReferenceWidthMetres > 0 ? $"User-supplied width {settings.ReferenceWidthMetres:0.#}m" : "Scale estimated from image pixels — NOT verified metres"
            : "Manually supplied grid dimensions";
        explanation += $"\nDetected grid: {cellsX} × {cellsZ} (4m cells); {scale}.";
        if (settings.TraceFloorPlan)
            explanation += $"\nInterior partitions: {detected.Count} traced, {added.Count} manually added, {removed.Count} suppressed. Amber walls have uncertain ink evidence (6/7 samples); all traced geometry requires review.";

        return new Result
        {
            Footprint = footprint, Elements = pieces, Explanation = explanation,
            WidthCells = cellsX, DepthCells = cellsZ,
            Foundations = fc, Walls = wc, InteriorWalls = interior, Roofs = rc,
            Doors = dc, Windows = windowCount, WallEdges = wallEdges,
            AutoDetectedInteriorWalls = detected.Keys.Select(k => new PlanWallEdge(k.X, k.Z, k.Side, true))
                .OrderBy(k => k.Z).ThenBy(k => k.X).ThenBy(k => k.Side).ToArray(),
            RemovedInteriorWalls = removed.Select(k => new PlanWallEdge(k.X, k.Z, k.Side, true))
                .OrderBy(k => k.Z).ThenBy(k => k.X).ThenBy(k => k.Side).ToArray(),
            InteriorWallEvidence = interiorEdges.Select(k => new InteriorWallEvidence(
                    new PlanWallEdge(k.X, k.Z, k.Side, true), detected.GetValueOrDefault(k), added.Contains(k)))
                .OrderBy(e => e.Edge.Z).ThenBy(e => e.Edge.X).ThenBy(e => e.Edge.Side).ToArray()
        };
    }

    private static PlaceableCatalogEntry Pick(PlaceableCatalogEntry[] pieces, int index) => pieces[index % pieces.Length];

    private static (int Width, int Depth) ComputeDimensions(Options options, ImageBounds bounds, bool plan)
    {
        if (!options.AutoSizeFromImage)
        {
            if (options.WidthCells is < 1 or > MaxCellsPerSide || options.DepthCells is < 1 or > MaxCellsPerSide)
                throw new ArgumentOutOfRangeException(nameof(options), $"Manual grid dimensions must be 1–{MaxCellsPerSide} cells.");
            return (options.WidthCells, options.DepthCells);
        }
        var width = options.ReferenceWidthMetres > 0
            ? (int)Math.Ceiling(options.ReferenceWidthMetres / GridMetres)
            : (int)Math.Ceiling((double)bounds.Width / options.ApproxPixelsPerCell);
        width = Math.Max(1, width);
        // An elevation photograph has no recoverable depth. Estimate half its width
        // instead of pretending image height is ground-plane depth.
        var depth = plan ? (int)Math.Round(width * (double)bounds.Height / bounds.Width)
            : Math.Max(1, (int)Math.Round(width * 0.5));
        if (width > MaxCellsPerSide || depth > MaxCellsPerSide)
            throw new InvalidOperationException($"Detected {width} × {depth} grid cells. Current safe limit is {MaxCellsPerSide} cells per side. Increase pixels/cell or reduce the reference width.");
        return (width, Math.Max(1, depth));
    }

    private static bool IsInk(byte luminance, bool lightInk) => lightInk ? luminance > 175 : luminance < 150;

    private static bool TryGetInkBounds(byte[] pixels, int width, int height, out ImageBounds bounds, out bool lightInk)
    {
        // Automatically support dark-background (CAD) plans as well as ink-on-paper.
        int corners = pixels[0] + pixels[width - 1] + pixels[(height - 1) * width] + pixels[height * width - 1];
        lightInk = corners / 4 < 110;
        int minX = width, minY = height, maxX = -1, maxY = -1, found = 0;
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            if (!IsInk(pixels[y * width + x], lightInk)) continue;
            minX = Math.Min(x, minX); maxX = Math.Max(x, maxX);
            minY = Math.Min(y, minY); maxY = Math.Max(y, maxY); found++;
        }
        bounds = new ImageBounds(minX, minY, maxX, maxY);
        return found >= 16 && found < 0.77 * width * height && bounds.Width >= 6 && bounds.Height >= 6;
    }

    private static bool[,] TracePlan(byte[] pixels, int width, int height, ImageBounds bounds,
        int cellsX, int cellsZ, bool lightInk)
    {
        // Dilate ink by one pixel to bridge tiny gaps in scanned paper plans.
        var blocked = new bool[pixels.Length];
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            if (!IsInk(pixels[y * width + x], lightInk)) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) blocked[(y + dy) * width + x + dx] = true;
        }
        var outside = new bool[blocked.Length];
        var queue = new Queue<int>();
        void Enqueue(int i)
        {
            if (blocked[i] || outside[i]) return;
            outside[i] = true; queue.Enqueue(i);
        }
        for (int x = 0; x < width; x++) { Enqueue(x); Enqueue((height - 1) * width + x); }
        for (int y = 0; y < height; y++) { Enqueue(y * width); Enqueue(y * width + width - 1); }
        while (queue.Count > 0)
        {
            var i = queue.Dequeue(); var x = i % width; var y = i / width;
            if (x > 0) Enqueue(i - 1);
            if (x + 1 < width) Enqueue(i + 1);
            if (y > 0) Enqueue(i - width);
            if (y + 1 < height) Enqueue(i + width);
        }
        var cells = new bool[cellsX, cellsZ];
        int filled = 0;
        for (int z = 0; z < cellsZ; z++)
        for (int x = 0; x < cellsX; x++)
        {
            int samples = 0;
            for (int sy = 0; sy < 5; sy++)
            for (int sx = 0; sx < 5; sx++)
            {
                var u = (x + (sx + .5) / 5) / cellsX;
                var v = (z + (sy + .5) / 5) / cellsZ;
                int px = Math.Clamp(bounds.Left + (int)(u * bounds.Width), 0, width - 1);
                int py = Math.Clamp(bounds.Top + (int)(v * bounds.Height), 0, height - 1);
                if (!outside[py * width + px]) samples++;
            }
            cells[x, z] = samples >= 13;
            if (cells[x, z]) filled++;
        }
        if (filled < 2 || filled < cellsX * cellsZ * 0.03)
            throw new InvalidOperationException("Could not trace an enclosed floorplan. No box was inserted. Try a closed dark outline, remove text/annotations, or crop to the building outline.");
        return cells;
    }

    private static int CountWallStrokeSamples(byte[] pixels, int width, ImageBounds bounds, bool lightInk,
        double fixedFraction, double fromFraction, double toFraction, bool vertical, int tolerance)
    {
        int strong = 0;
        for (int i = 0; i < 7; i++)
        {
            var varying = fromFraction + (toFraction - fromFraction) * (i + .5) / 7;
            int x = bounds.Left + (int)((vertical ? fixedFraction : varying) * bounds.Width);
            int y = bounds.Top + (int)((vertical ? varying : fixedFraction) * bounds.Height);
            var isLine = false;
            for (int o = -tolerance; o <= tolerance; o++)
            {
                int px = vertical ? x + o : x;
                int py = vertical ? y : y + o;
                if (px >= bounds.Left && px <= bounds.Right && py >= bounds.Top && py <= bounds.Bottom &&
                    IsInk(pixels[py * width + px], lightInk)) { isLine = true; break; }
            }
            if (isLine) strong++;
        }
        return strong;
    }

    private static HashSet<(int X, int Z, int Side)> ValidateInteriorEdits(
        IReadOnlyList<PlanWallEdge>? edits, bool[,] footprint, int cellsX, int cellsZ, string action)
    {
        var set = new HashSet<(int X, int Z, int Side)>();
        foreach (var wall in edits ?? [])
        {
            if (!wall.Interior || wall.Side is not (0 or 2) || wall.X < 0 || wall.Z < 0 ||
                wall.X >= cellsX || wall.Z >= cellsZ || !footprint[wall.X, wall.Z] ||
                (wall.Side == 0 && (wall.X + 1 >= cellsX || !footprint[wall.X + 1, wall.Z])) ||
                (wall.Side == 2 && (wall.Z + 1 >= cellsZ || !footprint[wall.X, wall.Z + 1])))
                throw new InvalidOperationException($"An {action} interior wall is outside the building or is not a valid internal seam.");
            if (!set.Add((wall.X, wall.Z, wall.Side)))
                throw new InvalidOperationException($"Duplicate {action} interior wall correction.");
        }
        return set;
    }

    private static bool TryGetPhotoSubjectBounds(byte[] pixels, int width, int height, out ImageBounds bounds)
    {
        // High-contrast foreground estimation against corner background. This
        // intentionally fails closed if a photograph has no distinct subject.
        int bg = (pixels[0] + pixels[width - 1] + pixels[(height - 1) * width] + pixels[height * width - 1]) / 4;
        var minX = width; var minY = height; var maxX = -1; var maxY = -1; int count = 0;
        for (int y = 2; y < height - 2; y++)
        for (int x = 2; x < width - 2; x++)
        {
            if (Math.Abs((int)pixels[y * width + x] - bg) < 42) continue;
            count++; minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        bounds = new ImageBounds(minX, minY, maxX, maxY);
        return count > width * height * .03 && count < width * height * .90 && bounds.Width >= 12 && bounds.Height >= 12;
    }

    private static int[] ExtractFacadeHeights(byte[] pixels, int width, int height, ImageBounds bounds, int cellsX, int layers)
    {
        int bg = (pixels[0] + pixels[width - 1] + pixels[(height - 1) * width] + pixels[height * width - 1]) / 4;
        var heights = new int[cellsX];
        for (int x = 0; x < cellsX; x++)
        {
            int fromX = bounds.Left + (int)((x + .20) * bounds.Width / cellsX);
            int toX = bounds.Left + (int)((x + .80) * bounds.Width / cellsX);
            int top = bounds.Bottom;
            for (int xx = fromX; xx <= toX; xx++)
            for (int yy = bounds.Top; yy <= bounds.Bottom; yy++)
                if (Math.Abs((int)pixels[yy * width + xx] - bg) > 42) top = Math.Min(top, yy);
            heights[x] = Math.Clamp((int)Math.Round(layers * (bounds.Bottom - top + 1.0) / bounds.Height), 1, layers);
        }
        return heights;
    }

    private static bool[,] RectangularFootprint(int width, int depth)
    {
        var footprint = new bool[width, depth];
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++) footprint[x, z] = true;
        return footprint;
    }
}
