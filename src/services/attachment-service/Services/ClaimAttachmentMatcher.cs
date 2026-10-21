using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace AttachmentService.Services;

/// <summary>
/// Finds the claim a 275 belongs to by its attachment control number: the
/// 275's TRN02 repeats the PWK06 the provider put on the 837. claims-service
/// keeps the PWK data on the claim and answers
/// <c>GET api/claims/attachment-control-numbers/{controlNumber}</c>.
/// </summary>
public interface IClaimAttachmentMatcher
{
    /// <summary>
    /// The claims in <paramref name="tenantId"/> that announced
    /// <paramref name="controlNumber"/>. Throws
    /// <see cref="AttachmentParentLookupException"/> when claims-service could
    /// not answer; an empty list means no claim has announced it (yet).
    /// </summary>
    Task<IReadOnlyList<ClaimControlNumberMatch>> FindAsync(string tenantId, string controlNumber, CancellationToken ct = default);

    /// <summary>
    /// The one claim the 275 belongs to, or null when none or more than one
    /// fits. A claim fits only when its billing NPI is the 275's provider:
    /// providers number their own attachments (often short, sequential
    /// numbers), so a control number alone would let one provider's document
    /// land on another provider's patient's claim. A single match from a
    /// different provider is held, not linked.
    /// </summary>
    static string? Choose(IReadOnlyList<ClaimControlNumberMatch> matches, string? providerId)
    {
        var sameProvider = matches
            .Where(m => !string.IsNullOrWhiteSpace(providerId)
                        && string.Equals(m.BillingProviderNPI, providerId.Trim(), StringComparison.Ordinal))
            .ToList();
        return sameProvider.Count == 1 ? sameProvider[0].ClaimId : null;
    }
}

public sealed class ClaimControlNumberMatch
{
    public string ClaimId { get; set; } = string.Empty;
    public string? ClaimNumber { get; set; }
    public string? BillingProviderNPI { get; set; }
}

/// <summary>
/// Calls claims-service with attachment-service's own service token for the
/// tenant (so the background matcher works with no user on the call), like
/// <see cref="HttpAttachmentParentLookup"/>. Fails closed.
/// </summary>
public sealed class HttpClaimAttachmentMatcher : IClaimAttachmentMatcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<HttpClaimAttachmentMatcher> _logger;

    public HttpClaimAttachmentMatcher(IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<HttpClaimAttachmentMatcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClaimControlNumberMatch>> FindAsync(string tenantId, string controlNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("A tenant is required to match an attachment to a claim.");

        var client = _httpClientFactory.CreateClient(HttpAttachmentParentLookup.ClaimsClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "api/claims/attachment-control-numbers/" + Uri.EscapeDataString(controlNumber.Trim()));
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);

        HttpResponseMessage response;
        try
        {
            var tokens = ChoServiceTokens.Resolve(_services);
            var target = client.BaseAddress is null ? request.RequestUri : new Uri(client.BaseAddress, request.RequestUri!);
            if (tokens is not null && _services.GetService<ChoOutboundHosts>()?.IsChoService(target) == true)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(tenantId));
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "claims-service unreachable matching an attachment control number for tenant {TenantId}",
                LogSanitizer.Sanitize(tenantId));
            throw new AttachmentParentLookupException("claims-service unreachable", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("claims-service answered {Status} matching an attachment control number for tenant {TenantId}",
                    (int)response.StatusCode, LogSanitizer.Sanitize(tenantId));
                throw new AttachmentParentLookupException($"claims-service answered {(int)response.StatusCode}");
            }

            var matches = await response.Content.ReadFromJsonAsync<List<ClaimControlNumberMatch>>(cancellationToken: ct);
            return (matches ?? []).Where(m => !string.IsNullOrWhiteSpace(m.ClaimId)).ToList();
        }
    }
}
