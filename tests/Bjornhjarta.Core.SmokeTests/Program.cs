using Bjornhjarta.Core.Blueprints;

// Standalone smoke tests with no external NuGet testing framework.
// Execute: dotnet run --project tests/Bjornhjarta.Core.SmokeTests
// Synthetic images and example.* Type IDs intentionally avoid game assets.

var sample = Enumerable.Repeat((byte)255, 96 * 96).ToArray();
for (int x = 15; x <= 80; x++)
{
    sample[15 * 96 + x] = 0;
    sample[80 * 96 + x] = 0;
}
for (int z = 15; z <= 80; z++)
{
    sample[z * 96 + 15] = 0;
    sample[z * 96 + 80] = 0;
}

var foundation = new PlaceableCatalogEntry { Name = "Foundation", Category = "Foundations", TypeId = "example.foundation" };
var wall = new PlaceableCatalogEntry { Name = "Wall", Category = "Walls", TypeId = "example.wall" };
var roof = new PlaceableCatalogEntry { Name = "Roof", Category = "Roofs", TypeId = "example.roof" };
var door = new PlaceableCatalogEntry { Name = "Door", Category = "Doors & Windows", TypeId = "example.door" };
var window = new PlaceableCatalogEntry { Name = "Window", Category = "Doors & Windows", TypeId = "example.window" };
var palette = new[] { foundation, wall, roof, door, window };

ImageBlueprintGenerator.Options Options(params PlaceableCatalogEntry[] pieces) => new()
{
    TraceFloorPlan = true,
    AutoSizeFromImage = true,
    ApproxPixelsPerCell = 12,
    WallLayers = 3,
    AllowedPieces = pieces.Length == 0 ? palette : pieces
};

void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Console.WriteLine("PASS: " + message); return; }
    throw new Exception("FAIL: expected " + typeof(T).Name + " – " + message);
}

var baseResult = ImageBlueprintGenerator.Generate(sample, 96, 96, Options());
Check(baseResult.WidthCells > 1 && baseResult.DepthCells > 1, "Auto-sized plan footprint");
Check(baseResult.Foundations > 0 && baseResult.Walls > 0 && baseResult.Roofs > 0, "Generated building categories");
Check(baseResult.Doors == 0 && baseResult.Windows == 0, "No implicit doors/windows");
var edge = baseResult.WallEdges.First(e => !e.Interior);
var doorMark = new ImageBlueprintGenerator.OpeningSelection(edge.X, edge.Z, edge.Side, ImageBlueprintGenerator.OpeningKind.Door);
var doorOptions = Options();
doorOptions.Openings = [doorMark];
var withDoor = ImageBlueprintGenerator.Generate(sample, 96, 96, doorOptions);
Check(withDoor.Doors == 1 && withDoor.Walls == baseResult.Walls - 1, "Door replaces exactly one wall layer");
Check(withDoor.Elements.Count == baseResult.Elements.Count, "Replacement preserves piece count");
Check(withDoor.Elements.Any(p => p.TypeId == door.TypeId), "Door has selected local Type ID");

var windowOptions = Options();
windowOptions.Openings = [new(edge.X, edge.Z, edge.Side, ImageBlueprintGenerator.OpeningKind.Window)];
var withWindow = ImageBlueprintGenerator.Generate(sample, 96, 96, windowOptions);
Check(withWindow.Windows == 1 && withWindow.Walls == baseResult.Walls - 1, "Window replaces exactly one wall layer");
Check(withWindow.Elements.Any(p => p.TypeId == window.TypeId), "Window has selected local Type ID");

var noWall = Options(foundation, roof, door, window);
noWall.Openings = [doorMark];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(sample, 96, 96, noWall), "Opening rejected without Wall palette entry");

var noDoor = Options(foundation, wall, roof);
noDoor.Openings = [doorMark];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(sample, 96, 96, noDoor), "Door opening rejected without Door entry");

var duplicate = Options();
duplicate.Openings = [doorMark, doorMark];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(sample, 96, 96, duplicate), "Duplicate opening rejected");

