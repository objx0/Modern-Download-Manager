namespace ModernDownloadManager.Core.Models;

/// <summary>
/// Domain aggregate for a download. It separates creation-time request data,
/// user options, and mutable execution state. DownloadItem remains as the
/// database/UI projection until all hosts migrate to this type.
/// </summary>
public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Url { get; init; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool FileNameIsExplicit { get; init; }
    public DownloadOptions Options { get; init; } = new();
    public DownloadRuntimeState Runtime { get; init; } = new();

    public string DestinationDirectory => Options.DestinationDirectory;
    public string FullPath => Path.Combine(DestinationDirectory, FileName);

    public static DownloadJob FromRecord(DownloadItem item) => new()
    {
        Id = item.Id,
        Url = item.Url,
        FileName = item.FileName,
        FileNameIsExplicit = item.FileNameIsExplicit,
        Options = new DownloadOptions
        {
            DestinationDirectory = item.DestinationDirectory,
            SuggestedFileName = item.FileNameIsExplicit ? item.FileName : null,
            SpeedLimitBytesPerSecond = item.SpeedLimitBytesPerSecond,
            SegmentCount = item.SegmentCount,
            ReferrerUrl = item.ReferrerUrl,
            CookieHeader = item.CookieHeader,
            UserAgent = item.UserAgent,
            Category = item.Category,
            StartImmediately = item.State == DownloadState.Queued
        },
        Runtime = new DownloadRuntimeState
        {
            TotalBytes = item.TotalBytes,
            DownloadedBytes = item.DownloadedBytes,
            State = item.State,
            SupportsResume = item.SupportsResume,
            ETag = item.ETag,
            LastModified = item.LastModified,
            CreatedAt = item.CreatedAt,
            CompletedAt = item.CompletedAt,
            ErrorMessage = item.ErrorMessage
        }
    };

    public DownloadItem ToRecord() => new()
    {
        Id = Id,
        Url = Url,
        FileName = FileName,
        FileNameIsExplicit = FileNameIsExplicit,
        DestinationDirectory = DestinationDirectory,
        TotalBytes = Runtime.TotalBytes,
        DownloadedBytes = Runtime.DownloadedBytes,
        State = Runtime.State,
        Category = Options.Category ?? DownloadCategoryResolver.Resolve(FileName),
        SupportsResume = Runtime.SupportsResume,
        ETag = Runtime.ETag,
        LastModified = Runtime.LastModified,
        SegmentCount = Options.SegmentCount,
        SpeedLimitBytesPerSecond = Options.SpeedLimitBytesPerSecond,
        ReferrerUrl = Options.ReferrerUrl,
        CookieHeader = Options.CookieHeader,
        UserAgent = Options.UserAgent,
        CreatedAt = Runtime.CreatedAt,
        CompletedAt = Runtime.CompletedAt,
        ErrorMessage = Runtime.ErrorMessage
    };

    public void UpdateFromRecord(DownloadItem item)
    {
        FileName = item.FileName;
        Runtime.TotalBytes = item.TotalBytes;
        Runtime.DownloadedBytes = item.DownloadedBytes;
        Runtime.State = item.State;
        Runtime.SupportsResume = item.SupportsResume;
        Runtime.ETag = item.ETag;
        Runtime.LastModified = item.LastModified;
        Runtime.CompletedAt = item.CompletedAt;
        Runtime.ErrorMessage = item.ErrorMessage;
    }
}
