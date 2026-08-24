using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Durable outbound claim-transmission record. Separate from claim
/// adjudication / payment state. Used for idempotency and operational audit.
/// Does not store raw 837 payloads.
/// </summary>
public interface IClaimTransmissionStore
{
    Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default);

    Task<ClaimTransmissionRecord?> GetByIdAsync(
        string transmissionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(
        string gatewayName, string submissionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(
        string gatewayName, string externalTransactionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(
        string gatewayName, string patientControlNumber, CancellationToken ct = default);

    Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default);
}

public sealed class ClaimTransmissionRecord
{
    public string TransmissionId { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public int ClaimVersion { get; set; } = 1;

    public string GatewayName { get; set; } = string.Empty;

    public GatewayClaimType ClaimType { get; set; }

    public HealthcareTransactionType TransactionType { get; set; } =
        HealthcareTransactionType.ProfessionalClaim837P;

    public GatewayClaimTransmissionStatus Status { get; set; } =
        GatewayClaimTransmissionStatus.ReadyForSubmission;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? SubmissionId { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Canonical payer id from the original submission. Not taken from inbound 277CA text.</summary>
    public string? PayerId { get; set; }

    /// <summary>Patient control number sent on the 837 (typically the CHO claim id).</summary>
    public string? PatientControlNumber { get; set; }

    public DateTimeOffset SubmittedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>When a 277CA was last applied to this transmission. Does not replace <see cref="SubmittedAtUtc"/>.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }

    public int RetryCount { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; } = GatewayErrorCategory.None;

    public string? ErrorMessage { get; set; }
}