var outOfBounds = Options();
outOfBounds.Openings = [new(-1, -1, 0, ImageBlueprintGenerator.OpeningKind.Door)];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(sample, 96, 96, outOfBounds), "Invalid opening location rejected");

Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(
    Enumerable.Repeat((byte)255, 96 * 96).ToArray(), 96, 96, Options()), "Blank drawing does not generate arbitrary box");


// Interior wall tracing and painted openings must agree. A continuous, visible
// partition at the center of a rectangular floorplan should become a real
// replaceable wall. A non-wall seam must NOT accept an opening.
var partitionPlan = (byte[])sample.Clone();
for (int y = 16; y < 80; y++) partitionPlan[y * 96 + 48] = 0;
var partitionBase = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, Options());
var interiorEdge = partitionBase.WallEdges.FirstOrDefault(e => e.Interior);
Check(partitionBase.InteriorWalls > 0, "Detect interior partition from the reference plan");
var interiorDoorOptions = Options();
interiorDoorOptions.Openings = [new(interiorEdge.X, interiorEdge.Z, interiorEdge.Side, ImageBlueprintGenerator.OpeningKind.Door)];
var interiorDoor = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, interiorDoorOptions);
Check(interiorDoor.Doors == 1 && interiorDoor.Walls == partitionBase.Walls - 1,
    "Door replaces exactly one interior wall layer");
Check(interiorDoor.InteriorWalls == partitionBase.InteriorWalls,
    "Interior wall count remains unchanged when a doorway is cut");
var interiorWindowOptions = Options();
interiorWindowOptions.Openings = [new(interiorEdge.X, interiorEdge.Z, interiorEdge.Side, ImageBlueprintGenerator.OpeningKind.Window)];
var interiorWindow = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, interiorWindowOptions);
Check(interiorWindow.Windows == 1 && interiorWindow.Walls == partitionBase.Walls - 1,
    "Window replaces exactly one interior wall layer");

// Two clicks on opposite sides of one partition refer to the same wall.
var oppositeSide = interiorEdge.Side == 0
    ? new ImageBlueprintGenerator.OpeningSelection(interiorEdge.X + 1, interiorEdge.Z, 1, ImageBlueprintGenerator.OpeningKind.Window)
    : new ImageBlueprintGenerator.OpeningSelection(interiorEdge.X, interiorEdge.Z + 1, 3, ImageBlueprintGenerator.OpeningKind.Window);
var oppositeOptions = Options();
oppositeOptions.Openings = [oppositeSide];
var oppositeResult = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, oppositeOptions);
Check(oppositeResult.Windows == 1, "Opposite-side interior wall marking normalizes to same edge");
var duplicateInteriorOptions = Options();
duplicateInteriorOptions.Openings = [interiorDoorOptions.Openings[0], oppositeSide];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, duplicateInteriorOptions),
    "Two opening marks on opposite sides of the same wall are rejected");

var interiorKeys = partitionBase.WallEdges.Select(e => (e.X, e.Z, e.Side)).ToHashSet();
ImageBlueprintGenerator.OpeningSelection? invalidSeam = null;
for (int z = 0; z < partitionBase.DepthCells && invalidSeam is null; z++)
for (int x = 0; x + 1 < partitionBase.WidthCells; x++)
{
    if (partitionBase.Footprint[x, z] && partitionBase.Footprint[x + 1, z] && !interiorKeys.Contains((x, z, 0)))
    {
        invalidSeam = new ImageBlueprintGenerator.OpeningSelection(x, z, 0, ImageBlueprintGenerator.OpeningKind.Door);
        break;
    }
}
Check(invalidSeam is not null, "Synthetic plan contains a non-wall interior grid seam");
var invalidSeamOptions = Options();
invalidSeamOptions.Openings = [invalidSeam!.Value];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, invalidSeamOptions),
    "Non-wall interior grid seam rejects painted opening");

