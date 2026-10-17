using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Persistence surface for the <see cref="ReversalRun"/> aggregate
/// (capability 5.12b). Mirrors <see cref="IPaymentRunRepository"/>
/// shape; implemented on MongoDB by <see cref="ReversalRunRepositoryMongo"/>.
/// </summary>
public interface IReversalRunRepository
{
    Task<ReversalRun?> GetByIdAsync(string id);
    Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber);
    Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null);
    Task<ReversalRun> CreateAsync(ReversalRun reversalRun);
    Task<ReversalRun> UpdateAsync(ReversalRun reversalRun);

    /// <summary>
    /// Atomically moves the run from Pending to Running with its executor and
    /// start time (one conditional write). False when the run is not Pending
    /// any more (another executor won), or does not exist.
    /// </summary>
    Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt);

    /// <summary>
    /// Records released and needs-attention reservations (and warnings) on the
    /// run as a partial update that never rewrites its status or results. False
    /// when the run is gone.
    /// </summary>
    Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes);
    Task DeleteAsync(string id);
}
