using Microsoft.AspNetCore.Mvc;

namespace FhirService.Models;

/// <summary>
/// Search parameters common to all four appeal-derived FHIR resource
/// surfaces (Task, Communication, DocumentReference, ClaimResponse).
/// Each resource projects a slice of the same underlying appeal record
/// owned by appeals-service.
/// </summary>
public class AppealTaskSearchParams : FhirSearchParamsBase
{
    /// <summary>`patient` — references the appeal's member.</summary>
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    /// <summary>`status` — R4 Task.status, narrowed to cho-appeal-task-status.</summary>
    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>`focus` — the original denied Claim the appeal acts on.</summary>
    [FromQuery(Name = "focus")]
    public string? Focus { get; set; }

    /// <summary>`authored-on` — Task.authoredOn.</summary>
    [FromQuery(Name = "authored-on")]
    public string? AuthoredOn { get; set; }

    /// <summary>`owner` — Task.owner (assigned reviewer).</summary>
    [FromQuery(Name = "owner")]
    public string? Owner { get; set; }
}

public class AppealCommunicationSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>`about` — references the Task the communication is attached to.</summary>
    [FromQuery(Name = "about")]
    public string? About { get; set; }

    [FromQuery(Name = "sent")]
    public string? Sent { get; set; }
}

public class AppealDocumentReferenceSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>`related` — references the Task/Claim the document relates to.</summary>
    [FromQuery(Name = "related")]
    public string? Related { get; set; }

    [FromQuery(Name = "type")]
    public string? Type { get; set; }
}

public class AppealClaimResponseSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    /// <summary>`request` — the original Claim the ClaimResponse adjudicates.</summary>
    [FromQuery(Name = "request")]
    public string? Request { get; set; }

    [FromQuery(Name = "created")]
    public string? Created { get; set; }
}
