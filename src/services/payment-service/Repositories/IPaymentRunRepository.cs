using PaymentService.Models;

namespace PaymentService.Repositories;

public interface IPaymentRunRepository
{
    Task<PaymentRun?> GetByIdAsync(string id);
    Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber);
    Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null);
    Task<PaymentRun> CreateAsync(PaymentRun paymentRun);
    Task<PaymentRun> UpdateAsync(PaymentRun paymentRun);

    /// <summary>
    /// Atomically moves the run from Pending to Running with its executor and
    /// start time (one conditional write). False when the run is not Pending
    /// any more (another executor won), or does not exist.
    /// </summary>
    Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt);

    /// <summary>
    /// Records released and needs-attention reservations (and warnings) on the
    /// run as a partial update: never rewrites the run's status or results, so
    /// it cannot undo a run that finished meanwhile. False when the run is gone.
    /// </summary>
    Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes);

    /// <summary>
    /// Writes only the run's EFT-file fields: sets <see cref="PaymentRun.EftFile"/>
    /// and appends <paramref name="addFallbacks"/> / <paramref name="addWarnings"/>,
    /// never rewriting anything else on the run (status, payments, results). With
    /// <paramref name="expectedSha256"/> null it applies only while the run has no
    /// EFT file (the first pin); otherwise only while the pinned file's SHA-256 is
    /// <paramref name="expectedSha256"/>. False when the condition did not hold or the run is gone.
    /// </summary>
    Task<bool> TrySaveEftFileAsync(string id, PaymentRunEftFile file, string? expectedSha256,
        IReadOnlyList<CheckFallbackPayment> addFallbacks, IReadOnlyList<string> addWarnings,
        IReadOnlyList<PaymentFileOutboxMessage>? addGlEvents = null);

    /// <summary>
    /// Records the reissue of a voided payment's remainder on its notice, with its GL event and a
    /// warning, in one conditional write: only while that notice has no reissue yet and the outbox
    /// holds no event with the message's id. False for the loser of a concurrent reissue.
    /// </summary>
    Task<bool> TryRecordRemainderReissueAsync(string id, string paymentId, DateTime at, string by, string method, string reference,
        PaymentFileOutboxMessage message, string warning);

    /// <summary>
    /// Replaces the pinned EFT file with a re-dated one and appends the old one
    /// (<paramref name="superseded"/>, with its Superseded* fields set) to
    /// <see cref="PaymentRun.EftFileHistory"/>, only while the pinned file's SHA-256
    /// is still <c>superseded.Sha256</c>, appending <paramref name="addWarnings"/> in the
    /// same write. Touches nothing else on the run.
    /// </summary>
    Task<bool> TryRepinEftFileAsync(string id, PaymentRunEftFile file, PaymentRunEftFile superseded, IReadOnlyList<string>? addWarnings = null,
        IReadOnlyList<PaymentFileOutboxMessage>? addGlEvents = null);

    Task DeleteAsync(string id);
}
