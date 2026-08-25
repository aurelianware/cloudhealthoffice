using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Applies a stored matched 835 to claim financials and member accumulators.
/// Source of the ERA is the remittance store — this type never invents an 835.
/// </summary>
public sealed class RemittancePoster : IRemittancePoster
{
    private readonly IRemittanceStore _receipts;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimRemittancePostingSink _claims;
    private readonly IRemittanceAccumulatorSink _accumulators;
    private readonly ILogger<RemittancePoster> _logger;
    private readonly TimeProvider _timeProvider;

    public RemittancePoster(
        IRemittanceStore receipts,
        IClaimTransmissionStore transmissions,
        IClaimRemittancePostingSink claims,
        IRemittanceAccumulatorSink accumulators,
        ILogger<RemittancePoster> logger,
        TimeProvider? timeProvider = null)
    {
        _receipts = receipts;
        _transmissions = transmissions;
        _claims = claims;
        _accumulators = accumulators;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RemittancePostResult> PostAsync(
        RemittancePostRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReceiptId) ||
            string.IsNullOrWhiteSpace(request.TenantId))
        {
            return Fail(GatewayErrorCategory.Validation, "Tenant and receipt id are required.");
        }

        var receipt = await _receipts.GetByIdAsync(request.ReceiptId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return Fail(GatewayErrorCategory.UnableToMatch, "Remittance receipt not found.");
        }

        if (!string.Equals(receipt.TenantId, request.TenantId.Trim(), StringComparison.Ordinal))
        {
            return Fail(GatewayErrorCategory.UnableToMatch, "Remittance receipt not found.");
        }

        if (receipt.Status == RemittanceLifecycleStatus.Posted)
        {
            return ToResult(receipt, replay: true, claims: CountPosted(receipt), accumulators: CountPosted(receipt));
        }

        if (receipt.Status != RemittanceLifecycleStatus.AvailableForPosting)
        {
            return new RemittancePostResult
            {
                Status = receipt.Status,
                RemittanceId = receipt.RemittanceId,
                ReceiptId = receipt.ReceiptId,
                TenantId = receipt.TenantId,
                ErrorCategory = GatewayErrorCategory.Validation,
                ErrorMessage = "Remittance is not available for posting."
            };
        }

        var claimsPosted = 0;
        var accumulatorsApplied = 0;
        foreach (var claim in receipt.Claims.Where(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched))
        {
            if (string.IsNullOrWhiteSpace(claim.ClaimId))
            {
                continue;
            }

            var claimPost = await _claims.PostAsync(
                new RemittanceClaimPost
                {
                    TenantId = receipt.TenantId,
                    ClaimId = claim.ClaimId,
                    RemittanceId = receipt.RemittanceId,
                    PaymentAmount = claim.PaidAmount,
                    PatientResponsibility = claim.PatientResponsibilityAmount,
                    CheckNumber = receipt.PaymentIdentifier,
                    PaymentDate = (receipt.PaymentDate ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime))
                        .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                    ControlNumber = receipt.ExternalTransactionId
                },
                cancellationToken).ConfigureAwait(false);

            if (claimPost.Outcome is RemittanceClaimPostOutcome.Failed
                or RemittanceClaimPostOutcome.Rejected)
            {
                _logger.LogWarning(
                    "Remittance claim post failed gateway={Gateway} id={RemittanceId} outcome={Outcome}",
                    Sanitize(receipt.Gateway),
                    Sanitize(receipt.RemittanceId),
                    claimPost.Outcome);
                return new RemittancePostResult
                {
                    Status = receipt.Status,
                    RemittanceId = receipt.RemittanceId,
                    ReceiptId = receipt.ReceiptId,
                    TenantId = receipt.TenantId,
                    ErrorCategory = GatewayErrorCategory.ServiceUnavailable,
                    ErrorMessage = claimPost.ErrorMessage ?? "claim-post-failed"
                };
            }

            if (claimPost.Outcome is RemittanceClaimPostOutcome.Posted
                or RemittanceClaimPostOutcome.AlreadyPosted)
            {
                claimsPosted++;
            }

            var memberId = await ResolveMemberIdAsync(claim, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(memberId))
            {
                continue;
            }

            var deltas = Deltas(claim);
            if (deltas.Deductible == 0m && deltas.Copay == 0m && deltas.Coinsurance == 0m)
            {
                continue;
            }

