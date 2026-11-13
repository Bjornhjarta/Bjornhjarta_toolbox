using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bjornhjarta.Core.Modding;

public sealed record EmlConfigSettings(
    bool EnableConsole = false,
    bool UseExportFlag = false,
    string ExportDirectory = "export");

/// <summary>
/// Reads and updates the documented EML settings while preserving unknown JSON fields.
/// Existing configuration is backed up before it is changed.
/// </summary>
public sealed class EmlConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public EmlConfigSettings Load(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        if (!File.Exists(configPath))
            return new EmlConfigSettings();

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(File.ReadAllText(configPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The existing eml.json is not valid JSON. It was left untouched.", ex);
        }

        if (parsed is not JsonObject root)
            throw new InvalidDataException("The existing eml.json must contain a JSON object. It was left untouched.");

        return new EmlConfigSettings(
            ReadBoolean(root, "enable_console"),
            ReadBoolean(root, "use_export_flag"),
            ReadString(root, "export_directory") ?? "export");
    }

    /// <returns>The backup file path, or null when no previous file existed.</returns>
    public string? Save(string configPath, EmlConfigSettings settings, string installationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);

        var fullConfigPath = Path.GetFullPath(configPath);
        var fullRoot = Path.GetFullPath(installationRoot);
        if (!string.Equals(Path.GetDirectoryName(fullConfigPath)?.TrimEnd(Path.DirectorySeparatorChar),
            fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(fullConfigPath), "eml.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("EML configuration must be the eml.json file inside the selected installation directory.");
        Directory.CreateDirectory(fullRoot);

        JsonObject root;
        if (File.Exists(fullConfigPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(fullConfigPath)) as JsonObject
                    ?? throw new InvalidDataException("The existing eml.json must contain a JSON object. It was left untouched.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The existing eml.json is not valid JSON. It was left untouched.", ex);
            }
        }
        else
        {
            root = new JsonObject();
        }

        // EML's documented keys. Other properties in the user's file are retained.
        root["enable_console"] = settings.EnableConsole;
        root["use_export_flag"] = settings.UseExportFlag;
        root["export_directory"] = string.IsNullOrWhiteSpace(settings.ExportDirectory)
            ? "export"
            : settings.ExportDirectory.Trim();

        string? backupPath = null;
        var originalExists = File.Exists(fullConfigPath);
        if (originalExists)
        {
            var backupDirectory = Path.Combine(fullRoot, ".bjornhjarta", "backups", "eml-config");
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory,
                $"eml-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json.bak");
        }

        var directory = Path.GetDirectoryName(fullConfigPath)
            ?? throw new InvalidOperationException("Could not determine the eml.json directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"eml.json.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, root, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (originalExists)
                File.Replace(temporaryPath, fullConfigPath, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, fullConfigPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return backupPath;
    }

    private static bool ReadBoolean(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result))
            return result;
        return false;
    }

    private static string? ReadString(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonValue value && value.TryGetValue<string>(out var result))
            return result;
        return null;
    }
}
