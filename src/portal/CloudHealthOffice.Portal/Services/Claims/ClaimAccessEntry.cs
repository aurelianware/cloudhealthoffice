namespace CloudHealthOffice.Portal.Services;

/// <summary>One read of a claim in the claim access log.</summary>
public sealed class ClaimAccessEntry
{
    public string Id { get; set; } = string.Empty;
    public string? ClaimId { get; set; }
    public List<string> ClaimIds { get; set; } = new();
    public int? ResultCount { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? Route { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string? ActorName { get; set; }
    public List<string> ActorRoles { get; set; } = new();
    public bool ActorIsService { get; set; }
    public string? ClientIp { get; set; }
    public DateTime OccurredAt { get; set; }

    public string Actor => string.IsNullOrWhiteSpace(ActorName) ? ActorId : $"{ActorName} ({ActorId})";

    /// <summary>"Viewed", "Notes", "Search (12 results)", ...</summary>
    public string What => Action switch
    {
        "View" => "Viewed the claim",
        "Search" or "List" => ResultCount is { } n ? $"{Action} returning it ({n} results)" : $"{Action} returning it",
        "Notes" => "Read notes",
        "Edits" => "Read edits",
        "AdjudicationDetail" => "Read adjudication detail",
        "AuditTimeline" => "Read history",
        "DuplicateMatches" => "Read duplicate matches",
        "AiExamination" => "Read AI examination",
        "Acknowledgment" => "Read 277CA",
        "Eob" => "Read EOB",
        "Adjustments" => "Read adjustments",
        "WorkQueue" => "Work queue",
        "AccessLog" => "Read this access log",
        _ => Action,
    };
}
