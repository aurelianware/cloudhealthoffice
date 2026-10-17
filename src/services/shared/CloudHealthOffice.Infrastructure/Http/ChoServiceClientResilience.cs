using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace CloudHealthOffice.Infrastructure.Http;

/// <summary>
/// Resilience for one named or typed <see cref="HttpClient"/> (named options:
/// the client's name). The defaults suit a CHO service-to-service call; a
/// client configures its own with
/// <c>services.Configure&lt;ChoResilienceOptions&gt;("ClientName", o =&gt; ...)</c>.
/// </summary>
public sealed class ChoResilienceOptions
{
    /// <summary>False: the client's requests pass through untouched (it has its own resilience handler).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The attempt timeout of a client that sets no <see cref="HttpClient.Timeout"/> of its own.</summary>
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The smallest total timeout a client gets unless it sets <see cref="TotalTimeout"/>.</summary>
    public static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(30);

    private TimeSpan? _totalTimeout;
    private TimeSpan? _attemptTimeout;

    /// <summary>
    /// Budget for the whole call, every retry included. Unless set, the larger
    /// of <see cref="DefaultTotalTimeout"/> and what every attempt and its
    /// back-off need (<see cref="ChoResiliencePipelines.Timeouts"/>).
    /// </summary>
    public TimeSpan TotalTimeout
    {
        get => _totalTimeout ?? DefaultTotalTimeout;
        set => _totalTimeout = value;
    }

    /// <summary>
    /// Budget for one attempt. Unless set, the client's own
    /// <see cref="HttpClient.Timeout"/> (as its <c>ConfigureHttpClient</c>
    /// actions set it), else <see cref="DefaultAttemptTimeout"/>.
    /// </summary>
    public TimeSpan AttemptTimeout
    {
        get => _attemptTimeout ?? DefaultAttemptTimeout;
        set => _attemptTimeout = value;
    }

    internal bool TotalTimeoutIsSet => _totalTimeout.HasValue;
    internal bool AttemptTimeoutIsSet => _attemptTimeout.HasValue;

    /// <summary>Retries after the first attempt (0 disables retrying).</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; exponential with jitter after it. A Retry-After header is honoured.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Also retry methods that are not idempotent (POST, PATCH). Only for an
    /// endpoint where a repeat has no side effect (a model completion, a
    /// read modelled as POST). Otherwise a request is retried only when it
    /// is idempotent (<see cref="ChoServiceClientResilience.IsRetryable"/>).
    /// </summary>
    public bool RetryNonIdempotent { get; set; }

    /// <summary>Extra response status codes treated as transient (on top of 408, 429 and 5xx).</summary>
    public int[] AdditionalTransientStatusCodes { get; set; } = [];

    /// <summary>The circuit opens when this share of the sampled calls failed transiently...</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>...and at least this many calls were sampled...</summary>
    public int CircuitMinimumThroughput { get; set; } = 10;

    /// <summary>...within this window.</summary>
    public TimeSpan CircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an open circuit fails calls at once before it lets one through.</summary>
    public TimeSpan CircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Retries, timeouts and a circuit breaker on every service <see cref="HttpClient"/>:
/// <list type="bullet">
///   <item>a total timeout and a per-attempt timeout;</item>
///   <item>retries with exponential back-off and jitter, only for idempotent
///     requests: GET, HEAD, OPTIONS, TRACE, PUT and DELETE, or any request
///     carrying an <c>Idempotency-Key</c> header. A POST or PATCH is never
///     retried unless its client opts in (<see cref="ChoResilienceOptions.RetryNonIdempotent"/>)
///     or the request does (<see cref="AllowRetry"/>);</item>
///   <item>a circuit breaker per client: after a sustained outage calls fail
///     at once instead of queueing on a dead dependency.</item>
/// </list>
/// Failures surface as the exceptions callers already handle. An open
/// circuit throws <see cref="HttpRequestException"/> with status 503 and the
/// <see cref="BrokenCircuitException"/> as inner exception; a timeout throws
/// <see cref="TaskCanceledException"/> with an inner <see cref="TimeoutException"/>,
/// as <see cref="HttpClient.Timeout"/> does. The outbound token handler still
/// runs inside the pipeline, once per attempt.
/// <para>
/// Timeouts: a client's own <see cref="HttpClient.Timeout"/> (set in its
/// <c>ConfigureHttpClient</c> / <c>AddHttpClient(name, configure)</c> actions)
/// becomes its attempt timeout, and the total timeout covers every attempt.
/// The client's <see cref="HttpClient.Timeout"/> is then raised just past the
/// total timeout, so the pipeline's timeouts are the ones that govern; it
/// still bounds reading a buffered response body. A timeout set later, in a
/// typed client's constructor, is not seen here and overrides the raised one:
/// set it in the registration instead.
/// </para>
/// </summary>
public static class ChoServiceClientResilience
{
    /// <summary>Set to true on a request to allow retrying it although its method is not idempotent.</summary>
    public static readonly HttpRequestOptionsKey<bool> AllowRetry = new("cho.resilience.allow-retry");

