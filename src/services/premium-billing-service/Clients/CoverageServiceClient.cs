using System.Net;
using CloudHealthOffice.Infrastructure.Middleware;

namespace PremiumBillingService.Clients;

/// <summary>
/// Calls to coverage-service. Every request names its tenant in
/// <c>X-Tenant-ID</c> so the shared outbound handler can mint a service token
/// when there is no inbound caller.
/// </summary>
public interface ICoverageServiceClient
{
    /// <summary>
    /// Every active coverage for the sponsor group, following continuation
    /// tokens to the end. Throws on any failure: an empty list would bill the
    /// sponsor $0.
    /// </summary>
    Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every coverage record of the group, terminated ones included (rated
    /// billing credits retro terms from them). Throws on any failure.
    /// </summary>
    Task<List<CoverageDto>> GetAllCoveragesByGroupAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This coverage client cannot list terminated coverage");
}

public sealed class CoverageServiceClient : ICoverageServiceClient
{
    public const string HttpClientName = "CoverageService";
    internal const int PageSize = 100;
    internal const int MaxPages = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CoverageServiceClient> _logger;

    public CoverageServiceClient(IHttpClientFactory httpClientFactory, ILogger<CoverageServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(
        string tenantId, string groupNumber, CancellationToken cancellationToken = default)
        => ListAsync(tenantId, groupNumber, activeOnly: true, cancellationToken);

    public Task<List<CoverageDto>> GetAllCoveragesByGroupAsync(
        string tenantId, string groupNumber, CancellationToken cancellationToken = default)
        => ListAsync(tenantId, groupNumber, activeOnly: false, cancellationToken);

    private async Task<List<CoverageDto>> ListAsync(
        string tenantId, string groupNumber, bool activeOnly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("A coverage-service call must name its tenant");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var coverages = new List<CoverageDto>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
                throw new InvalidOperationException(
                    $"coverage-service returned more than {MaxPages} pages for group {groupNumber}");

            // coverage-service: GET /api/v1/coverage (singular) with activeOnly,
            // answering { coverage, continuationToken, totalCount }.
            var url = $"/api/v1/coverage?groupNumber={Uri.EscapeDataString(groupNumber)}&activeOnly={(activeOnly ? "true" : "false")}&pageSize={PageSize}";
            if (continuationToken != null)
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _logger.LogError(
                        "coverage-service refused the coverage list ({StatusCode}) for group {GroupNumber}, tenant {TenantId}",
                        (int)response.StatusCode, groupNumber, tenantId);
                throw new HttpRequestException(
                    $"coverage-service answered {(int)response.StatusCode} {response.StatusCode} for group {groupNumber}",
                    null, response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<CoverageListPage>(
                           SponsorServiceClient.JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("coverage-service returned an empty coverage list body");

            coverages.AddRange(body.Coverage.Select(c => new CoverageDto
            {
                CoverageId = c.Id,
                MemberId = c.MemberId,
                GroupNumber = c.GroupNumber,
                PlanId = c.PlanId,
                CoverageLevel = c.CoverageLevel,
                InsuranceLineCode = c.InsuranceLineCode,
                EffectiveDate = c.EffectiveDate,
                TerminationDate = c.TerminationDate,
                MonthlyPremium = c.MonthlyPremium,
                EmployerContribution = c.EmployerContribution
            }));

            continuationToken = string.IsNullOrEmpty(body.ContinuationToken) ? null : body.ContinuationToken;
            if (continuationToken == null)
                break;
            if (!seenTokens.Add(continuationToken))
                throw new InvalidOperationException(
                    $"coverage-service repeated a continuation token for group {groupNumber}");
        }

        return coverages;
    }

    internal sealed class CoverageListPage
    {
        public List<CoverageWire> Coverage { get; set; } = new();
        public string? ContinuationToken { get; set; }
    }

    internal sealed class CoverageWire
    {
        public string Id { get; set; } = string.Empty;
        public string MemberId { get; set; } = string.Empty;
        public string GroupNumber { get; set; } = string.Empty;
        public string? PlanId { get; set; }
        public string? CoverageLevel { get; set; }
        public string? InsuranceLineCode { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public decimal? MonthlyPremium { get; set; }
        public decimal? EmployerContribution { get; set; }
    }
}

/// <summary>A coverage record as premium billing uses it.</summary>
public class CoverageDto
{
    public string CoverageId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? MemberName { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string? PlanId { get; set; }
    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public decimal? MonthlyPremium { get; set; }
    public decimal? EmployerContribution { get; set; }
}
