using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// How a shadow run's answer for one claim compares with the core's answer.
/// Stored as its name. One kind per claim, chosen in this order of precedence.
/// </summary>
public static class ShadowDiscrepancyKind
{
    /// <summary>The core's claim is not final (pended, voided, in process): nothing to compare.</summary>
    public const string NotComparable = "NotComparable";

    /// <summary>
    /// CHO could not reach a decision (it pended or rejected the claim). Almost
    /// always an input CHO does not have yet: a contract or fee schedule, the
    /// benefit plan, the member. <see cref="ShadowAdjudicationResult.ChoPendCode"/> names it.
    /// </summary>
    public const string ChoUndecided = "ChoUndecided";

    /// <summary>One side paid the claim and the other denied it.</summary>
    public const string OutcomeMismatch = "OutcomeMismatch";

    /// <summary>Both denied the claim, with different denial codes (CARC).</summary>
    public const string DenialCodeMismatch = "DenialCodeMismatch";

    /// <summary>The allowed amount differs on the claim or a line: a pricing difference.</summary>
    public const string AllowedVariance = "AllowedVariance";

    /// <summary>Allowed agrees; the plan's payment differs: a benefit or accumulator difference.</summary>
    public const string PaidVariance = "PaidVariance";

    /// <summary>Allowed and paid agree; the member's share differs.</summary>
    public const string MemberLiabilityVariance = "MemberLiabilityVariance";

    /// <summary>Same outcome and, within the tolerance, the same amounts.</summary>
    public const string Match = "Match";

    public static readonly IReadOnlyList<string> All =
    [
        NotComparable, ChoUndecided, OutcomeMismatch, DenialCodeMismatch,
        AllowedVariance, PaidVariance, MemberLiabilityVariance, Match,
    ];
}

/// <summary>Outcome names shared by both sides of a comparison.</summary>
public static class ShadowOutcome
{
    public const string Paid = "Paid";
    public const string Denied = "Denied";
    public const string Pended = "Pended";
    public const string Rejected = "Rejected";
    public const string NotFinal = "NotFinal";
}

/// <summary>
/// One claim's comparison in one shadow run. Stored in its own collection
/// (<c>ShadowAdjudicationResults</c>), never on the claim, so nothing a member,
/// a provider or a FHIR API sees changes.
/// </summary>
[BsonIgnoreExtraElements]
public class ShadowAdjudicationResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;

    /// <summary>The claim's chain id (<c>ClaimVersionId</c>) and the version compared.</summary>
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimVersionDocumentId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? SourceSystem { get; set; }
    public string? SourceClaimId { get; set; }
    public string? ImportBatchId { get; set; }
    public string ClaimType { get; set; } = string.Empty;
    public DateTime ServiceDateFrom { get; set; }
    public string? BillingProviderNpi { get; set; }

    public string Kind { get; set; } = ShadowDiscrepancyKind.NotComparable;

    public string CoreOutcome { get; set; } = ShadowOutcome.NotFinal;
    public string ChoOutcome { get; set; } = ShadowOutcome.Pended;

    public decimal CoreAllowed { get; set; }
    public decimal ChoAllowed { get; set; }
    public decimal CorePaid { get; set; }
    public decimal ChoPaid { get; set; }
    public decimal CoreMemberLiability { get; set; }
    public decimal ChoMemberLiability { get; set; }

    /// <summary>CHO paid minus core paid. Positive: CHO would have paid more. Zero when CHO did not decide.</summary>
    public decimal PaidVariance { get; set; }

    /// <summary>|<see cref="PaidVariance"/>|, stored so results sort by size of difference.</summary>
    public decimal AbsolutePaidVariance { get; set; }

    /// <summary>CHO allowed minus core allowed. Zero when CHO did not decide.</summary>
    public decimal AllowedVariance { get; set; }

    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }
    public string? CoreNetworkTier { get; set; }
    public string? ChoNetworkTier { get; set; }

    /// <summary>CHO's pend code when it did not decide (NOCONTRACT, PRICING, ...).</summary>
    public string? ChoPendCode { get; set; }
    public string? ChoPendReason { get; set; }

    /// <summary>Every CHO stage that did not pass, as "Stage: reason".</summary>
    public List<string> ChoStageFindings { get; set; } = [];

    public List<ShadowLineComparison> Lines { get; set; } = [];

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One service line, core vs CHO.</summary>
public class ShadowLineComparison
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string? RevenueCode { get; set; }
    public decimal Charge { get; set; }

    /// <summary>False when the core's file carried no line amounts; only claim totals were compared.</summary>
    public bool CoreAmountsPresent { get; set; }

    public decimal? CoreAllowed { get; set; }
    public decimal? ChoAllowed { get; set; }
    public decimal? CorePaid { get; set; }
    public decimal? ChoPaid { get; set; }
    public decimal? CoreMemberLiability { get; set; }
    public decimal? ChoMemberLiability { get; set; }
    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }

    /// <summary>What differs on this line: Allowed, Paid, MemberLiability, LineOutcome, DenialCode.</summary>
    public List<string> Differences { get; set; } = [];
}

