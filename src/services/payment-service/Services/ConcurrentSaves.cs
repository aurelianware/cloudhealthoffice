using PaymentService.Models;
using PaymentService.Repositories;

namespace PaymentService.Services;

/// <summary>
/// Saves a run makes of documents another writer may change meanwhile. A save
/// based on a stale read fails (<see cref="PaymentConcurrencyException"/>); where
/// re-applying the run's own change to the fresh copy is correct, these re-read
/// and retry a bounded number of times instead of failing the run.
/// </summary>
public static class ConcurrentSaves
{
    /// <summary>Attempts of one save before the conflict is let through.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Saves the outcome of finalizing (or voiding) a payment's claims: each claim
    /// payment's FinalizedAt / FinalizeError, the payment's 835 envelope id, and the
    /// status that follows (Posted once every claim is finalized, else
    /// PaidPendingFinalize). On a conflict exactly that is re-applied to a fresh
    /// copy: a finalize recorded meanwhile is kept, never cleared.
    /// </summary>
    public static async Task SaveFinalizeOutcomeAsync(IPaymentRepository payments, Payment payment)
    {
        payment.Status = FinalizeStatus(payment);
        try
        {
            await payments.UpdateAsync(payment);
            return;
        }
        catch (PaymentConcurrencyException)
        {
            // Re-applied below to a fresh copy.
        }

        for (var attempt = 2; ; attempt++)
        {
            var fresh = await payments.GetByIdAsync(payment.Id);
            if (fresh == null)
                return; // Gone: a save of a missing payment writes nothing either.

            if (payment.EraEnvelopeId != null)
                fresh.EraEnvelopeId = payment.EraEnvelopeId;
            foreach (var cp in payment.ClaimPayments)
            {
                var target = fresh.ClaimPayments.FirstOrDefault(c => c.ClaimId == cp.ClaimId);
                if (target == null)
                    continue;
                if (cp.FinalizedAt != null)
                {
                    target.FinalizedAt ??= cp.FinalizedAt;
                    target.FinalizeError = null;
                }
                else if (target.FinalizedAt == null)
                {
                    target.FinalizeError = cp.FinalizeError;
                }
            }
            fresh.Status = FinalizeStatus(fresh);

            try
            {
                await payments.UpdateAsync(fresh);
                return;
            }
            catch (PaymentConcurrencyException) when (attempt < MaxAttempts)
            {
            }
        }
    }

    private static PaymentStatus FinalizeStatus(Payment payment) =>
        payment.ClaimPayments.All(cp => cp.FinalizedAt != null)
            ? PaymentStatus.Posted
            : PaymentStatus.PaidPendingFinalize;

    /// <summary>
    /// Keeps on the executing run what reservation reconciliation recorded on it
    /// meanwhile (the only other writer of a Running run: reconciliation of a run
    /// Running past the stuck threshold, or a person releasing one of its
    /// reservations). The run itself never writes those lists; the warnings
    /// recorded meanwhile are appended to the run's own.
    /// </summary>
    public static void KeepReservationOutcomes(PaymentRun run, PaymentRun fresh)
    {
        run.Revision = fresh.Revision;
        run.ReleasedReservationClaimIds = fresh.ReleasedReservationClaimIds;
        run.ReservationsNeedingAttention = fresh.ReservationsNeedingAttention;
        AppendNew(run.Warnings, fresh.Warnings);
    }

    /// <inheritdoc cref="KeepReservationOutcomes(PaymentRun, PaymentRun)"/>
    public static void KeepReservationOutcomes(ReversalRun run, ReversalRun fresh)
    {
        run.Revision = fresh.Revision;
        run.ReleasedReservationClaimIds = fresh.ReleasedReservationClaimIds;
        run.ReservationsNeedingAttention = fresh.ReservationsNeedingAttention;
        AppendNew(run.Warnings, fresh.Warnings);
    }

    private static void AppendNew(List<string> warnings, IEnumerable<string> fresh)
    {
        var known = new HashSet<string>(warnings, StringComparer.Ordinal);
        warnings.AddRange(fresh.Where(known.Add).ToList());
    }
}
