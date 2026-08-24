using System.Collections.Concurrent;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

public sealed class InMemoryRemittanceStore : IRemittanceStore
{
    private readonly ConcurrentDictionary<string, RemittanceReceipt> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _eventToId = new(StringComparer.OrdinalIgnoreCase);

    public Task<RemittanceReceipt?> GetByIdempotencyKeyAsync(
        string gateway, string remittanceId, CancellationToken ct = default)
    {
        if (_idempotencyToId.TryGetValue($"{gateway}|{remittanceId}", out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<RemittanceReceipt?>(null);
    }

    public Task<RemittanceReceipt?> GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return Task.FromResult<RemittanceReceipt?>(null);
        }

        if (_eventToId.TryGetValue($"{gateway}|{eventId}", out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<RemittanceReceipt?>(null);
    }

    public Task<RemittanceReceipt?> GetByIdAsync(string receiptId, CancellationToken ct = default)
    {
        _byId.TryGetValue(receiptId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<IReadOnlyList<RemittanceReceipt>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default)
    {
        var list = _byId.Values
            .Where(r => r.Claims.Any(c =>
                string.Equals(c.TransmissionId, transmissionId, StringComparison.Ordinal)))
            .OrderBy(r => r.ReceivedAtUtc)
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult<IReadOnlyList<RemittanceReceipt>>(list);
    }

    public Task<IReadOnlyList<RemittanceReceipt>> ListByTenantAsync(
        string tenantId, int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        var list = _byId.Values
            .Where(r => string.Equals(r.TenantId, tenantId, StringComparison.Ordinal))
            .OrderByDescending(r => r.ReceivedAtUtc)
            .Take(limit)
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult<IReadOnlyList<RemittanceReceipt>>(list);
    }

    public Task SaveAsync(RemittanceReceipt record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.ReceiptId] = clone;
        _idempotencyToId[clone.IdempotencyKey] = clone.ReceiptId;
        IndexEvent(clone);
        return Task.CompletedTask;
    }

    public Task<(bool Created, RemittanceReceipt Record)> TryCreateAsync(
        RemittanceReceipt record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        if (!_byId.TryAdd(clone.ReceiptId, clone))
        {
            _byId.TryGetValue(clone.ReceiptId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        if (!_idempotencyToId.TryAdd(clone.IdempotencyKey, clone.ReceiptId))
        {
            _byId.TryRemove(clone.ReceiptId, out _);
            var winnerId = _idempotencyToId[clone.IdempotencyKey];
            _byId.TryGetValue(winnerId, out var winner);
            return Task.FromResult((false, Clone(winner) ?? clone));
        }

        IndexEvent(clone);
        return Task.FromResult((true, Clone(clone)!));
    }

    public Task<IReadOnlyList<RemittanceReceipt>> ListPendingOutboxAsync(
        int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        var list = _byId.Values
            .Where(r => r.HasPendingOutbox)
            .OrderBy(r => r.ReceivedAtUtc)
            .Take(limit)
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult<IReadOnlyList<RemittanceReceipt>>(list);
    }

    private void IndexEvent(RemittanceReceipt record)
    {
        if (!string.IsNullOrWhiteSpace(record.EventId))
        {
            _eventToId[$"{record.Gateway}|{record.EventId}"] = record.ReceiptId;
        }
    }

    private static RemittanceReceipt? Clone(RemittanceReceipt? source)
    {
        if (source is null)
        {
            return null;
        }

        return new RemittanceReceipt
        {
            ReceiptId = source.ReceiptId,
            RemittanceId = source.RemittanceId,
            Gateway = source.Gateway,
            EventId = source.EventId,
            TenantId = source.TenantId,
            PayerId = source.PayerId,
            ExternalTransactionId = source.ExternalTransactionId,
            PaymentIdentifier = source.PaymentIdentifier,
            PaymentMethodCode = source.PaymentMethodCode,
            PaymentDate = source.PaymentDate,
            PaymentAmount = source.PaymentAmount,
            ReceivedAtUtc = source.ReceivedAtUtc,
            Status = source.Status,
            CorrelationId = source.CorrelationId,
            RawSourceReference = source.RawSourceReference,
            UnmatchedReason = source.UnmatchedReason,
            Claims = source.Claims.ToList(),
            Outbox = source.Outbox.Select(o => new RemittanceOutboxEntry
            {
                EventType = o.EventType,
                CreatedAtUtc = o.CreatedAtUtc,
                PublishedAtUtc = o.PublishedAtUtc,
                AttemptCount = o.AttemptCount,
                LastError = o.LastError
            }).ToList(),
            ProcessingAttempts = source.ProcessingAttempts,
            LastErrorCategory = source.LastErrorCategory,
            LastError = source.LastError
        };
    }
}
