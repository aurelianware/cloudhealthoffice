using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace PaymentService.Services;

/// <summary>
/// The approval a payment or reversal run's downstream calls act under. Opened
/// by the run services only after the caller passed
/// <c>[RequirePermission("payments:approve")]</c> and
/// <see cref="IRunSeparationOfDuties.EnsureMayRelease"/> (or, for a finalize
/// retry, for a run a second user already released). While it is open, calls
/// made through the run-execution clients (claims-service and
/// trading-partner-service) carry payment-service's own service token for the
/// run's tenant; the approver is recorded on the run, the payments and the 835s,
/// and is never sent as a credential.
///
/// Ambient (<see cref="AsyncLocal{T}"/>) like <c>Activity.Current</c>: it flows
/// through awaits into the HttpClient pipeline and ends when disposed.
/// </summary>
public sealed class RunExecutionGrant : IDisposable
{
    private static readonly AsyncLocal<RunExecutionGrant?> Ambient = new();

    private readonly RunExecutionGrant? _previous;
    private bool _disposed;

    private RunExecutionGrant(string tenantId, string runId, string approvedBy)
    {
        TenantId = tenantId;
        RunId = runId;
        ApprovedBy = approvedBy;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>The grant the current flow runs under, or null.</summary>
    public static RunExecutionGrant? Current => Ambient.Value;

    /// <summary>The run's tenant; the only tenant a service token is minted for.</summary>
    public string TenantId { get; }

    public string RunId { get; }

    /// <summary>The user who released the run (the checker). Audit only.</summary>
    public string ApprovedBy { get; }

    /// <summary>
    /// Opens the grant for a run whose release was just approved by
    /// <paramref name="approvedBy"/>. Call it after the separation-of-duties check.
    /// </summary>
    public static RunExecutionGrant Open(string tenantId, string runId, string approvedBy)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A run execution needs the run's tenant; none was recorded on the run.");
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new InvalidOperationException("A run execution needs the approving user.");
        return new RunExecutionGrant(tenantId, runId, approvedBy);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Ambient.Value, this))
            Ambient.Value = _previous;
    }
}

/// <summary>
/// The outbound handler of the run-execution clients. It never forwards the
/// caller's token: inside an open <see cref="RunExecutionGrant"/> it replaces
/// whatever Authorization the request carries with a payment-service service
/// token for the grant's tenant (and pins <c>X-Tenant-ID</c> to it); outside a
/// grant it strips any credential, so the call goes out unauthenticated and the
/// callee refuses it. Only these clients use it; every other client keeps the
/// shared <c>ChoOutboundTokenHandler</c>, which
/// <see cref="RunExecutionHttpClientExtensions.AddRunExecutionServiceToken"/>
/// removes from these clients.
/// </summary>
public sealed class RunExecutionServiceTokenHandler : DelegatingHandler
{
    public static readonly EventId NoGrantEvent = new(4631, "PaymentRunCallWithoutApproval");

    private readonly IServiceProvider _services;
    private readonly ILogger<RunExecutionServiceTokenHandler> _logger;

    public RunExecutionServiceTokenHandler(IServiceProvider services, ILogger<RunExecutionServiceTokenHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = null;

        var grant = RunExecutionGrant.Current;
        if (grant == null)
        {
            _logger.LogError(NoGrantEvent,
                "Call to {Host}{Path} outside an approved run execution; sent without credentials",
                request.RequestUri?.Host, request.RequestUri?.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }

        if (request.Headers.TryGetValues(TenantMiddleware.TenantHeaderName, out var named)
            && named.FirstOrDefault() is { Length: > 0 } namedTenant
            && !string.Equals(namedTenant, grant.TenantId, StringComparison.Ordinal))
        {
            _logger.LogError(NoGrantEvent,
                "Call to {Host}{Path} names a tenant other than the approved run's; sent without credentials",
                request.RequestUri?.Host, request.RequestUri?.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }

        if (!IsInternal(request.RequestUri))
        {
            _logger.LogError(NoGrantEvent,
                "Run-execution call to non-CHO host {Host}; sent without credentials", request.RequestUri?.Host);
            return base.SendAsync(request, cancellationToken);
        }

        var tokens = ChoServiceTokens.Resolve(_services);
        if (tokens == null)
        {
            _logger.LogError(NoGrantEvent,
                "Call to {Host}{Path} for an approved run, but no service token is configured; sent without credentials",
                request.RequestUri?.Host, request.RequestUri?.AbsolutePath);
            return base.SendAsync(request, cancellationToken);
        }

        request.Headers.Remove(TenantMiddleware.TenantHeaderName);
        request.Headers.Add(TenantMiddleware.TenantHeaderName, grant.TenantId);
        return SendWithServiceTokenAsync(request, tokens, grant.TenantId, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithServiceTokenAsync(
        HttpRequestMessage request, IChoServiceTokenSource tokens, string tenantId, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await tokens.GetTokenAsync(tenantId, cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>The same notion of "a CHO service" as ChoOutboundTokenHandler: the configured allowlist.</summary>
    private bool IsInternal(Uri? uri)
        => _services.GetService<ChoOutboundHosts>()?.IsChoService(uri) == true;
}

public static class RunExecutionHttpClientExtensions
{
    /// <summary>
    /// Makes this client a run-execution client: the shared forwarding handler
    /// is removed, and <see cref="RunExecutionServiceTokenHandler"/> attaches the
    /// payment-service service token inside an approved run only.
    /// </summary>
    public static IHttpClientBuilder AddRunExecutionServiceToken(this IHttpClientBuilder builder)
    {
        builder.Services.AddTransient<RunExecutionServiceTokenHandler>();
        return builder
            .ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
            {
                for (var i = handlers.Count - 1; i >= 0; i--)
                {
                    if (handlers[i] is ChoOutboundTokenHandler)
                        handlers.RemoveAt(i);
                }
            })
            .AddHttpMessageHandler<RunExecutionServiceTokenHandler>();
    }
}