    /// <summary>Set to true on a request that must not be retried (a PUT or DELETE that is not idempotent).</summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("cho.resilience.no-retry");

    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>
    /// Adds the resilience handler to every factory-built client of this
    /// process (<c>ConfigureHttpClientDefaults</c>), outermost, so the token
    /// handler and the client's own handlers run inside it. Called by
    /// <c>AddChoAuthentication</c>; safe to call more than once.
    /// </summary>
    public static IServiceCollection AddChoServiceClientResilience(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ChoResiliencePipelines)))
            return services;

        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ChoResiliencePipelines>();
        // Runs after every client's own configuration: raises HttpClient.Timeout
        // past the pipeline's total timeout (ChoClientTimeout).
        services.AddSingleton<IPostConfigureOptions<HttpClientFactoryOptions>, ChoClientTimeoutPostConfigure>();
#pragma warning disable CS0618 // ConfigureHttpMessageHandlerBuilder: the only hook that knows the client's name for a default handler
        services.ConfigureHttpClientDefaults(client => client.ConfigureHttpMessageHandlerBuilder(builder =>
        {
            var pipelines = builder.Services.GetRequiredService<ChoResiliencePipelines>();
            if (pipelines.For(builder.Name ?? string.Empty) is { } pipeline)
                builder.AdditionalHandlers.Insert(0, new ChoResilienceHandler(builder.Name ?? string.Empty, pipeline));
        }));
#pragma warning restore CS0618
        return services;
    }

    /// <summary>A named client with the default service-client resilience (and whatever <paramref name="configure"/> sets).</summary>
    public static IHttpClientBuilder AddChoServiceClient(
        this IServiceCollection services, string name, Action<HttpClient> configureClient, Action<ChoResilienceOptions>? configure = null)
    {
        services.AddChoServiceClientResilience();
        if (configure is not null) services.Configure(name, configure);
        return services.AddHttpClient(name, configureClient);
    }

    /// <summary>Turns the default resilience off for one client (it brings its own, e.g. <c>AddStandardResilienceHandler</c>).</summary>
    public static IHttpClientBuilder WithoutChoResilience(this IHttpClientBuilder builder)
    {
        builder.Services.Configure<ChoResilienceOptions>(builder.Name, o => o.Enabled = false);
        return builder;
    }

    /// <summary>Sets this client's resilience options.</summary>
    public static IHttpClientBuilder ConfigureChoResilience(this IHttpClientBuilder builder, Action<ChoResilienceOptions> configure)
    {
        builder.Services.Configure(builder.Name, configure);
        return builder;
    }

    /// <summary>Whether a failed <paramref name="request"/> may be sent again (see the type's remarks).</summary>
    public static bool IsRetryable(HttpRequestMessage? request, ChoResilienceOptions options)
    {
        if (request is null) return false;
        if (request.Options.TryGetValue(NoRetry, out var no) && no) return false;
        if (request.Options.TryGetValue(AllowRetry, out var allow) && allow) return true;
        if (options.RetryNonIdempotent) return true;
        if (request.Headers.Contains(IdempotencyKeyHeader)) return true;
        var method = request.Method;
        return method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options
               || method == HttpMethod.Trace || method == HttpMethod.Put || method == HttpMethod.Delete;
    }
}

/// <summary>One resilience pipeline per client name, built once so the circuit's state outlives handler rotation.</summary>
public sealed class ChoResiliencePipelines
{
    internal static readonly ResiliencePropertyKey<HttpRequestMessage> RequestKey = new("cho.resilience.request");

    // Polly's timeout strategy accepts at most a day.
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(1);

    // HttpClient.Timeout before a client's own actions run: tells "not set" apart from any real value.
    private static readonly TimeSpan NotSet = TimeSpan.FromMilliseconds(int.MaxValue - 1);

    private readonly IOptionsMonitor<ChoResilienceOptions> _options;
    private readonly IOptionsMonitor<HttpClientFactoryOptions>? _clients;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory? _loggers;
    private readonly ConcurrentDictionary<string, Lazy<ResiliencePipeline<HttpResponseMessage>?>> _pipelines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<ChoClientTimeouts?>> _timeouts = new(StringComparer.Ordinal);

    public ChoResiliencePipelines(
        IOptionsMonitor<ChoResilienceOptions> options, TimeProvider clock, ILoggerFactory? loggers = null,
        IOptionsMonitor<HttpClientFactoryOptions>? clients = null)
    {
        _options = options;
        _clock = clock;
        _loggers = loggers;
        _clients = clients;
    }