            var serviceDate = await ResolveServiceDateAsync(claim, cancellationToken).ConfigureAwait(false);
            var year = serviceDate.Year;
            var apply = await _accumulators.ApplyAsync(
                new RemittanceAccumulatorApply
                {
                    TenantId = receipt.TenantId,
                    MemberId = memberId,
                    ClaimId = claim.ClaimId,
                    RemittanceId = receipt.RemittanceId,
                    PlanYearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    PlanYearEnd = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc),
                    DeductibleDelta = deltas.Deductible,
                    CopayDelta = deltas.Copay,
                    CoinsuranceDelta = deltas.Coinsurance,
                    OopDelta = deltas.Deductible + deltas.Copay + deltas.Coinsurance
                },
                cancellationToken).ConfigureAwait(false);

            if (apply.Outcome == RemittanceAccumulatorApplyOutcome.Failed)
            {
                return new RemittancePostResult
                {
                    Status = receipt.Status,
                    RemittanceId = receipt.RemittanceId,
                    ReceiptId = receipt.ReceiptId,
                    TenantId = receipt.TenantId,
                    ErrorCategory = GatewayErrorCategory.ServiceUnavailable,
                    ErrorMessage = apply.ErrorMessage ?? "accumulator-apply-failed"
                };
            }

            if (apply.Outcome is RemittanceAccumulatorApplyOutcome.Applied
                or RemittanceAccumulatorApplyOutcome.Duplicate)
            {
                accumulatorsApplied++;
            }
        }

        var now = _timeProvider.GetUtcNow();
        receipt.Status = RemittanceLifecycleStatus.Posted;
        receipt.PostedAtUtc = now;
        if (receipt.Outbox.All(e => e.EventType != RemittanceMessageTypes.Posted))
        {
            receipt.Outbox.Add(new RemittanceOutboxEntry
            {
                EventType = RemittanceMessageTypes.Posted,
                CreatedAtUtc = now
            });
        }

        await _receipts.SaveAsync(receipt, cancellationToken).ConfigureAwait(false);
        ChoMetrics.RemittancePosted.Add(1,
            new KeyValuePair<string, object?>("cho.gateway", receipt.Gateway),
            new KeyValuePair<string, object?>("cho.status", receipt.Status.ToString()));
        _logger.LogInformation(
            "Remittance posted gateway={Gateway} id={RemittanceId} tenant={TenantId} claims={Claims} accumulators={Accumulators}",
            Sanitize(receipt.Gateway),
            Sanitize(receipt.RemittanceId),
            Sanitize(receipt.TenantId),
            claimsPosted,
            accumulatorsApplied);

        return ToResult(receipt, replay: false, claimsPosted, accumulatorsApplied);
    }

    private async Task<string?> ResolveMemberIdAsync(RemittedClaim claim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claim.TransmissionId))
        {
            return null;
        }

        var tx = await _transmissions.GetByIdAsync(claim.TransmissionId, ct).ConfigureAwait(false);
        return FirstNonBlank(
            tx?.InquirySource?.Subscriber?.MemberId,
            tx?.InquirySource?.Patient?.MemberId);
    }

    private async Task<DateTime> ResolveServiceDateAsync(RemittedClaim claim, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(claim.TransmissionId))
        {
            var tx = await _transmissions.GetByIdAsync(claim.TransmissionId, ct).ConfigureAwait(false);
            if (tx?.ServiceDateFrom is DateOnly d)
            {
                return d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            }
        }

        return _timeProvider.GetUtcNow().UtcDateTime.Date;
    }

    private static (decimal Deductible, decimal Copay, decimal Coinsurance) Deltas(RemittedClaim claim)
    {
        decimal deductible = 0, copay = 0, coinsurance = 0;
        foreach (var adjustment in claim.Adjustments.Concat(claim.ServiceLines.SelectMany(l => l.Adjustments)))
        {
            switch (adjustment.Kind)
            {
                case RemittanceAdjustmentKind.Deductible:
                    deductible += adjustment.Amount;
                    break;
                case RemittanceAdjustmentKind.Copay:
                    copay += adjustment.Amount;
                    break;
                case RemittanceAdjustmentKind.Coinsurance:
                    coinsurance += adjustment.Amount;
                    break;
            }
        }

        return (deductible, copay, coinsurance);
    }

    private static int CountPosted(RemittanceReceipt receipt) =>
        receipt.Claims.Count(c => c.MatchStatus == RemittanceClaimMatchStatus.Matched);

    private static RemittancePostResult ToResult(
        RemittanceReceipt receipt, bool replay, int claims, int accumulators) =>
        new()
        {
            Replay = replay,
            Status = receipt.Status,
            RemittanceId = receipt.RemittanceId,
            ReceiptId = receipt.ReceiptId,
            TenantId = receipt.TenantId,
            ClaimsPosted = claims,
            AccumulatorsApplied = accumulators
        };

    private static RemittancePostResult Fail(GatewayErrorCategory category, string message) =>
        new() { ErrorCategory = category, ErrorMessage = message };

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Sanitize(string? value) => ClaimAttachmentRules.SanitizeForLog(value);
}
