using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Ipc;

/// <summary>Local process-to-process transport used by browser adapters.</summary>
public interface ILocalIpc
{
    Task RunServerAsync(Func<DownloadRequestMessage, Task> onRequest, CancellationToken cancellationToken);
    Task<bool> TrySendAsync(DownloadRequestMessage request, TimeSpan timeout);
    Task<bool> TrySendWithRetryAsync(DownloadRequestMessage request, int attempts = 12,
        int timeoutMilliseconds = 500);
}
