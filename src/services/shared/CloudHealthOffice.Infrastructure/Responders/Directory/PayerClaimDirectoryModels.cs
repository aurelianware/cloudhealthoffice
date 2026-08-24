using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Payer-side claim projection used to match inbound 275s. This is not an
/// outbound Stedi transmission and not a second claims-service database —
/// production hosts register a directory backed by the existing claim store.
/// </summary>
public sealed class PayerDirectoryClaim
{
    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;

    public string CanonicalPayerId { get; set; } = string.Empty;

    public string? PayerClaimControlNumber { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? AttachmentControlNumber { get; set; }

    public PayerDirectoryClaimStatus Status { get; set; } = PayerDirectoryClaimStatus.Pended;

    public bool DocumentationReceived { get; set; }

    public List<PayerDirectoryClaimLine> ServiceLines { get; set; } = new();

    public bool IsAdjudicated =>
        Status is PayerDirectoryClaimStatus.Approved or PayerDirectoryClaimStatus.Denied;

    public bool IsPaid => Status == PayerDirectoryClaimStatus.Paid;
}

public sealed class PayerDirectoryClaimLine
{
    public int LineNumber { get; set; }

    public string? LineControlNumber { get; set; }

    public string? ProcedureCode { get; set; }

    public string? ToothNumber { get; set; }
}

public enum PayerDirectoryClaimStatus
{
    Submitted = 1,
    Received = 2,
    InAdjudication = 3,
    Pended = 4,
    Approved = 5,
    Denied = 6,
    Paid = 7
}

public readonly struct PayerClaimMatch
{
    public PayerClaimMatch(IReadOnlyList<PayerDirectoryClaim> claims)
    {
        Claims = claims;
    }

    public IReadOnlyList<PayerDirectoryClaim> Claims { get; }

    public PayerDirectoryClaim? Unique => Claims.Count == 1 ? Claims[0] : null;

    public bool None => Claims.Count == 0;

    public bool Ambiguous => Claims.Count > 1;
}
