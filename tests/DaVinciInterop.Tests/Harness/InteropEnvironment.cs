using System.Diagnostics;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Coordinates the lifetime of one interoperability environment: the external
/// implementations a scenario needs, the CHO endpoint when CHO is the system under
/// test, the network they share, their startup, their readiness and their
/// teardown.
///
/// The harness never assumes CHO is the payer or the client. A scenario declares
/// the services it requires and its own <see cref="ChoRole"/>; the environment
/// starts exactly those Compose profiles and nothing else, so the ordinary
/// development workflow never pays for tools it is not using.
///
/// Cleanup is unconditional: <see cref="DisposeAsync"/> tears the stack down even
/// when the scenario threw, and captures service logs first so a failure is still
/// diagnosable afterwards.
/// </summary>
public sealed class InteropEnvironment : IAsyncDisposable
{
    private readonly DockerCompose _compose;
    private readonly List<string> _profiles;
    private readonly List<ExternalServiceDefinition> _services;
    private readonly Dictionary<string, string> _serviceLogs = new(StringComparer.Ordinal);
    private bool _started;

    private InteropEnvironment(
        InteropVersions versions,
        DockerCompose compose,
        IReadOnlyList<ExternalServiceDefinition> services)
    {
        Versions = versions;
        _compose = compose;
        _services = services.ToList();
        _profiles = services.SelectMany(s => s.Compose.Profiles).Distinct().ToList();
    }

    /// <summary>The pinned target manifest this environment was built from.</summary>
    public InteropVersions Versions { get; }

    /// <summary>The external implementations this environment coordinates.</summary>
    public IReadOnlyList<ExternalServiceDefinition> Services => _services;

    /// <summary>Captured container logs, keyed by compose service name.</summary>
    public IReadOnlyDictionary<string, string> ServiceLogs => _serviceLogs;

    /// <summary>How long startup and readiness took, for the run summary.</summary>
    public TimeSpan StartupDuration { get; private set; }

    /// <summary>Readiness outcomes, keyed by target key.</summary>
    public Dictionary<string, ReadinessOutcome> Readiness { get; } = new(StringComparer.Ordinal);

    /// <summary>Builds an environment for the named external targets.</summary>
    public static InteropEnvironment For(params string[] targetKeys)
    {
        var versions = InteropVersions.Load();
        var services = targetKeys.Select(versions.Target).ToList();
        return new InteropEnvironment(versions, DockerCompose.ForRepository(), services);
    }

    /// <summary>Looks up a coordinated service by target key.</summary>
    public ExternalServiceDefinition Service(string key) =>
        _services.SingleOrDefault(s => s.Key == key)
        ?? throw new KeyNotFoundException($"'{key}' is not part of this interop environment.");

    /// <summary>
    /// Pulls the pinned images, starts the required Compose profiles and waits for
    /// each service to reach its required readiness stage.
    /// </summary>
    /// <exception cref="InteropEnvironmentException">Startup or readiness failed within the bounded timeouts.</exception>
    /// <param name="requiredStage">How ready each service must be before the scenario proceeds.</param>
    /// <param name="buildImages">
    /// True only for profiles that build locally (CHO's own service, an Inferno kit
    /// checkout). Third-party targets always come from a pinned image.
    /// </param>
    public async Task StartAsync(
        ReadinessStage requiredStage = ReadinessStage.FhirMetadataAvailable,
        bool buildImages = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var pull = await _compose.PullAsync(_profiles, InteropSettings.Timeouts.ImagePull, cancellationToken);
        if (!pull.Succeeded)
        {
            throw new InteropEnvironmentException(
                "Could not pull the pinned external images. Every external target is pinned by digest in " +
                $"interop/versions.json, so this is a registry or network problem, not a version drift.{Environment.NewLine}{pull.Combined}");
        }

        _started = true;
        var up = await _compose.UpAsync(
            _profiles, InteropSettings.Timeouts.ContainerStart, buildImages, cancellationToken);
        if (!up.Succeeded)
        {
            throw new InteropEnvironmentException(
                $"`docker compose up` failed for profiles [{string.Join(", ", _profiles)}].{Environment.NewLine}{up.Combined}");
        }

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var probe = new ReadinessProbe(http, _compose);

        foreach (var service in _services)
        {
            var outcome = await probe.WaitAsync(
                service,
                requiredStage,
                InteropSettings.Timeouts.Readiness,
                InteropSettings.Timeouts.ReadinessPoll,
                cancellationToken);

            Readiness[service.Key] = outcome;

            if (!outcome.IsReady)
            {
                await CaptureLogsAsync(cancellationToken);
                throw new InteropEnvironmentException(
                    $"{outcome.Diagnostic}{Environment.NewLine}" +
                    $"Pinned as: {service.Pin.Reference}{Environment.NewLine}" +
                    $"Container logs were captured to the run artifacts.");
            }
        }

        stopwatch.Stop();
        StartupDuration = stopwatch.Elapsed;
    }

    /// <summary>Captures the tail of each service's container logs for diagnostics.</summary>
    public async Task CaptureLogsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var service in _services)
        {
            try
            {
                var logs = await _compose.LogsAsync(
                    service.Compose.Service, tailLines: 400, TimeSpan.FromSeconds(60), cancellationToken);
                _serviceLogs[service.Compose.Service] = Redaction.Body(logs);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
            {
                _serviceLogs[service.Compose.Service] = $"(log capture failed: {ex.Message})";
            }
        }
    }

    /// <summary>
    /// Tears the environment down: containers, network and volumes. Runs even
    /// after a failed scenario, and captures logs before removing anything.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return;
        }

        // Logs are not captured here: a scenario captures them on failure, before
        // it writes its evidence. Capturing at teardown would only produce output
        // that nothing reads.
        if (InteropSettings.KeepStack)
        {
            return;
        }

        try
        {
            await _compose.DownAsync(_profiles, InteropSettings.Timeouts.Teardown);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException)
        {
            // Teardown is best-effort from the harness's point of view; the run
            // script does a second unconditional `compose down` so a wedged
            // container can never outlive the run.
            Console.Error.WriteLine($"[interop] teardown reported: {ex.Message}");
        }
        finally
        {
            _started = false;
        }
    }
}

/// <summary>Raised when an interop environment could not be brought up or made ready.</summary>
public sealed class InteropEnvironmentException : Exception
{
    public InteropEnvironmentException(string message) : base(message) { }
}
