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
    public List<ShadowReasonRow> ByChoStage { get; set; } = new();
    public List<ShadowReasonRow> ByDenialCodes { get; set; } = new();
    public List<ShadowReasonRow> ByProcedure { get; set; } = new();
    public List<ShadowReasonRow> ByBillingProvider { get; set; } = new();
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
