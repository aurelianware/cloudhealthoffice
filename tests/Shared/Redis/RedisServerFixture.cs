// Shared by every test project that needs a real redis-server. Linked into each
// project with <Compile Include="..\Shared\Redis\*.cs" />. Needs Xunit.SkippableFact.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;

namespace CloudHealthOffice.Testing.Redis;

/// <summary>
/// A real redis-server for <see cref="RedisAccumulatorCommitTests"/>:
/// <c>CHO_TEST_REDIS</c> (a connection string, as CI provides) or, when that is
/// unset, a <c>redis-server</c> on the PATH started on a free port, without
/// persistence. Neither available: the tests are skipped, never faked.
/// </summary>
public sealed class RedisServerFixture : IDisposable
{
    private readonly Process? _process;

    public ConnectionMultiplexer? Connection { get; }
    public string? Unavailable { get; }
    private string? _configuration;

    /// <summary>
    /// A connection whose default database is <paramref name="database"/> (tests with
    /// fixed key names isolate on their own database and flush it), allowAdmin on.
    /// </summary>
    public ConnectionMultiplexer Connect(int database) =>
        ConnectionMultiplexer.Connect($"{_configuration},allowAdmin=true,connectTimeout=1000,defaultDatabase={database}");

    public RedisServerFixture()
    {
        var configured = Environment.GetEnvironmentVariable("CHO_TEST_REDIS");
        try
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                var port = FreePort();
                _process = Process.Start(new ProcessStartInfo("redis-server",
                    $"--port {port} --bind 127.0.0.1 --save \"\" --appendonly no")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                configured = $"127.0.0.1:{port}";
            }
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try
                {
                    Connection = ConnectionMultiplexer.Connect(configured + ",allowAdmin=true,connectTimeout=1000");
                    _configuration = configured;
                    break;
                }
                catch (RedisConnectionException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or RedisConnectionException)
        {
            Unavailable = $"No Redis for the accumulator store tests (set CHO_TEST_REDIS or install redis-server): {ex.Message}";
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Connection?.Dispose();
        if (_process is { HasExited: false })
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }
        _process?.Dispose();
    }
}

