using CloudHealthOffice.Infrastructure.Contacts;
using AppealsService.Services.RulePacks;
using MongoDB.Bson.Serialization.Attributes;

namespace AppealsService.Models;

/// <summary>Member-self intake without fabricated claim/provider identifiers. Separate from legacy claim appeals.</summary>
[BsonIgnoreExtraElements]
public sealed class MemberServiceCase
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string? AuthorizationId { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string EncryptedSummary { get; set; } = string.Empty;
    public string Status { get; set; } = "Received";
    public long Revision { get; set; }
    public string? EncryptedResolution { get; set; }
    public DateTime? DecisionRecordedAt { get; set; }
    public string? DecisionRecordedBy { get; set; }
    /// <summary>The structured appeal outcome. Required for an appeal decision; null for a grievance.</summary>
    public MemberAppealOutcome? DecisionOutcome { get; set; }
    /// <summary>
    /// CMS-0057-F reporting: when the appeal outcome was accepted as an AppealDecision fact on the
    /// authorization's reporting ledger. Null while it is still owed; see <see cref="ReportingRejectedReason"/>.
    /// </summary>
    public DateTime? ReportedToAuthorizationAt { get; set; }
    /// <summary>Delivery attempts so far, for the retry sweep.</summary>
    public int ReportingAttempts { get; set; }
    /// <summary>When the retry sweep may try again after a failed delivery.</summary>
    public DateTime? ReportingNextAttemptAt { get; set; }
    /// <summary>Set when authorization-service refused the fact for good; the sweep stops retrying.</summary>
    public string? ReportingRejectedReason { get; set; }
    /// <summary>
    /// Each time an operator put a refused or given-up outcome back on the queue, newest last
    /// (at most <see cref="MaxReportingRequeues"/>), so the refusal it cleared stays on record.
    /// </summary>
    public List<AppealReportingRequeue> ReportingRequeues { get; set; } = new();
    public const int MaxReportingRequeues = 20;
    // Persist JSON to keep the pinned evaluation independent of BSON immutable-record conventions.
    public string PinnedRuleEvaluationJson { get; set; } = string.Empty;
    public ContactCaseResult Receipt { get; set; } = new();
}

/// <summary>An operator re-queue of a refused appeal-outcome report, and the refusal it cleared.</summary>
public sealed class AppealReportingRequeue
{
    public DateTime At { get; set; }
    public string By { get; set; } = string.Empty;
    public string PriorReason { get; set; } = string.Empty;
    public int PriorAttempts { get; set; }
}

/// <summary>What the appeal decided about the adverse prior-authorization determination.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum MemberAppealOutcome
{
    /// <summary>The denial stands.</summary>
    Upheld = 1,
    /// <summary>The denial is reversed in full: the service is approved.</summary>
    Overturned = 2,
    /// <summary>The denial is reversed in part.</summary>
    PartiallyOverturned = 3,
}
