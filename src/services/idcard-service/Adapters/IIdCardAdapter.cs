using IdCardService.Models;

namespace IdCardService.Adapters;

/// <summary>
/// Abstraction for ID card issuance backends. Each tenant can be configured to
/// use a different platform (CHO internal generator, QNXT augment mirror,
/// external fulfillment vendor). The adapter normalizes issuance into a common
/// order + card record shape.
/// </summary>
public interface IIdCardAdapter
{
    string Platform { get; }

    Task<IdCardIssueResult> IssueAsync(IdCardIssueRequest request, CancellationToken ct = default);
}

public class IdCardIssueRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string OrderId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public IdCardDeliveryChannel Channel { get; set; } = IdCardDeliveryChannel.Digital;
    public string? LanguageCode { get; set; }
    public string? RequestedBy { get; set; }
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

public class IdCardIssueResult
{
    public bool Success { get; set; }

    /// <summary>Populated on success.</summary>
    public IdCardRecord? Record { get; set; }

    /// <summary>Populated on failure.</summary>
    public string? FailureCode { get; set; }
    public string? FailureReason { get; set; }
}
