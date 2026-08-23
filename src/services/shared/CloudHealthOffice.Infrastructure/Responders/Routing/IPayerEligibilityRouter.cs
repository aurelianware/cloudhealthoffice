using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Routing;

/// <summary>
/// Resolves an inbound eligibility inquiry to a Cloud Health Office tenant /
/// payer using trusted identifiers only. Never uses
/// <see cref="PayerEligibilityInquiry.ClaimedTenantId"/>.
///
/// Ambiguous and unknown routing fail explicitly; the router never guesses.
/// </summary>
public interface IPayerEligibilityRouter
{
    PayerEligibilityRouteResolution Resolve(PayerEligibilityInquiry inquiry);
}

/// <summary>Outcome of inbound payer / tenant routing.</summary>
public sealed class PayerEligibilityRouteResolution
{
    public EligibilityBusinessStatus Status { get; init; }

    public string? TenantId { get; init; }

    public string? CanonicalPayerId { get; init; }

    public string? PayerName { get; init; }

    public string? Message { get; init; }

    public bool IsResolved => Status == EligibilityBusinessStatus.Success && !string.IsNullOrWhiteSpace(TenantId);

    public static PayerEligibilityRouteResolution Found(
        string tenantId, string canonicalPayerId, string payerName) =>
        new()
        {
            Status = EligibilityBusinessStatus.Success,
            TenantId = tenantId,
            CanonicalPayerId = canonicalPayerId,
            PayerName = payerName
        };

    public static PayerEligibilityRouteResolution Fail(EligibilityBusinessStatus status, string message) =>
        new() { Status = status, Message = message };
}
