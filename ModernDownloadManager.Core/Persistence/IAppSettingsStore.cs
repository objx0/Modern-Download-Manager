using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Persistence;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync();
    Task SaveAsync(AppSettings settings);
}
