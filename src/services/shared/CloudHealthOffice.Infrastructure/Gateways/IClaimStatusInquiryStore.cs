using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Durable 276/277 claim-status inquiry snapshots. Separate from 837
/// transmissions, 277CA acknowledgments, adjudication, and 835 payment.
/// Does not store raw 276/277 payloads.
///
/// Listing by transmission is the seam for later follow-up monitoring;
/// this PR does not register a recurring poller.
/// </summary>
public interface IClaimStatusInquiryStore
{
    Task<ClaimStatusInquiryRecord?> GetByIdAsync(
        string inquiryId, CancellationToken ct = default);

    Task<ClaimStatusInquiryRecord?> GetByExternalTransactionIdAsync(
        string gatewayName, string externalTransactionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimStatusInquiryRecord>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default);

    Task<IReadOnlyList<ClaimStatusInquiryRecord>> ListByTenantAndClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default);

    Task SaveAsync(ClaimStatusInquiryRecord record, CancellationToken ct = default);

    /// <summary>
    /// Insert if the idempotency key is new. On a lost race, returns the
    /// winning record so replayed Stedi transaction ids are not duplicated.
    /// </summary>
    Task<(bool Created, ClaimStatusInquiryRecord Record)> TryCreateAsync(
        ClaimStatusInquiryRecord record, CancellationToken ct = default);
}

public sealed class ClaimStatusInquiryRecord
{
    public string InquiryId { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string? TransmissionId { get; set; }

    public string GatewayName { get; set; } = string.Empty;

    public string? PayerId { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public GatewayClaimStatus NormalizedStatus { get; set; } = GatewayClaimStatus.Unknown;

    public string? StatusCategoryCode { get; set; }

    public string? StatusCode { get; set; }

    public DateOnly? StatusDate { get; set; }

    public string? PayerClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? ExternalTransactionId { get; set; }

    public string? CorrelationId { get; set; }

    public int RetryCount { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; } = GatewayErrorCategory.None;

    public string? ErrorMessage { get; set; }

    public int? ServiceLineNumber { get; set; }

    public ClaimStatusResponse? Response { get; set; }

    public string IdempotencyKey =>
        string.IsNullOrWhiteSpace(ExternalTransactionId)
            ? $"{TenantId}|{GatewayName}|{InquiryId}"
            : $"{TenantId}|{GatewayName}|{ExternalTransactionId}";
}
