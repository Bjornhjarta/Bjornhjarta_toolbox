using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bjornhjarta.Core.Modding;

public sealed record EmlModArchivePreview(
    string ModId, string Name, string Version, int FileCount, long UncompressedBytes,
    bool IsReplacement, string TargetPath, string ArchiveSha256);

public sealed record EmlModInstallResult(string InstalledPath, string? BackupPath, string ModId);

/// <summary>
/// Safely imports a single user-selected EML mod ZIP into one selected Enshrouded
/// installation. No network access, code execution or mod-loader changes.
/// Callers must check that the relevant game/server process is stopped before writing.
/// </summary>
public sealed class EmlModPackageManager
{
    private const int MaxEntries = 2048;
    private const long MaxUncompressedBytes = 128L * 1024 * 1024;
    private const long MaxSingleFileBytes = 64L * 1024 * 1024;
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private static readonly Regex ModIdPattern = new("^[a-z][a-z0-9_-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemVerPattern = new("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.OrdinalIgnoreCase) { "patch", "export", "runtime" };

    public EmlModArchivePreview Preview(string installationRoot, string archivePath)
    {
        var root = VerifyInstallation(installationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var archiveFile = new FileInfo(Path.GetFullPath(archivePath));
        if (!archiveFile.Exists || archiveFile.Length > MaxArchiveBytes)
            throw new InvalidDataException("The selected ZIP is missing or exceeds the 128 MB archive limit.");

        using var archive = ZipFile.OpenRead(archiveFile.FullName);
        var plan = AnalyzeArchive(archive);
        var modsRoot = Path.Combine(root, "mods");
        RejectReparsePoint(modsRoot);
        var target = Path.Combine(modsRoot, plan.ModId);
        RejectReparsePoint(target);
        return new EmlModArchivePreview(plan.ModId, plan.Name, plan.Version, plan.FileCount,
            plan.TotalBytes, Directory.Exists(target), target, ComputeArchiveHash(archiveFile.FullName));
    }

    public EmlModInstallResult Install(string installationRoot, string archivePath, string expectedModId,
        string? expectedArchiveSha256 = null)
    {
        var root = VerifyInstallation(installationRoot);
        var preview = Preview(root, archivePath);
        if (!string.Equals(preview.ModId, expectedModId, StringComparison.Ordinal) ||
            (expectedArchiveSha256 is not null && !string.Equals(preview.ArchiveSha256, expectedArchiveSha256, StringComparison.Ordinal)))
            throw new InvalidOperationException("The archive changed after preview. Review it again before installing.");

        var modsRoot = Path.Combine(root, "mods");
        var privateRoot = Path.Combine(root, ".bjornhjarta");
        var stagingRoot = Path.Combine(privateRoot, "staging");
        var backupsRoot = Path.Combine(privateRoot, "backups", "eml-mods");
        var disabledRoot = Path.Combine(privateRoot, "disabled-mods");
        foreach (var path in new[] { modsRoot, privateRoot, stagingRoot, backupsRoot, disabledRoot })
            RejectReparsePoint(path);

        var disabledPath = Path.Combine(disabledRoot, preview.ModId);
        if (Directory.Exists(disabledPath))
            throw new InvalidOperationException("This mod is disabled. Enable it before attempting an update, or handle the disabled copy manually.");

        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(stagingRoot);
        var stagedPath = Path.Combine(stagingRoot, $"{preview.ModId}-{Guid.NewGuid():N}");
        var targetPath = preview.TargetPath;
        string? backupPath = null;
        var installed = false;

        try
        {
            Directory.CreateDirectory(stagedPath);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var plan = AnalyzeArchive(archive);
                if (plan.ModId != preview.ModId || plan.FileCount != preview.FileCount ||
                    plan.TotalBytes != preview.UncompressedBytes || plan.Version != preview.Version)
                    throw new InvalidOperationException("The archive changed after preview. Installation cancelled.");
                foreach (var (entry, relativeName) in plan.Files)
                {
                    var outputPath = Path.GetFullPath(Path.Combine(stagedPath, relativeName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!IsChildOf(stagedPath, outputPath))
                        throw new InvalidDataException("Archive contains a path outside the staging directory.");
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    using var input = entry.Open();
                    using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyWithLimit(input, output, entry.Length);
                }
            }
            // Do not move any live mod if the archive changed during extraction.
            if (!string.Equals(preview.ArchiveSha256, ComputeArchiveHash(archivePath), StringComparison.Ordinal))
                throw new InvalidOperationException("The ZIP changed during extraction. No installed mod was changed.");
            // No write to the active mod until the entire archive is validated and extracted.
            RejectReparsePoint(targetPath);
            if (Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(backupsRoot);
                backupPath = Path.Combine(backupsRoot, $"{preview.ModId}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
                Directory.Move(targetPath, backupPath);
            }
            try
            {
                Directory.Move(stagedPath, targetPath);
                installed = true;
            }
            catch
            {
                if (backupPath is not null && !Directory.Exists(targetPath) && Directory.Exists(backupPath))
                    Directory.Move(backupPath, targetPath);
                throw;
            }
            return new EmlModInstallResult(targetPath, backupPath, preview.ModId);
        }
        finally
        {
            if (!installed && Directory.Exists(stagedPath))
                Directory.Delete(stagedPath, recursive: true);
        }
    }

    public string Disable(string installationRoot, string modId)
    {
        var root = VerifyInstallation(installationRoot);
        ValidateModId(modId);
        var activePath = Path.Combine(root, "mods", modId);
        var disabledRoot = Path.Combine(root, ".bjornhjarta", "disabled-mods");
        var disabledPath = Path.Combine(disabledRoot, modId);
        foreach (var path in new[] { Path.Combine(root, "mods"), Path.Combine(root, ".bjornhjarta"), disabledRoot, activePath, disabledPath })
            RejectReparsePoint(path);
        if (!Directory.Exists(activePath))
            throw new DirectoryNotFoundException("The selected active mod folder was not found.");
        if (Directory.Exists(disabledPath))
            throw new InvalidOperationException("A disabled copy with the same mod ID already exists.");
        ValidateExistingManifest(activePath, modId);
        Directory.CreateDirectory(disabledRoot);
        Directory.Move(activePath, disabledPath);
        return disabledPath;
    }

    public string Enable(string installationRoot, string modId)
    {
        var root = VerifyInstallation(installationRoot);
        ValidateModId(modId);
        var modsRoot = Path.Combine(root, "mods");
        var disabledRoot = Path.Combine(root, ".bjornhjarta", "disabled-mods");
        var disabledPath = Path.Combine(disabledRoot, modId);
        var activePath = Path.Combine(modsRoot, modId);
        foreach (var path in new[] { Path.Combine(root, ".bjornhjarta"), modsRoot, disabledRoot, activePath, disabledPath })
            RejectReparsePoint(path);
        if (!Directory.Exists(disabledPath))
            throw new DirectoryNotFoundException("The selected disabled mod was not found.");
        if (Directory.Exists(activePath))
            throw new InvalidOperationException("An active mod folder with the same ID already exists.");
        ValidateExistingManifest(disabledPath, modId);
        Directory.CreateDirectory(modsRoot);
        Directory.Move(disabledPath, activePath);
        return activePath;
    }

    private static string ComputeArchiveHash(string archivePath)
    {
        using var input = File.OpenRead(archivePath);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static string VerifyInstallation(string installationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        var root = Path.GetFullPath(installationRoot);
        if (!Directory.Exists(root) ||
            (!File.Exists(Path.Combine(root, "enshrouded.exe")) && !File.Exists(Path.Combine(root, "enshrouded_server.exe"))))
            throw new InvalidOperationException("Select a directory containing enshrouded.exe or enshrouded_server.exe.");
        RejectReparsePoint(root);
        return root;
    }

    private static void ValidateModId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !ModIdPattern.IsMatch(id))
            throw new InvalidDataException("The mod ID is missing or invalid.");
    }

    private static void ValidateExistingManifest(string directory, string expectedId)
    {
        var manifest = Path.Combine(directory, "mod.json");
        if (!File.Exists(manifest))
            throw new InvalidDataException("The mod folder has no mod.json. No files were moved.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        if (!document.RootElement.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String || id.GetString() != expectedId)
            throw new InvalidDataException("The mod manifest ID does not match its folder. No files were moved.");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Symbolic links and directory junctions are not supported for EML mod operations.");
    }

    private static bool IsChildOf(string root, string path) =>
        path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void CopyWithLimit(Stream input, Stream output, long expectedLength)
    {
        var buffer = new byte[64 * 1024];
        long count = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            count += read;
            if (count > MaxSingleFileBytes || count > expectedLength)
                throw new InvalidDataException("Archive file exceeded its declared or permitted size.");
            output.Write(buffer, 0, read);
        }
        if (count != expectedLength)
            throw new InvalidDataException("Archive entry ended before its declared size.");
    }

    private sealed record ArchivePlan(string ModId, string Name, string Version, int FileCount,
        long TotalBytes, IReadOnlyList<(ZipArchiveEntry Entry, string RelativePath)> Files);

    private static ArchivePlan AnalyzeArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("ZIP must contain 1–2048 entries.");
        var files = new List<(ZipArchiveEntry Entry, string Name)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            ValidateArchivePath(path);
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidDataException("ZIP symbolic links are not supported.");
            if (path.EndsWith('/'))
                continue;
            if (!seen.Add(path))
                throw new InvalidDataException("ZIP contains duplicate file names (case insensitive).");
            if (entry.Length > MaxSingleFileBytes || entry.Length < 0 || bytes > MaxUncompressedBytes - entry.Length)
                throw new InvalidDataException("ZIP exceeds safe decompressed size limits.");
            bytes += entry.Length;
            files.Add((entry, path));
        }
        var manifests = files.Where(f => string.Equals(Path.GetFileName(f.Name), "mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1)
            throw new InvalidDataException("ZIP must contain exactly one mod.json manifest.");
        var manifestPath = manifests[0].Name;
        var folderPrefix = manifestPath == "mod.json" ? "" : manifestPath[..^"mod.json".Length];
        if (!string.IsNullOrEmpty(folderPrefix) && (folderPrefix.Count(c => c == '/') != 1 || !folderPrefix.EndsWith('/')))
            throw new InvalidDataException("mod.json must be at the ZIP root or within one top-level mod folder.");
        if (files.Any(f => !f.Name.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ZIP contains files outside the single mod package folder.");
        using var stream = manifests[0].Entry.Open();
        if (manifests[0].Entry.Length > 512 * 1024)
            throw new InvalidDataException("mod.json exceeds the 512 KB manifest limit.");
        using var json = JsonDocument.Parse(stream);
        var obj = json.RootElement;
        if (obj.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("mod.json must be a JSON object.");
        static string RequiredString(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException($"mod.json is missing a valid {key} field.");
            return value.GetString()!;
        }
        var id = RequiredString(obj, "id");
        ValidateModId(id);
        if (folderPrefix.Length > 0 && !string.Equals(folderPrefix.TrimEnd('/'), id, StringComparison.Ordinal))
            throw new InvalidDataException("ZIP top-level folder must match the manifest mod ID.");
        var name = RequiredString(obj, "name");
        var version = RequiredString(obj, "version");
        if (!SemVerPattern.IsMatch(version))
            throw new InvalidDataException("mod.json version must follow semantic versioning.");
        if (!obj.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
            capabilities.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || !KnownCapabilities.Contains(v.GetString() ?? "")))
            throw new InvalidDataException("mod.json capabilities must be an array of patch/export/runtime strings.");
        return new ArchivePlan(id, name, version, files.Count, bytes,
            files.Select(f => (f.Entry, f.Name[folderPrefix.Length..])).ToArray());
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') ||
            path.Any(c => c < ' ' || "<>\"|?*".Contains(c)))
            throw new InvalidDataException("ZIP contains an unsafe path.");
        var segments = path.TrimEnd('/').Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' '))
                throw new InvalidDataException("ZIP contains invalid or traversal path segments.");
            var baseName = segment.Split('.')[0].ToUpperInvariant();
            if (baseName is "CON" or "PRN" or "AUX" or "NUL" ||
                (baseName.Length == 4 && (baseName.StartsWith("COM") || baseName.StartsWith("LPT")) && baseName[3] is >= '1' and <= '9'))
                throw new InvalidDataException("ZIP contains reserved Windows file names.");
        }
    }
}