// v0.16: Editable interior partitions must affect BOTH preview edges and
// generated blueprint elements. Explicit user edits cannot invent exterior
// seams and cannot leave dangling door/window openings on deleted walls.
var firstPartition = partitionBase.AutoDetectedInteriorWalls.First();
var correctionRemove = Options();
correctionRemove.RemovedInteriorWalls = [firstPartition];
var withoutPartition = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, correctionRemove);
Check(withoutPartition.InteriorWalls == partitionBase.InteriorWalls - 1 &&
    withoutPartition.Walls == partitionBase.Walls - correctionRemove.WallLayers,
    "Removing an interior partition removes all its generated wall layers");
Check(withoutPartition.RemovedInteriorWalls.Contains(firstPartition) &&
      !withoutPartition.WallEdges.Contains(firstPartition),
    "Suppressed wall appears in diagnostics, never among rendered walls");
var invalidOpeningOnRemoved = Options();
invalidOpeningOnRemoved.RemovedInteriorWalls = [firstPartition];
invalidOpeningOnRemoved.Openings = [new(firstPartition.X, firstPartition.Z, firstPartition.Side,
    ImageBlueprintGenerator.OpeningKind.Door)];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, invalidOpeningOnRemoved),
    "Door cannot be placed on a removed interior wall");
var correctedAdded = Options();
correctedAdded.AddedInteriorWalls = [new(invalidSeam!.Value.X, invalidSeam.Value.Z,
    invalidSeam.Value.Side, true)];
var withAdded = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, correctedAdded);
Check(withAdded.InteriorWalls == partitionBase.InteriorWalls + 1 &&
      withAdded.Walls == partitionBase.Walls + correctedAdded.WallLayers,
    "Manually added interior partition creates a complete wall");
Check(withAdded.InteriorWallEvidence.Any(e => e.Edge.X == invalidSeam.Value.X &&
    e.Edge.Z == invalidSeam.Value.Z && e.Edge.Side == invalidSeam.Value.Side && e.UserAdded),
    "Preview evidence distinguishes manual and traced walls");
var drawnDoor = Options();
drawnDoor.AddedInteriorWalls = correctedAdded.AddedInteriorWalls;
drawnDoor.Openings = [invalidSeam.Value];
var correctedWithDoor = ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, drawnDoor);
Check(correctedWithDoor.Doors == 1 && correctedWithDoor.Walls == withAdded.Walls - 1,
    "A door can replace one layer of a manually drawn partition");
var invalidCorrection = Options();
invalidCorrection.AddedInteriorWalls = [new(-1, 0, 0, true)];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, invalidCorrection),
    "Out-of-bounds interior wall edits are rejected");
var contradictory = Options();
contradictory.AddedInteriorWalls = [firstPartition];
contradictory.RemovedInteriorWalls = [firstPartition];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, contradictory),
    "Conflicting wall edits are rejected");
var duplicateManual = Options();
duplicateManual.AddedInteriorWalls = [firstPartition];
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, duplicateManual),
    "Manual addition of an already detected wall is rejected");
var staleRemoval = Options();
staleRemoval.RemovedInteriorWalls = correctedAdded.AddedInteriorWalls;
Throws<InvalidOperationException>(() => ImageBlueprintGenerator.Generate(partitionPlan, 96, 96, staleRemoval),
    "Removing a wall not found in the image is rejected");
Check(partitionBase.InteriorWallEvidence.All(e => e.InkSamples is >= 6 and <= 7 && !e.UserAdded),
    "Detected interior wall evidence is grounded in 7 image samples");

