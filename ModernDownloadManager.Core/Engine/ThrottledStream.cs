namespace ModernDownloadManager.Core.Engine;

/// <summary>
/// Wraps a stream and caps write throughput to a target bytes/sec using a
/// simple leaky-bucket delay. BytesPerSecond can be changed live (e.g. when
/// the user drags a speed-limit slider mid-download).
/// </summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly BandwidthLimiter? _sharedLimiter;
    private long _bytesThisWindow;
    private DateTime _windowStart = DateTime.UtcNow;

    /// <summary>0 = unlimited.</summary>
    public long BytesPerSecond { get; set; }

    public ThrottledStream(Stream inner, long bytesPerSecond = 0, BandwidthLimiter? sharedLimiter = null)
    {
        _inner = inner;
        BytesPerSecond = bytesPerSecond;
        _sharedLimiter = sharedLimiter;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_sharedLimiter is not null)
            await _sharedLimiter.WaitAsync(count, cancellationToken);
        else if (BytesPerSecond > 0)
            await ThrottleAsync(count, cancellationToken);

        await _inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_sharedLimiter is not null)
            await _sharedLimiter.WaitAsync(buffer.Length, cancellationToken);
        else if (BytesPerSecond > 0)
            await ThrottleAsync(buffer.Length, cancellationToken);

        await _inner.WriteAsync(buffer, cancellationToken);
    }

    private async Task ThrottleAsync(int incomingBytes, CancellationToken cancellationToken)
    {
        var elapsed = DateTime.UtcNow - _windowStart;
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            _windowStart = DateTime.UtcNow;
            _bytesThisWindow = 0;
            elapsed = TimeSpan.Zero;
        }

        _bytesThisWindow += incomingBytes;

        if (_bytesThisWindow > BytesPerSecond)
        {
            var remainder = TimeSpan.FromSeconds(1) - elapsed;
            if (remainder > TimeSpan.Zero)
                await Task.Delay(remainder, cancellationToken);

            _windowStart = DateTime.UtcNow;
            _bytesThisWindow = 0;
        }
    }

    // --- Pass-through plumbing ---
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
