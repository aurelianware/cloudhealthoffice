using System.Net;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;

namespace PremiumBillingService.Clients;

/// <summary>
/// member-service's rating census: the minimum a premium needs about each
/// member of a group (who is the subscriber, relationship, date of birth,
/// tobacco use). Names the tenant in <c>X-Tenant-ID</c> on every request.
/// member-service admits only premium-billing-service's service token there,
/// so the call always carries this service's own token, never a user's.
/// </summary>
public interface IMemberServiceClient
{
    /// <summary>Every member of the group, following continuation tokens. Throws on any failure.</summary>
    Task<List<RatingCensusMemberDto>> GetRatingCensusAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default);
}

public sealed class MemberServiceClient : IMemberServiceClient
{
    public const string HttpClientName = "MemberService";
    internal const int PageSize = 100;
    internal const int MaxPages = 1000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IChoServiceTokenSource? _tokens;
    private readonly ILogger<MemberServiceClient> _logger;

    public MemberServiceClient(IHttpClientFactory httpClientFactory, IServiceProvider services, ILogger<MemberServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokens = ChoServiceTokens.Resolve(services);
        _logger = logger;
    }

    public async Task<List<RatingCensusMemberDto>> GetRatingCensusAsync(
        string tenantId, string groupNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("A member-service call must name its tenant");
        if (_tokens == null)
            throw new InvalidOperationException(
                "The rating census cannot be read: premium-billing-service has no service token configured (ChoAuth:ServiceToken)");

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var members = new List<RatingCensusMemberDto>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? continuationToken = null;

        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
                throw new InvalidOperationException($"member-service returned more than {MaxPages} census pages for group {groupNumber}");

            var url = $"/api/v1/members/rating-census?groupNumber={Uri.EscapeDataString(groupNumber)}&pageSize={PageSize}";
            if (continuationToken != null)
                url += $"&continuationToken={Uri.EscapeDataString(continuationToken)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            // Always this service's own token: set here, the shared outbound handler leaves it alone.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(tenantId, cancellationToken));
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    _logger.LogError("member-service refused the rating census ({StatusCode}) for group {GroupNumber}, tenant {TenantId}",
                        (int)response.StatusCode, groupNumber, tenantId);
                throw new HttpRequestException(
                    $"member-service answered {(int)response.StatusCode} {response.StatusCode} to the rating census of group {groupNumber}",
                    null, response.StatusCode);
            }

            var body = await response.Content.ReadFromJsonAsync<CensusPage>(SponsorServiceClient.JsonOptions, cancellationToken)
                       ?? throw new InvalidOperationException("member-service returned an empty rating census body");
            members.AddRange(body.Members);

            continuationToken = string.IsNullOrEmpty(body.ContinuationToken) ? null : body.ContinuationToken;
            if (continuationToken == null)
                break;
            if (!seenTokens.Add(continuationToken))
                throw new InvalidOperationException($"member-service repeated a continuation token for group {groupNumber}");
        }

        return members;
    }

    internal sealed class CensusPage
    {
        public List<RatingCensusMemberDto> Members { get; set; } = new();
        public string? ContinuationToken { get; set; }
    }
}

/// <summary>One member as member-service's rating census returns it.</summary>
public class RatingCensusMemberDto
{
    public string MemberId { get; set; } = string.Empty;
    public bool IsSubscriber { get; set; }
    public string? SubscriberMemberId { get; set; }

    /// <summary>X12 834 INS02: 18 self, 01 spouse, 53 life partner, 19 child…</summary>
    public string? RelationshipCode { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateTime? DateOfBirth { get; set; }

    /// <summary>Null when not reported; rated as a non-tobacco user.</summary>
    public bool? TobaccoUser { get; set; }
}
