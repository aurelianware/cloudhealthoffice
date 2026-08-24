using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

public sealed class InMemoryClaimAcknowledgmentStore : IClaimAcknowledgmentStore
{
    private readonly ConcurrentDictionary<string, ClaimAcknowledgmentRecord> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _eventToId = new(StringComparer.OrdinalIgnoreCase);

    public Task<ClaimAcknowledgmentRecord?> GetByIdempotencyKeyAsync(
        string gateway, string acknowledgmentId, CancellationToken ct = default)
    {
        if (_idempotencyToId.TryGetValue($"{gateway}|{acknowledgmentId}", out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<ClaimAcknowledgmentRecord?>(null);
    }

    public Task<ClaimAcknowledgmentRecord?> GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return Task.FromResult<ClaimAcknowledgmentRecord?>(null);
        }

        if (_eventToId.TryGetValue($"{gateway}|{eventId}", out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<ClaimAcknowledgmentRecord?>(null);
    }

    public Task<ClaimAcknowledgmentRecord?> GetByIdAsync(string recordId, CancellationToken ct = default)
    {
        _byId.TryGetValue(recordId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<IReadOnlyList<ClaimAcknowledgmentRecord>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default)
    {
        var list = _byId.Values
            .Where(r => string.Equals(r.TransmissionId, transmissionId, StringComparison.Ordinal))
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult<IReadOnlyList<ClaimAcknowledgmentRecord>>(list);
    }

    public Task SaveAsync(ClaimAcknowledgmentRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.RecordId] = clone;
        _idempotencyToId[clone.IdempotencyKey] = clone.RecordId;
        IndexEvent(clone);
        return Task.CompletedTask;
    }

    public Task<(bool Created, ClaimAcknowledgmentRecord Record)> TryCreateAsync(
        ClaimAcknowledgmentRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        if (!_idempotencyToId.TryAdd(clone.IdempotencyKey, clone.RecordId))
        {
            var existingId = _idempotencyToId[clone.IdempotencyKey];
            _byId.TryGetValue(existingId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        _byId[clone.RecordId] = clone;
        IndexEvent(clone);
        return Task.FromResult((true, Clone(clone)!));
    }

    private void IndexEvent(ClaimAcknowledgmentRecord clone)
    {
        if (!string.IsNullOrWhiteSpace(clone.EventId))
        {
            _eventToId.TryAdd($"{clone.Gateway}|{clone.EventId}", clone.RecordId);
        }
    }

    private static ClaimAcknowledgmentRecord? Clone(ClaimAcknowledgmentRecord? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ClaimAcknowledgmentRecord
        {
            RecordId = source.RecordId,
            AcknowledgmentId = source.AcknowledgmentId,
            Gateway = source.Gateway,
            EventId = source.EventId,
            TransmissionId = source.TransmissionId,
            TenantId = source.TenantId,
            ClaimId = source.ClaimId,
            ClaimType = source.ClaimType,
            ReceivedAtUtc = source.ReceivedAtUtc,
            Status = source.Status,
            ExternalTransactionId = source.ExternalTransactionId,
            OriginalSubmissionId = source.OriginalSubmissionId,
            ClaimControlNumber = source.ClaimControlNumber,
            PatientControlNumber = source.PatientControlNumber,
            CorrelationId = source.CorrelationId,
            RawSourceReference = source.RawSourceReference,
            UnmatchedReason = source.UnmatchedReason,
            Errors = source.Errors.ToList(),
            Warnings = source.Warnings.ToList(),
            ServiceLineResults = source.ServiceLineResults.ToList(),
            ClaimLevelResults = source.ClaimLevelResults.ToList(),
            EventsPublished = source.EventsPublished
        };
    }
}

public sealed class InMemoryClaimAcknowledgmentCursorStore : IClaimAcknowledgmentCursorStore
{
    private readonly ConcurrentDictionary<string, ClaimAcknowledgmentCursor> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<ClaimAcknowledgmentCursor?> GetAsync(string gatewayName, CancellationToken ct = default)
    {
        _items.TryGetValue(gatewayName, out var cursor);
        return Task.FromResult(cursor is null ? null : Copy(cursor));
    }

    public Task SaveAsync(ClaimAcknowledgmentCursor cursor, CancellationToken ct = default)
    {
        _items[cursor.GatewayName] = Copy(cursor);
        return Task.CompletedTask;
    }

    private static ClaimAcknowledgmentCursor Copy(ClaimAcknowledgmentCursor source) =>
        new()
        {
            GatewayName = source.GatewayName,
            PageToken = source.PageToken,
            LastSuccessAtUtc = source.LastSuccessAtUtc,
            WindowStartUtc = source.WindowStartUtc
        };
}
