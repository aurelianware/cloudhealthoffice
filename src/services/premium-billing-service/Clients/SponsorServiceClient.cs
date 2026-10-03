using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Middleware;
using PremiumBillingService.Models;

namespace PremiumBillingService.Clients;

/// <summary>
/// Calls to sponsor-service. Every request names its tenant in
/// <c>X-Tenant-ID</c> (taken from the billing run or invoice being processed),
/// so the shared outbound handler can mint a service token when there is no
/// inbound caller. Inside a request the caller's own token is forwarded.
/// </summary>
public interface ISponsorServiceClient
{
    /// <summary>
    /// Every Active sponsor of the tenant, following sponsor-service's
    /// continuation tokens to the end. Throws on any failure: a partial list
    /// would silently leave sponsors unbilled.
    /// </summary>
    Task<List<SponsorDto>> GetActiveSponsorsAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the sponsor's status to Suspended. Never throws: the outcome says
    /// whether it worked and, if not, why (status code and body).
    /// </summary>
    Task<SponsorSuspensionOutcome> SuspendSponsorAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default);
}

public sealed class SponsorServiceClient : ISponsorServiceClient
{
    public const string HttpClientName = "SponsorService";

    /// <summary>sponsor-service caps pageSize at 100.</summary>
    internal const int PageSize = 100;

    /// <summary>A guard against a sponsor-service that keeps handing out tokens.</summary>
    internal const int MaxPages = 1000;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // sponsor-service writes enums as strings ("Active", "Commercial").
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SponsorServiceClient> _logger;

    public SponsorServiceClient(IHttpClientFactory httpClientFactory, ILogger<SponsorServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<SponsorDto>> GetActiveSponsorsAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var sponsors = new List<SponsorDto>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
                throw new InvalidOperationException(
                    $"sponsor-service returned more than {MaxPages} pages of Active sponsors; stopping rather than loop forever");

            var url = $"/api/v1/sponsors?status=Active&pageSize={PageSize}";
            if (continuationToken != null)
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _logger.LogError(
                        "sponsor-service refused the Active sponsor list ({StatusCode}) for tenant {TenantId}: {Body}",
                        (int)response.StatusCode, tenantId, body);
                throw new HttpRequestException(
                    $"sponsor-service answered {(int)response.StatusCode} {response.StatusCode} to the Active sponsor list: {body}",
                    null, response.StatusCode);
            }

            var pageBody = await response.Content.ReadFromJsonAsync<SponsorListPage>(JsonOptions, cancellationToken)
                ?? throw new InvalidOperationException("sponsor-service returned an empty sponsor list body");

            sponsors.AddRange(pageBody.Sponsors.Select(ToSponsorDto));

            continuationToken = string.IsNullOrEmpty(pageBody.ContinuationToken) ? null : pageBody.ContinuationToken;
            if (continuationToken == null)
                break;
            if (!seenTokens.Add(continuationToken))
                throw new InvalidOperationException(
                    "sponsor-service repeated a continuation token; stopping rather than bill sponsors twice");
        }

        _logger.LogInformation("Fetched {Count} Active sponsors for tenant {TenantId}", sponsors.Count, tenantId);
        return sponsors;
    }

    public async Task<SponsorSuspensionOutcome> SuspendSponsorAsync(
        string tenantId, string groupNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            return SponsorSuspensionOutcome.Failed(null, "No tenant to name on the sponsor-service call");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}")
            {
                Content = JsonContent.Create(new { status = "Suspended" })
            };
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
                return SponsorSuspensionOutcome.Succeeded();

            var body = await SafeReadAsync(response, cancellationToken);
            return SponsorSuspensionOutcome.Failed((int)response.StatusCode,
                $"sponsor-service answered {(int)response.StatusCode} {response.StatusCode}: {body}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return SponsorSuspensionOutcome.Failed(null, $"sponsor-service call failed: {ex.Message}");
        }
    }

    private static SponsorDto ToSponsorDto(SponsorWire wire) => new()
    {
        GroupNumber = wire.GroupNumber,
        EmployerName = wire.EmployerName,
        LineOfBusiness = wire.LineOfBusiness,
        BillingDay = wire.BillingInfo?.BillingDay is > 0 ? wire.BillingInfo.BillingDay : 1,
        GracePeriodDays = wire.BillingInfo?.GracePeriodDays ?? 30,
        PaymentMethod = wire.BillingInfo?.PaymentMethod
    };

    private static void RequireTenant(string tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("A sponsor-service call must name its tenant");
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            body = body.Replace("\r", string.Empty).Replace("\n", " ");
            return body.Length > 500 ? body[..500] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── sponsor-service wire shapes (GET /api/v1/sponsors) ─────────────

    internal sealed class SponsorListPage
    {
        public List<SponsorWire> Sponsors { get; set; } = new();
        public string? ContinuationToken { get; set; }
        public int TotalCount { get; set; }
    }

    internal sealed class SponsorWire
    {
        public string GroupNumber { get; set; } = string.Empty;
        public string EmployerName { get; set; } = string.Empty;
        public string? Status { get; set; }
        public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;
        public SponsorBillingInfoWire? BillingInfo { get; set; }
    }

    internal sealed class SponsorBillingInfoWire
    {
        public int BillingDay { get; set; } = 1;
        public int GracePeriodDays { get; set; } = 30;
        public string? PaymentMethod { get; set; }
    }
}

/// <summary>The result of asking sponsor-service to suspend a sponsor.</summary>
public sealed record SponsorSuspensionOutcome(bool Success, int? StatusCode, string? Error)
{
    public static SponsorSuspensionOutcome Succeeded() => new(true, null, null);
    public static SponsorSuspensionOutcome Failed(int? statusCode, string error) => new(false, statusCode, error);
}

/// <summary>
/// A sponsor as premium billing uses it, mapped from sponsor-service's
/// <c>Sponsor</c> (billing day and grace period live under its BillingInfo).
/// </summary>
public class SponsorDto
{
    public string GroupNumber { get; set; } = string.Empty;
    public string EmployerName { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;
    public int BillingDay { get; set; } = 1;
    public int GracePeriodDays { get; set; } = 30;
    public string? PaymentMethod { get; set; }
}
