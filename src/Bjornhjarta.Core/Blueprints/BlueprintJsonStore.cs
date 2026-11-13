using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bjornhjarta.Core.Blueprints;

/// <summary>Versioned local blueprint I/O; writes to a temporary file before replacing an existing file.</summary>
public sealed class BlueprintJsonStore
{
    private const long MaximumBlueprintBytes = 100L * 1024 * 1024;
    private const int MaximumElements = 100_000;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task SaveAsync(BlueprintDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(temporary).Length > MaximumBlueprintBytes)
                throw new InvalidDataException("Blueprint exceeds the 100 MB size limit.");
            if (File.Exists(fullPath))
            {
                // The previous version is kept as a local .bak file. File.Replace
                // commits atomically on a local NTFS volume without truncating the original.
                File.Replace(temporary, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
            }
            else
                File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<BlueprintDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > MaximumBlueprintBytes)
            throw new InvalidDataException("Blueprint exceeds the 100 MB size limit.");
        await using var stream = File.OpenRead(fullPath);
        var document = await JsonSerializer.DeserializeAsync<BlueprintDocument>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("The selected file did not contain a valid blueprint.");
        ValidateDocument(document);
        return document;
    }

    /// <summary>
    /// Defensive schema checks applied to both imports and exports.  This is not
    /// game-specific collision validation, which remains the editor's task.
    /// </summary>
    private static void ValidateDocument(BlueprintDocument document)
    {
        if (document.SchemaVersion != 1 || document.Elements is null || document.Metadata is null || document.Origin is null)
            throw new InvalidDataException("Blueprint has an unsupported schema or missing required fields.");
        if (string.IsNullOrWhiteSpace(document.Metadata.Name) || document.Metadata.Name.Length > 512 ||
            document.Metadata.Description is null || document.Metadata.Description.Length > 16_384)
            throw new InvalidDataException("Blueprint metadata contains a missing or excessively long value.");
        if (document.Elements.Count > MaximumElements)
            throw new InvalidDataException($"Blueprint exceeds the {MaximumElements:N0} element safety limit.");
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool FiniteVector(BlueprintVector3? position) => position is not null &&
            Finite(position.X) && Finite(position.Y) && Finite(position.Z);
        if (!FiniteVector(document.Origin))
            throw new InvalidDataException("Blueprint origin has invalid coordinates.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.Elements)
        {
            if (element is null || string.IsNullOrWhiteSpace(element.Id) || element.Id.Length > 256 ||
                !ids.Add(element.Id))
                throw new InvalidDataException("Blueprint contains a missing, repeated or invalid element ID.");
            if (string.IsNullOrWhiteSpace(element.TypeId) || element.TypeId.Length > 1024 ||
                !FiniteVector(element.Position) || element.Rotation is null ||
                !Finite(element.Rotation.Pitch) || !Finite(element.Rotation.Yaw) || !Finite(element.Rotation.Roll))
                throw new InvalidDataException($"Blueprint element {element.Id} has invalid type or coordinates.");
            if ((element.ModelAssetId?.Length ?? 0) > 1024 || (element.ModelAssetName?.Length ?? 0) > 2048 ||
                (element.Variant?.Length ?? 0) > 2048)
                throw new InvalidDataException($"Blueprint element {element.Id} contains an excessively long field.");
        }
    }

}
