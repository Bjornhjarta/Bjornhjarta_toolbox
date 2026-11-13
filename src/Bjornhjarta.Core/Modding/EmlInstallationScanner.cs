using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bjornhjarta.Core.Modding;

public sealed record EmlModPackage(
    string Id,
    string Name,
    string? Version,
    string CapabilitySummary,
    string PackagePath,
    string MetadataStatus,
    string? Description,
    bool IsEnabled = true)
{
    public string EnabledStatus => IsEnabled ? "Enabled" : "Disabled";
}

public sealed record EmlInstallationScan(
    string RootPath,
    bool HasClientExecutable,
    bool HasServerExecutable,
    bool HasEmlProxy,
    string? EmlProxyPath,
    bool HasAlternativeProxy,
    string? AlternativeProxyPath,
    string ModsPath,
    bool ModsDirectoryExists,
    string ConfigPath,
    bool ConfigExists,
    IReadOnlyList<EmlModPackage> Mods,
    IReadOnlyList<string> Warnings)
{
    public bool LooksLikeEnshroudedInstallation => HasClientExecutable || HasServerExecutable;
}

/// <summary>
/// Reads an EML installation without changing game files. Mod manifests are
/// inspected at the documented package root: mods/&lt;mod-id&gt;/mod.json.
/// </summary>
public sealed class EmlInstallationScanner
{
    private static readonly Regex ModIdPattern = new("^[a-z][a-z0-9_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemanticVersionPattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch", "export", "runtime"
    };

    public EmlInstallationScan Scan(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The selected directory does not exist: {root}");

        var clientExe = Path.Combine(root, "enshrouded.exe");
        var serverExe = Path.Combine(root, "enshrouded_server.exe");
        var loader = Path.Combine(root, "dbghelp.dll");
        var alternativeProxy = Path.Combine(root, "dinput8.dll");
        var modsPath = Path.Combine(root, "mods");
        var configPath = Path.Combine(root, "eml.json");
        var warnings = new List<string>();

        var hasClient = File.Exists(clientExe);
        var hasServer = File.Exists(serverExe);
        var hasLoader = File.Exists(loader);
        var hasAlternative = File.Exists(alternativeProxy);

        if (!hasClient && !hasServer)
            warnings.Add("No Enshrouded client or dedicated-server executable was found in this directory.");
        if (!hasLoader)
            warnings.Add("The EML proxy (dbghelp.dll) was not found in the selected directory.");
        if (hasAlternative && !hasLoader)
            warnings.Add("A dinput8.dll proxy was found. EML documentation lists this as a client troubleshooting alternative; verify its origin before relying on it.");
        if (!Directory.Exists(modsPath))
            warnings.Add("The EML mods directory does not exist yet.");

        var packages = Directory.Exists(modsPath)
            ? DiscoverPackages(modsPath, warnings).ToList()
            : new List<EmlModPackage>();

        AddDuplicateIdWarnings(packages);
        // Inactive mods are kept OUTSIDE mods/ so EML cannot discover them.
        var disabledRoot = Path.Combine(root, ".bjornhjarta", "disabled-mods");
        if (Directory.Exists(disabledRoot))
        {
            if ((File.GetAttributes(disabledRoot) & FileAttributes.ReparsePoint) != 0)
                warnings.Add("Disabled mods directory is a symbolic link/junction and has not been scanned.");
            else
                packages.AddRange(DiscoverPackages(disabledRoot, warnings).Select(mod => mod with { IsEnabled = false }));
        }

        return new EmlInstallationScan(
            root,
            hasClient,
            hasServer,
            hasLoader,
            hasLoader ? loader : null,
            hasAlternative,
            hasAlternative ? alternativeProxy : null,
            modsPath,
            Directory.Exists(modsPath),
            configPath,
            File.Exists(configPath),
            packages,
            warnings);
    }

