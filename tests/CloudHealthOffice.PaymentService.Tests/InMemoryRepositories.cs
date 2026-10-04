using PaymentService.Models;
using PaymentService.Repositories;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>Single-tenant in-memory payment store for run tests.</summary>
public sealed class InMemoryPaymentRepository : IPaymentRepository
{
    private readonly List<Payment> _items = new();
    private readonly string? _tenant;

    public InMemoryPaymentRepository(string? tenant = null) => _tenant = tenant;

    public IReadOnlyList<Payment> All
    {
        get { lock (_items) return _items.ToList(); }
    }

    public Task<Payment?> GetByIdAsync(string id) => Task.FromResult(All.FirstOrDefault(p => p.Id == id));
    public Task<Payment?> GetByCheckNumberAsync(string checkNumber) => Task.FromResult(All.FirstOrDefault(p => p.CheckNumber == checkNumber));
    public Task<IEnumerable<Payment>> GetByClaimIdAsync(string claimId)
        => Task.FromResult<IEnumerable<Payment>>(All.Where(p => p.ClaimPayments.Any(cp => cp.ClaimId == claimId)).ToList());

    public Task<IReadOnlyCollection<string>> GetClaimIdsWithPaymentAsync(IReadOnlyCollection<string> claimIds, bool reversal)
    {
        var wanted = new HashSet<string>(claimIds, StringComparer.Ordinal);
        IReadOnlyCollection<string> found = All
            .Where(p => p.IsReversal == reversal)
            .SelectMany(p => p.ClaimPayments.Select(cp => cp.ClaimId))
            .Where(wanted.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    public Task<IEnumerable<Payment>> SearchAsync(DateTime? paymentDateFrom, DateTime? paymentDateTo, string? payerId, PaymentStatus? status, int page = 1, int pageSize = 50)
        => Task.FromResult<IEnumerable<Payment>>(All);
    public Task<PaymentsSummary> GetPaymentsSummaryAsync(DateTime from, DateTime to) => Task.FromResult(new PaymentsSummary());

    public Task<Payment> CreateAsync(Payment payment)
    {
        if (_tenant != null) payment.TenantId = _tenant;
        lock (_items) _items.Add(payment);
        return Task.FromResult(payment);
    }

    public Task<Payment> UpdateAsync(Payment payment)
    {
        lock (_items)
        {
            _items.RemoveAll(p => p.Id == payment.Id);
            _items.Add(payment);
        }
        return Task.FromResult(payment);
    }

    public Task DeleteAsync(string id)
    {
        lock (_items) _items.RemoveAll(p => p.Id == id);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPaymentRunRepository : IPaymentRunRepository
{
    private readonly List<PaymentRun> _items = new();
    private readonly string? _tenant;

    public InMemoryPaymentRunRepository(string? tenant = null) => _tenant = tenant;

    public Task<PaymentRun?> GetByIdAsync(string id) => Task.FromResult<PaymentRun?>(_items.FirstOrDefault(r => r.Id == id));
    public Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber) => Task.FromResult<PaymentRun?>(_items.FirstOrDefault(r => r.PaymentRunNumber == paymentRunNumber));
    public Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null) => Task.FromResult<IEnumerable<PaymentRun>>(_items.ToList());
    public Task<PaymentRun> CreateAsync(PaymentRun run) { if (_tenant != null) run.TenantId = _tenant; _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
    public Task<PaymentRun> UpdateAsync(PaymentRun run) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
    public Task DeleteAsync(string id) { _items.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
}

public sealed class InMemoryReversalRunRepository : IReversalRunRepository
{
    private readonly List<ReversalRun> _items = new();
    private readonly string? _tenant;

    public InMemoryReversalRunRepository(string? tenant = null) => _tenant = tenant;

    public Task<ReversalRun?> GetByIdAsync(string id) => Task.FromResult<ReversalRun?>(_items.FirstOrDefault(r => r.Id == id));
    public Task<ReversalRun?> GetByReversalRunNumberAsync(string number) => Task.FromResult<ReversalRun?>(_items.FirstOrDefault(r => r.ReversalRunNumber == number));
    public Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null) => Task.FromResult<IEnumerable<ReversalRun>>(_items.ToList());
    public Task<ReversalRun> CreateAsync(ReversalRun run) { if (_tenant != null) run.TenantId = _tenant; _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
    public Task<ReversalRun> UpdateAsync(ReversalRun run) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(run); return Task.FromResult(run); }
    public Task DeleteAsync(string id) { _items.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
}
