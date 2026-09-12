using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Persistence;

/// <summary>Storage boundary used by the queue. Implementations may use SQLite,
/// an in-memory store, or a platform-provided database.</summary>
public interface IDownloadRepository : IAsyncDisposable
{
    Task<List<DownloadItem>> GetAllAsync();
    Task<DownloadItem?> GetAsync(Guid id);
    Task<int> UpsertAsync(DownloadItem item);
    Task<int> DeleteAsync(DownloadItem item);
}
