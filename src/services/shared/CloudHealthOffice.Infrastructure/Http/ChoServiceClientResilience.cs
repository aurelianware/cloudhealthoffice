using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>Budget for the whole call, every retry included. The client's own <see cref="HttpClient.Timeout"/> still applies on top.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Budget for one attempt.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

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

    private readonly IOptionsMonitor<ChoResilienceOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory? _loggers;
    private readonly ConcurrentDictionary<string, Lazy<ResiliencePipeline<HttpResponseMessage>?>> _pipelines = new(StringComparer.Ordinal);

    public ChoResiliencePipelines(IOptionsMonitor<ChoResilienceOptions> options, TimeProvider clock, ILoggerFactory? loggers = null)
    {
        _options = options;
        _clock = clock;
        _loggers = loggers;
    }

    /// <summary>The pipeline of <paramref name="clientName"/>; null when its resilience is turned off.</summary>
    public ResiliencePipeline<HttpResponseMessage>? For(string clientName) =>
        _pipelines.GetOrAdd(clientName, name => new Lazy<ResiliencePipeline<HttpResponseMessage>?>(() => Build(name))).Value;

    private ResiliencePipeline<HttpResponseMessage>? Build(string clientName)
    {
        var o = _options.Get(clientName);
        if (!o.Enabled) return null;
        var logger = _loggers?.CreateLogger("CloudHealthOffice.Infrastructure.Http.ChoResilience");

        bool Transient(Outcome<HttpResponseMessage> outcome) =>
            HttpClientResiliencePredicates.IsTransient(outcome)
            || (outcome.Result is { } r && o.AdditionalTransientStatusCodes.Contains((int)r.StatusCode));

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = _clock };
        builder.AddTimeout(new TimeoutStrategyOptions { Name = "total", Timeout = o.TotalTimeout });
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
        builder.AddTimeout(new TimeoutStrategyOptions { Name = "attempt", Timeout = o.AttemptTimeout });
        return builder.Build();
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
