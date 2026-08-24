namespace CloudHealthOffice.Infrastructure.Responders.Directory;

public interface IPayerClaimDirectory
{
    Task<PayerClaimMatch> FindAsync(PayerClaimLookup lookup, CancellationToken ct = default);

    Task MarkDocumentationReceivedAsync(string tenantId, string claimId, CancellationToken ct = default);
}

public sealed class PayerClaimLookup
{
    public string TenantId { get; init; } = string.Empty;

    public string CanonicalPayerId { get; init; } = string.Empty;

    public string? ClaimId { get; init; }

    public string? ClaimControlNumber { get; init; }

    public string? PatientControlNumber { get; init; }

    public string? AttachmentControlNumber { get; init; }
}
