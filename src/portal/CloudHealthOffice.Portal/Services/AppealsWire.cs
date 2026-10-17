using System.Text.Json;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Wire shapes returned by appeals-service (<c>Appeal</c> and
/// <c>AppealListResponse</c> in appeals-service) and their mapping to the
/// portal's view models. The service returns <c>{ "items": [...] }</c> for
/// list endpoints and serializes enums either as names or as numbers, so the
/// enum-valued fields are read as <see cref="JsonElement"/>.
/// </summary>
internal sealed class AppealListWire
{
    public List<AppealWire> Items { get; set; } = new();
}

internal sealed class AppealWire
{
    public string Id { get; set; } = string.Empty;
    public string AppealNumber { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public JsonElement AppealType { get; set; }
    public JsonElement Status { get; set; }
    public string AppealReason { get; set; } = string.Empty;
    public bool IsUrgent { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateTime? TargetResponseDate { get; set; }
    public DateTime? DecisionDate { get; set; }
    public string? AssignedReviewerId { get; set; }
    public bool IsOverdue { get; set; }
    public AppealDecisionWire? Decision { get; set; }
    public List<AppealNoteWire> Notes { get; set; } = new();
    public List<AppealAttachmentWire> Attachments { get; set; } = new();
}

internal sealed class AppealDecisionWire
{
    public JsonElement DecisionType { get; set; }
    public string? DecisionReason { get; set; }
    public string? DecisionMaker { get; set; }
    public DateTime DecisionDate { get; set; }
}

internal sealed class AppealNoteWire
{
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public string NoteText { get; set; } = string.Empty;
}

internal sealed class AppealAttachmentWire
{
    public string AttachmentId { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? AttachmentTypeDescription { get; set; }
    public string AttachmentTypeCode { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
}

internal static class AppealWireMapper
{
    // Mirrors the appeals-service enums (1-based).
    private static readonly string[] AppealTypes = { "", "Reconsideration", "PeerReview", "ExternalReview", "Grievance" };
    private static readonly string[] Statuses = { "", "Draft", "Submitted", "InReview", "PendingInfo", "Closed" };
    private static readonly string[] DecisionTypes = { "", "Approved", "Denied", "PartialApproval" };

    internal static string EnumText(JsonElement value, string[] names)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number when value.TryGetInt32(out var n) && n > 0 && n < names.Length => names[n],
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    internal static AppealSummary ToSummary(AppealWire a, DateTime nowUtc)
    {
        var status = EnumText(a.Status, Statuses);
        var due = a.TargetResponseDate ?? a.SubmittedDate;
        return new AppealSummary
        {
            Id = a.Id,
            AppealId = string.IsNullOrEmpty(a.AppealNumber) ? a.Id : a.AppealNumber,
            MemberId = a.MemberId,
            MemberName = a.PatientName,
            AppealType = EnumText(a.AppealType, AppealTypes),
            OriginalDecisionId = string.IsNullOrEmpty(a.ClaimNumber) ? a.ClaimId : a.ClaimNumber,
            OriginalDecision = "Denied",
            OriginalDenialReason = JoinNonEmpty(a.DenialReasonCode, a.DenialReason),
            Status = status,
            IsExpedited = a.IsUrgent,
            FiledDate = a.SubmittedDate,
            DueDate = due,
            DaysRemaining = a.TargetResponseDate is null ? 0 : (int)Math.Ceiling((due - nowUtc).TotalDays),
            AssignedReviewer = a.AssignedReviewerId ?? "Unassigned",
            ComplianceStatus = Compliance(a, status, due, nowUtc)
        };
    }

    internal static AppealDetails ToDetails(AppealWire a, DateTime nowUtc)
    {
        var s = ToSummary(a, nowUtc);
        var details = new AppealDetails
        {
            AppealId = s.AppealId,
            RecordId = s.Id,
            MemberName = s.MemberName,
            MemberId = s.MemberId,
            AppealType = s.AppealType,
            OriginalDecisionId = s.OriginalDecisionId,
            OriginalDecision = s.OriginalDecision,
            OriginalDenialReason = s.OriginalDenialReason,
            AppealReason = a.AppealReason,
            Status = s.Status,
            IsExpedited = s.IsExpedited,
            FiledDate = s.FiledDate,
            DueDate = s.DueDate,
            DaysRemaining = s.DaysRemaining,
            AssignedReviewer = s.AssignedReviewer,
            ComplianceStatus = s.ComplianceStatus,
            FinalDecision = a.Decision is null ? string.Empty : EnumText(a.Decision.DecisionType, DecisionTypes),
            FinalDecisionNotes = a.Decision?.DecisionReason ?? string.Empty,
            DecisionDate = a.DecisionDate ?? a.Decision?.DecisionDate,
            Documents = a.Attachments.Select(att => new AppealDocument
            {
                DocumentId = att.AttachmentId,
                DocumentName = att.FileName ?? att.AttachmentTypeDescription ?? att.AttachmentTypeCode,
                DocumentType = att.AttachmentTypeDescription ?? att.AttachmentTypeCode,
                UploadedDate = att.UploadedAt,
                UploadedBy = string.Empty
            }).ToList(),
            Timeline = a.Notes
                .OrderBy(n => n.CreatedAt)
                .Select(n => new AppealTimelineEvent
                {
                    EventDate = n.CreatedAt,
                    EventType = "Note",
                    Description = n.NoteText,
                    PerformedBy = n.CreatedBy
                }).ToList()
        };
        details.Timeline.Insert(0, new AppealTimelineEvent
        {
            EventDate = a.SubmittedDate,
            EventType = "Filed",
            Description = "Appeal submitted",
            PerformedBy = string.Empty
        });
        return details;
    }

    private static string Compliance(AppealWire a, string status, DateTime due, DateTime nowUtc)
    {
        if (string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase)) return "Closed";
        if (a.IsOverdue) return "Overdue";
        if (a.TargetResponseDate is null) return "No deadline set";
        return (due - nowUtc).TotalDays <= 3 ? "At risk" : "On track";
    }

    private static string JoinNonEmpty(params string?[] parts) =>
        string.Join(" — ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
