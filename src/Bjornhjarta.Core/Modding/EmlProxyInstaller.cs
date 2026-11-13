namespace Bjornhjarta.Core.Modding;

public sealed record EmlProxyInstallResult(string InstalledProxyPath, string ModsDirectoryPath, string? PreviousProxyBackupPath);

/// <summary>
/// Installs a user-supplied EML proxy DLL. It never downloads or executes the DLL.
/// Any existing dbghelp.dll is backed up before replacement.
/// </summary>
public sealed class EmlProxyInstaller
{
    public EmlProxyInstallResult Install(string installationRoot, string selectedProxyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedProxyPath);

        var root = Path.GetFullPath(installationRoot);
        var source = Path.GetFullPath(selectedProxyPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"The game directory does not exist: {root}");
        if (!File.Exists(Path.Combine(root, "enshrouded.exe")) && !File.Exists(Path.Combine(root, "enshrouded_server.exe")))
            throw new InvalidOperationException("The selected destination does not contain enshrouded.exe or enshrouded_server.exe.");
        if (!File.Exists(source))
            throw new FileNotFoundException("The selected proxy DLL could not be found.", source);
        if (!string.Equals(Path.GetFileName(source), "dbghelp.dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select the dbghelp.dll file from the EML release archive.");

        var modsDirectory = Path.Combine(root, "mods");
        Directory.CreateDirectory(modsDirectory);

        var destination = Path.Combine(root, "dbghelp.dll");
        string? backupPath = null;
        if (File.Exists(destination))
        {
            var backupDirectory = Path.Combine(root, ".bjornhjarta", "backups", "eml-loader");
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, $"dbghelp-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.dll.bak");
            File.Copy(destination, backupPath, overwrite: false);
        }

        var temporaryPath = Path.Combine(root, $"dbghelp.dll.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new EmlProxyInstallResult(destination, modsDirectory, backupPath);
    }
}
