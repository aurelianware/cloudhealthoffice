using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// Persistence for interchange control: received control numbers (the
/// duplicate check), the TA1 control-number sequences, stored TA1s, and the
/// outbound interchanges a partner's TA1 is matched to. Every read and write
/// is tenant-scoped.
/// </summary>
public interface IX12InterchangeStore
{
    /// <summary>
    /// Records that <paramref name="controlNumber"/> was received on the
    /// interchange path <paramref name="partyKey"/> (sender and receiver,
    /// <see cref="X12InterchangeHeader.PartyKey"/>). Returns false, and records
    /// nothing, when the same path already used that control number within
    /// <paramref name="window"/> (a duplicate interchange). Atomic: two
    /// concurrent receipts of the same interchange cannot both succeed.
    /// </summary>
    Task<bool> TryRegisterReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Removes a receipt this request registered (same <paramref name="receivedAt"/>),
    /// so a resend after a failure that was not the envelope's is not a duplicate.
    /// A receipt registered by someone else since is left alone.
    /// </summary>
    Task ReleaseReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, CancellationToken ct = default);

    /// <summary>
    /// The next value (1–999999999, then wrapping to 1) of the control-number
    /// sequence <paramref name="sequenceKey"/>. Atomic and monotonic per key.
    /// </summary>
    Task<long> NextControlNumberAsync(string tenantId, string sequenceKey, CancellationToken ct = default);

    Task SaveAcknowledgmentAsync(InterchangeAcknowledgmentRecord record, CancellationToken ct = default);

    Task<InterchangeAcknowledgmentRecord?> GetAcknowledgmentAsync(string tenantId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<InterchangeAcknowledgmentRecord>> ListAcknowledgmentsAsync(string tenantId, InterchangeAcknowledgmentQuery query, CancellationToken ct = default);

    Task SaveOutboundAsync(OutboundInterchangeRecord record, CancellationToken ct = default);

    /// <summary>Outbound interchanges with ISA13 = <paramref name="controlNumber"/>, newest first.</summary>
    Task<IReadOnlyList<OutboundInterchangeRecord>> FindOutboundAsync(string tenantId, string controlNumber, CancellationToken ct = default);

    /// <summary>The outbound interchange already recorded for this transaction type and owning record, if any.</summary>
    Task<OutboundInterchangeRecord?> FindOutboundBySourceAsync(string tenantId, string transactionType, string sourceReference, CancellationToken ct = default);

    Task<IReadOnlyList<OutboundInterchangeRecord>> ListOutboundAsync(string tenantId, string? ackStatus, int limit, CancellationToken ct = default);
}

/// <summary>Wraps a sequence value into the ISA13 range 1–999999999.</summary>
public static class X12ControlNumberRange
{
    public const long Max = 999_999_999;

    public static long Wrap(long sequenceValue) => ((sequenceValue - 1) % Max + Max) % Max + 1;
}

/// <summary>
/// Process-local store: used when the service has no MongoDB (Development and
/// test hosts). The duplicate window and the sequences only span this process.
/// </summary>
public sealed class InMemoryX12InterchangeStore : IX12InterchangeStore
{
    private readonly ConcurrentDictionary<string, DateTime> _receipts = new();
    private readonly ConcurrentDictionary<string, long> _sequences = new();
    private readonly ConcurrentDictionary<string, InterchangeAcknowledgmentRecord> _acks = new();
    private readonly ConcurrentDictionary<string, OutboundInterchangeRecord> _outbound = new();
    private readonly object _gate = new();

    private static string Key(string tenantId, string partyKey, string controlNumber) => $"{tenantId}|{partyKey}|{controlNumber}";

    /// <summary>Starts a sequence at <paramref name="value"/> (tests of the wrap).</summary>
    public void SeedSequence(string tenantId, string sequenceKey, long value) => _sequences[$"{tenantId}|{sequenceKey}"] = value;

    public Task<bool> TryRegisterReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(tenantId, partyKey, controlNumber);
            if (_receipts.TryGetValue(key, out var previous) && previous > receivedAt - window)
                return Task.FromResult(false);
            _receipts[key] = receivedAt;
            return Task.FromResult(true);
        }
    }

    public Task ReleaseReceiptAsync(string tenantId, string partyKey, string controlNumber, DateTime receivedAt, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(tenantId, partyKey, controlNumber);
            if (_receipts.TryGetValue(key, out var stored) && stored == receivedAt)
                _receipts.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public Task<long> NextControlNumberAsync(string tenantId, string sequenceKey, CancellationToken ct = default) =>
        Task.FromResult(X12ControlNumberRange.Wrap(_sequences.AddOrUpdate($"{tenantId}|{sequenceKey}", 1, (_, v) => v + 1)));

    public Task SaveAcknowledgmentAsync(InterchangeAcknowledgmentRecord record, CancellationToken ct = default)
    {
        _acks[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<InterchangeAcknowledgmentRecord?> GetAcknowledgmentAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_acks.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null);

    public Task<IReadOnlyList<InterchangeAcknowledgmentRecord>> ListAcknowledgmentsAsync(string tenantId, InterchangeAcknowledgmentQuery query, CancellationToken ct = default)
    {
        IReadOnlyList<InterchangeAcknowledgmentRecord> list = _acks.Values
            .Where(r => r.TenantId == tenantId)
            .Where(r => query.Direction is null || r.Direction == query.Direction)
            .Where(r => query.AckCode is null || r.AckCode == query.AckCode)
            .Where(r => query.AcknowledgedControlNumber is null || r.AcknowledgedControlNumber == query.AcknowledgedControlNumber)
            .Where(r => query.SenderId is null || r.SenderId == query.SenderId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToList();
        return Task.FromResult(list);
    }

    public Task SaveOutboundAsync(OutboundInterchangeRecord record, CancellationToken ct = default)
    {
        _outbound[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboundInterchangeRecord>> FindOutboundAsync(string tenantId, string controlNumber, CancellationToken ct = default)
    {
        IReadOnlyList<OutboundInterchangeRecord> list = _outbound.Values
            .Where(r => r.TenantId == tenantId && r.ControlNumber == controlNumber)
            .OrderByDescending(r => r.SentAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<OutboundInterchangeRecord?> FindOutboundBySourceAsync(string tenantId, string transactionType, string sourceReference, CancellationToken ct = default) =>
        Task.FromResult(_outbound.Values
            .Where(r => r.TenantId == tenantId && r.TransactionType == transactionType && r.SourceReference == sourceReference)
            .OrderByDescending(r => r.SentAt)
            .FirstOrDefault());

    public Task<IReadOnlyList<OutboundInterchangeRecord>> ListOutboundAsync(string tenantId, string? ackStatus, int limit, CancellationToken ct = default)
    {
        IReadOnlyList<OutboundInterchangeRecord> list = _outbound.Values
            .Where(r => r.TenantId == tenantId && (ackStatus is null || r.AckStatus == ackStatus))
            .OrderByDescending(r => r.SentAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();
        return Task.FromResult(list);
    }
}
