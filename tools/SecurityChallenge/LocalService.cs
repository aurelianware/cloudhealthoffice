using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CloudHealthOffice.Infrastructure.Security;

namespace SecurityChallenge;

/// <summary>Owned loopback process; never accepts a remote target or inherited service configuration.</summary>
internal sealed class LocalService : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string? _contentRoot;
    public HttpClient Http { get; }

    public LocalService(string assembly, string mongoConnection, string database, bool development, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var address = new Uri($"http://127.0.0.1:{port}");
        // An empty owned development content root selects the existing Development-only
        // NoOp encryption modes for appeals/member labs. Production guards stay intact.
        if (development)
        {
            _contentRoot = Path.Combine(Path.GetTempPath(), "cho-security-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_contentRoot);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _contentRoot ?? Path.GetDirectoryName(Path.GetFullPath(assembly))!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(Path.GetFullPath(assembly));
        var inherited = start.Environment.ToDictionary(x => x.Key, x => x.Value);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (inherited.TryGetValue(key, out var value)) start.Environment[key] = value;
        var settings = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = development ? "Development" : "Testing",
            ["CHO_TESTING"] = "1", ["ASPNETCORE_URLS"] = address.ToString(),
            ["MongoDb:ConnectionString"] = mongoConnection, ["MongoDb:DatabaseName"] = database, ["MongoDb:UseTenantScoping"] = "false",
            ["SecretProvider:Provider"] = "None",
            ["Kafka:BootstrapServers"] = "", ["Observability:OtlpEndpoint"] = "",
            ["Observability:EnableConsole"] = "false", ["Logging:LogLevel:Default"] = "Warning",
            ["AppealOutbox:Enabled"] = "false", ["AppealOutbox:DispatchInline"] = "false",
            ["ClaimsService:BaseUrl"] = "http://127.0.0.1:1/"
        };
        foreach (var name in new[] { "ProviderService", "ReferenceDataService", "TerminologyServiceUrl", "TerminologyService", "BenefitPlanService", "MemberService", "CoverageService", "AuthorizationService" })
            settings["Services:" + name] = "http://127.0.0.1:1/";
        foreach (var setting in ChoDevelopmentAuth.Configuration(Path.GetFileNameWithoutExtension(assembly)))
            settings[setting.Key] = setting.Value;
        if (overrides != null)
            foreach (var setting in overrides) settings[setting.Key] = setting.Value?.Replace("{SELF}", address.ToString(), StringComparison.Ordinal);
        foreach (var setting in settings) start.Environment[setting.Key.Replace(":", "__")] = setting.Value;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Local process start failed");
        _process.OutputDataReceived += Log;
        _process.ErrorDataReceived += Log;
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        Http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
    }

    private static void Log(object sender, DataReceivedEventArgs args)
    {
        if (Environment.GetEnvironmentVariable("CHO_SECURITY_LAB_DEBUG") == "1") Console.Error.WriteLine(args.Data);
    }

    public async Task Ready()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!_process.HasExited)
        {
            try
            {
                using var response = await Http.GetAsync("health/live", deadline.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200, deadline.Token);
        }
        throw new InvalidOperationException("Local process exited before readiness");
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        finally
        {
            _process.Dispose();
            if (_contentRoot != null) Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
