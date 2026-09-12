using ModernDownloadManager.Core.Models;
using SQLite;

namespace ModernDownloadManager.Core.Persistence;

/// <summary>
/// Persists the download queue/history so the app can restore state after a
/// restart or crash (in-progress items resume from their temp-file byte offset).
/// </summary>
public sealed class DownloadRepository : IDownloadRepository
{
    private readonly SQLiteAsyncConnection _db;

    private DownloadRepository(SQLiteAsyncConnection db) => _db = db;

    public static async Task<DownloadRepository> CreateAsync(string dbPath)
    {
        var db = new SQLiteAsyncConnection(dbPath);
        await db.CreateTableAsync<DownloadItem>();
        return new DownloadRepository(db);
    }

    public Task<List<DownloadItem>> GetAllAsync() =>
        _db.Table<DownloadItem>().OrderByDescending(d => d.CreatedAt).ToListAsync();

    public Task<DownloadItem?> GetAsync(Guid id) =>
        _db.Table<DownloadItem>().Where(d => d.Id == id).FirstOrDefaultAsync()!;

    public Task<int> UpsertAsync(DownloadItem item) => _db.InsertOrReplaceAsync(item);

    public Task<int> DeleteAsync(DownloadItem item) => _db.DeleteAsync(item);

    public ValueTask DisposeAsync() => new(_db.CloseAsync());
}
