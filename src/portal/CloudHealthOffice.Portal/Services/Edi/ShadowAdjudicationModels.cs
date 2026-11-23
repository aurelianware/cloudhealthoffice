namespace CloudHealthOffice.Portal.Services;

/// <summary>Comparison kinds claims-service records, in its order of precedence.</summary>
public static class ShadowKinds
{
    public const string NotComparable = "NotComparable";
    public const string ChoUndecided = "ChoUndecided";
    public const string OutcomeMismatch = "OutcomeMismatch";
    public const string DenialCodeMismatch = "DenialCodeMismatch";
    public const string AllowedVariance = "AllowedVariance";
    public const string PaidVariance = "PaidVariance";
    public const string MemberLiabilityVariance = "MemberLiabilityVariance";
    public const string Match = "Match";

    public static readonly IReadOnlyList<string> All =
    [
        NotComparable, ChoUndecided, OutcomeMismatch, DenialCodeMismatch,
        AllowedVariance, PaidVariance, MemberLiabilityVariance, Match,
    ];

    /// <summary>A plan reader's name for a kind.</summary>
    public static string Label(string kind) => kind switch
    {
        NotComparable => "Core not final",
        ChoUndecided => "CHO could not decide",
        OutcomeMismatch => "Paid vs denied",
        DenialCodeMismatch => "Different denial code",
        AllowedVariance => "Allowed differs (pricing)",
        PaidVariance => "Paid differs (benefits)",
        MemberLiabilityVariance => "Member share differs",
        Match => "Match",
        _ => kind,
    };

    /// <summary>claims-service's pend code for a claim held only for provider screening.</summary>
    public const string ProviderScreeningCode = "PROVIDERSCREEN";

    /// <summary>
    /// The "Why" of one claim: CHO's pend reason or first stage finding. A claim
    /// held only for provider screening was compared as CHO decides it once the
    /// provider clears (the page explains the hold above the table), so its Why
    /// names what changed the amounts and notes the hold.
    /// </summary>
    public static string? Why(ShadowResult r)
    {
        if (!string.Equals(r.ChoPendCode, ProviderScreeningCode, StringComparison.OrdinalIgnoreCase))
            return r.ChoPendReason ?? r.ChoStageFindings.FirstOrDefault();

        var reason = r.ChoStageFindings.FirstOrDefault(f => !f.StartsWith("ProviderIntegrity", StringComparison.Ordinal))
            ?? r.Kind switch
            {
                AllowedVariance => "The allowed amounts differ: CHO priced the lines from the plan's fee schedule.",
                PaidVariance => "The payments differ under the plan's benefits.",
                MemberLiabilityVariance => "The member's share differs under the plan's benefits.",
                Match => null,
                _ => r.ChoPendReason,
            };
        return reason is null ? "Held for provider screening." : $"{reason} Held for provider screening.";
    }
}

/// <summary>One shadow run's totals.</summary>
public class ShadowRun
{
    public string Id { get; set; } = string.Empty;
    public string? RequestedBy { get; set; }
    public string? ImportBatchId { get; set; }

    /// <summary>Queued, Running, Completed or Failed.</summary>
    public string Status { get; set; } = "Completed";
    public bool Background { get; set; }
    public int ClaimsRequested { get; set; }
    public int ClaimsProcessed { get; set; }
    public string? Error { get; set; }

