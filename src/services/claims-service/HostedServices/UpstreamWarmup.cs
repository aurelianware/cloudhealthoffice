using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ClaimsService.HostedServices;

/// <summary>
/// <c>Adjudication:Warmup</c>: hold claims-service's readiness until the
/// services adjudication calls answer from this pod (issue #1824).
/// </summary>
public sealed class UpstreamWarmupOptions
{
    public const string SectionName = "Adjudication:Warmup";

    /// <summary>
    /// Null: on everywhere except Development and Testing hosts, where the
    /// upstream services usually are not running.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Readiness opens after this long even when an upstream never answered,
    /// so one service that is down cannot keep claims-service out of the
    /// rotation (the stages then degrade or pend as they always have).
    /// </summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Time between probes of an upstream that has not answered yet.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Path probed on each upstream (anonymous on every CHO service).</summary>
    public string ProbePath { get; set; } = "/health/live";

    /// <summary>
    /// Consecutive answers each upstream must give before it counts as warm:
    /// one lucky answer from a pod whose networking is still settling is not enough.
    /// </summary>
    public int RequiredSuccesses { get; set; } = 2;
}

/// <summary>An upstream the warm-up probes: a name for logs and its base address.</summary>
public sealed record UpstreamWarmupTarget(string Name, Uri BaseAddress);

/// <summary>The upstreams the warm-up probes.</summary>
public sealed class UpstreamWarmupTargets
{
    public UpstreamWarmupTargets(IEnumerable<UpstreamWarmupTarget> targets)
    {
        // One probe per address: several named clients can share a service.
        Items = targets
            .GroupBy(t => t.BaseAddress.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public IReadOnlyList<UpstreamWarmupTarget> Items { get; }

    /// <summary>
    /// The base address of each named client in <paramref name="clientNames"/>
    /// (as its registration set it), plus token-service when this service gets
    /// its service tokens there. A client with no base address is skipped.
    /// </summary>
    public static UpstreamWarmupTargets FromClients(
        IHttpClientFactory factory, IEnumerable<string> clientNames, string? tokenServiceUrl)
    {
        var targets = new List<UpstreamWarmupTarget>();
        foreach (var name in clientNames)
        {
            using var client = factory.CreateClient(name);
            if (client.BaseAddress is { } address)
                targets.Add(new UpstreamWarmupTarget(name, address));
        }
        if (Uri.TryCreate(tokenServiceUrl, UriKind.Absolute, out var tokenService))
            targets.Add(new UpstreamWarmupTarget("TokenService", tokenService));
        return new UpstreamWarmupTargets(targets);
    }
}

/// <summary>What the warm-up has reached so far. Read by <see cref="UpstreamWarmupHealthCheck"/>.</summary>
public sealed class UpstreamWarmupState
{
    private volatile bool _complete;
    private volatile string _pending = string.Empty;

    /// <summary>True once every upstream answered, the wait ran out, or warm-up is off.</summary>
    public bool Complete => _complete;

    /// <summary>The upstreams still not answering, comma separated (for logs and the health check).</summary>
    public string Pending => _pending;

    internal void SetPending(IEnumerable<string> names) => _pending = string.Join(", ", names);

    internal void MarkComplete()
    {
        _pending = string.Empty;
        _complete = true;
    }
}

/// <summary>
/// Readiness check: Unhealthy while the warm-up is still waiting on an
/// upstream. Kubernetes sends a new pod no claims until it passes, so the
/// first claims after a restart no longer pend because this pod could not yet
/// reach coverage-service (COB), benefit-plan-service or token-service.
/// </summary>
public sealed class UpstreamWarmupHealthCheck : IHealthCheck
{
    public const string Name = "upstream-warmup";

    private readonly UpstreamWarmupState _state;

    public UpstreamWarmupHealthCheck(UpstreamWarmupState state) => _state = state;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_state.Complete
            ? HealthCheckResult.Healthy("Upstream services reached")
            : HealthCheckResult.Unhealthy($"Waiting for upstream services: {_state.Pending}"));
}

/// <summary>
/// Probes each upstream adjudication depends on until it answers
/// <see cref="UpstreamWarmupOptions.RequiredSuccesses"/> times in a row, then
/// opens readiness. Uses its own client (<see cref="HttpClientName"/>), with no
/// retries, circuit breaker or service token, so a failed probe never counts
/// against the circuit of the client claims use.
/// </summary>
public sealed class UpstreamWarmupService : BackgroundService
{
    public const string HttpClientName = "claims-upstream-warmup";

    private readonly IReadOnlyList<UpstreamWarmupTarget> _targets;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UpstreamWarmupState _state;
    private readonly UpstreamWarmupOptions _options;
    private readonly bool _enabled;
    private readonly ILogger<UpstreamWarmupService> _logger;
    private readonly TimeProvider _time;

    public UpstreamWarmupService(
        UpstreamWarmupTargets targets,
        IHttpClientFactory httpClientFactory,
        UpstreamWarmupState state,
        IOptions<UpstreamWarmupOptions> options,
        IHostEnvironment environment,
        ILogger<UpstreamWarmupService> logger,
        TimeProvider? time = null)
    {
        _targets = targets.Items;
        _httpClientFactory = httpClientFactory;
        _state = state;
        _options = options.Value;
        _enabled = _options.Enabled
            ?? !(environment.IsDevelopment() || environment.IsEnvironment("Testing"));
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _targets.Count == 0)
        {
            _state.MarkComplete();
            return;
        }

        var started = _time.GetTimestamp();
        var streak = _targets.ToDictionary(t => t.Name, _ => 0, StringComparer.Ordinal);
        var required = Math.Max(1, _options.RequiredSuccesses);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        while (!stoppingToken.IsCancellationRequested)
        {
            var waiting = _targets.Where(t => streak[t.Name] < required).ToList();
            if (waiting.Count == 0)
            {
                _logger.LogInformation(
                    "Upstream warm-up done after {Elapsed:0.0}s: {Upstreams} answered; claims-service is ready",
                    _time.GetElapsedTime(started).TotalSeconds, string.Join(", ", streak.Keys));
                _state.MarkComplete();
                return;
            }

            _state.SetPending(waiting.Select(t => t.Name));

            if (_time.GetElapsedTime(started) >= _options.MaxWait)
            {
                _logger.LogWarning(
                    "Upstream warm-up gave up after {MaxWait}: {Upstreams} still not answering. " +
                    "claims-service is ready anyway; claims needing them pend or degrade until they answer",
                    _options.MaxWait, _state.Pending);
                _state.MarkComplete();
                return;
            }

            var answered = await Task.WhenAll(waiting.Select(t => ProbeAsync(client, t, stoppingToken)))
                .ConfigureAwait(false);
            for (var i = 0; i < waiting.Count; i++)
                streak[waiting[i].Name] = answered[i] ? streak[waiting[i].Name] + 1 : 0;

            if (_targets.Any(t => streak[t.Name] < required))
            {
                try
                {
                    await Task.Delay(_options.Interval, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> ProbeAsync(HttpClient client, UpstreamWarmupTarget target, CancellationToken ct)
    {
        try
        {
            using var response = await client
                .GetAsync(new Uri(target.BaseAddress, _options.ProbePath), ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;
            _logger.LogInformation("Upstream warm-up: {Upstream} answered {Status}", target.Name, (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Upstream warm-up: {Upstream} not reachable yet ({Error}: {Message})",
                target.Name, ex.GetType().Name, ex.Message);
            return false;
        }
    }
}