// v0.15: Automatic model resolution is deterministic, case insensitive, and
// never overwrites an explicit model choice or guesses ambiguous mappings.
ModelAssetRecord Model(string id, string typeId, string name = "Test asset", bool valid = true) => new()
{
    AssetId = id, TypeId = typeId, DisplayName = name, IsValid = valid, IsModelValid = valid
};
var modelResolver = new AutomaticModelResolver([
    Model("stone-asset", "example.foundation", "Stone foundation"),
    Model("wall-asset", "Example.Wall", "Wall"),
    Model("roof-A", "example.roof"),
    Model("roof-B", "example.roof"),
    Model("bad-door", "example.door", valid: false)
]);
var pieces = new[]
{
    new BlueprintElement { TypeId = "EXAMPLE.FOUNDATION" },
    new BlueprintElement { TypeId = "example.foundation" },
    new BlueprintElement { TypeId = "example.wall" },
    new BlueprintElement { TypeId = "example.roof" },
    new BlueprintElement { TypeId = "example.door" },
    new BlueprintElement { TypeId = "example.window" },
    new BlueprintElement { TypeId = "example.foundation", ModelAssetId = "manually-picked", ModelAssetName = "Manual" }
};
var report = modelResolver.AssignMissing(pieces);
Check(report.Assigned == 3, "Unique Type ID mappings assigned to multiple blueprint pieces");
Check(report.Ambiguous == 1 && report.AmbiguousTypeIds.Contains("example.roof"), "Ambiguous Type ID mapping rejected");
Check(report.Missing == 2 && report.SkippedExisting == 1, "Invalid/missing models counted without overwriting explicit links");
Check(pieces[0].ModelAssetId == "stone-asset" && pieces[1].ModelAssetId == "stone-asset" &&
      pieces[2].ModelAssetId == "wall-asset", "Case-insensitive mapping and asset reuse");
Check(pieces[^1].ModelAssetId == "manually-picked", "User model choice preserved");
var repeated = modelResolver.AssignMissing(pieces);
Check(repeated.Assigned == 0 && repeated.SkippedExisting == 4, "Auto-mapping is idempotent");
var reversed = new AutomaticModelResolver(new [] { Model("roof-B", "example.roof"), Model("roof-A", "example.roof") });
Check(reversed.IsAmbiguous("EXAMPLE.ROOF"), "Mapping cannot depend on model file scan order");

// Local JSON I/O must be lossless, and replacing a blueprint must preserve a
// recovery copy instead of truncating the previous file on serialization failure.
var fileRoot = Path.Combine(Path.GetTempPath(), "Bjornhjarta-Smoketest-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new BlueprintJsonStore();
    var path = Path.Combine(fileRoot, "test.json");
    var document = new BlueprintDocument { Elements = [new BlueprintElement
    {
        TypeId = "example.foundation", ModelAssetId = "stone-asset", ModelAssetName = "Stone foundation",
        Position = new BlueprintVector3 { X = 4, Y = 8, Z = 12 }
    }] };
    store.SaveAsync(document, path).GetAwaiter().GetResult();
    var loaded = store.LoadAsync(path).GetAwaiter().GetResult();
    Check(loaded.Elements.Single().Position.Y == 8 && loaded.Elements[0].ModelAssetId == "stone-asset", "Save and reload retains 3D positions and model links");
    document.Elements[0].Position.X = 16;
    store.SaveAsync(document, path).GetAwaiter().GetResult();
    Check(File.Exists(path + ".bak"), "Overwriting a blueprint keeps an on-disk recovery backup");
    var backup = store.LoadAsync(path + ".bak").GetAwaiter().GetResult();
    Check(backup.Elements.Single().Position.X == 4, "Recovery backup contains prior blueprint version");
    File.WriteAllText(path, "not JSON");
    Throws<System.Text.Json.JsonException>(() => store.LoadAsync(path).GetAwaiter().GetResult(), "Malformed blueprint JSON rejected");

    // Saving an imported model without a Type ID is now supported. It remains
    // unresolvable until the user explicitly supplies a trusted local mapping.
    var modelDir = Path.Combine(fileRoot, "models");
    Directory.CreateDirectory(modelDir);
    var gltfPath = Path.Combine(modelDir, "minimal.gltf");
    File.WriteAllText(gltfPath, "{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{}]}");
    var assetCatalog = new ModelAssetCatalog();
    var firstScan = assetCatalog.Scan(modelDir);
    Check(firstScan.Count == 1 && firstScan[0].IsModelValid, "Minimal local glTF model indexed");
    var unmapped = assetCatalog.SaveMetadata(modelDir, firstScan[0], "Unmapped example model", "", null, null);
    Check(unmapped.IsModelValid && !unmapped.IsValid && unmapped.Status == "Needs Type ID", "Model details can be saved without a Type ID");
    var mapped = assetCatalog.SaveMetadata(modelDir, unmapped, "Mapped example model", "example.foundation", null, null);
    Check(mapped.IsValid && mapped.AssetId == unmapped.AssetId, "Adding Type ID preserves asset identity and makes mapping reusable");
    // Ensure the catalog exposes preview compatibility without changing model
    // validity or making a false claim of complete game-material fidelity.
    var materialGltf = Path.Combine(modelDir, "material-test.gltf");
    File.WriteAllText(materialGltf, """
{"asset":{"version":"2.0"},"meshes":[{}],"materials":[{"pbrMetallicRoughness":{"baseColorTexture":{"index":0},"metallicRoughnessTexture":{"index":1}},"normalTexture":{"index":2}}]}
""");
    var materialRecord = assetCatalog.Scan(modelDir).Single(record => record.ModelFilePath == materialGltf);
    Check(materialRecord.PreviewStatus == "Partial material preview" &&
          materialRecord.PreviewNotes.Contains("normal maps") && materialRecord.IsModelValid,
          "Model catalog exposes honest WPF material limitations without invalidating valid glTF data");
    File.Delete(materialGltf);
    var resolved = new AutomaticModelResolver(assetCatalog.Scan(modelDir));
    Check(resolved.TryResolve("example.foundation", out var linked) && linked?.AssetId == mapped.AssetId,
        "Saved Type ID mapping resolves from local asset library");
}
finally
{
    if (Directory.Exists(fileRoot)) Directory.Delete(fileRoot, recursive: true);
}

