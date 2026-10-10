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

    /// <summary>When true, the next CreateAsync throws (the insert was attempted; the run keeps its reservations).</summary>
    public bool FailNextCreate { get; set; }

    /// <summary>When true, the next CreateAsync stores the payment and then throws (a write that landed but timed out).</summary>
    public bool StoreThenFailNextCreate { get; set; }

    public Task<Payment> CreateAsync(Payment payment)
    {
        if (FailNextCreate)
        {
            FailNextCreate = false;
            throw new InvalidOperationException("payment store unavailable");
        }
        if (StoreThenFailNextCreate)
        {
            StoreThenFailNextCreate = false;
            if (_tenant != null) payment.TenantId = _tenant;
            lock (_items) _items.Add(payment);
            throw new TimeoutException("payment insert timed out");
        }
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

    public int EftFileWrites;

    public Task<bool> TrySaveEftFileAsync(string id, PaymentRunEftFile file, string? expectedSha256,
        IReadOnlyList<CheckFallbackPayment> addFallbacks, IReadOnlyList<string> addWarnings)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run == null)
                return Task.FromResult(false);
            var holds = expectedSha256 == null ? run.EftFile == null : run.EftFile?.Sha256 == expectedSha256;
            if (!holds)
                return Task.FromResult(false);
            run.EftFile = JsonSerializer.Deserialize<PaymentRunEftFile>(JsonSerializer.Serialize(file));
            run.CheckFallbacks.AddRange(addFallbacks);
            run.Warnings.AddRange(addWarnings);
            EftFileWrites++;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryRepinEftFileAsync(string id, PaymentRunEftFile file, PaymentRunEftFile superseded)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run?.EftFile == null || run.EftFile.Sha256 != superseded.Sha256)
                return Task.FromResult(false);
            run.EftFile = JsonSerializer.Deserialize<PaymentRunEftFile>(JsonSerializer.Serialize(file));
            run.EftFileHistory.Add(JsonSerializer.Deserialize<PaymentRunEftFile>(JsonSerializer.Serialize(superseded))!);
            return Task.FromResult(true);
        }
    }

    /// <summary>Changes a stored run in place (a concurrent writer), bypassing the service.</summary>
    public void Mutate(string id, Action<PaymentRun> change)
    {
        lock (_items) change(_items.First(r => r.Id == id));
    }

    public Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run == null)
                return Task.FromResult(false);
            outcomes.ApplyTo(run.ReleasedReservationClaimIds, run.ReservationsNeedingAttention, run.Warnings);
            return Task.FromResult(true);
        }
    }

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

    public Task<bool> RecordReservationOutcomesAsync(string id, ReservationOutcomes outcomes)
    {
        lock (_items)
        {
            var run = _items.FirstOrDefault(r => r.Id == id);
            if (run == null)
                return Task.FromResult(false);
            outcomes.ApplyTo(run.ReleasedReservationClaimIds, run.ReservationsNeedingAttention, run.Warnings);
            return Task.FromResult(true);
        }
    }

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
        reservation.Version ??= Guid.NewGuid().ToString("N");
        return _items.TryAdd(reservation.Id, Copy(reservation));
    }

    public Task ReleaseAsync(ClaimReservationKind kind, string tenantId, string claimId, string runId)
    {
        var key = ClaimReservation.KeyFor(kind, tenantId, claimId);
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var held) && held.RunId == runId)
                _items.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    private readonly object _lock = new();

    // Copies in and out, as a database does.
    private static ClaimReservation Copy(ClaimReservation r) => JsonSerializer.Deserialize<ClaimReservation>(JsonSerializer.Serialize(r))!;

    /// <summary>Runs once before each conditional delete; concurrency tests use it to interleave another release and a new run.</summary>
    public Func<Task>? BeforeConditionalDelete { get; set; }

    public Task<ClaimReservation?> GetAsync(ClaimReservationKind kind, string tenantId, string claimId)
        => Task.FromResult(_items.TryGetValue(ClaimReservation.KeyFor(kind, tenantId, claimId), out var r) ? Copy(r) : null);

    public Task<IReadOnlyCollection<string>> ListTenantsAsync()
        => Task.FromResult<IReadOnlyCollection<string>>(_items.Values.Select(r => r.TenantId).Distinct().ToList());

    public Task<IReadOnlyList<ClaimReservation>> ListByTenantAsync(string tenantId, bool needsAttentionOnly = false)
        => Task.FromResult<IReadOnlyList<ClaimReservation>>(_items.Values
            .Where(r => r.TenantId == tenantId && (!needsAttentionOnly || r.NeedsAttention))
            .OrderBy(r => r.ReservedAt)
            .Select(Copy)
            .ToList());

    public async Task<bool> TryDeleteIfUnchangedAsync(ClaimReservation observed)
    {
        var hook = BeforeConditionalDelete;
        if (hook != null)
        {
            BeforeConditionalDelete = null;
            await hook();
        }
        lock (_lock)
        {
            if (!_items.TryGetValue(observed.Id, out var current))
                return false;
            if (current.RunId != observed.RunId || current.Version != observed.Version)
                return false;
            return _items.TryRemove(observed.Id, out _);
        }
    }

    public Task<bool> TrySetAttentionIfUnchangedAsync(ClaimReservation observed, string? reason, DateTime? flaggedAt)
    {
        lock (_lock)
        {
            if (!_items.TryGetValue(observed.Id, out var current)
                || current.RunId != observed.RunId || current.Version != observed.Version)
                return Task.FromResult(false);
            var updated = Copy(current);
            updated.NeedsAttention = reason != null;
            updated.AttentionReason = reason;
            updated.FlaggedAt = reason != null ? flaggedAt : null;
            _items[observed.Id] = updated;
            return Task.FromResult(true);
        }
    }
}

