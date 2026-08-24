using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Responders;

public sealed class InMemoryInboundClaimAttachmentReceiptStore : IInboundClaimAttachmentReceiptStore
{
    private readonly ConcurrentDictionary<string, InboundClaimAttachmentReceipt> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.Ordinal);

    public Task<InboundClaimAttachmentReceipt?> GetByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        if (_idempotencyToId.TryGetValue(idempotencyKey, out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<InboundClaimAttachmentReceipt?>(null);
    }

    public Task<InboundClaimAttachmentReceipt?> GetByIdAsync(string receiptId, CancellationToken ct = default)
    {
        _byId.TryGetValue(receiptId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListByClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default)
    {
        IReadOnlyList<InboundClaimAttachmentReceipt> matches = _byId.Values
            .Where(r =>
                string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(r.ClaimId, claimId, StringComparison.Ordinal))
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListPendingOutboxAsync(
        int take, CancellationToken ct = default)
    {
        var limit = take <= 0 ? 50 : take;
        IReadOnlyList<InboundClaimAttachmentReceipt> matches = _byId.Values
            .Where(r => r.HasPendingOutbox)
            .OrderBy(r => r.ReceivedAtUtc)
            .Take(limit)
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task SaveAsync(InboundClaimAttachmentReceipt record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.ReceiptId] = clone;
        _idempotencyToId[clone.IdempotencyKey] = clone.ReceiptId;
        return Task.CompletedTask;
    }

    public Task<(bool Created, InboundClaimAttachmentReceipt Record)> TryCreateAsync(
        InboundClaimAttachmentReceipt record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        if (!_idempotencyToId.TryAdd(clone.IdempotencyKey, clone.ReceiptId))
        {
            var existingId = _idempotencyToId[clone.IdempotencyKey];
            _byId.TryGetValue(existingId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        _byId[clone.ReceiptId] = clone;
        return Task.FromResult((true, Clone(clone)!));
    }

    internal static InboundClaimAttachmentReceipt? Clone(InboundClaimAttachmentReceipt? source)
    {
        if (source is null)
        {
            return null;
        }

        return new InboundClaimAttachmentReceipt
        {
            ReceiptId = source.ReceiptId,
            IdempotencyKey = source.IdempotencyKey,
            TenantId = source.TenantId,
            CanonicalPayerId = source.CanonicalPayerId,
            ClaimId = source.ClaimId,
            ServiceLineNumber = source.ServiceLineNumber,
            ExternalTransactionId = source.ExternalTransactionId,
            AttachmentControlNumber = source.AttachmentControlNumber,
            AttachmentType = source.AttachmentType,
            Mode = source.Mode,
            ContentType = source.ContentType,
            ContentLength = source.ContentLength,
            ChecksumSha256 = source.ChecksumSha256,
            ContentContainer = source.ContentContainer,
            ContentStorageKey = source.ContentStorageKey,
            SourceAdapter = source.SourceAdapter,
            Status = source.Status,
            AssociationMethod = source.AssociationMethod,
            MatchingIdentifier = source.MatchingIdentifier,
            ReceivedAtUtc = source.ReceivedAtUtc,
            MatchedAtUtc = source.MatchedAtUtc,
            ErrorCategory = source.ErrorCategory,
            ErrorMessage = source.ErrorMessage,
            Outbox = source.Outbox.Select(e => new InboundAttachmentOutboxEntry
            {
                EventType = e.EventType,
                CreatedAtUtc = e.CreatedAtUtc,
                PublishedAtUtc = e.PublishedAtUtc,
                AttemptCount = e.AttemptCount,
                LastError = e.LastError
            }).ToList()
        };
    }
}
