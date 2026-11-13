using System.Text.Json;

namespace Bjornhjarta.Core.Modding;

public enum EmlInstallationProfileKind
{
    Client,
    DedicatedServer
}

/// <summary>
/// Per-user paths for the two independent Enshrouded installations. The EMM CLI
/// is shared because the same executable can target either game directory.
/// </summary>
public sealed record EmlManagerPreferences
{
    public string? ClientRootPath { get; init; }
    public string? DedicatedServerRootPath { get; init; }
    public string? EmmPath { get; init; }
    public EmlInstallationProfileKind LastActiveProfile { get; init; } = EmlInstallationProfileKind.Client;

    public string? GetRootPath(EmlInstallationProfileKind profile) => profile switch
    {
        EmlInstallationProfileKind.Client => ClientRootPath,
        EmlInstallationProfileKind.DedicatedServer => DedicatedServerRootPath,
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    public EmlManagerPreferences WithRootPath(EmlInstallationProfileKind profile, string? rootPath) => profile switch
    {
        EmlInstallationProfileKind.Client => this with { ClientRootPath = rootPath },
        EmlInstallationProfileKind.DedicatedServer => this with { DedicatedServerRootPath = rootPath },
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}

/// <summary>
/// Persists separate client/server roots and the shared EMM CLI path. The first
/// load migrates the previous single-root preferences without discarding them.
/// </summary>
public sealed class EmlManagerPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsDirectory;
    private readonly string _preferencesPath;

    public EmlManagerPreferencesStore(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        _settingsDirectory = Path.GetFullPath(settingsDirectory);
        _preferencesPath = Path.Combine(_settingsDirectory, "eml-manager.json");
    }

    public EmlManagerPreferences Load()
    {
        if (File.Exists(_preferencesPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<EmlManagerPreferences>(File.ReadAllText(_preferencesPath), JsonOptions)
                    ?? throw new InvalidDataException(
                        $"Bjornhjarta's EML preferences contain JSON null: {_preferencesPath}. The file was left untouched.");
                if (!Enum.IsDefined(typeof(EmlInstallationProfileKind), loaded.LastActiveProfile))
                    throw new InvalidDataException(
                        $"Bjornhjarta's EML preferences contain an unknown profile: {_preferencesPath}. The file was left untouched.");
                return loaded;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Bjornhjarta's EML preferences file is not valid JSON: {_preferencesPath}. The file was left untouched.", ex);
            }
        }

        return MigrateLegacyPreferences();
    }

    public void Save(EmlManagerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!Enum.IsDefined(typeof(EmlInstallationProfileKind), preferences.LastActiveProfile))
            throw new InvalidDataException("Cannot save EML preferences with an unsupported installation profile.");
        Directory.CreateDirectory(_settingsDirectory);

        var temporaryPath = Path.Combine(_settingsDirectory, $"eml-manager.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_preferencesPath))
                File.Replace(temporaryPath, _preferencesPath, _preferencesPath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _preferencesPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private EmlManagerPreferences MigrateLegacyPreferences()
    {
        var rootPathFile = Path.Combine(_settingsDirectory, "enshrouded-root.txt");
        var emmPathFile = Path.Combine(_settingsDirectory, "emm-path.txt");
        string? clientRoot = null;
        string? serverRoot = null;
        string? emmPath = null;
        var lastActiveProfile = EmlInstallationProfileKind.Client;

        if (File.Exists(rootPathFile))
        {
            var oldRoot = File.ReadAllText(rootPathFile).Trim();
            if (!string.IsNullOrWhiteSpace(oldRoot) && Directory.Exists(oldRoot))
            {
                var hasClient = File.Exists(Path.Combine(oldRoot, "enshrouded.exe"));
                var hasServer = File.Exists(Path.Combine(oldRoot, "enshrouded_server.exe"));
                if (hasServer && !hasClient)
                {
                    serverRoot = Path.GetFullPath(oldRoot);
                    lastActiveProfile = EmlInstallationProfileKind.DedicatedServer;
                }
                else if (hasClient)
                {
                    clientRoot = Path.GetFullPath(oldRoot);
                    lastActiveProfile = EmlInstallationProfileKind.Client;
                }
            }
        }

        if (File.Exists(emmPathFile))
        {
            var candidate = File.ReadAllText(emmPathFile).Trim();
            if (File.Exists(candidate) && string.Equals(Path.GetFileName(candidate), "emm.exe", StringComparison.OrdinalIgnoreCase))
                emmPath = Path.GetFullPath(candidate);
        }

        return new EmlManagerPreferences
        {
            ClientRootPath = clientRoot,
            DedicatedServerRootPath = serverRoot,
            EmmPath = emmPath,
            LastActiveProfile = lastActiveProfile
        };
    }
}
