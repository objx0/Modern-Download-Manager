using System.Text.Json;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Persistence;

/// <summary>
/// Simple JSON-file settings persistence. Deliberately not using
/// Windows.Storage.ApplicationData — that API assumes package identity and
/// is unreliable for unpackaged apps, whereas a plain file next to the
/// SQLite DB (same app-data folder) works identically either way.
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettingsStore(string filePath) => _filePath = filePath;

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_filePath))
            return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt settings file shouldn't block the app from starting.
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
                await stream.FlushAsync();
            }
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
