using System.IO;

namespace Bjornhjarta.Desktop;

internal static class AppDiagnostics
{
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private static readonly object Sync = new();
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bjornhjarta", "Logs", "bjornhjarta.log");

    public static void WriteEvent(string message) => Append($"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");

    public static void WriteException(string context, Exception exception) => Append(
        $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");

    private static void Append(string entry)
    {
        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(directory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length + entry.Length * 2 > MaxLogBytes)
                    File.Move(LogPath, Path.Combine(directory, "bjornhjarta.previous.log"), overwrite: true);
                File.AppendAllText(LogPath, entry);
            }
        }
        catch
        {
            // Diagnostics must not introduce a second crash.
        }
    }
}