    public bool InProgress => Status is "Queued" or "Running";
    public List<string> Skipped { get; set; } = new();
    public List<string> Failed { get; set; } = new();
    public int ClaimsCompared { get; set; }
    public Dictionary<string, int> CountsByKind { get; set; } = new();
    public decimal? MatchRate { get; set; }
    public decimal? UndecidedRate { get; set; }
    public decimal NetPaidVariance { get; set; }
    public decimal AbsolutePaidVariance { get; set; }
    public decimal Tolerance { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public class ShadowMoneyBucket
{
    public int Claims { get; set; }
    public decimal Amount { get; set; }
}

public class ShadowReasonRow
{
    public string Key { get; set; } = string.Empty;
    public int Claims { get; set; }
    public int Lines { get; set; }
    public decimal NetPaidVariance { get; set; }
    public decimal AbsolutePaidVariance { get; set; }
    public decimal CorePaid { get; set; }
    public List<string> ExampleClaims { get; set; } = new();
}

/// <summary>The run report (<c>GET .../runs/{id}/report</c>).</summary>
public class ShadowRunReport
{
    public ShadowRun Run { get; set; } = new();
    public int ComparableClaims { get; set; }
    public int DecidedClaims { get; set; }
    public ShadowMoneyBucket CorePaidMore { get; set; } = new();
    public ShadowMoneyBucket CorePaidLess { get; set; } = new();
    public List<ShadowReasonRow> ByKind { get; set; } = new();
    public List<ShadowReasonRow> UndecidedByGap { get; set; } = new();

    /// <summary>Decided claims CHO would still hold until it can screen their providers (PROVIDERSCREEN).</summary>
    public ShadowReasonRow HeldForProviderScreening { get; set; } = new();
    public List<ShadowReasonRow> ByChoStage { get; set; } = new();
    public List<ShadowReasonRow> ByDenialCodes { get; set; } = new();

    /// <summary>Claims one side denied for prior authorization and the other did not.</summary>
    public List<ShadowReasonRow> AuthorizationMismatches { get; set; } = new();

    /// <summary>The medical policy (and version) behind those disagreements.</summary>
    public List<ShadowReasonRow> AuthorizationMismatchesByPolicy { get; set; } = new();

    /// <summary>Claims with no authorization number that CHO matched to an approved authorization.</summary>
    public ShadowReasonRow AuthorizationsMatchedWithoutNumber { get; set; } = new();
    public List<ShadowReasonRow> ByProcedure { get; set; } = new();
    public List<ShadowReasonRow> ByBillingProvider { get; set; } = new();

    /// <summary>Every core denial checked against CHO's answer.</summary>
    public ShadowDenialVerification DenialVerification { get; set; } = new();
}

/// <summary>The core's denials, each with CHO's verdict (<c>denialVerification</c> in the run report).</summary>
public class ShadowDenialVerification
{
    public int CoreDenials { get; set; }
    public int Claims { get; set; }
    public ShadowDenialGroup ChoWouldPay { get; set; } = new();
    public ShadowDenialGroup ChoDeniesForAnotherReason { get; set; } = new();
    public ShadowDenialGroup ChoConfirms { get; set; } = new();
    public ShadowDenialGroup ChoUndecided { get; set; } = new();
    public decimal? ConfirmRate { get; set; }
    public List<ShadowDenialCodeRow> ByCoreDenialCode { get; set; } = new();
}

public class ShadowDenialGroup
{
    public int Denials { get; set; }
    public int Claims { get; set; }
    public decimal Billed { get; set; }
    public decimal ChoWouldPayAmount { get; set; }
    public List<string> ExampleClaims { get; set; } = new();
}

public class ShadowDenialCodeRow
{
    public string CoreDenialCode { get; set; } = string.Empty;
    public int Denials { get; set; }
    public int ChoWouldPay { get; set; }
    public int ChoDeniesForAnotherReason { get; set; }
    public int ChoConfirms { get; set; }
    public int ChoUndecided { get; set; }
    public decimal ChoWouldPayAmount { get; set; }
    public decimal Billed { get; set; }
    public decimal? ConfirmRate { get; set; }
    public List<string> ChoCodesInstead { get; set; } = new();
    public List<string> ExampleClaims { get; set; } = new();
}

/// <summary>One claim's comparison (the fields the page shows).</summary>
public class ShadowResult
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string? SourceClaimId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string CoreOutcome { get; set; } = string.Empty;
    public string ChoOutcome { get; set; } = string.Empty;
    public decimal CoreAllowed { get; set; }
    public decimal ChoAllowed { get; set; }
    public decimal CorePaid { get; set; }
    public decimal ChoPaid { get; set; }
    public decimal PaidVariance { get; set; }
    public string? CoreDenialCode { get; set; }
    public string? ChoDenialCode { get; set; }
    public string? ChoPendCode { get; set; }
    public string? ChoPendReason { get; set; }
    public List<string> ChoStageFindings { get; set; } = new();
}

/// <summary>Completed runs rolled up by day or week (<c>GET .../shadow-adjudication/trend</c>).</summary>
public class ShadowRunTrend
{
    public string Bucket { get; set; } = "week";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public List<ShadowRunTrendPoint> Points { get; set; } = new();
}

public class ShadowRunTrendPoint
{
    public string Period { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int ClaimsCompared { get; set; }
    public int ComparableClaims { get; set; }
    public int DecidedClaims { get; set; }
    public int MatchedClaims { get; set; }
    public decimal? DecidedRate { get; set; }
    public decimal? MatchRate { get; set; }
    public decimal CorePaidMoreAmount { get; set; }
    public decimal CorePaidLessAmount { get; set; }
}
