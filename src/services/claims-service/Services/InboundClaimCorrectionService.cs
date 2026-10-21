using System.Security.Cryptography;
using System.Text;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services;

/// <summary>What happened to one inbound 837 replacement or void.</summary>
public sealed record InboundCorrectionOutcome(bool Success, string? ClaimId, IReadOnlyList<string> Errors)
{
    public static InboundCorrectionOutcome Failed(string error) => new(false, null, [error]);
}

/// <summary>
/// Applies an inbound 837 replacement (CLM05-3 = 7) or void (8) to the claim
/// it amends, named by 2300 REF*F8 (the payer claim control number CHO sent
/// as 835 CLP07, i.e. the claim's id). A correction is never processed as a
/// new claim: that would pay a corrected claim twice, or pay a voided one.
/// <list type="bullet">
/// <item>A replacement becomes an adjustment of the latest version of the
/// original's chain (<see cref="IClaimAdjustmentService"/>): the corrected
/// claim is adjudicated as the new version and the original is reversed.</item>
/// <item>A void voids the latest version (<see cref="IClaimFinalizationService.VoidAsync"/>).</item>
/// </list>
/// Refused, with nothing changed: no REF*F8, an original that does not exist
/// in the tenant, an original billed by a different provider (billing NPI),
/// or an original the adjustment or void rules don't allow (e.g. not yet
/// finalized). The error says why, for the import result and its 277CA.
/// </summary>
public interface IInboundClaimCorrectionService
{
    /// <summary>True for frequency codes this service handles (7 replacement, 8 void).</summary>
    static bool IsCorrection(string? frequencyCode) => frequencyCode?.Trim() is ReplacementFrequency or VoidFrequency;

    const string ReplacementFrequency = "7";
    const string VoidFrequency = "8";

    Task<InboundCorrectionOutcome> ApplyAsync(
        AdapterClaim claim,
        string sourceKey,
        string tenantId,
        string actorId,
        string? correlationId,
        CancellationToken ct = default);
}

public sealed class InboundClaimCorrectionService : IInboundClaimCorrectionService
{
    private readonly IClaimRepository _claims;
    private readonly IClaimAdjustmentService _adjustments;
    private readonly IClaimFinalizationService _finalization;
    private readonly ILogger<InboundClaimCorrectionService> _logger;

    public InboundClaimCorrectionService(
        IClaimRepository claims,
        IClaimAdjustmentService adjustments,
        IClaimFinalizationService finalization,
        ILogger<InboundClaimCorrectionService> logger)
    {
        _claims = claims;
        _adjustments = adjustments;
        _finalization = finalization;
        _logger = logger;
    }

    /// <param name="sourceKey">
    /// Identifies this correction in its file (interchange, transaction set
    /// and claim control numbers): resubmitting the same file returns the
    /// same adjustment instead of creating another.
    /// </param>
    public async Task<InboundCorrectionOutcome> ApplyAsync(
        AdapterClaim claim,
        string sourceKey,
        string tenantId,
        string actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var frequency = claim.ClaimFrequencyCode?.Trim();
        var kind = frequency == IInboundClaimCorrectionService.VoidFrequency ? "void" : "replacement";

        var reference = claim.OriginalClaimReference?.Trim();
        if (string.IsNullOrEmpty(reference))
        {
            return InboundCorrectionOutcome.Failed(
                $"A {kind} (frequency {frequency}) must name the claim it amends in REF*F8 (the payer claim number from the 835); nothing was processed.");
        }

        var original = await _claims.GetByIdAsync(reference);
        if (original is null || original.TenantId != tenantId)
        {
            return InboundCorrectionOutcome.Failed(
                $"REF*F8 {reference} names no claim of this plan; the {kind} was not processed.");
        }

        if (!string.Equals(original.BillingProviderNPI?.Trim(), claim.BillingProviderNPI?.Trim(), StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Inbound {Kind} for claim {ClaimId} refused: billing provider differs from the original's",
                kind, LogSanitizer.Sanitize(original.Id));
            return InboundCorrectionOutcome.Failed(
                $"REF*F8 {reference} names a claim billed by a different provider; the {kind} was not processed.");
        }

        // Correct the chain as it stands now: the latest version of the original.
        var chainId = string.IsNullOrWhiteSpace(original.ClaimVersionId) ? original.Id : original.ClaimVersionId;
        var latest = await _claims.GetLatestVersionAsync(chainId, DateTime.UtcNow) ?? original;

        return frequency == IInboundClaimCorrectionService.VoidFrequency
            ? await VoidAsync(latest, reference, tenantId, actorId, correlationId, ct)
            : await ReplaceAsync(latest, claim, reference, sourceKey, tenantId, actorId, correlationId, ct);
    }

    private async Task<InboundCorrectionOutcome> VoidAsync(
        Claim latest, string reference, string tenantId, string actorId, string? correlationId, CancellationToken ct)
    {
        var result = await _finalization.VoidAsync(
            latest.Id,
            new ClaimVoidRequest { Reason = $"837 void (frequency 8) of claim {reference}" },
            tenantId, actorId, correlationId, ct);

        return result.Outcome switch
        {
            ClaimVoidOutcome.Voided or ClaimVoidOutcome.AlreadyVoided => new InboundCorrectionOutcome(true, latest.Id, []),
            ClaimVoidOutcome.NotFound => InboundCorrectionOutcome.Failed($"REF*F8 {reference} names no claim of this plan; the void was not processed."),
            _ => InboundCorrectionOutcome.Failed($"Claim {reference} can't be voided: {result.Message}"),
        };
    }

    private async Task<InboundCorrectionOutcome> ReplaceAsync(
        Claim latest, AdapterClaim corrected, string reference, string sourceKey,
        string tenantId, string actorId, string? correlationId, CancellationToken ct)
    {
        var result = await _adjustments.CreateAdjustmentAsync(
            latest.Id,
            new ClaimAdjustmentRequest
            {
                AdjustmentReason = $"837 replacement (frequency 7) of claim {reference}",
                CorrectedClaim = corrected,
            },
            IdempotencyKey(tenantId, sourceKey),
            tenantId, actorId, correlationId, ct);

        return result.Outcome switch
        {
            ClaimAdjustmentOutcome.Created or ClaimAdjustmentOutcome.AlreadyExists =>
                new InboundCorrectionOutcome(true, result.NewVersion?.Id ?? result.Adjustment?.NewClaimId, []),
            ClaimAdjustmentOutcome.IdempotencyConflict =>
                InboundCorrectionOutcome.Failed($"This replacement of claim {reference} was already received with different content."),
            ClaimAdjustmentOutcome.SubmissionFailed =>
                new InboundCorrectionOutcome(false, null, result.SubmissionErrors.Select(e => $"{e.Field}: {e.Message}").DefaultIfEmpty(result.Message ?? "The replacement was rejected.").ToList()),
            _ => InboundCorrectionOutcome.Failed($"Claim {reference} can't be replaced: {result.Message}"),
        };
    }

    /// <summary>One key per correction in one file, so a resubmitted file is answered from the first run.</summary>
    internal static string IdempotencyKey(string tenantId, string sourceKey) =>
        "837-correction-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{sourceKey}"))).ToLowerInvariant()[..40];
}
