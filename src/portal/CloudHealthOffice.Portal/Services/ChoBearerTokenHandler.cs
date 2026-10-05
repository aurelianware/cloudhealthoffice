using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The hosts the portal may send a CHO token to: the base URLs configured under
/// <c>Services:*</c>, except the token service (it takes the Entra token, never a
/// CHO token) and non-CHO infrastructure listed in <see cref="ExcludedServices"/>.
/// Anything else (Stripe, Microsoft Graph, any external URL, a hard-coded fallback
/// URL that is not configured) gets no CHO token.
/// </summary>
public sealed class ChoServiceHosts
{
    /// <summary><c>Services:*</c> entries that are not CHO backend services.</summary>
    public static readonly IReadOnlySet<string> ExcludedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TokenService",
        "ArgoWorkflows",
        "Prometheus",
    };

    private readonly HashSet<string> _authorities;

    public ChoServiceHosts(IConfiguration configuration)
    {
        _authorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in configuration.GetSection("Services").GetChildren())
        {
            if (ExcludedServices.Contains(entry.Key))
                continue;
            if (Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                _authorities.Add(Authority(uri));
            }
        }
    }

    public bool IsChoService(Uri? uri)
        => uri != null && uri.IsAbsoluteUri
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && _authorities.Contains(Authority(uri));

    private static string Authority(Uri uri) => $"{uri.Host}:{uri.Port}";
}

/// <summary>
/// Attaches the signed-in user's CHO token (from <see cref="IChoTokenProvider"/>)
/// to requests for configured CHO services, and sets <c>X-Tenant-ID</c> to the
/// token's tenant so the two can never disagree. Requests to any other host pass
/// through untouched. A 401 from a CHO service drops the cached token and retries
/// once with a fresh one.
///
/// It is created per DI scope (per Blazor circuit) around the pooled
/// <c>"default"</c> handler, rather than added to the factory pipeline, because
/// IHttpClientFactory builds pipeline handlers in its own scope, where the
/// circuit's AuthenticationStateProvider (and so the user) is not available.
/// </summary>
public sealed class ChoBearerTokenHandler : DelegatingHandler
{
    public const string TenantHeader = "X-Tenant-ID";

    private readonly IChoTokenProvider _tokens;
    private readonly ChoServiceHosts _hosts;
    private readonly ILogger<ChoBearerTokenHandler> _logger;

    public ChoBearerTokenHandler(IChoTokenProvider tokens, ChoServiceHosts hosts, ILogger<ChoBearerTokenHandler> logger)
    {
        _tokens = tokens;
        _hosts = hosts;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_hosts.IsChoService(request.RequestUri))
            return await base.SendAsync(request, cancellationToken);

        var token = await RequireTokenAsync(request, cancellationToken);
        Apply(request, token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || !CanResend(request))
            return response;

        _logger.LogInformation("CHO service {Host} returned 401; refreshing the CHO token and retrying once",
            request.RequestUri?.Host);
        _tokens.Invalidate(token);

        var refreshed = await _tokens.GetTokenAsync(cancellationToken);
        if (!refreshed.Succeeded)
            return response;

        response.Dispose();
        Apply(request, refreshed.Token!);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<ChoTokenExchangeResponse> RequireTokenAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var result = await _tokens.GetTokenAsync(cancellationToken);
        if (result.Succeeded)
            return result.Token!;

        _logger.LogWarning("No CHO token ({Status}); not sending {Method} {Host}{Path}",
            result.Status, request.Method, request.RequestUri?.Host, request.RequestUri?.AbsolutePath);
        throw new ChoTokenUnavailableException(result.Status, request.RequestUri?.Host);
    }

    private static void Apply(HttpRequestMessage request, ChoTokenExchangeResponse token)
    {
        // Replaces anything set earlier (e.g. DefaultRequestHeaders), so the
        // tenant header always names the tenant the token acts in.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Remove(TenantHeader);
        request.Headers.TryAddWithoutValidation(TenantHeader, token.TenantId);
    }

    /// <summary>Only bodies that serialize again on a second send are retried.</summary>
    private static bool CanResend(HttpRequestMessage request)
        => request.Content is null or ByteArrayContent or JsonContent or FormUrlEncodedContent;
}
