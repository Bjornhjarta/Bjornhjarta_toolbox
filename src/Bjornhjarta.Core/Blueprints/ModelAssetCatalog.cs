using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Bjornhjarta.Core.Blueprints;

/// <summary>
/// Sidecar metadata for a user-exported model. The referenced model remains in
/// the user's local asset library; no Enshrouded game assets are bundled here.
/// </summary>
public sealed class ModelAssetManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string AssetId { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Untitled model asset";
    public string TypeId { get; set; } = "";
    public string? SourceModelGuid { get; set; }
    public string ModelFile { get; set; } = "";
    public string Source { get; set; } = "User import";
    public string CreatedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string? Notes { get; set; }
    public string? SourceTexturePacking { get; set; }
    public string? GltfTexturePacking { get; set; }
    public bool? TexturePackingConverted { get; set; }
}

/// <summary>One indexed model file and its local metadata/inspection status.</summary>
public sealed class ModelAssetRecord
{
    public string AssetId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TypeId { get; set; } = "";
    public string? SourceModelGuid { get; set; }
    public string ModelFilePath { get; set; } = "";
    public string RelativeModelFile { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string Format { get; set; } = "";
    public int MeshCount { get; set; }
    public int MaterialCount { get; set; }
    public int TextureCount { get; set; }
    public int ImageCount { get; set; }
    public int EmbeddedImageCount { get; set; }
    public int ExternalImageCount { get; set; }
    public string MaterialChannels { get; set; } = "";
    public string PreviewStatus { get; set; } = "Not inspected";
    public string PreviewNotes { get; set; } = "";
    public string MeshNames { get; set; } = "";
    public bool HasManifest { get; set; }
    public bool IsModelValid { get; set; }
    public bool IsValid { get; set; }
    public string Status { get; set; } = "";
    public string? Warning { get; set; }
    public string? Notes { get; set; }
    public string? SourceTexturePacking { get; set; }
    public string? GltfTexturePacking { get; set; }
    public bool? TexturePackingConverted { get; set; }
}

/// <summary>
/// Indexes local glTF 2.0 / GLB model exports. Scanning is read-only. Metadata
/// is stored beside each model in a .bjornhjarta.asset.json sidecar file.
/// </summary>
public sealed class ModelAssetCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private const uint GlbMagic = 0x46546C67;      // glTF
    private const uint JsonChunkType = 0x4E4F534A; // JSON
    public const long MaximumModelFileBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumGltfJsonBytes = 64 * 1024 * 1024;

    public IReadOnlyList<ModelAssetRecord> Scan(string libraryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        var root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Model asset library folder does not exist: {root}");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        };

        var files = Directory.EnumerateFiles(root, "*.glb", options)
            .Concat(Directory.EnumerateFiles(root, "*.gltf", options))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        var results = new List<ModelAssetRecord>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(InspectOne(root, file));
        }

        // Asset IDs are blueprint references. Duplicate IDs make a reference ambiguous,
        // so flag every conflicting record and prevent assignment until it is corrected.
        var duplicateAssetIds = results
            .Where(record => record.HasManifest && !string.IsNullOrWhiteSpace(record.AssetId))
            .GroupBy(record => record.AssetId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var record in results.Where(record => duplicateAssetIds.Contains(record.AssetId)))
        {
            record.IsValid = false;
            record.Status = "Duplicate asset ID";
            record.Warning = $"Asset ID '{record.AssetId}' is used by more than one model file. Regenerate or correct the duplicate sidecar ID before assigning either asset.";
        }