// EML installation scanning is intentionally read-only. Verify that a minimal
// synthetic dedicated-server directory can be inspected without being edited.
var fakeServer = Path.Combine(Path.GetTempPath(), "Bjornhjarta-EML-Smoke-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(fakeServer);
    File.WriteAllBytes(Path.Combine(fakeServer, "enshrouded_server.exe"), Array.Empty<byte>());
    var modDir = Path.Combine(fakeServer, "mods", "example_test_mod");
    Directory.CreateDirectory(modDir);
    var manifestPath = Path.Combine(modDir, "mod.json");
    File.WriteAllText(manifestPath, "{\"id\":\"example_test_mod\",\"name\":\"Test mod\",\"version\":\"1.0.0\",\"capabilities\":[\"patch\"]}");
    var scan = new Bjornhjarta.Core.Modding.EmlInstallationScanner().Scan(fakeServer);
    Check(scan.HasServerExecutable && !scan.HasClientExecutable && scan.Mods.Count == 1,
        "EML scanner recognizes synthetic dedicated-server profile");
    Check(scan.Mods.Single().MetadataStatus == "Manifest fields look valid", "EML manifest recognized without running a mod");
    Check(!File.Exists(Path.Combine(fakeServer, "eml.json")), "EML scanner does not change server config");
}
finally
{
    if (Directory.Exists(fakeServer)) Directory.Delete(fakeServer, recursive: true);
}

