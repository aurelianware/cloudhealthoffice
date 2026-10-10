using System.ComponentModel.DataAnnotations;
using MongoDB.Bson.Serialization.Attributes;

namespace ClaimsService.Models;

/// <summary>
/// Single 837 import transaction (one CLM segment out of a raw837 upload)
/// persisted for audit + an admin console view — the 837-side counterpart
/// of enrollment-import-service's <c>EnrollmentTransaction</c>. Written
/// once per parsed claim at <c>ClaimsV1Controller.ImportRaw837</c>,
/// regardless of whether submission succeeded, so a rejected claim is still
/// visible to whoever is troubleshooting an evaluator's file drop.
/// </summary>
[BsonIgnoreExtraElements]
public class ClaimImportTransaction
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string ClaimNumber { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string MemberId { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"Accepted" or "Rejected" — mirrors EnrollmentTransaction.Status.</summary>
    public string Status { get; set; } = "Accepted";

    public List<string> Errors { get; set; } = [];

    /// <summary>ST02 of the 837 transaction set the claim came from (null when SNIP validation is disabled).</summary>
    public string? TransactionSetControlNumber { get; set; }

    /// <summary>SNIP outcome of that transaction set, as in 999 IK501: A, E or R.</summary>
    public string? AcknowledgmentCode { get; set; }

    /// <summary>
    /// ISA13 of the 999 returned for the upload, linking the record to the
    /// acknowledgment. The 999 text itself is per file and is returned in
    /// the import response, not stored on each claim record.
    /// </summary>
    public string? Acknowledgment999ControlNumber { get; set; }

    /// <summary>ISA05 interchange sender id qualifier of the submitter.</summary>
    public string? SubmitterQualifier { get; set; }

    /// <summary>ISA06 interchange sender id — the submitter / trading partner (<c>x12Config.senderId</c>).</summary>
    public string? SubmitterId { get; set; }

    /// <summary>GS02 application sender code (trimmed).</summary>
    public string? ApplicationSenderCode { get; set; }

    /// <summary><see cref="SubmitterId"/> trimmed and upper-cased: the submitter filter matches on this.</summary>
    public string? SubmitterIdNormalized { get; set; }

    /// <summary><see cref="ApplicationSenderCode"/> trimmed and upper-cased: the submitter filter matches on this.</summary>
    public string? ApplicationSenderCodeNormalized { get; set; }

    /// <summary>ISA13 interchange control number of the inbound 837.</summary>
    public string? InterchangeControlNumber { get; set; }

    /// <summary>GS06 functional group control number of the inbound 837.</summary>
    public string? GroupControlNumber { get; set; }

    /// <summary>
    /// <c>ClaimsImport:Snip:PartnerOverrides</c> key whose levels validated the
    /// claim's transaction set; null when the global levels applied.
    /// </summary>
    public string? SnipPartnerOverride { get; set; }

    /// <summary>
    /// SNIP findings at a Warn level (accepted, reported in the 999 as IK3/IK4
    /// with IK5 E) for this claim and its transaction set. Lets ops query who
    /// sends e.g. a claim-level DTP*472 (<c>L2-2300-DTP472</c>) before moving
    /// that submitter's Level 2 to Reject. Findings are attached by CLM01, so
    /// claims sharing a CLM01 in one set share claim-level findings.
    /// </summary>
    public List<SnipFindingRecord> SnipWarnings { get; set; } = [];
}

/// <summary>One SNIP finding as stored on a <see cref="ClaimImportTransaction"/>.</summary>
[BsonIgnoreExtraElements]
public class SnipFindingRecord
{
    /// <summary>SNIP level 1–5.</summary>
    public int Level { get; set; }

    /// <summary>Stable rule id, e.g. <c>L2-2300-DTP472</c>.</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>"Warning" or "Error".</summary>
    public string Severity { get; set; } = "Warning";

    /// <summary>
    /// The finding's message, stored for Level 2 only. Messages at other
    /// levels can carry claim values (diagnosis or procedure codes, amounts)
    /// and are left null; use <see cref="RuleId"/> and the location.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>ST02 of the transaction set.</summary>
    public string? TransactionSetControlNumber { get; set; }

    /// <summary>True when the finding is about this claim (CLM01 matched), false for a set-level finding.</summary>
    public bool ClaimLevel { get; set; }

    public string? Loop { get; set; }
    public string? SegmentId { get; set; }
    public int? SegmentPosition { get; set; }
    public int? ElementPosition { get; set; }
    public int? ComponentPosition { get; set; }
    public string? DataElementReference { get; set; }

    /// <summary>Location in X12 notation, e.g. <c>2300 DTP*472</c> or <c>2300 CLM05-2</c>.</summary>
    public string? Location { get; set; }
}
