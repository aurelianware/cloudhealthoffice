namespace FhirService.Models.PayerToPayer;

/// <summary>
/// Where an outbound bulk Payer-to-Payer job stands. It moves forward only:
/// Pending → Matching → Matched → Exporting → Completed, or to Failed.
/// </summary>
public enum PayerToPayerBulkOutboundStatus
{
    /// <summary>Recorded; the members have not been sent to the payer yet.</summary>
    Pending,

    /// <summary><c>$bulk-member-match</c> was accepted; polling for its answer.</summary>
    Matching,

    /// <summary>The payer matched members; the export has not been kicked off yet.</summary>
    Matched,

    /// <summary><c>$davinci-data-export</c> was accepted; polling for its manifest.</summary>
    Exporting,

    /// <summary>Done. Each member's own outcome says what became of them.</summary>
    Completed,

    /// <summary>The job could not finish; see <see cref="PayerToPayerBulkOutboundJob.Failure"/>.</summary>
    Failed,
}

/// <summary>Why a bulk job failed, as a closed set (never a peer's text).</summary>
public enum PayerToPayerBulkOutboundFailure
{
    None,
    TargetPayerNotConfigured,
    RemoteUnauthorized,
    InvalidRemoteResponse,
    ExportTooLarge,
    TimedOut,
}

/// <summary>What became of one member of a bulk job.</summary>
public enum PayerToPayerBulkMemberOutcome
{
    /// <summary>Not yet sent.</summary>
    Pending,

    /// <summary>Not a member of the tenant: nothing was sent.</summary>
    MemberNotFound,

    /// <summary>Several coverages with the payer and none could be chosen: nothing was sent.</summary>
    LocalCoverageAmbiguous,

    /// <summary>No active Payer-to-Payer opt-in: nothing was sent.</summary>
    NotAuthorized,

    /// <summary>Sent in the <c>$bulk-member-match</c>; no answer yet.</summary>
    Submitted,

    /// <summary>The payer matched the member; their data is being exported.</summary>
    Matched,

    /// <summary>The payer could not match the member (<c>nomatch</c>, or not answered for).</summary>
    NoMatch,

    /// <summary>The payer matched the member but cannot honour the consent (<c>consentconstraint</c>).</summary>
    ConsentConstrained,

    /// <summary>The payer answered two of the job's members as the same patient: neither is used.</summary>
    Ambiguous,

    /// <summary>The member's opt-in ended after the match: their data was not kept.</summary>
    ConsentWithdrawn,

    /// <summary>The export carried nothing for the member.</summary>
    NoData,

    /// <summary>What the export carried for the member failed validation, and was not kept.</summary>
    Rejected,

    /// <summary>Valid data could not be stored; the member's record is unchanged.</summary>
    IngestionFailed,

    /// <summary>The member's data was validated and stored.</summary>
    Completed,
}

/// <summary>One member of a bulk job. CHO's ids and outcome codes only: no demographics, no payload.</summary>
public sealed class PayerToPayerBulkOutboundMember
{
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Opaque token sent as an identifier on the member's submitted Patient,
    /// which the payer echoes back in its Groups: how its answer is tied to the
    /// member without trusting its own ids.
    /// </summary>
    public string? CorrelationId { get; set; }

    public PayerToPayerBulkMemberOutcome Outcome { get; set; } = PayerToPayerBulkMemberOutcome.Pending;

    /// <summary>The member's id at the payer, from its matched Group.</summary>
    public string? RemoteMemberId { get; set; }

    /// <summary>The consent the member was sent under.</summary>
    public string? AuthorizingConsentId { get; set; }

    /// <summary>The import ledger's exchange id for this member's data.</summary>
    public string? ExchangeId { get; set; }

    public int ReceivedResourceCount { get; set; }
    public int PersistedResourceCount { get; set; }
}

/// <summary>
/// One outbound bulk Payer-to-Payer request and its progress. The status URLs
/// it keeps are the peer's, checked against the directory origin before they
/// were kept; they are never logged.
/// </summary>
public sealed class PayerToPayerBulkOutboundJob
{
    public string JobId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string TargetPayerId { get; set; } = string.Empty;
    public string? TargetEndpointKey { get; set; }
    public string? InitiatedBy { get; set; }

    /// <summary>The date the members' coverage with the payer is matched as of (yyyy-MM-dd), when given.</summary>
    public string? AsOfDate { get; set; }

    public PayerToPayerBulkOutboundStatus Status { get; set; } = PayerToPayerBulkOutboundStatus.Pending;
    public PayerToPayerBulkOutboundFailure Failure { get; set; } = PayerToPayerBulkOutboundFailure.None;

    /// <summary>The status URL being polled (the match's, then the export's).</summary>
    public string? StatusUrl { get; set; }

    /// <summary>The payer's matched Group, which the export is run on.</summary>
    public string? MatchedGroupId { get; set; }

    public List<PayerToPayerBulkOutboundMember> Members { get; set; } = [];

    /// <summary>Files the export manifest listed.</summary>
    public int ExportFileCount { get; set; }

    /// <summary>Resources the export carried that named more than one of the members, which were not kept.</summary>
    public int CrossMemberResourceCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the worker holding the job lets go of it.</summary>
    public DateTimeOffset? LeasedUntil { get; set; }

    /// <summary>When the record is removed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsTerminal => Status is PayerToPayerBulkOutboundStatus.Completed or PayerToPayerBulkOutboundStatus.Failed;
}