// v0.18: All EML package operations are tested using fake installations and
// synthetic ZIPs. These tests never use the real game or execute mod scripts.
var emlRoot = Path.Combine(Path.GetTempPath(), "Bjornhjarta-Eml-Workflow-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(emlRoot);
    File.WriteAllBytes(Path.Combine(emlRoot, "enshrouded_server.exe"), []);
    var zipPath = Path.Combine(emlRoot, "test-mod.zip");
    var manager = new Bjornhjarta.Core.Modding.EmlModPackageManager();
    void WriteTestModZip(string version, bool invalid = false)
    {
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("test_mod/mod.json");
        using (var writer = new StreamWriter(manifest.Open()))
            writer.Write($$"""{"id":"test_mod","name":"Test mod","version":"{{version}}","capabilities":["patch"]}""");
        var source = zip.CreateEntry(invalid ? "test_mod/../escape.txt" : "test_mod/src/mod.lua");
        using var sourceWriter = new StreamWriter(source.Open());
        sourceWriter.Write("print('synthetic test only')");
    }
    WriteTestModZip("1.0.0");
    var draft = manager.Preview(emlRoot, zipPath);
    Check(draft.ModId == "test_mod" && draft.Name == "Test mod" && !draft.IsReplacement && draft.FileCount == 2,
        "EML ZIP preview reads manifest without writing a mod");
    var installed = manager.Install(emlRoot, zipPath, draft.ModId, draft.ArchiveSha256);
    var sourceFile = Path.Combine(installed.InstalledPath, "src", "mod.lua");
    Check(File.Exists(sourceFile) && installed.BackupPath is null,
        "EML ZIP installation extracts a single mod without requiring a backup for a new mod");
    var scannedMod = new Bjornhjarta.Core.Modding.EmlInstallationScanner().Scan(emlRoot);
    Check(scannedMod.Mods.Single().IsEnabled && scannedMod.Mods.Single().EnabledStatus == "Enabled",
        "EML scanner labels active mods correctly");

    var disabledPath = manager.Disable(emlRoot, "test_mod");
    Check(Directory.Exists(disabledPath) && !Directory.Exists(installed.InstalledPath) && File.Exists(Path.Combine(disabledPath, "src", "mod.lua")),
        "Disabling moves the mod outside mods without deleting its contents");
    var scannedDisabled = new Bjornhjarta.Core.Modding.EmlInstallationScanner().Scan(emlRoot);
    Check(scannedDisabled.Mods.Count == 1 && scannedDisabled.Mods.Single().EnabledStatus == "Disabled",
        "EML scanner sees disabled mods in Bjornhjarta storage");
    Throws<InvalidOperationException>(() => manager.Install(emlRoot, zipPath, "test_mod"),
        "Cannot install over a disabled mod with the same ID");
    var reenabled = manager.Enable(emlRoot, "test_mod");
    Check(reenabled == installed.InstalledPath && File.Exists(sourceFile),
        "Re-enabling returns the exact folder without data loss");
    Throws<DirectoryNotFoundException>(() => manager.Enable(emlRoot, "test_mod"),
        "A previously enabled mod cannot be enabled twice");

    WriteTestModZip("1.1.0");
    var replacementPreview = manager.Preview(emlRoot, zipPath);
    Check(replacementPreview.IsReplacement, "ZIP preview warns when replacement backup is needed");
    Throws<InvalidOperationException>(() => manager.Install(emlRoot, zipPath, draft.ModId, draft.ArchiveSha256),
        "Changed ZIP cannot be installed using an older confirmation preview");
    var replaced = manager.Install(emlRoot, zipPath, replacementPreview.ModId, replacementPreview.ArchiveSha256);
    Check(replaced.BackupPath is not null && Directory.Exists(replaced.BackupPath) &&
        File.ReadAllText(Path.Combine(replaced.BackupPath, "mod.json")).Contains("1.0.0") &&
        File.ReadAllText(Path.Combine(replaced.InstalledPath, "mod.json")).Contains("1.1.0"),
        "Updating an EML mod preserves its previous version in a backup folder");

    WriteTestModZip("1.2.0", invalid: true);
    Throws<InvalidDataException>(() => manager.Preview(emlRoot, zipPath),
        "ZIP traversal paths are rejected during read-only preview");
    Check(File.ReadAllText(Path.Combine(replaced.InstalledPath, "mod.json")).Contains("1.1.0"),
        "Invalid ZIP cannot replace or modify a previously installed mod");
    Throws<InvalidDataException>(() => manager.Disable(emlRoot, "../test_mod"),
        "Mod ID traversal is rejected on toggle operations");
}
finally
{
    if (Directory.Exists(emlRoot)) Directory.Delete(emlRoot, recursive: true);
}

