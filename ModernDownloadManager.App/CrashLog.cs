using System.Text;

namespace ModernDownloadManager.App;

internal static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernDownloadManager", "crash.log");

    public static void Write(string source, Exception exception)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath,
                    $"[{DateTimeOffset.Now:u}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { }
    }
}
