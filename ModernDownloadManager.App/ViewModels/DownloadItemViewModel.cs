using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Scheduling;

namespace ModernDownloadManager.App.ViewModels;

public enum RemoveDecision
{
    Cancel,
    RemoveFromList,
    DeleteFile
}

/// <summary>
/// Thin observable shell around a Core DownloadItem. Kept deliberately dumb —
/// all real logic lives in DownloadQueueManager; this just exposes it to XAML
/// bindings and formats a couple of display strings.
/// </summary>
public partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queue;

    public DownloadItem Model { get; }
    public event EventHandler? Removed;
    public Func<DownloadItemViewModel, Task<RemoveDecision>>? ConfirmRemoveAsync { get; set; }

    public DownloadItemViewModel(DownloadItem model, DownloadQueueManager queue)
    {
        Model = model;
        _queue = queue;
    }

    public Guid Id => Model.Id;
    public string FileName => Model.FileName;
    public string Url => Model.Url;
    public DownloadCategory Category => Model.Category;
    public string CategoryText => Category.ToString();
    public string DownloadedDateText => Model.CompletedAt is { } completed
        ? $"Downloaded {completed.ToLocalTime():MMM d, yyyy h:mm tt}"
        : string.Empty;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private DownloadState state;

    [ObservableProperty]
    private string speedText = string.Empty;

    [ObservableProperty]
    private string sizeText = string.Empty;

    [ObservableProperty]
    private string etaText = string.Empty;

    public void ApplyProgress(long downloaded, long total, double bytesPerSecond, TimeSpan? eta)
    {
        ProgressPercent = total > 0 ? Math.Clamp((double)downloaded / total * 100, 0, 100) : 0;
        SizeText = $"{FormatBytes(downloaded)} / {FormatBytes(total)}";
        SpeedText = bytesPerSecond > 0 ? $"{FormatBytes((long)bytesPerSecond)}/s" : string.Empty;
        EtaText = eta is { } t ? FormatEta(t) : string.Empty;
    }

    [ObservableProperty]
    private string stateText = string.Empty;

    public void ApplyState(DownloadState newState)
    {
        State = newState;
        StateText = newState.ToString();
        if (newState is DownloadState.Completed)
        {
            // Completion is authoritative. The final throttled progress event can
            // arrive before merge finishes, so never leave a completed row showing
            // the last partial byte count.
            if (Model.TotalBytes > 0)
            {
                Model.DownloadedBytes = Model.TotalBytes;
                ProgressPercent = 100;
                SizeText = $"{FormatBytes(Model.TotalBytes)} / {FormatBytes(Model.TotalBytes)}";
            }
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }

        // FileName is a pass-through onto Model, which the engine updates in place
        // once it learns the real name from Content-Disposition/redirect — this is
        // the hook that tells the OneWay x:Bind in the UI to re-read it.
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanOpenFile));
        OnPropertyChanged(nameof(CanOpenFolder));
        OnPropertyChanged(nameof(CanShowMini));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(DownloadedDateText));
    }

    public bool CanPause => State is DownloadState.Downloading or DownloadState.Connecting or DownloadState.Queued;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed;
    public bool CanOpenFile => State == DownloadState.Completed && File.Exists(Model.FullPath);
    public bool CanOpenFolder => Directory.Exists(Model.DestinationDirectory);
    public bool CanShowMini => State is DownloadState.Queued or DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging or DownloadState.Paused;
    public bool CanCancel => State is not DownloadState.Completed and not DownloadState.Cancelled;

    [RelayCommand]
    private Task Pause() => _queue.PauseAsync(Id);

    [RelayCommand]
    private async Task Resume()
    {
        await _queue.ResumeAsync(Id);
        ModernDownloadManager.App.App.ShowMiniFor(Id);
    }

    [RelayCommand]
    private Task Cancel() => _queue.CancelAsync(Id);

    [RelayCommand]
    private async Task Remove()
    {
        var decision = ConfirmRemoveAsync is null
            ? RemoveDecision.RemoveFromList
            : await ConfirmRemoveAsync(this);
        if (decision == RemoveDecision.Cancel)
            return;
        await _queue.RemoveAsync(Id, decision == RemoveDecision.DeleteFile);
        Removed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (!CanOpenFile) return;
        Process.Start(new ProcessStartInfo(Model.FullPath) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!CanOpenFolder) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Model.FullPath}\"")
        {
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ShowMini()
    {
        if (CanShowMini)
            ModernDownloadManager.App.App.ShowMiniFor(Id);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.#} {units[unit]}";
    }

    private static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m left" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s left" :
        $"{t.Seconds}s left";
}