// v0.17: WPF only renders base color/factors. Material declarations must
// distinguish an acceptable basic preview from genuine full PBR support.
using (var testJson = System.Text.Json.JsonDocument.Parse("""
{
  "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicRoughnessTexture": {"index": 1}},
                 "normalTexture": {"index": 2}, "occlusionTexture": {"index": 1}, "emissiveTexture": {"index": 3}}]
}
"""))
{
    var preview = ModelPreviewInspector.Assess(testJson.RootElement);
    Check(preview.Status == "Partial material preview" && preview.HasBaseColorTexture &&
          preview.Details.Contains("normal maps") && preview.Details.Contains("emissive maps"),
          "v0.17 preview reports unsupported PBR material channels without claiming full rendering");
}
using (var requiredJson = System.Text.Json.JsonDocument.Parse("""
{"extensionsRequired": ["KHR_draco_mesh_compression"]}
"""))
{
    var preview = ModelPreviewInspector.Assess(requiredJson.RootElement);
    Check(preview.RequiresUnsupportedExtensions && preview.Status == "Unsupported GLB preview" &&
          preview.Details.Contains("KHR_draco_mesh_compression"),
          "v0.17 required GLB extensions are flagged as unsupported for WPF preview");
}
using (var baseJson = System.Text.Json.JsonDocument.Parse("""
{"materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}}]}
"""))
{
    var preview = ModelPreviewInspector.Assess(baseJson.RootElement);
    Check(preview.Status == "Basic WPF preview" && preview.HasBaseColorTexture,
          "v0.17 basic glTF base-color preview is distinguished from complete material fidelity");
}


