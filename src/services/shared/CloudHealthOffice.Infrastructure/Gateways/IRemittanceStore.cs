using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Durable inbound 835 remittance receipts. Does not store raw X12/JSON
/// payloads and does not post payments.
/// </summary>
public interface IRemittanceStore
{
    Task<RemittanceReceipt?> GetByIdempotencyKeyAsync(
        string gateway, string remittanceId, CancellationToken ct = default);

    Task<RemittanceReceipt?> GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct = default);

    Task<RemittanceReceipt?> GetByIdAsync(string receiptId, CancellationToken ct = default);

    Task<IReadOnlyList<RemittanceReceipt>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default);

    Task<IReadOnlyList<RemittanceReceipt>> ListByTenantAsync(
        string tenantId, int take, CancellationToken ct = default);

    Task SaveAsync(RemittanceReceipt record, CancellationToken ct = default);

    Task<(bool Created, RemittanceReceipt Record)> TryCreateAsync(
        RemittanceReceipt record, CancellationToken ct = default);

    Task<IReadOnlyList<RemittanceReceipt>> ListPendingOutboxAsync(
        int take, CancellationToken ct = default);
}

public sealed class RemittanceReceipt
{
    public string ReceiptId { get; set; } = Guid.NewGuid().ToString("N");

    public string RemittanceId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string? PayerId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? PaymentIdentifier { get; set; }

    public string? PaymentMethodCode { get; set; }

    public DateOnly? PaymentDate { get; set; }

    public decimal PaymentAmount { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public RemittanceLifecycleStatus Status { get; set; } = RemittanceLifecycleStatus.Received;

    public string? CorrelationId { get; set; }

    public string? RawSourceReference { get; set; }

    public string? UnmatchedReason { get; set; }

    public List<RemittedClaim> Claims { get; set; } = new();

    public List<RemittanceOutboxEntry> Outbox { get; set; } = new();

    public int ProcessingAttempts { get; set; }

    public GatewayErrorCategory LastErrorCategory { get; set; } = GatewayErrorCategory.None;

    public string? LastError { get; set; }

    public bool EventsPublished =>
        Outbox.Count > 0 && Outbox.All(e => e.PublishedAtUtc is not null);

    public bool HasPendingOutbox =>
        Outbox.Any(e => e.PublishedAtUtc is null);

    public string IdempotencyKey => $"{Gateway}|{RemittanceId}";
}

public sealed class RemittanceOutboxEntry
{
    public string EventType { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}
