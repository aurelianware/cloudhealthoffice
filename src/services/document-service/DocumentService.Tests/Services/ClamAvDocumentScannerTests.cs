using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DocumentService.Models;
using DocumentService.Services;
using Microsoft.Extensions.Options;

namespace DocumentService.Tests.Services;

public class ClamAvDocumentScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", ScanStatus.Clean)]
    [InlineData("stream: Synthetic.Test FOUND\0", ScanStatus.Infected)]
    [InlineData("INSTREAM size limit exceeded. ERROR\0", ScanStatus.Failed)]
    [InlineData("unexpected OK\0", ScanStatus.Failed)]
    [InlineData("stream: OK", ScanStatus.Failed)]
    public async Task Complete_protocol_verdict_is_required(string verdict, ScanStatus expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var body = Encoding.UTF8.GetBytes("synthetic document");
        var server = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync();
            await using var stream = peer.GetStream();
            var command = new byte[10]; await stream.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
            using var received = new MemoryStream();
            var header = new byte[4];
            while (true) {
                await stream.ReadExactlyAsync(header);
                var size = BinaryPrimitives.ReadInt32BigEndian(header);
                if (size == 0) break;
                var chunk = new byte[size]; await stream.ReadExactlyAsync(chunk); await received.WriteAsync(chunk);
            }
            Assert.Equal(body, received.ToArray());
            await stream.WriteAsync(Encoding.UTF8.GetBytes(verdict));
        });
        var scanner = new ClamAvDocumentScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = port }));
        var result = await scanner.ScanAsync(new MemoryStream(body), "text/plain", default);
        Assert.Equal(expected, result.Status);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unreachable_scanner_is_failed_never_clean()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var scanner = new ClamAvDocumentScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = port, TimeoutSeconds = 1 }));
        var result = await scanner.ScanAsync(new MemoryStream([1, 2, 3]), "text/plain", default);
        Assert.Equal(ScanStatus.Failed, result.Status);
    }
}