// v0.19: Defensive storage and recovery smoke checks.
var hardeningRoot = Path.Combine(Path.GetTempPath(), "Bjornhjarta-v019-Smoke-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(hardeningRoot);
    var store = new BlueprintJsonStore();
    var safePath = Path.Combine(hardeningRoot, "safe.json");
    var stable = new BlueprintDocument { Metadata = new BlueprintMetadata { Name = "Recovery test" },
        Elements = [new BlueprintElement { TypeId = "example.foundation" }] };
    store.SaveAsync(stable, safePath).GetAwaiter().GetResult();
    var before = File.ReadAllBytes(safePath);
    var duplicateId = new BlueprintDocument { Elements = [
        new BlueprintElement { Id = "duplicated", TypeId = "example.wall" },
        new BlueprintElement { Id = "DUPLICATED", TypeId = "example.roof" }] };
    Throws<InvalidDataException>(() => store.SaveAsync(duplicateId, safePath).GetAwaiter().GetResult(),
        "v0.19 duplicate element IDs rejected before replacing saved data");
    Check(File.ReadAllBytes(safePath).SequenceEqual(before), "v0.19 rejected blueprint leaves original on disk unchanged");
    var invalidCoordinate = new BlueprintDocument { Elements = [new BlueprintElement {
        Position = new BlueprintVector3 { X = double.NaN } }] };
    Throws<InvalidDataException>(() => store.SaveAsync(invalidCoordinate, safePath).GetAwaiter().GetResult(),
        "v0.19 nonfinite blueprint coordinates rejected");
    var tooMany = new BlueprintDocument { Elements = Enumerable.Range(0, 100_001)
        .Select(i => new BlueprintElement { Id = "large-" + i }).ToList() };
    Throws<InvalidDataException>(() => store.SaveAsync(tooMany, safePath).GetAwaiter().GetResult(),
        "v0.19 oversized element count refused before serialization");
    var malformedButJson = Path.Combine(hardeningRoot, "invalid-blueprint.json");
    File.WriteAllText(malformedButJson,
        """{"schemaVersion":1,"metadata":{"name":"Invalid"},"elements":[{"id":"same"},{"id":"same"}]}""");
    Throws<InvalidDataException>(() => store.LoadAsync(malformedButJson).GetAwaiter().GetResult(),
        "v0.19 semantically corrupt blueprint rejected on load");

    // Large but valid blueprint: exercises the real JSON serialization and
    // round-trip paths without setting brittle time limits on user hardware.
    var bigPath = Path.Combine(hardeningRoot, "large.json");
    var many = new BlueprintDocument { Metadata = new BlueprintMetadata { Name = "10k-piece stress sample" },
        Elements = Enumerable.Range(0, 10_000)
            .Select(i => new BlueprintElement { Id = $"piece-{i}", TypeId = "example.foundation",
                Position = new BlueprintVector3 { X = (i % 100) * 4, Y = (i / 10_000) * 4, Z = (i / 100) * 4 } })
            .ToList() };
    store.SaveAsync(many, bigPath).GetAwaiter().GetResult();
    var manyAgain = store.LoadAsync(bigPath).GetAwaiter().GetResult();
    Check(manyAgain.Elements.Count == 10_000 && manyAgain.Elements[^1].Position.Z == 396,
        "v0.19 10,000-piece blueprint round-trip preserves coordinates");

    // Preferences must be backed up on update; invalid JSON must never be
    // silently replaced with defaults or erase client/server separation.
    var settings = Path.Combine(hardeningRoot, "settings");
    var prefsStore = new Bjornhjarta.Core.Modding.EmlManagerPreferencesStore(settings);
    var client = Path.Combine(hardeningRoot, "client");
    var server = Path.Combine(hardeningRoot, "server");
    var prefs = new Bjornhjarta.Core.Modding.EmlManagerPreferences
    { ClientRootPath = client, DedicatedServerRootPath = server,
      LastActiveProfile = Bjornhjarta.Core.Modding.EmlInstallationProfileKind.DedicatedServer };
    prefsStore.Save(prefs);
    Check(prefsStore.Load().ClientRootPath == client && prefsStore.Load().DedicatedServerRootPath == server,
        "v0.19 client/server EML preferences preserved after round-trip");
    prefsStore.Save(prefs with { LastActiveProfile = Bjornhjarta.Core.Modding.EmlInstallationProfileKind.Client });
    Check(File.Exists(Path.Combine(settings, "eml-manager.json.bak")),
        "v0.19 EML preference updates keep a recovery backup");
    File.WriteAllText(Path.Combine(settings, "eml-manager.json"), "{not-json");
    Throws<InvalidDataException>(() => prefsStore.Load(),
        "v0.19 corrupt EML preferences surfaced without resetting them");
    var preferencesPath = Path.Combine(settings, "eml-manager.json");
    File.WriteAllText(preferencesPath, "null");
    Throws<InvalidDataException>(() => prefsStore.Load(),
        "v1.0 RC1 valid-JSON null preferences rejected instead of losing client/server paths");
    Check(File.ReadAllText(preferencesPath) == "null",
        "v1.0 RC1 rejected settings remain untouched for manual recovery");
    File.WriteAllText(preferencesPath, "{\"lastActiveProfile\":999}");
    Throws<InvalidDataException>(() => prefsStore.Load(),
        "v1.0 RC1 unknown EML profile rejected instead of passing invalid state into the UI");
    Throws<InvalidDataException>(() => prefsStore.Save(prefs with
        { LastActiveProfile = (Bjornhjarta.Core.Modding.EmlInstallationProfileKind)999 }),
        "v1.0 RC1 refuses to write an invalid profile preference");

    // EML configuration writes may affect the game: enforce an exact target
    // path, preserve custom fields, and keep a backup after updates.
    var game = Path.Combine(hardeningRoot, "game");
    Directory.CreateDirectory(game);
    var configPath = Path.Combine(game, "eml.json");
    File.WriteAllText(configPath,
        """{"enable_console":false,"custom_setting":"preserve-me"}""");
    var config = new Bjornhjarta.Core.Modding.EmlConfigStore();
    var backup = config.Save(configPath, new Bjornhjarta.Core.Modding.EmlConfigSettings(true, false, "export"), game);
    Check(backup is not null && File.Exists(backup) && File.ReadAllText(backup).Contains("preserve-me"),
        "v0.19 EML config update keeps recoverable original bytes");
    Check(File.ReadAllText(configPath).Contains("preserve-me") && config.Load(configPath).EnableConsole,
        "v0.19 EML config update preserves unknown fields");
    Throws<InvalidOperationException>(() => config.Save(Path.Combine(hardeningRoot, "unrelated.json"),
        new Bjornhjarta.Core.Modding.EmlConfigSettings(), game),
        "v0.19 EML config writes outside selected game root refused");
    Throws<InvalidOperationException>(() => config.Save(Path.Combine(game, "other.json"),
        new Bjornhjarta.Core.Modding.EmlConfigSettings(), game),
        "v0.19 EML config writes to non-eml.json name refused");
}
finally
{
    if (Directory.Exists(hardeningRoot)) Directory.Delete(hardeningRoot, recursive: true);
}

Console.WriteLine("All Bjornhjarta core smoke checks passed.");
