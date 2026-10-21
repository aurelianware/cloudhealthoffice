using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Resolution;

/// <summary>
/// HTTP-backed <see cref="IMemberResolver"/> calling member-service's
/// <c>GET /api/v1/members/{memberId}</c>. Resolves a small summary view
/// of the member that the adjudication pipeline needs; member-service
/// remains the system-of-record for full member documents.
///
/// <para>
/// <see cref="IHttpClientFactory"/> with a named client and the
/// <c>X-Tenant-ID</c> header for tenant scoping. A 404 is a definitive
/// "no such member" (<c>null</c>); any other failure (status, transport,
/// timeout, unreadable body) throws <see cref="MemberUnavailableException"/>,
/// like <see cref="HttpBenefitPlanResolver"/>: eligibility fails closed.
/// </para>
/// </summary>
public class HttpMemberResolver : IMemberResolver
{
    public const string HttpClientName = "MemberService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpMemberResolver> _logger;

    public HttpMemberResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpMemberResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<ResolvedMember?> GetMemberAsync(
        string tenantId, string memberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return null;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/api/v1/members/{Uri.EscapeDataString(memberId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Member-service returned {StatusCode} resolving member {MemberId}",
                    response.StatusCode, LogSanitizer.MaskId(memberId));
                throw new MemberUnavailableException($"member-service answered {(int)response.StatusCode}");
            }

            var dto = await response.Content
                .ReadFromJsonAsync<MemberDto>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (dto is null) throw new MemberUnavailableException("member-service answered an empty body");

            return new ResolvedMember
            {
                MemberId = dto.MemberId ?? memberId,
                SubscriberMemberId = dto.SubscriberMemberId,
                IsSubscriber = dto.IsSubscriber,
                DateOfBirth = dto.DateOfBirth,
                Gender = dto.Gender,
                EnrollmentStatus = dto.Status,
                EffectiveDate = dto.EffectiveDate,
                TerminationDate = dto.TerminationDate,
                PlanChangeEffectiveDate = dto.PlanChangeEffectiveDate,
                MedicaidSpendDownLiabilityAmount = dto.MedicaidSpendDownLiabilityAmount,
                MedicaidSpendDownAmountMet = dto.MedicaidSpendDownAmountMet,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException
                                    or JsonException
                                    || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex,
                "Failed to resolve member {MemberId} for tenant {TenantId}",
                LogSanitizer.MaskId(memberId), LogSanitizer.Sanitize(tenantId));
            throw new MemberUnavailableException("member-service unreachable", ex);
        }
    }

    private sealed class MemberDto
    {
        public string? MemberId { get; set; }
        public string? SubscriberMemberId { get; set; }
        public bool IsSubscriber { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? Gender { get; set; }
        public string? Status { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public DateTime? PlanChangeEffectiveDate { get; set; }
        public decimal? MedicaidSpendDownLiabilityAmount { get; set; }
        public decimal MedicaidSpendDownAmountMet { get; set; }
    }
}
