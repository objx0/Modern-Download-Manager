using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModernDownloadManager.Core.Ipc;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.NativeHost;

/// <summary>
/// Chrome/Edge spawn this process fresh for each chrome.runtime.sendNativeMessage
/// call: read exactly one length-prefixed JSON message from stdin, act on it,
/// write exactly one length-prefixed JSON response to stdout, exit. No
/// persistent state â€” that all lives in the main app via the local pipe.
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        try
        {
            var request = await ReadMessageAsync<DownloadRequestMessage>();
            if (request is null || string.IsNullOrWhiteSpace(request.Url))
            {
                await WriteMessageAsync(new { status = "error", message = "No URL in request" });
                return;
            }

            var status = await DownloadPipe.TryConfirmedCaptureAsync(request, TimeSpan.FromMilliseconds(500));
            if (status is null)
            {
                LaunchApp();
                for (var attempt = 0; attempt < 20 && status is null; attempt++)
                {
                    status = await DownloadPipe.TryConfirmedCaptureAsync(request, TimeSpan.FromMilliseconds(500));
                    if (status is null) await Task.Delay(150);
                }
            }
            await WriteMessageAsync(new { status = status ?? "not-ready" });
        }
        catch (Exception ex)
        {
            // Native messaging expects a response even on failure, or the
            // extension sees a silent disconnect with no error detail.
            await WriteMessageAsync(new { status = "error", message = ex.Message });
        }
    }

    private static void LaunchApp()
    {
        var appPath = Path.Combine(AppContext.BaseDirectory, "ModernDownloadManager.App.exe");
        if (!File.Exists(appPath))
            throw new FileNotFoundException(
                "ModernDownloadManager.App.exe not found next to the native host. " +
                "Deploy NativeHost and App to the same folder.", appPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = false
        });
    }

    // --- Chrome native messaging wire format: 4-byte little-endian length
    // prefix, then that many bytes of UTF-8 JSON. Must use the raw stdio
    // streams (not Console.In/Out) to avoid text-mode newline translation
    // corrupting the binary length prefix. ---

    private static async Task<T?> ReadMessageAsync<T>()
    {
        await using var stdin = Console.OpenStandardInput();

        var lengthBuffer = new byte[4];
        if (!await ReadExactAsync(stdin, lengthBuffer))
            return default;

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException("Native message is outside the supported size limit.");

        var messageBuffer = new byte[length];
        if (!await ReadExactAsync(stdin, messageBuffer))
            return default;

        var json = Encoding.UTF8.GetString(messageBuffer);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (read == 0)
                return false; // stdin closed early
            offset += read;
        }
        return true;
    }

    private static async Task WriteMessageAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await using var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await stdout.WriteAsync(bytes);
        await stdout.FlushAsync();
    }
}
