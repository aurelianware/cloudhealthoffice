using System.ComponentModel.DataAnnotations;

namespace ClaimsService.Models;

/// <summary>
/// Single 837 import transaction (one CLM segment out of a raw837 upload)
/// persisted for audit + an admin console view — the 837-side counterpart
/// of enrollment-import-service's <c>EnrollmentTransaction</c>. Written
/// once per parsed claim at <c>ClaimsV1Controller.ImportRaw837</c>,
/// regardless of whether submission succeeded, so a rejected claim is still
/// visible to whoever is troubleshooting an evaluator's file drop.
/// </summary>
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
}
