namespace ModernDownloadManager.Core.Engine;

/// <summary>One aggregate byte-rate limiter shared by all segments of a job.</summary>
public sealed class BandwidthLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _bytesPerSecond;
    private long _bytesInWindow;
    private DateTime _windowStart = DateTime.UtcNow;

    public BandwidthLimiter(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        _bytesPerSecond = bytesPerSecond;
    }

    public async Task WaitAsync(int bytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _windowStart;
                if (elapsed >= TimeSpan.FromSeconds(1))
                {
                    _windowStart = now;
                    _bytesInWindow = 0;
                    elapsed = TimeSpan.Zero;
                }

                if (_bytesInWindow + bytes <= _bytesPerSecond)
                {
                    _bytesInWindow += bytes;
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }
}
