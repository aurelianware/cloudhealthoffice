using System.Text.Json;
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

    /// <summary>Runs after the "already paid" lookup; concurrency tests use it as a rendezvous.</summary>
    public Func<Task>? AfterPaidLookup { get; set; }

    public async Task<IReadOnlyCollection<string>> GetClaimIdsWithPaymentAsync(IReadOnlyCollection<string> claimIds, bool reversal)
    {
        var wanted = new HashSet<string>(claimIds, StringComparer.Ordinal);
        IReadOnlyCollection<string> found = All
            .Where(p => p.IsReversal == reversal)
            .SelectMany(p => p.ClaimPayments.Select(cp => cp.ClaimId))
            .Where(wanted.Contains)
            .ToHashSet(StringComparer.Ordinal);
        if (AfterPaidLookup != null)
            await AfterPaidLookup();
        return found;
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

    // Copies in and out, as a database does: concurrent requests never share an instance.
    private static PaymentRun Copy(PaymentRun run) => JsonSerializer.Deserialize<PaymentRun>(JsonSerializer.Serialize(run))!;

    /// <summary>Runs after a read; concurrency tests use it as a rendezvous.</summary>
    public Func<Task>? AfterGet { get; set; }

    public async Task<PaymentRun?> GetByIdAsync(string id)
    {
        PaymentRun? copy;
        lock (_items) copy = _items.FirstOrDefault(r => r.Id == id) is { } r ? Copy(r) : null;
        if (AfterGet != null)
            await AfterGet();
        return copy;
    }
    public Task<PaymentRun?> GetByPaymentRunNumberAsync(string paymentRunNumber) => Task.FromResult<PaymentRun?>(_items.FirstOrDefault(r => r.PaymentRunNumber == paymentRunNumber));
    public Task<IEnumerable<PaymentRun>> SearchAsync(DateTime from, DateTime to, PaymentRunStatus? status = null) => Task.FromResult<IEnumerable<PaymentRun>>(_items.ToList());
    public Task<PaymentRun> CreateAsync(PaymentRun run) { if (_tenant != null) run.TenantId = _tenant; lock (_items) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(Copy(run)); } return Task.FromResult(run); }
    public Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run == null || run.Status != PaymentRunStatus.Pending)
                return Task.FromResult(false);
            run.Status = PaymentRunStatus.Running;
            run.ExecutedBy = executedBy;
            run.ExecutionStartedAt = startedAt;
            return Task.FromResult(true);
        }
    }

    public Task<PaymentRun> UpdateAsync(PaymentRun run) { lock (_items) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(Copy(run)); } return Task.FromResult(run); }
    public Task DeleteAsync(string id) { _items.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
}

public sealed class InMemoryReversalRunRepository : IReversalRunRepository
{
    private readonly List<ReversalRun> _items = new();
    private readonly string? _tenant;

    public InMemoryReversalRunRepository(string? tenant = null) => _tenant = tenant;

    // Copies in and out, as a database does: concurrent requests never share an instance.
    private static ReversalRun Copy(ReversalRun run) => JsonSerializer.Deserialize<ReversalRun>(JsonSerializer.Serialize(run))!;

    /// <summary>Runs after a read; concurrency tests use it as a rendezvous.</summary>
    public Func<Task>? AfterGet { get; set; }

    public async Task<ReversalRun?> GetByIdAsync(string id)
    {
        ReversalRun? copy;
        lock (_items) copy = _items.FirstOrDefault(r => r.Id == id) is { } r ? Copy(r) : null;
        if (AfterGet != null)
            await AfterGet();
        return copy;
    }
    public Task<ReversalRun?> GetByReversalRunNumberAsync(string number) => Task.FromResult<ReversalRun?>(_items.FirstOrDefault(r => r.ReversalRunNumber == number));
    public Task<IEnumerable<ReversalRun>> SearchAsync(DateTime from, DateTime to, ReversalRunStatus? status = null) => Task.FromResult<IEnumerable<ReversalRun>>(_items.ToList());
    public Task<ReversalRun> CreateAsync(ReversalRun run) { if (_tenant != null) run.TenantId = _tenant; lock (_items) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(Copy(run)); } return Task.FromResult(run); }
    public Task<bool> TryStartAsync(string id, string executedBy, DateTime startedAt)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run == null || run.Status != ReversalRunStatus.Pending)
                return Task.FromResult(false);
            run.Status = ReversalRunStatus.Running;
            run.ExecutedBy = executedBy;
            run.ExecutionStartedAt = startedAt;
            return Task.FromResult(true);
        }
    }

    public Task<ReversalRun> UpdateAsync(ReversalRun run) { lock (_items) { _items.RemoveAll(r => r.Id == run.Id); _items.Add(Copy(run)); } return Task.FromResult(run); }
    public Task DeleteAsync(string id) { _items.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
}

/// <summary>Insert-if-absent reservations, as the Mongo / Cosmos stores behave (duplicate key / 409).</summary>
public sealed class InMemoryClaimReservationRepository : IClaimReservationRepository
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ClaimReservation> _items = new();

    public IReadOnlyCollection<ClaimReservation> All => _items.Values.ToList();

    /// <summary>Optional delay inside TryReserveAsync, to widen race windows in concurrency tests.</summary>
    public Func<Task>? BeforeReserve { get; set; }

    public async Task<bool> TryReserveAsync(ClaimReservation reservation)
    {
        if (BeforeReserve != null)
            await BeforeReserve();
        reservation.Id = ClaimReservation.KeyFor(reservation.Kind, reservation.TenantId, reservation.ClaimId);
        return _items.TryAdd(reservation.Id, reservation);
    }

    public Task ReleaseAsync(ClaimReservationKind kind, string tenantId, string claimId, string runId)
    {
        var key = ClaimReservation.KeyFor(kind, tenantId, claimId);
        if (_items.TryGetValue(key, out var held) && held.RunId == runId)
            _items.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
