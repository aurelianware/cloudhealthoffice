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

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPayerClaimControlNumberAsync(
        string gatewayName, string payerClaimControlNumber, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(
        string gatewayName, string correlationId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimTransmissionRecord>> FindByTenantAndClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default);

    Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default);

    /// <summary>
    /// Insert if <c>tenant + idempotency key</c> is new. On a lost race, returns
    /// the winning record instead of throwing.
    /// </summary>
    Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(
        ClaimTransmissionRecord record, CancellationToken ct = default);
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

    /// <summary>
    /// Payer-assigned claim control number copied from a matched 277CA when
    /// present. Used by later 276 inquiries. Does not replace 277CA records.
    /// </summary>
    public string? PayerClaimControlNumber { get; set; }

    public DateOnly? ServiceDateFrom { get; set; }

    public DateOnly? ServiceDateTo { get; set; }

    public decimal? ClaimAmount { get; set; }

    public string? TypeOfBill { get; set; }

    /// <summary>
    /// Provider/subscriber/line snapshot captured at 837 submit so a later
    /// 276 can be built without reconstructing the claim. Not a raw 837.
    /// </summary>
    public ClaimStatusInquirySource? InquirySource { get; set; }

    /// <summary>
    /// Line numbers from the original submitted claim. Used to validate
    /// service-line 275 association. Empty when the claim had no lines.
    /// </summary>
    public List<int> ServiceLineNumbers { get; set; } = new();

    public DateTimeOffset SubmittedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>When a 277CA was last applied to this transmission. Does not replace <see cref="SubmittedAtUtc"/>.</summary>
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }

    public int RetryCount { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; } = GatewayErrorCategory.None;

    public string? ErrorMessage { get; set; }
}