    /// <summary>The pipeline of <paramref name="clientName"/>; null when its resilience is turned off.</summary>
    public ResiliencePipeline<HttpResponseMessage>? For(string clientName) =>
        _pipelines.GetOrAdd(clientName, name => new Lazy<ResiliencePipeline<HttpResponseMessage>?>(() => Build(name))).Value;

    /// <summary>
    /// The timeouts of <paramref name="clientName"/>; null when its resilience is turned off.
    /// <list type="bullet">
    ///   <item>Attempt: <see cref="ChoResilienceOptions.AttemptTimeout"/> when set; otherwise the
    ///     client's own <see cref="HttpClient.Timeout"/> (none when that is infinite); otherwise
    ///     <see cref="ChoResilienceOptions.DefaultAttemptTimeout"/>.</item>
    ///   <item>Total: <see cref="ChoResilienceOptions.TotalTimeout"/> when set; otherwise the larger of
    ///     <see cref="ChoResilienceOptions.DefaultTotalTimeout"/> and attempt × (retries + 1) plus
    ///     twice the nominal back-off (jitter); none when there is no attempt timeout.</item>
    ///   <item>The client's <see cref="HttpClient.Timeout"/>: one second past the total, or infinite.</item>
    /// </list>
    /// </summary>
    public ChoClientTimeouts? Timeouts(string clientName) =>
        _timeouts.GetOrAdd(clientName, name => new Lazy<ChoClientTimeouts?>(() => ComputeTimeouts(name))).Value;

    private ChoClientTimeouts? ComputeTimeouts(string clientName)
    {
        var o = _options.Get(clientName);
        if (!o.Enabled) return null;

        var own = OwnClientTimeout(clientName);
        TimeSpan? attempt = o.AttemptTimeoutIsSet ? o.AttemptTimeout
            : own is { } t ? (t == Timeout.InfiniteTimeSpan ? null : t)
            : o.AttemptTimeout;

        TimeSpan? total;
        if (o.TotalTimeoutIsSet)
            total = o.TotalTimeout;
        else if (attempt is { } a)
        {
            var retries = Math.Max(0, o.MaxRetryAttempts);
            // Exponential back-off: base, 2 × base, 4 × base ...; doubled for jitter.
            var backoff = TimeSpan.FromTicks(o.RetryBaseDelay.Ticks * ((1L << Math.Min(retries, 20)) - 1) * 2);
            var needed = TimeSpan.FromTicks(a.Ticks * (retries + 1)) + backoff;
            total = needed > o.TotalTimeout ? needed : o.TotalTimeout;
        }
        else
            total = null;

        attempt = Clamp(attempt);
        total = Clamp(total);
        var httpClientTimeout = total is { } tt && tt + TimeSpan.FromSeconds(1) < NotSet
            ? tt + TimeSpan.FromSeconds(1)
            : Timeout.InfiniteTimeSpan;
        return new ChoClientTimeouts(attempt, total, httpClientTimeout, own);
    }

    private static TimeSpan? Clamp(TimeSpan? value) => value is { } v && v > MaxTimeout ? MaxTimeout : value;

    /// <summary>
    /// The <see cref="HttpClient.Timeout"/> the client's own configuration sets: its
    /// <c>HttpClientActions</c> are run against a client that sends nothing. Null when
    /// none sets one.
    /// </summary>
    private TimeSpan? OwnClientTimeout(string clientName)
    {
        if (_clients is null) return null;
        using var probe = new HttpClient(new NothingSent()) { Timeout = NotSet };
        foreach (var action in _clients.Get(clientName).HttpClientActions)
        {
            if (action.Target is ChoClientTimeout) continue; // ours, which applies the result
            try
            {
                action(probe);
            }
            catch (Exception ex)
            {
                _loggers?.CreateLogger("CloudHealthOffice.Infrastructure.Http.ChoResilience").LogWarning(ex,
                    "{Client}: a client configuration action failed while reading its timeout", clientName);
            }
        }
        return probe.Timeout == NotSet ? null : probe.Timeout;
    }