        return results;
    }

    public ModelAssetRecord SaveMetadata(
        string libraryRoot,
        ModelAssetRecord record,
        string displayName,
        string typeId,
        string? sourceModelGuid,
        string? notes)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var root = Path.GetFullPath(libraryRoot);
        var modelPath = Path.GetFullPath(record.ModelFilePath);
        EnsureInsideDirectory(root, modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The selected model file no longer exists.", modelPath);

        var manifestPath = GetManifestPath(modelPath);
        var existing = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<ModelAssetManifest>(File.ReadAllText(manifestPath), JsonOptions)
            : null;

        var manifest = new ModelAssetManifest
        {
            SchemaVersion = 1,
            AssetId = string.IsNullOrWhiteSpace(existing?.AssetId) ? Guid.NewGuid().ToString("N") : existing.AssetId,
            DisplayName = displayName.Trim(),
            TypeId = typeId.Trim(),
            SourceModelGuid = string.IsNullOrWhiteSpace(sourceModelGuid) ? null : sourceModelGuid.Trim(),
            ModelFile = Path.GetRelativePath(Path.GetDirectoryName(manifestPath)!, modelPath),
            Source = existing?.Source ?? "User import",
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow.ToString("O"),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            SourceTexturePacking = existing?.SourceTexturePacking,
            GltfTexturePacking = existing?.GltfTexturePacking,
            TexturePackingConverted = existing?.TexturePackingConverted
        };

        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var temporaryPath = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return InspectOne(root, modelPath);
    }

    public static string GetManifestPath(string modelFilePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(modelFilePath))!;
        var stem = Path.GetFileNameWithoutExtension(modelFilePath);
        return Path.Combine(directory, stem + ".bjornhjarta.asset.json");
    }

    private static ModelAssetRecord InspectOne(string root, string modelPath)
    {
        var fullPath = Path.GetFullPath(modelPath);
        var relativePath = Path.GetRelativePath(root, fullPath);
        var manifestPath = GetManifestPath(fullPath);
        ModelAssetManifest? manifest = null;
        string? warning = null;
        var hasManifest = File.Exists(manifestPath);

        if (hasManifest)
        {
            try
            {
                manifest = JsonSerializer.Deserialize<ModelAssetManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || manifest.SchemaVersion != 1)
                    warning = "Unsupported or empty Bjornhjarta asset manifest.";
                else if (string.IsNullOrWhiteSpace(manifest.ModelFile))
                    warning = "Asset manifest does not contain a modelFile entry.";
                else if (!string.Equals(Path.GetFileName(manifest.ModelFile), Path.GetFileName(fullPath), StringComparison.OrdinalIgnoreCase))
                    warning = "Manifest modelFile does not match the scanned model filename.";
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                warning = "Manifest could not be read: " + ex.Message;
            }
        }

        var inspection = InspectGltf(fullPath);
        if (!string.IsNullOrWhiteSpace(inspection.Warning))
            warning = string.IsNullOrWhiteSpace(warning) ? inspection.Warning : warning + " " + inspection.Warning;

        var isModelValid = inspection.IsValid && string.IsNullOrWhiteSpace(inspection.Warning);
        var isValid = isModelValid && manifest is not null && manifest.SchemaVersion == 1 &&
                      !string.IsNullOrWhiteSpace(manifest.AssetId) &&
                      !string.IsNullOrWhiteSpace(manifest.TypeId) &&
                      string.IsNullOrWhiteSpace(warning);

        var status = !inspection.IsValid ? "Invalid model" :
            !string.IsNullOrWhiteSpace(warning) ? "Review required" :
            !hasManifest ? "Needs mapping" :
            string.IsNullOrWhiteSpace(manifest?.TypeId) ? "Needs Type ID" : "Ready";

        return new ModelAssetRecord
        {
            AssetId = manifest?.AssetId ?? "",
            DisplayName = string.IsNullOrWhiteSpace(manifest?.DisplayName)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : manifest.DisplayName,
            TypeId = manifest?.TypeId ?? "",
            SourceModelGuid = manifest?.SourceModelGuid,
            ModelFilePath = fullPath,
            RelativeModelFile = relativePath,
            ManifestPath = manifestPath,
            Format = Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
            MeshCount = inspection.MeshCount,
            MaterialCount = inspection.MaterialCount,
            TextureCount = inspection.TextureCount,
            ImageCount = inspection.ImageCount,
            EmbeddedImageCount = inspection.EmbeddedImageCount,
            ExternalImageCount = inspection.ExternalImageCount,
            MaterialChannels = inspection.MaterialChannels,
            PreviewStatus = inspection.PreviewStatus,
            PreviewNotes = inspection.PreviewNotes + (manifest is not null && !string.IsNullOrWhiteSpace(manifest.SourceTexturePacking) && manifest.TexturePackingConverted != true
                ? " Source texture packing was recorded but conversion to glTF ORM is not confirmed." : ""),
            MeshNames = inspection.MeshNames,
            HasManifest = hasManifest,
            IsModelValid = isModelValid,
            IsValid = isValid,
            Status = status,
            Warning = warning,
            Notes = manifest?.Notes,
            SourceTexturePacking = manifest?.SourceTexturePacking,
            GltfTexturePacking = manifest?.GltfTexturePacking,
            TexturePackingConverted = manifest?.TexturePackingConverted
        };
    }

    private static GltfInspection InspectGltf(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return GltfInspection.Invalid("Model file not found.");
            if (info.Length < 20 || info.Length > MaximumModelFileBytes)
                return GltfInspection.Invalid("Model file size is outside the supported range (20 bytes–2 GB).");

            using var jsonDocument = Path.GetExtension(filePath).Equals(".glb", StringComparison.OrdinalIgnoreCase)
                ? ReadGlbJson(filePath)
                : ReadGltfJson(filePath);

            var root = jsonDocument.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object ||
                !asset.TryGetProperty("version", out var versionElement) || versionElement.ValueKind != JsonValueKind.String ||
                !string.Equals(versionElement.GetString(), "2.0", StringComparison.Ordinal))
                return GltfInspection.Invalid("Only glTF 2.0 models are supported.");

            var meshCount = GetArrayLength(root, "meshes");
            var materialCount = GetArrayLength(root, "materials");
            var textureCount = GetArrayLength(root, "textures");
            var imageStorage = GetImageStorage(root);
            var materialChannels = GetMaterialChannels(root);
            var meshNames = GetArrayNames(root, "meshes", 6);
            var referenceWarning = ValidateExternalReferences(root, Path.GetDirectoryName(filePath)!);
            var preview = ModelPreviewInspector.Assess(root);

            return new GltfInspection(true, meshCount, materialCount, textureCount, imageStorage.Count, imageStorage.Embedded, imageStorage.External, materialChannels, meshNames, preview.Status, preview.Details, referenceWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException or UriFormatException or NotSupportedException)
        {
            return GltfInspection.Invalid(ex.Message);
        }
    }

    private static JsonDocument ReadGltfJson(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length > MaximumGltfJsonBytes)
            throw new InvalidDataException("The glTF JSON file is larger than the supported 64 MB limit.");
        return JsonDocument.Parse(stream);
    }

    private static JsonDocument ReadGlbJson(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        if (stream.Length < 20)
            throw new InvalidDataException("The file does not have a complete GLB header.");

        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[..4]) != GlbMagic)
            throw new InvalidDataException("The file does not have a valid GLB header.");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        if (version != 2 || totalLength != stream.Length || totalLength < 20)
            throw new InvalidDataException("Unsupported, truncated, or length-mismatched GLB container.");

        Span<byte> chunkHeader = stackalloc byte[8];
        while (stream.Position + 8 <= totalLength)
        {
            stream.ReadExactly(chunkHeader);
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[..4]);
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
            var chunkEnd = stream.Position + chunkLength;
            if (chunkEnd > totalLength || chunkEnd > stream.Length)
                throw new InvalidDataException("GLB chunk exceeds the container length.");

            if (chunkType == JsonChunkType)
            {
                if (chunkLength > MaximumGltfJsonBytes)
                    throw new InvalidDataException("The GLB JSON chunk is larger than the supported 64 MB limit.");
                var jsonBytes = new byte[checked((int)chunkLength)];
                stream.ReadExactly(jsonBytes);
                return JsonDocument.Parse(jsonBytes);
            }

            stream.Position = chunkEnd;
        }

        throw new InvalidDataException("The GLB container does not contain a JSON chunk.");
    }

    private static int GetArrayLength(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;

    private static (int Count, int Embedded, int External) GetImageStorage(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return (0, 0, 0);

        var embedded = 0;
        var external = 0;
        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
                continue;
            if (image.TryGetProperty("bufferView", out _) ||
                (image.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String &&
                 uri.GetString()!.StartsWith("data:", StringComparison.OrdinalIgnoreCase)))
                embedded++;
            else if (image.TryGetProperty("uri", out var externalUri) && externalUri.ValueKind == JsonValueKind.String)
                external++;
        }

        return (images.GetArrayLength(), embedded, external);
    }

    private static string GetMaterialChannels(JsonElement root)
    {
        if (!root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            return "No material texture channels detected.";

        var channels = new List<string>();
        foreach (var material in materials.EnumerateArray())
        {
            if (material.ValueKind != JsonValueKind.Object)
                continue;

            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.ValueKind == JsonValueKind.Object)
            {
                if (HasObjectProperty(pbr, "baseColorTexture")) AddUnique(channels, "Base Color / Albedo");
                if (HasObjectProperty(pbr, "metallicRoughnessTexture")) AddUnique(channels, "Metallic / Roughness");
            }
            if (HasObjectProperty(material, "normalTexture")) AddUnique(channels, "Normal");
            if (HasObjectProperty(material, "occlusionTexture")) AddUnique(channels, "Occlusion");
            if (HasObjectProperty(material, "emissiveTexture")) AddUnique(channels, "Emissive");
        }

        return channels.Count == 0 ? "No material texture channels detected." : string.Join(" · ", channels);
    }

    private static bool HasObjectProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object;

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
            values.Add(value);
    }

    private static string GetArrayNames(JsonElement root, string propertyName, int limit)
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
            return "";

        var names = values.EnumerateArray()
            .Take(limit)
            .Select(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name));
        return string.Join(", ", names);
    }

    private static string? ValidateExternalReferences(JsonElement root, string modelDirectory)
    {
        var missing = new List<string>();
        var unsafeReferences = new List<string>();
        var rootPath = Path.GetFullPath(modelDirectory);
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var section in new[] { "buffers", "images" })
        {
            if (!root.TryGetProperty(section, out var values) || values.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("uri", out var uriElement) || uriElement.ValueKind != JsonValueKind.String)
                    continue; // GLB bufferView or data URI.

                var uri = uriElement.GetString() ?? "";
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var decoded = Uri.UnescapeDataString(uri);
                if (Uri.TryCreate(decoded, UriKind.Absolute, out _) || Path.IsPathRooted(decoded))
                {
                    unsafeReferences.Add(uri);
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(rootPath, decoded.Replace('/', Path.DirectorySeparatorChar)));
                if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    unsafeReferences.Add(uri);
                else if (!File.Exists(resolved))
                    missing.Add(uri);
            }
        }

        if (unsafeReferences.Count > 0)
            return "Model references paths outside its folder; they will not be followed: " + string.Join(", ", unsafeReferences.Take(3));
        if (missing.Count > 0)
            return "Missing external model resource(s): " + string.Join(", ", missing.Take(3));
        return null;
    }

    private static void EnsureInsideDirectory(string root, string filePath)
    {
        var rootPath = Path.GetFullPath(root);
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!filePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected model must be inside the configured asset library folder.");
    }

    private sealed record GltfInspection(
        bool IsValid,
        int MeshCount,
        int MaterialCount,
        int TextureCount,
        int ImageCount,
        int EmbeddedImageCount,
        int ExternalImageCount,
        string MaterialChannels,
        string MeshNames,
        string PreviewStatus,
        string PreviewNotes,
        string? Warning)
    {
        public static GltfInspection Invalid(string message) => new(false, 0, 0, 0, 0, 0, 0, "", "", "Cannot preview", message, message);
    }
}
