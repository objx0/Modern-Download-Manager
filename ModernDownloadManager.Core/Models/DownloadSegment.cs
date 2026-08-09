namespace ModernDownloadManager.Core.Models;

/// <summary>
/// Represents one byte-range chunk of a segmented (multi-connection) download.
/// Each segment downloads to its own temp file so it can resume independently.
/// </summary>
public class DownloadSegment
{
    public int Index { get; set; }
    public long StartByte { get; set; }
    public long EndByte { get; set; }

    /// <summary>Bytes written so far, relative to StartByte.</summary>
    public long DownloadedBytes { get; set; }

    public string TempFilePath { get; set; } = string.Empty;
    public SegmentState State { get; set; } = SegmentState.Pending;

    public long TotalBytes => EndByte - StartByte + 1;
    public bool IsComplete => DownloadedBytes >= TotalBytes;

    /// <summary>The byte offset to resume from, accounting for partial temp-file data already on disk.</summary>
    public long ResumeOffset => StartByte + DownloadedBytes;
}
