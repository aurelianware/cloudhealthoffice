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
}