    private sealed class NothingSent : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The timeout probe never sends.");
    }

    private ResiliencePipeline<HttpResponseMessage>? Build(string clientName)
    {
        var o = _options.Get(clientName);
        if (!o.Enabled || Timeouts(clientName) is not { } timeouts) return null;
        var logger = _loggers?.CreateLogger("CloudHealthOffice.Infrastructure.Http.ChoResilience");

        bool Transient(Outcome<HttpResponseMessage> outcome) =>
            HttpClientResiliencePredicates.IsTransient(outcome)
            || (outcome.Result is { } r && o.AdditionalTransientStatusCodes.Contains((int)r.StatusCode));

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = _clock };
        if (timeouts.Total is { } total)
            builder.AddTimeout(new TimeoutStrategyOptions { Name = "total", Timeout = total });
        if (o.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = o.MaxRetryAttempts,
                Delay = o.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = true,
                ShouldHandle = args => ValueTask.FromResult(
                    ChoServiceClientResilience.IsRetryable(args.Context.Properties.GetValue(RequestKey, null!), o)
                    && Transient(args.Outcome)),
                OnRetry = args =>
                {
                    logger?.LogWarning(args.Outcome.Exception,
                        "{Client}: attempt {Attempt} failed ({Status}); retrying in {Delay}",
                        clientName, args.AttemptNumber + 1,
                        args.Outcome.Result is { } r ? (int)r.StatusCode : (object?)args.Outcome.Exception?.GetType().Name,
                        args.RetryDelay);
                    args.Outcome.Result?.Dispose();
                    return default;
                },
            });
        }
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            Name = "circuit",
            FailureRatio = o.CircuitFailureRatio,
            MinimumThroughput = o.CircuitMinimumThroughput,
            SamplingDuration = o.CircuitSamplingDuration,
            BreakDuration = o.CircuitBreakDuration,
            ShouldHandle = args => ValueTask.FromResult(Transient(args.Outcome)),
            OnOpened = args =>
            {
                logger?.LogError("{Client}: circuit opened for {Duration} after repeated failures", clientName, args.BreakDuration);
                return default;
            },
            OnClosed = _ =>
            {
                logger?.LogInformation("{Client}: circuit closed", clientName);
                return default;
            },
        });
        if (timeouts.Attempt is { } attempt)
            builder.AddTimeout(new TimeoutStrategyOptions { Name = "attempt", Timeout = attempt });
        return builder.Build();
    }
}

/// <summary>
/// A client's resilience timeouts (<see cref="ChoResiliencePipelines.Timeouts"/>). Null
/// means none. <see cref="OwnTimeout"/> is the <see cref="HttpClient.Timeout"/> the
/// client's configuration set, if any; <see cref="HttpClientTimeout"/> is what the
/// client is given instead, so the pipeline governs.
/// </summary>
public sealed record ChoClientTimeouts(TimeSpan? Attempt, TimeSpan? Total, TimeSpan HttpClientTimeout, TimeSpan? OwnTimeout);

/// <summary>Adds <see cref="ChoClientTimeout"/> as the last configuration action of every client.</summary>
internal sealed class ChoClientTimeoutPostConfigure : IPostConfigureOptions<HttpClientFactoryOptions>
{
    // The pipelines read HttpClientFactoryOptions themselves, so they are resolved when a client is made, not here.
    private readonly IServiceProvider _services;

    public ChoClientTimeoutPostConfigure(IServiceProvider services) => _services = services;

    public void PostConfigure(string? name, HttpClientFactoryOptions options) =>
        options.HttpClientActions.Add(new ChoClientTimeout(_services, name ?? Options.DefaultName).Apply);
}

/// <summary>Sets a client's <see cref="HttpClient.Timeout"/> past its pipeline's total timeout.</summary>
internal sealed class ChoClientTimeout
{
    private readonly IServiceProvider _services;
    private readonly string _clientName;

    public ChoClientTimeout(IServiceProvider services, string clientName)
    {
        _services = services;
        _clientName = clientName;
    }

    public void Apply(HttpClient client)
    {
        if (_services.GetRequiredService<ChoResiliencePipelines>().Timeouts(_clientName) is { } timeouts)
            client.Timeout = timeouts.HttpClientTimeout;
    }
}

/// <summary>Runs a client's requests through its <see cref="ChoResiliencePipelines"/> pipeline.</summary>
public sealed class ChoResilienceHandler : DelegatingHandler
{
    private readonly string _clientName;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public ChoResilienceHandler(string clientName, ResiliencePipeline<HttpResponseMessage> pipeline)
    {
        _clientName = clientName;
        _pipeline = pipeline;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(cancellationToken);
        context.Properties.Set(ChoResiliencePipelines.RequestKey, request);
        try
        {
            return await _pipeline.ExecuteAsync(
                async ctx => await base.SendAsync(request, ctx.CancellationToken).ConfigureAwait(false),
                context).ConfigureAwait(false);
        }
        catch (BrokenCircuitException ex)
        {
            throw new HttpRequestException(
                $"{_clientName}: the circuit is open after repeated failures; the call was not made.",
                ex, HttpStatusCode.ServiceUnavailable);
        }
        catch (TimeoutRejectedException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"{_clientName}: the call timed out after {ex.Timeout}.", new TimeoutException(ex.Message, ex));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
