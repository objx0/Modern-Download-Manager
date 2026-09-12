using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Ipc;

/// <summary>
/// Minimal local IPC so the browser extension's native-messaging host can hand
/// a download off to an already-running app instance instead of always
/// launching a new process. One JSON message per connection, then a one-word
/// ack â€” deliberately simple since this only ever runs on localhost between
/// two processes we control.
/// </summary>
public static class DownloadPipe
{
    public const string PipeName = "ModernDownloadManager.Queue";
    public const string ConfirmedPipeName = "ModernDownloadManager.Capture.v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Confirmed capture protocol: one JSON line, then an explicit acceptance.
    // Keep the existing transport for platform adapters that have not migrated.
    public static async Task RunConfirmedServerAsync(Func<DownloadRequestMessage, Task<bool>> onRequest, CancellationToken ct, string pipeName = ConfirmedPipeName)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await server.WaitForConnectionAsync(ct); }
            catch { server.Dispose(); if (ct.IsCancellationRequested) return; throw; }
            _ = HandleConfirmedAsync(server, onRequest, ct);
        }
    }

    private static async Task HandleConfirmedAsync(NamedPipeServerStream server,
        Func<DownloadRequestMessage, Task<bool>> onRequest, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var json = await reader.ReadLineAsync(ct);
                var request = json is null ? null : JsonSerializer.Deserialize<DownloadRequestMessage>(json, JsonOptions);
                var accepted = request is not null && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https") && await onRequest(request);
                await writer.WriteLineAsync(accepted ? "accepted" : "declined");
            }
            catch (Exception) { /* A disconnected browser must not stop the capture listener. */ }
        }
    }

    // null means no listener: only that case is safe to retry/launch the app.
    public static async Task<string?> TryConfirmedCaptureAsync(DownloadRequestMessage request, TimeSpan connectTimeout, string pipeName = ConfirmedPipeName)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync((int)connectTimeout.TotalMilliseconds); }
        catch (Exception ex) when (ex is TimeoutException or IOException) { return null; }
        try
        {
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            return await reader.ReadLineAsync(deadline.Token) == "accepted" ? "queued" : "declined";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return "error"; }
    }

    /// <summary>Server side: call once at startup, keeps accepting connections until cancelled.</summary>
    public static async Task RunServerAsync(Func<DownloadRequestMessage, Task> onRequest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(
                PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var json = await reader.ReadToEndAsync(ct);

                var request = JsonSerializer.Deserialize<DownloadRequestMessage>(json, JsonOptions);
                if (request is not null && !string.IsNullOrWhiteSpace(request.Url))
                    await onRequest(request);

                using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteAsync("OK");
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (IOException)
            {
                // client disconnected mid-write; just loop and accept the next connection
            }
        }
    }

    /// <summary>Client side: returns true if it connected and handed off the request
    /// (meaning an app instance was already running), false if nothing is listening
    /// (meaning the caller should launch the app instead).</summary>
    public static async Task<bool> TrySendAsync(DownloadRequestMessage request, TimeSpan timeout)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync((int)timeout.TotalMilliseconds, cts.Token);

            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteAsync(json);

            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            return false; // no app instance listening â€” caller should launch one
        }
    }

    /// <summary>Allows a cold-starting app instance time to finish creating its
    /// pipe before a second native-host process decides to launch another app.</summary>
    public static async Task<bool> TrySendWithRetryAsync(DownloadRequestMessage request,
        int attempts = 12, int timeoutMilliseconds = 500)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await TrySendAsync(request, TimeSpan.FromMilliseconds(timeoutMilliseconds)))
                return true;

            await Task.Delay(150);
        }

        return false;
    }
}