/// <summary>Line-level difference names.</summary>
public static class ShadowLineDifference
{
    public const string Allowed = "Allowed";
    public const string Paid = "Paid";
    public const string MemberLiability = "MemberLiability";
    public const string LineOutcome = "LineOutcome";
    public const string DenialCode = "DenialCode";
}

/// <summary>One shadow run: what was asked, and the totals of what came back.</summary>
[BsonIgnoreExtraElements]
public class ShadowAdjudicationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public string? ImportBatchId { get; set; }
    public int ClaimsRequested { get; set; }

    /// <summary>Asked for but not found, or not loaded from a core (no external source).</summary>
    public List<string> Skipped { get; set; } = [];

    /// <summary>Claims whose run failed outright (an exception escaped the pipeline).</summary>
    public List<string> Failed { get; set; } = [];

    public int ClaimsCompared { get; set; }
    public Dictionary<string, int> CountsByKind { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Share of the decided claims (core final, CHO decided) that matched.
    /// Null when there were none.
    /// </summary>
    public decimal? MatchRate { get; set; }

    /// <summary>Share of comparable claims CHO could not decide. Null when there were none.</summary>
    public decimal? UndecidedRate { get; set; }

    /// <summary>Sum of CHO paid minus core paid over the decided claims.</summary>
    public decimal NetPaidVariance { get; set; }

    /// <summary>Sum of the absolute paid differences over the decided claims.</summary>
    public decimal AbsolutePaidVariance { get; set; }

    public decimal Tolerance { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

/// <summary>Body of <c>POST /api/v1/claims/shadow-adjudication/runs</c>.</summary>
public class ShadowAdjudicationRunRequest
{
    /// <summary>Claim chain ids (<c>ClaimVersionId</c>, as the CPCDS import report's <c>choClaimId</c>).</summary>
    public List<string>? ClaimIds { get; set; }

    /// <summary>Every claim a CPCDS import batch created, updated or left unchanged.</summary>
    public string? ImportBatchId { get; set; }

    /// <summary>At most this many claims; capped by <see cref="ShadowAdjudicationOptions.MaxClaimsPerRun"/>.</summary>
    public int? Limit { get; set; }
}

/// <summary>The run and its per-claim results.</summary>
public class ShadowAdjudicationRunResponse
{
    public required ShadowAdjudicationRun Run { get; init; }
    public required IReadOnlyList<ShadowAdjudicationResult> Results { get; init; }
}

/// <summary>Configuration section <c>Adjudication:Shadow</c>. Off unless enabled.</summary>
public class ShadowAdjudicationOptions
{
    public const string SectionName = "Adjudication:Shadow";

    /// <summary>Shadow runs are refused unless this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>When set, only these tenants may run. Empty: every tenant, once enabled.</summary>
    public List<string> Tenants { get; set; } = [];

    /// <summary>Most claims one synchronous run adjudicates.</summary>
    public int MaxClaimsPerRun { get; set; } = 200;

    /// <summary>Two amounts within this many dollars are the same.</summary>
    public decimal Tolerance { get; set; } = 0.01m;

    public bool IsEnabledFor(string tenantId) =>
        Enabled && (Tenants.Count == 0 || Tenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase));
}
