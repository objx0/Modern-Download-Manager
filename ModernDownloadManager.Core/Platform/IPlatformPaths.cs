namespace ModernDownloadManager.Core.Platform;

/// <summary>OS-specific locations used by the application composition root.</summary>
public interface IPlatformPaths
{
    string ApplicationDataDirectory { get; }
    string DefaultDownloadDirectory { get; }
}
