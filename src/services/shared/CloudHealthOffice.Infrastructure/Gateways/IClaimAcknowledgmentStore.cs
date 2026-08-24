using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Durable 277CA acknowledgment records. Does not store raw X12/JSON payloads.
/// </summary>
public interface IClaimAcknowledgmentStore
{
    Task<ClaimAcknowledgmentRecord?> GetByIdempotencyKeyAsync(
        string gateway, string acknowledgmentId, CancellationToken ct = default);

    Task<ClaimAcknowledgmentRecord?> GetByEventIdAsync(
        string gateway, string eventId, CancellationToken ct = default);

    Task<ClaimAcknowledgmentRecord?> GetByIdAsync(
        string recordId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimAcknowledgmentRecord>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default);

    Task SaveAsync(ClaimAcknowledgmentRecord record, CancellationToken ct = default);
}

public sealed class ClaimAcknowledgmentRecord
{
    public string RecordId { get; set; } = Guid.NewGuid().ToString("N");

    public string AcknowledgmentId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string? EventId { get; set; }

    public string? TransmissionId { get; set; }

    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public GatewayClaimType? ClaimType { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public ClaimAcknowledgmentStatus Status { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? OriginalSubmissionId { get; set; }

    public string? ClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? CorrelationId { get; set; }

    public string? RawSourceReference { get; set; }

    public string? UnmatchedReason { get; set; }

    public List<GatewayClaimAcknowledgmentIssue> Errors { get; set; } = new();

    public List<GatewayClaimAcknowledgmentIssue> Warnings { get; set; } = new();

    public List<GatewayClaimAcknowledgmentLineResult> ServiceLineResults { get; set; } = new();

    public List<GatewayClaimAcknowledgmentClaimResult> ClaimLevelResults { get; set; } = new();

    public bool EventsPublished { get; set; }

    public string IdempotencyKey => $"{Gateway}|{AcknowledgmentId}";
}

public interface IClaimAcknowledgmentCursorStore
{
    Task<ClaimAcknowledgmentCursor?> GetAsync(string gatewayName, CancellationToken ct = default);

    Task SaveAsync(ClaimAcknowledgmentCursor cursor, CancellationToken ct = default);
}

public sealed class ClaimAcknowledgmentCursor
{
    public string GatewayName { get; set; } = string.Empty;

    public string? PageToken { get; set; }

    public DateTimeOffset? LastSuccessAtUtc { get; set; }

    public DateTimeOffset? WindowStartUtc { get; set; }
}
