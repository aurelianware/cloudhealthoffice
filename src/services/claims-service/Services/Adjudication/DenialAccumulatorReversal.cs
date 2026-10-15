using ClaimsService.Models;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// What a move to <see cref="ClaimStatus.Deny"/> owes the accumulator store.
/// Deny is not final (the claim may be reprocessed back to Pay before the
/// payment cycle), so a reversal it owes is NON-terminal: it takes back what
/// the claim contributed without fencing the claim id, and a later commit of
/// it (a new commit id) is accepted and counted once. A claim that never
/// contributed owes nothing.
/// <list type="bullet">
///   <item><description>From Pay (<see cref="ClaimStatus.Approved"/>): its commit
///     may have been applied (or be in flight) — reverse.</description></item>
///   <item><description>From Pended / Deny: claims price Prospective and commit
///     only on Pay, so normally nothing was written. A claim pended before
///     deferred commits shipped may still hold a direct write, so the store is
///     asked (<see cref="IAccumulatorCommitLookup"/>); reverse only if it holds
///     one. When the store cannot be asked, reverse anyway: a non-terminal
///     reversal of nothing changes nothing.</description></item>
/// </list>
/// Finalization (Deny → Denied) adds nothing; a void keeps its terminal
/// reversal (<see cref="AccumulatorOutboxItem.ForReversal"/>).
/// </summary>
public static class DenialAccumulatorReversal
{
    public static async Task<AccumulatorOutboxItem?> ForDenialAsync(
        Claim claim, ClaimStatus sourceStatus, DateTime now,
        IAccumulatorCommitLookup? lookup, ILogger logger, CancellationToken ct)
    {
        if (!Guid.TryParse(claim.BenefitPlanId, out var planId)) return null;
        if (sourceStatus == ClaimStatus.Approved)
            return AccumulatorOutboxItem.ForReversal(claim, now, terminal: false);
        if (lookup is null)
            return AccumulatorOutboxItem.ForReversal(claim, now, terminal: false);

        try
        {
            var subscriber = string.IsNullOrWhiteSpace(claim.SubscriberId) ? claim.MemberId : claim.SubscriberId!;
            var recorded = await lookup.LookupCommitRecordsAsync(
                [new AccumulatorCommitLookupItem(claim.Id, claim.MemberId, subscriber, planId,
                    claim.ServiceDateFrom.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))],
                ct);
            return recorded.TryGetValue(claim.Id, out var has) && has
                ? AccumulatorOutboxItem.ForReversal(claim, now, terminal: false)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex,
                "Accumulator lookup for denied claim {ClaimId} failed; queuing a non-terminal reversal (a no-op if nothing was written)",
                claim.Id.Replace("\r", "").Replace("\n", ""));
            return AccumulatorOutboxItem.ForReversal(claim, now, terminal: false);
        }
    }
}
