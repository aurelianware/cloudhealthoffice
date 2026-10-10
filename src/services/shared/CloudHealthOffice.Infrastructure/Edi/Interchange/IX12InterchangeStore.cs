using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>
/// Persistence for interchange control: received control numbers (the
/// duplicate check), stored TA1s, and the outbound interchanges a partner's
/// TA1 is matched to. Every read and write is tenant-scoped.
/// </summary>
public interface IX12InterchangeStore
{
    /// <summary>
    /// Records that <paramref name="controlNumber"/> was received from
    /// <paramref name="senderKey"/>. Returns false, and records nothing, when
    /// the same sender already used that control number within
    /// <paramref name="window"/> (a duplicate interchange). Atomic: two
    /// concurrent receipts of the same interchange cannot both succeed.
    /// </summary>
    Task<bool> TryRegisterReceiptAsync(string tenantId, string senderKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default);

    Task SaveAcknowledgmentAsync(InterchangeAcknowledgmentRecord record, CancellationToken ct = default);

    Task<InterchangeAcknowledgmentRecord?> GetAcknowledgmentAsync(string tenantId, string id, CancellationToken ct = default);

    Task<IReadOnlyList<InterchangeAcknowledgmentRecord>> ListAcknowledgmentsAsync(string tenantId, InterchangeAcknowledgmentQuery query, CancellationToken ct = default);

    Task SaveOutboundAsync(OutboundInterchangeRecord record, CancellationToken ct = default);

    /// <summary>Outbound interchanges with ISA13 = <paramref name="controlNumber"/>, newest first.</summary>
    Task<IReadOnlyList<OutboundInterchangeRecord>> FindOutboundAsync(string tenantId, string controlNumber, CancellationToken ct = default);

    Task<IReadOnlyList<OutboundInterchangeRecord>> ListOutboundAsync(string tenantId, string? ackStatus, int limit, CancellationToken ct = default);
}

/// <summary>
/// Process-local store: used when the service has no MongoDB (Cosmos-only
/// deployments, tests). The duplicate window only spans this process.
/// </summary>
public sealed class InMemoryX12InterchangeStore : IX12InterchangeStore
{
    private readonly ConcurrentDictionary<string, DateTime> _receipts = new();
    private readonly ConcurrentDictionary<string, InterchangeAcknowledgmentRecord> _acks = new();
    private readonly ConcurrentDictionary<string, OutboundInterchangeRecord> _outbound = new();
    private readonly object _gate = new();

    private static string Key(string tenantId, string senderKey, string controlNumber) => $"{tenantId}|{senderKey}|{controlNumber}";

    public Task<bool> TryRegisterReceiptAsync(string tenantId, string senderKey, string controlNumber, DateTime receivedAt, TimeSpan window, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = Key(tenantId, senderKey, controlNumber);
            if (_receipts.TryGetValue(key, out var previous) && previous > receivedAt - window)
                return Task.FromResult(false);
            _receipts[key] = receivedAt;
            return Task.FromResult(true);
        }
    }

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
