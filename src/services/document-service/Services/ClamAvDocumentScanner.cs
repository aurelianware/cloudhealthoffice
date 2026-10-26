using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using DocumentService.Models;
using Microsoft.Extensions.Options;

namespace DocumentService.Services;

public sealed class ClamAvOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 60;
    public long MaxBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>
/// ClamAV's zINSTREAM protocol. Only a complete, exactly recognised clean
/// verdict is Clean; malformed, disconnected, oversized and timed-out scans fail.
/// The scanner address is operator configuration, never uploaded metadata.
/// </summary>
public sealed class ClamAvDocumentScanner(IOptions<ClamAvOptions> options) : IDocumentScanner
{
    public async Task<DocumentScan> ScanAsync(Stream content, string contentType, CancellationToken ct)
    {
        var config = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        var token = timeout.Token;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(config.Host, config.Port, token);
            await using var socket = client.GetStream();
            await socket.WriteAsync("zINSTREAM\0"u8.ToArray(), token);
            var data = new byte[64 * 1024];
            var header = new byte[4];
            long total = 0;
            int count;
            while ((count = await content.ReadAsync(data, token)) != 0)
            {
                total += count;
                if (total > config.MaxBytes) return Verdict(ScanStatus.Failed);
                BinaryPrimitives.WriteInt32BigEndian(header, count);
                await socket.WriteAsync(header, token);
                await socket.WriteAsync(data.AsMemory(0, count), token);
            }
            await socket.WriteAsync(new byte[4], token);
            var response = new byte[4096];
            var used = 0;
            while (used < response.Length)
            {
                var n = await socket.ReadAsync(response.AsMemory(used, 1), token);
                if (n == 0) return Verdict(ScanStatus.Failed);
                if (response[used] == 0)
                {
                    var text = Encoding.UTF8.GetString(response, 0, used);
                    if (text == "stream: OK") return Verdict(ScanStatus.Clean);
                    if (text.StartsWith("stream: ", StringComparison.Ordinal)
                        && text.EndsWith(" FOUND", StringComparison.Ordinal)) return Verdict(ScanStatus.Infected);
                    return Verdict(ScanStatus.Failed);
                }
                used++;
            }
            return Verdict(ScanStatus.Failed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Never log content, file name, signature or the daemon's response.
            return Verdict(ScanStatus.Failed);
        }
    }
    private static DocumentScan Verdict(ScanStatus status) => new() { Status = status, Engine = "clamav-instream", At = DateTime.UtcNow };
}

public static class DocumentScannerRegistration
{
    public static IServiceCollection AddDocumentScanner(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["Documents:Scanner:Provider"] ?? "None";
        if (provider.Equals("None", StringComparison.OrdinalIgnoreCase))
            return services.AddSingleton<IDocumentScanner, NoDocumentScanner>();
        if (!provider.Equals("ClamAv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Documents:Scanner:Provider must be None or ClamAv.");
        services.AddOptions<ClamAvOptions>().Bind(config.GetSection("Documents:Scanner:ClamAv"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host) && o.Port is > 0 and <= 65535
                && o.TimeoutSeconds is >= 1 and <= 300 && o.MaxBytes is > 0 and <= 64L * 1024 * 1024,
                "Configure a ClamAV host, valid port, timeout (1–300s) and maximum size (up to 64 MiB).")
            .ValidateOnStart();
        return services.AddSingleton<IDocumentScanner, ClamAvDocumentScanner>();
    }
}
