using System.Net;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace AttachmentService.Services;

/// <summary>What an attachment is filed against.</summary>
public enum AttachmentParentType
{
    Claim,
    Authorization,
    Appeal,
}

/// <summary>
/// Confirms that an attachment's claim, authorization or appeal exists for
/// the caller's tenant before anything is stored against it.
/// </summary>
public interface IAttachmentParentLookup
{
    /// <summary>
    /// True when the parent exists in <paramref name="tenantId"/>; false when
    /// the owning service answers 404 (which is also what it answers for
    /// another tenant's record). Throws <see cref="AttachmentParentLookupException"/>
    /// when existence could not be established.
    /// </summary>
    Task<bool> ExistsAsync(string tenantId, AttachmentParentType type, string parentId, CancellationToken ct = default);
}

/// <summary>
/// Reads the parent from the service that owns it (claims-service
/// <c>GET api/claims/{id}</c>, authorization-service
/// <c>GET api/authorizations/{id}</c>, appeals-service <c>GET api/appeals/{id}</c>),
/// each of which scopes the read to the tenant of the presented token.
/// <para>
/// Like <see cref="HttpTradingPartnerLookup"/>, the call carries
/// attachment-service's own service token for the tenant when one is
/// configured and the target is a CHO host; otherwise the shared handler
/// forwards the caller's token. Fails closed: 401/403, 5xx, a timeout or a
/// transport error throw, and no attachment is stored.
/// </para>
/// </summary>
public sealed class HttpAttachmentParentLookup : IAttachmentParentLookup
{
    public const string ClaimsClientName = "ClaimsService";
    public const string AuthorizationClientName = "AuthorizationService";
    public const string AppealsClientName = "AppealsService";

    public const string ClaimsDefaultBaseUrl = "http://claims-service.cloudhealthoffice";
    public const string AuthorizationDefaultBaseUrl = "http://authorization-service.cloudhealthoffice";
    public const string AppealsDefaultBaseUrl = "http://appeals-service.cloudhealthoffice";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpAttachmentParentLookup> _logger;

    public HttpAttachmentParentLookup(
        IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpAttachmentParentLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string tenantId, AttachmentParentType type, string parentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A tenant is required to look up an attachment's parent.");

        var (clientName, path) = type switch
        {
            AttachmentParentType.Claim => (ClaimsClientName, "api/claims/"),
            AttachmentParentType.Authorization => (AuthorizationClientName, "api/authorizations/"),
            AttachmentParentType.Appeal => (AppealsClientName, "api/appeals/"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        var client = _httpClientFactory.CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, path + Uri.EscapeDataString(parentId));
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);

        HttpResponseMessage response;
        try
        {
            await AttachServiceTokenAsync(request, client, tenantId);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "{Client} unreachable checking a {ParentType} for tenant {TenantId}",
                clientName, type, LogSanitizer.Sanitize(tenantId));
            throw new AttachmentParentLookupException($"{clientName} unreachable", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode == HttpStatusCode.NotFound) return false;

            _logger.LogError("{Client} answered {Status} checking a {ParentType} for tenant {TenantId}",
                clientName, (int)response.StatusCode, type, LogSanitizer.Sanitize(tenantId));
            throw new AttachmentParentLookupException($"{clientName} answered {(int)response.StatusCode}");
        }
    }

    private async Task AttachServiceTokenAsync(HttpRequestMessage request, HttpClient client, string tenantId)
    {
        var tokens = ChoServiceTokens.Resolve(_services);
        var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
        if (tokens is null || _services.GetService<ChoOutboundHosts>()?.IsChoService(target) != true)
        {
            return; // the shared handler forwards the caller's token (CHO hosts only)
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId));
    }
}

/// <summary>Whether an attachment's parent exists could not be established.</summary>
public sealed class AttachmentParentLookupException : Exception
{
    public AttachmentParentLookupException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
