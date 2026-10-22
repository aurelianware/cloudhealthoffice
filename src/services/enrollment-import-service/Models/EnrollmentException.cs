using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EnrollmentImportService.Models;

public enum EnrollmentExceptionStatus
{
    /// <summary>Waiting for an enrollment specialist (an Enrollment834 work item).</summary>
    Open,

    /// <summary>Waiting for the sponsor; comes back to the queue at <see cref="EnrollmentException.HeldUntil"/>.</summary>
    Held,

    /// <summary>Decided: see <see cref="EnrollmentException.Resolution"/>.</summary>
    Resolved,
}

public enum EnrollmentExceptionResolution
{
    /// <summary>Reprocessed (as received, corrected or matched to a member) and applied.</summary>
    Applied,

    /// <summary>Returned to the sponsor to correct in a later file.</summary>
    SentBackToSponsor,

    /// <summary>Nothing to do (e.g. a duplicate of a change already applied).</summary>
    NoAction,
}

/// <summary>What an exception would re-run: a subscriber loop, or a dependent on its own.</summary>
public enum EnrollmentExceptionSourceKind { Subscriber, Dependent }

/// <summary>One action a specialist took, kept in order.</summary>
public sealed class EnrollmentExceptionAction
{
    public string Action { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public string? Note { get; set; }

    /// <summary>For a reprocess: the reason it failed again, if it did.</summary>
    public string? OutcomeReasonCode { get; set; }
}

/// <summary>
/// An 834 member change CHO could not apply, kept so a specialist can fix it
/// (docs/architecture/enrollment-operations.md §3). One per rejected
/// transaction; its id is that transaction row's id. Holds the change as
/// received so it can be reprocessed, and is PHI like the member record.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class EnrollmentException
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Short reference for screens and work items, e.g. "EX-7F3A9C21".</summary>
    public string Number { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public EnrollmentExceptionStatus Status { get; set; } = EnrollmentExceptionStatus.Open;

    [BsonRepresentation(BsonType.String)]
    public EnrollmentExceptionResolution? Resolution { get; set; }

    // Where it came from
    public string BatchId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? EdiFileId { get; set; }
    public string? FilePosition { get; set; }

    /// <summary>The first transaction row (the original rejection), then each reprocess attempt's row.</summary>
    public List<string> TransactionIds { get; set; } = new();

    // Who it is about
    public string? GroupNumber { get; set; }
    public string? SubscriberId { get; set; }
    public string? MemberId { get; set; }
    public string? MemberName { get; set; }

    // What the sponsor asked for
    public string? MaintenanceTypeCode { get; set; }
    public string? MaintenanceReasonCode { get; set; }
    public string? EffectiveDate { get; set; }
    public string? TerminationDate { get; set; }

    // Why CHO could not apply it (the latest attempt)
    public string ReasonCode { get; set; } = string.Empty;
    public List<EnrollmentTransactionError> Errors { get; set; } = new();

    /// <summary>The change as received (or as last corrected), for reprocessing.</summary>
    [BsonRepresentation(BsonType.String)]
    public EnrollmentExceptionSourceKind SourceKind { get; set; }
    public MemberEnrollment? Subscriber { get; set; }
    public Dependent? Dependent { get; set; }

    public DateTime? HeldUntil { get; set; }
    public int Attempts { get; set; }
    public List<EnrollmentExceptionAction> Actions { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }

    /// <summary>Optimistic concurrency: +1 on every write.</summary>
    public long Version { get; set; }

    /// <summary>Needs a person now: open, or a hold that has run out.</summary>
    [JsonIgnore, BsonIgnore]
    public bool NeedsWork => Status == EnrollmentExceptionStatus.Open;

    public bool IsWorkable(DateTime now) =>
        Status == EnrollmentExceptionStatus.Open
        || (Status == EnrollmentExceptionStatus.Held && HeldUntil is { } until && until <= now);

    public static string NumberFor(string id) =>
        "EX-" + new string(id.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToUpperInvariant();
}
