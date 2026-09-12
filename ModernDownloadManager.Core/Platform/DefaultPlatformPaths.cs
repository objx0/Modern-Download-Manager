namespace ModernDownloadManager.Core.Platform;

/// <summary>
/// Sensible conventions for Windows, macOS, and Linux. A platform UI may
/// replace this with a stricter implementation when it needs native APIs.
/// </summary>
public sealed class DefaultPlatformPaths : IPlatformPaths
{
    public string ApplicationDataDirectory { get; }
    public string DefaultDownloadDirectory { get; }

    public DefaultPlatformPaths(string? applicationName = null)
    {
        applicationName ??= "ModernDownloadManager";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DefaultDownloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (OperatingSystem.IsWindows())
        {
            ApplicationDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), applicationName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            ApplicationDataDirectory = Path.Combine(home, "Library", "Application Support", applicationName);
        }
        else
        {
            var dataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataRoot))
                dataRoot = Path.Combine(home, ".local", "share");
            ApplicationDataDirectory = Path.Combine(dataRoot, applicationName);
        }
    }
}
