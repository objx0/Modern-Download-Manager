using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Ipc;

/// <summary>
/// Default .NET local transport. The application depends on <see cref="ILocalIpc"/>
/// so Unix-domain sockets or another native transport can be substituted later.
/// </summary>
public sealed class NamedPipeIpc : ILocalIpc
{
    public Task RunServerAsync(Func<DownloadRequestMessage, Task> onRequest,
        CancellationToken cancellationToken) =>
        DownloadPipe.RunServerAsync(onRequest, cancellationToken);

    public Task<bool> TrySendAsync(DownloadRequestMessage request, TimeSpan timeout) =>
        DownloadPipe.TrySendAsync(request, timeout);

    public Task<bool> TrySendWithRetryAsync(DownloadRequestMessage request, int attempts = 12,
        int timeoutMilliseconds = 500) =>
        DownloadPipe.TrySendWithRetryAsync(request, attempts, timeoutMilliseconds);
}
