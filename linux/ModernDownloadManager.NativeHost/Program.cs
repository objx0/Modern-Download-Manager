using ModernDownloadManager.Core.Ipc;
using ModernDownloadManager.Core.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ModernDownloadManager.NativeHost;

internal static class Program
{
    private static async Task Main()
    {
        ILocalIpc localIpc = new NamedPipeIpc();
        var request = await ReadMessageAsync<DownloadRequestMessage>();
        if (request is null || string.IsNullOrWhiteSpace(request.Url))
        {
            await WriteMessageAsync(new { status = "error", message = "No URL in request" });
            return;
        }

        var delivered = await localIpc.TrySendWithRetryAsync(request);
        if (!delivered)
        {
            var app = Path.Combine(AppContext.BaseDirectory, "ModernDownloadManager");
            if (!File.Exists(app))
                throw new FileNotFoundException("Linux desktop application was not found.", app);
            Process.Start(new ProcessStartInfo(app) { UseShellExecute = false });
            await Task.Delay(400);
            delivered = await localIpc.TrySendWithRetryAsync(request);
        }
        await WriteMessageAsync(new { status = delivered ? "queued" : "launched" });
    }

    private static async Task<T?> ReadMessageAsync<T>()
    {
        var input = Console.OpenStandardInput();
        var header = new byte[4];
        if (!await ReadExactAsync(input, header)) return default;
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Invalid message size.");
        var bytes = new byte[length];
        if (!await ReadExactAsync(input, bytes)) return default;
        return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset));
            if (count == 0) return false;
            offset += count;
        }
        return true;
    }

    private static async Task WriteMessageAsync(object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var output = Console.OpenStandardOutput();
        await output.WriteAsync(BitConverter.GetBytes(bytes.Length));
        await output.WriteAsync(bytes);
        await output.FlushAsync();
    }
}