/// <summary>Append-only audit, kept for assertions.</summary>
public sealed class InMemoryReservationAuditLog : IReservationAuditLog
{
    private readonly List<ReservationAuditEntry> _entries = new();

    public IReadOnlyList<ReservationAuditEntry> All
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public Task RecordAsync(ReservationAuditEntry entry)
    {
        lock (_entries) _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ReservationAuditEntry>> ListAsync(string tenantId, string? runId = null)
        => Task.FromResult<IReadOnlyList<ReservationAuditEntry>>(All
            .Where(e => e.TenantId == tenantId && (runId == null || e.RunId == runId))
            .OrderByDescending(e => e.At)
            .ToList());
}

/// <summary>File ID modifier claims in memory, with the stores' insert-if-absent semantics.</summary>
public sealed class InMemoryNachaFileIdModifierAllocator : INachaFileIdModifierAllocator
{
    private readonly Dictionary<string, string> _holders = new();

    public Task<string> AllocateAsync(string tenantId, string immediateDestination, string immediateOrigin, DateTime fileCreationDate, string runId)
        => NachaFileIdModifiers.AllocateAsync(tenantId, immediateDestination, immediateOrigin, fileCreationDate, runId, claim =>
        {
            lock (_holders)
            {
                _holders.TryAdd(claim.Id, claim.RunId);
                return Task.FromResult(_holders[claim.Id]);
            }
        });
}

/// <summary>
/// Transmission records in memory with the stores' semantics: copies in and
/// out, insert-if-absent, replace only while the version is the one read.
/// </summary>
public sealed class InMemoryPaymentFileTransmissionRepository : IPaymentFileTransmissionRepository
{
    private readonly Dictionary<string, string> _json = new();

    /// <summary>When set, the next replace throws it (a database outage after the send).</summary>
    public Exception? FailNextReplace { get; set; }

    private static PaymentFileTransmission Copy(PaymentFileTransmission r)
        => JsonSerializer.Deserialize<PaymentFileTransmission>(JsonSerializer.Serialize(r))!;

    public IReadOnlyList<PaymentFileTransmission> All
    {
        get { lock (_json) return _json.Values.Select(j => JsonSerializer.Deserialize<PaymentFileTransmission>(j)!).ToList(); }
    }

    public string RawJson { get { lock (_json) return string.Join("\n", _json.Values); } }

    /// <summary>Writes a record as is (tests that start from a given state).</summary>
    public void Put(PaymentFileTransmission record)
    {
        record.Id = PaymentFileTransmission.KeyFor(record.TenantId, record.FileReference);
        if (string.IsNullOrEmpty(record.Version)) record.Version = Guid.NewGuid().ToString("N");
        lock (_json) _json[record.Id] = JsonSerializer.Serialize(record);
    }

    public Task<PaymentFileTransmission?> GetAsync(string tenantId, string fileReference, CancellationToken cancellationToken = default)
    {
        lock (_json)
            return Task.FromResult(_json.TryGetValue(PaymentFileTransmission.KeyFor(tenantId, fileReference), out var j)
                ? JsonSerializer.Deserialize<PaymentFileTransmission>(j) is { } r && r.TenantId == tenantId ? r : null
                : null);
    }

    public Task<bool> TryInsertAsync(PaymentFileTransmission record, CancellationToken cancellationToken = default)
    {
        record.Id = PaymentFileTransmission.KeyFor(record.TenantId, record.FileReference);
        record.Version = Guid.NewGuid().ToString("N");
        lock (_json) return Task.FromResult(_json.TryAdd(record.Id, JsonSerializer.Serialize(Copy(record))));
    }

    public Task<bool> TryReplaceAsync(PaymentFileTransmission record, string expectedVersion, CancellationToken cancellationToken = default)
    {
        lock (_json)
        {
            if (FailNextReplace is { } failure)
            {
                FailNextReplace = null;
                throw failure;
            }
            if (!_json.TryGetValue(record.Id, out var j)
                || JsonSerializer.Deserialize<PaymentFileTransmission>(j)!.Version != expectedVersion)
                return Task.FromResult(false);
            record.Version = Guid.NewGuid().ToString("N");
            _json[record.Id] = JsonSerializer.Serialize(record);
            return Task.FromResult(true);
        }
    }
}
