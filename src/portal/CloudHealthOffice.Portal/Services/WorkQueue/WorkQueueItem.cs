namespace CloudHealthOffice.Portal.Services;

public class WorkQueueItem
{
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>Human-readable claim number. <see cref="ClaimId"/> stays the route key.</summary>
    public string ClaimNumber { get; set; } = string.Empty;

    public string DisplayClaimNumber => string.IsNullOrWhiteSpace(ClaimNumber) ? ClaimId : ClaimNumber;

    /// <summary>Fingerprint of the pends shown; sent back with an approval.</summary>
    public string? PendFingerprint { get; set; }
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public string QueueReason { get; set; } = string.Empty;
    public string QueueReasonCode { get; set; } = string.Empty;

    /// <summary>
    /// Every pend on the claim ("{code}: {reason}"), the routing one first —
    /// a COB pend can be an additional one (see <see cref="CobPend"/>).
    /// </summary>
    public List<string> PendReasons { get; set; } = new();
    public int DaysInQueue { get; set; }
    public string Priority { get; set; } = "Low";
    public string AssignedTo { get; set; } = string.Empty;
    public decimal TotalCharged { get; set; }
    public List<string> ProcedureCodes { get; set; } = new();
    public string? AiRecommendedDisposition { get; set; }
    public double? AiConfidenceScore { get; set; }
    public string? AiRationale { get; set; }
    public List<string> AiPolicyCitations { get; set; } = new();
    public string? AiExaminerAgreement { get; set; }
    /// <summary>Claim numbers a DUPLICATE-pended claim matched; empty otherwise.</summary>
    public List<string> DuplicateMatchedClaimNumbers { get; set; } = new();
}