    private static IReadOnlyList<EmlModPackage> DiscoverPackages(string modsPath, List<string> warnings)
    {
        var packages = new List<EmlModPackage>();

        // Standard EML packages are folders named after the mod ID, with mod.json
        // at the top level of each folder.
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsPath)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate the mods directory: {ex.Message}");
            return packages;
        }

        try
        {
            var looseFiles = Directory.EnumerateFiles(modsPath).ToArray();
            if (looseFiles.Length > 0)
                warnings.Add($"Found {looseFiles.Length} loose file(s) directly in mods. The inventory expects each EML mod in its own folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect files in the mods directory: {ex.Message}");
        }

        foreach (var directory in directories)
        {
            var manifest = Path.Combine(directory, "mod.json");
            if (File.Exists(manifest))
            {
                packages.Add(ReadPackage(directory, manifest));
            }
            else
            {
                var folderName = Path.GetFileName(directory);
                packages.Add(new EmlModPackage(
                    folderName,
                    folderName,
                    null,
                    string.Empty,
                    directory,
                    "Folder found, but the required root mod.json manifest is missing.",
                    null));
            }
        }

        return packages;
    }

    private static EmlModPackage ReadPackage(string packageDirectory, string manifestPath)
    {
        var fallbackName = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidPackage(fallbackName, packageDirectory, "mod.json must contain a JSON object.");
            }

            var id = ReadString(root, "id") ?? string.Empty;
            var name = ReadString(root, "name") ?? string.Empty;
            var version = ReadString(root, "version");
            var description = ReadString(root, "description");
            var capabilities = ReadStringArray(root, "capabilities");
            var issues = new List<string>();

            if (string.IsNullOrWhiteSpace(id))
                issues.Add("Required id is missing");
            else if (!ModIdPattern.IsMatch(id))
                issues.Add("id must start with a lowercase letter and contain only lowercase letters, numbers, hyphens or underscores");

            if (string.IsNullOrWhiteSpace(name))
                issues.Add("Required name is missing");

            if (string.IsNullOrWhiteSpace(version))
                issues.Add("Required version is missing");
            else if (!SemanticVersionPattern.IsMatch(version))
                issues.Add("version is not valid semantic-version syntax");

            if (capabilities is null)
            {
                issues.Add("Required capabilities array is missing or invalid");
                capabilities = [];
            }
            else
            {
                var unknown = capabilities.Where(capability => !KnownCapabilities.Contains(capability)).ToArray();
                if (unknown.Length > 0)
                    issues.Add($"Unknown capability: {string.Join(", ", unknown)}");
                if (capabilities.Any(capability => string.Equals(capability, "runtime", StringComparison.OrdinalIgnoreCase)))
                    issues.Add("runtime capability is marked WIP in the EML developer documentation");
            }

            if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, fallbackName, StringComparison.Ordinal))
                issues.Add("Package folder name does not match manifest id");

            var status = issues.Count == 0 ? "Manifest fields look valid" : string.Join("; ", issues);
            var displayName = string.IsNullOrWhiteSpace(name) ? fallbackName : name;
            return new EmlModPackage(
                id,
                displayName,
                version,
                string.Join(", ", capabilities),
                packageDirectory,
                status,
                description);
        }
        catch (JsonException)
        {
            return InvalidPackage(fallbackName, packageDirectory, "mod.json exists but contains invalid JSON.");
        }
        catch (IOException ex)
        {
            return InvalidPackage(fallbackName, packageDirectory, $"Could not read mod.json: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return InvalidPackage(fallbackName, packageDirectory, "mod.json could not be accessed.");
        }
    }

    private static EmlModPackage InvalidPackage(string fallbackName, string packageDirectory, string status) =>
        new(string.Empty, fallbackName, null, string.Empty, packageDirectory, status, null);

    private static void AddDuplicateIdWarnings(List<EmlModPackage> packages)
    {
        var duplicateIds = packages
            .Where(package => !string.IsNullOrWhiteSpace(package.Id))
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < packages.Count; index++)
        {
            var package = packages[index];
            if (duplicateIds.Contains(package.Id))
                packages[index] = package with { MetadataStatus = package.MetadataStatus + "; duplicate mod ID detected" };
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static List<string>? ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            var capability = item.GetString();
            if (string.IsNullOrWhiteSpace(capability))
                return null;
            result.Add(capability);
        }

        return result;
    }
}
