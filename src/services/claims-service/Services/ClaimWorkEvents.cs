using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Events;

namespace ClaimsService.Services;

/// <summary>
/// Tells work-management-service when a claim needs an examiner and when it
/// no longer does (<see cref="ClaimWorkEventTopics.Topic"/>). Same transport
/// and degraded-mode posture as <see cref="IClaimEventPublisher"/>: the claim
/// database is the source of truth, a failed or skipped publish is logged and
/// never fails the claim write, and work-management's reconcile repairs any
/// drift. Nothing here waits on work-management, so resolving a claim never
/// depends on it being up.
/// </summary>
public interface IClaimWorkEventPublisher
{
    /// <summary>The claim is (still, or newly) Pended: an examiner must work it.</summary>
    Task PublishWorkNeededAsync(Claim claim, string tenantId, string? actorId, CancellationToken ct = default);

    /// <summary>
    /// The claim left Pended. <paramref name="resolution"/> says how
    /// (Approved / Deny / Voided / Paid / Reprocessed ...).
    /// </summary>
    Task PublishWorkResolvedAsync(Claim claim, string tenantId, string resolution, string? actorId, CancellationToken ct = default);
}

/// <summary>Builds <see cref="ClaimWorkEvent"/> payloads from a claim (pure; unit-tested).</summary>
public static class ClaimWorkEvents
{
    /// <summary>Institutional admission types that mean expedited handling: 1 = emergency, 2 = urgent.</summary>
    private static readonly HashSet<string> ExpeditedAdmissionTypes = new(StringComparer.Ordinal) { "1", "2" };

    public static ClaimWorkEvent Needed(Claim claim, string tenantId, string? actorId, DateTime? observedAt = null) =>
        Build(claim, tenantId, ClaimWorkEventTopics.WorkNeeded, ClaimStatus.Pended.ToString(), null, actorId, observedAt);

    public static ClaimWorkEvent Resolved(Claim claim, string tenantId, string resolution, string? actorId, DateTime? observedAt = null) =>
        Build(claim, tenantId, ClaimWorkEventTopics.WorkResolved, claim.Status.ToString(), resolution, actorId, observedAt);

    private static ClaimWorkEvent Build(
        Claim claim, string tenantId, string eventType, string status, string? resolution, string? actorId, DateTime? observedAt)
    {
        var at = observedAt ?? DateTime.UtcNow;
        var pend = claim.PendDetails;
        var codes = ExaminerApproval.ReviewedFrom(pend)
            .Select(p => p.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // Pre-PendDetails claims carried the pend in the legacy denial code.
        var primary = pend?.PendCode is { Length: > 0 } code
            ? code.Trim().ToUpperInvariant()
            : claim.AdjudicationResult?.DenialReasonCode?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(primary) && !codes.Contains(primary))
            codes.Insert(0, primary);

        return new ClaimWorkEvent
        {
            EventId = $"{eventType}:{claim.Id}:{at.Ticks}",
            EventType = eventType,
            OccurredAt = at,
            SourceVersion = at.Ticks,
            TenantId = tenantId,
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber ?? string.Empty,
            ClaimStatus = status,
            Resolution = resolution,
            ActorId = actorId,
            PrimaryPendCode = string.IsNullOrWhiteSpace(primary) ? null : primary,
            PendCodes = codes,
            PendReason = pend?.PendReason,
            PendFingerprint = pend is null ? null : Models.PendDetails.ComputeFingerprint(pend),
            PendedAt = pend?.PendedAt,
            LineOfBusiness = claim.LineOfBusiness.ToString(),
            ClaimType = claim.ClaimType.ToString(),
            TotalChargeAmount = claim.TotalChargeAmount,
            BillingProviderNpi = string.IsNullOrWhiteSpace(claim.BillingProviderNPI) ? null : claim.BillingProviderNPI,
            BillingProviderName = claim.BillingProviderName,
            NetworkTier = claim.AdjudicationResult?.NetworkTier,
            Expedited = claim.Institutional?.AdmissionTypeCode is { } admission
                        && ExpeditedAdmissionTypes.Contains(admission.Trim()),
            ReceivedDate = claim.ReceivedDate ?? claim.SubmittedDate,
            ServiceDateFrom = claim.ServiceDateFrom == default ? null : claim.ServiceDateFrom,
            MemberId = claim.MemberId,
        };
    }
}
