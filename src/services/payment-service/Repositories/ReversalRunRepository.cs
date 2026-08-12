using PaymentService.Models;

namespace PaymentService.Repositories;

/// <summary>
/// Persistence surface for the <see cref="ReversalRun"/> aggregate
/// (capability 5.12b). Mirrors <see cref="IPaymentRunRepository"/>
/// shape — Mongo-canonical with a Cosmos-noop fallback inherited from
/// the same DI branching pattern in <c>Program.cs</c>.
/// </summary>
public interface IReversalRunRepository
{
    Task<ReversalRun?> GetByIdAsync(string id);
    Task<ReversalRun?> GetByReversalRunNumberAsync(string reversalRunNumber);
    Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null);
    Task<ReversalRun> CreateAsync(ReversalRun reversalRun);
    Task<ReversalRun> UpdateAsync(ReversalRun reversalRun);
    Task DeleteAsync(string id);
}
