namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Portal projection of enrollment-import-service's <c>EnrollmentEvent</c>. Surfaced via
/// the member-service proxy at GET /members/{id}/enrollment-events so consent /
/// audit / tenant filtering happens on the same boundary as every other member read.
/// </summary>
public class EnrollmentEvent
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime? EventDate { get; set; }
    public DateTime? RetroEffectiveDate { get; set; }
    public string? SourceBatchId { get; set; }
    public string? TransactionId { get; set; }
    public string? MaintenanceType { get; set; }
    public string? MaintenanceReason { get; set; }
    public string? Source { get; set; }

    /// <summary>Raw JSON payload (changed fields, plan ids, etc.). Rendered as-is for now.</summary>
    public System.Text.Json.JsonElement? Payload { get; set; }

    /// <summary>Raw 834 (or manual JSON) snippet captured at write time, for audit display.</summary>
    public string? RawSegment { get; set; }
}
