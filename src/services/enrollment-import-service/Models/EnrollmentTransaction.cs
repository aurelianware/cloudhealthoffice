using System.ComponentModel.DataAnnotations;

namespace EnrollmentImportService.Models;

/// <summary>
/// Single 834 transaction (one <c>MemberEnrollment</c> row out of a batch)
/// persisted for audit + member-service /834-transactions queries.
/// </summary>
public class EnrollmentTransaction
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string TransactionId { get; set; } = string.Empty;

    [Required]
    public string BatchId { get; set; } = string.Empty;

    [Required]
    public string MemberId { get; set; } = string.Empty;

    public string MemberName { get; set; } = string.Empty;

    public string MaintenanceTypeCode { get; set; } = string.Empty;

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = "Accepted";

    public string? FileName { get; set; }

    public string? SubscriberId { get; set; }
}
